using System.Text.Json;
using Employee.Core;

namespace Employee.Infrastructure;
public sealed partial class SqliteStore
{
    // Safe state repair only: no inference, file changes, budget resets, or reopening user-completed work.
    public bool RepairMisclassifiedGoal(string id)
    {
        lock (gate)
        {
            var goal = WorkItems().Single(w => w.Id == id);
            if (RepairBlockedProjectAssignment(goal))
                return true;
            if (!goal.IsGoal || goal.SessionId == null || goal.State is WorkState.Completed or WorkState.Running || goal.Kind is not (WorkKind.Classification or WorkKind.Extraction) || TaskIntent.IsExplicitDataTask(goal.Request, goal.Kind))
                return false;
            var logs = Logs(id);
            if (!logs.Any(l => l.Event == "Assignment"))
                return false;
            var steps = WorkItems().Where(w => w.SessionId == goal.SessionId && !w.IsCoordinator && !w.IsGoal).ToArray();
            if (steps.Length == 0 || steps.Any(w => w.ProviderId != "ollama" || w.Access != AccessLevel.TextOnly || w.Kind is not (WorkKind.Classification or WorkKind.Extraction) || w.State is not (WorkState.Completed or WorkState.NeedsReview)))
                return false;
            var created = logs.FirstOrDefault(l => l.Event == "Created")?.Message ?? "";
            var original = created.Contains("Access: WriteProject", StringComparison.Ordinal) ? AccessLevel.WriteProject : created.Contains("Access: ReadProject", StringComparison.Ordinal) ? AccessLevel.ReadProject : AccessLevel.TextOnly;
            if (original == AccessLevel.TextOnly)
                return false; // Never infer a historical project-access grant.
            var session = Sessions().Single(s => s.Id == goal.SessionId);
            if (session.State is SessionState.Running or SessionState.Planning)
                return false;
            var pendingDecisions = Decisions(session.Id).Where(d => d.State == DecisionState.Pending).ToArray();
            var pendingPlans = Plans(session.Id).Where(p => p.State == DecisionState.Pending).ToArray();
            using var transaction = connection.BeginTransaction();
            void Run(string sql, params (string, object? )[] args)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = sql;
                foreach (var(key, value)in args)
                    command.Parameters.AddWithValue(key, value ?? DBNull.Value);
                command.ExecuteNonQuery();
            }

            foreach (var step in steps)
            {
                step.IsCoordinator = true;
                step.State = WorkState.NeedsReview;
                step.Notes += " Historical misclassified text result retained; this did not implement the requested work.";
                Run("UPDATE work SET data=$data WHERE id=$id", ("$data", WorkJson(step, transaction)), ("$id", step.Id));
                Run("UPDATE model_runs SET coordinator=1 WHERE work_id=$id", ("$id", step.Id));
                InvalidateModelData();
            }

            foreach (var decision in pendingDecisions)
            {
                decision.State = DecisionState.Rejected;
                Run("UPDATE decisions SET data=$data WHERE id=$id", ("$data", JsonSerializer.Serialize(decision)), ("$id", decision.Id));
            }

            foreach (var plan in pendingPlans)
            {
                plan.State = DecisionState.Rejected;
                Run("UPDATE plans SET data=$data WHERE id=$id", ("$data", JsonSerializer.Serialize(plan)), ("$id", plan.Id));
            }

            goal.AutoAssign = true;
            goal.Kind = WorkKind.Coding;
            goal.Access = original;
            goal.State = WorkState.Paused;
            goal.QueuedAt = null;
            goal.ProviderId = null;
            goal.ModelId = null;
            goal.RoutingApproved = false;
            goal.Verification = VerificationState.Untested;
            if (goal.PreferredRoute?.ProviderId == "ollama")
                goal.PreferredRoute = null;
            goal.Notes = "Incorrect text classification repaired. No code was changed by Ollama. Run / resume to execute the original task with a permitted worker; prior usage/history are retained.";
            session.AccessCeiling = original;
            session.State = SessionState.Paused;
            session.Notes = goal.Notes;
            if (session.Coordinator?.ProviderId == "ollama")
                session.Coordinator = null;
            Run("UPDATE work SET data=$data WHERE id=$id", ("$data", WorkJson(goal, transaction)), ("$id", goal.Id));
            Run("UPDATE sessions SET data=$data WHERE id=$id", ("$data", JsonSerializer.Serialize(session)), ("$id", session.Id));
            Run("INSERT INTO logs(work_id,at,event,message) VALUES($id,$at,'AssignmentRepair',$message)", ("$id", goal.Id), ("$at", DateTimeOffset.UtcNow.ToString("O")), ("$message", goal.Notes + " Restored only the original Created access grant: " + original));
            transaction.Commit();
            return true;
        }
    }

    private bool RepairBlockedProjectAssignment(WorkItem goal)
    {
        // Only reclassify untouched automatic assignments; retain manual intent and all usage/history.
        if (!goal.IsGoal || goal.SessionId == null || goal.ToolWorkflowId != null || goal.Access != AccessLevel.TextOnly || goal.State is not (WorkState.WaitingForProvider or WorkState.Paused or WorkState.Saved or WorkState.AwaitingApproval) || goal.Kind is not (WorkKind.Content or WorkKind.Research or WorkKind.Classification or WorkKind.Extraction))
            return false;
        var logs = Logs(goal.Id);
        if (!logs.Any(l => l.Event == "Assignment"))
            return false;
        var project = Projects().SingleOrDefault(p => p.Id == goal.ProjectId);
        if (project == null || !TaskIntent.TryResolve(goal.Request, out var kind, out var access, project) || kind != WorkKind.Coding || access == AccessLevel.TextOnly)
            return false;
        var created = logs.FirstOrDefault(l => l.Event == "Created")?.Message ?? "";
        var original = created.Contains("Access: WriteProject", StringComparison.Ordinal) ? AccessLevel.WriteProject : created.Contains("Access: ReadProject", StringComparison.Ordinal) ? AccessLevel.ReadProject : AccessLevel.TextOnly;
        if (original == AccessLevel.TextOnly || access > original)
            return false;
        var steps = WorkItems().Where(w => w.SessionId == goal.SessionId && !w.IsGoal && !w.IsCoordinator).ToArray();
        if (steps.Any(w => w.Attempts != 0 || w.State is not (WorkState.WaitingForProvider or WorkState.Queued or WorkState.Paused) || w.Request != goal.Request) || Plans(goal.SessionId).Count > 0)
            return false;
        var session = Sessions().Single(s => s.Id == goal.SessionId);
        if (session.State is SessionState.Running or SessionState.Planning)
            return false;
        var pending = Decisions(session.Id).Where(d => d.State == DecisionState.Pending).ToArray();
        using var transaction = connection.BeginTransaction();
        void Run(string sql, params (string, object? )[] args)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            foreach (var(key, value)in args)
                command.Parameters.AddWithValue(key, value ?? DBNull.Value);
            command.ExecuteNonQuery();
        }

        foreach (var step in steps)
        {
            step.Kind = kind;
            step.Access = access;
            step.State = WorkState.Queued;
            step.ProviderId = null;
            step.ModelId = null;
            step.RoutingApproved = false;
            if (step.PreferredRoute?.ProviderId == "ollama")
                step.PreferredRoute = null;
            Run("UPDATE work SET data=$data WHERE id=$id", ("$data", WorkJson(step, transaction)), ("$id", step.Id));
        }

        foreach (var decision in pending)
        {
            decision.State = DecisionState.Rejected;
            Run("UPDATE decisions SET data=$data WHERE id=$id", ("$data", JsonSerializer.Serialize(decision)), ("$id", decision.Id));
        }

        goal.Kind = kind;
        goal.Access = access;
        goal.AutoAssign = false;
        goal.State = WorkState.Paused;
        goal.ProviderId = null;
        goal.ModelId = null;
        goal.QueuedAt = null;
        goal.RoutingApproved = false;
        if (goal.PreferredRoute?.ProviderId == "ollama")
            goal.PreferredRoute = null;
        goal.Notes = "Automatic text assignment corrected to project implementation before any worker ran. Original permissions, history and budgets retained.";
        session.AccessCeiling = access;
        session.State = SessionState.Paused;
        session.Notes = goal.Notes;
        Run("UPDATE work SET data=$data WHERE id=$id", ("$data", WorkJson(goal, transaction)), ("$id", goal.Id));
        Run("UPDATE sessions SET data=$data WHERE id=$id", ("$data", JsonSerializer.Serialize(session)), ("$id", session.Id));
        Run("INSERT INTO logs(work_id,at,event,message) VALUES($id,$at,'AssignmentRepair',$message)", ("$id", goal.Id), ("$at", DateTimeOffset.UtcNow.ToString("O")), ("$message", goal.Notes));
        transaction.Commit();
        return true;
    }
}
