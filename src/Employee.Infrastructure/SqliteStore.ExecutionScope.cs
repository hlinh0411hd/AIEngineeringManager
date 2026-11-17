using System.Text.Json;
using System.Text.RegularExpressions;
using Employee.Core;

namespace Employee.Infrastructure;
public sealed partial class SqliteStore
{
    public AccessLevel ApprovedTaskAccess(WorkItem goal)
    {
        if (goal.GrantedAccess is { } saved)
            return saved;
        // Latest explicit permission edit wins over the original grant, including read-only restrictions.
        foreach (var entry in Logs(goal.Id).Where(l => l.Event is "UserProjectAccess" or "UserClassification" or "Created").OrderByDescending(l => l.Sequence))
        {
            var pattern = entry.Event == "Created" ? @"Access:\s*(TextOnly|ReadProject|WriteProject)\b" : entry.Event == "UserProjectAccess" ? @"\bto\s+[^/\r\n]+/\s*(TextOnly|ReadProject|WriteProject)\b" : @"classification to\s+\w+\s*/\s*(TextOnly|ReadProject|WriteProject)\b";
            var match = Regex.Match(entry.Message, pattern);
            if (match.Success && Enum.TryParse<AccessLevel>(match.Groups[1].Value, out var access))
                return access;
        }

        return goal.Access; // Unknown provenance never creates new access.
    }

    public bool NormalizeImplementationScope(string goalId)
    {
        lock (gate)
        {
            var goal = WorkItems().Single(w => w.Id == goalId);
            if (!goal.IsGoal || goal.ToolWorkflowId != null || goal.State is WorkState.Running or WorkState.Completed or WorkState.Skipped)
                return false;
            var explicitGrant = goal.GrantedAccess != null;
            var logs = Logs(goalId);
            var approved = ApprovedTaskAccess(goal);
            var grant = !explicitGrant && !TaskAccessProposal.HasWriteRequest(goal, Projects().Single(p => p.Id == goal.ProjectId)) && TaskIntent.IsReadOnly(goal.Request) && approved > AccessLevel.ReadProject ? AccessLevel.ReadProject : approved;
            var automatic = goal.AutomaticIntent ?? (goal.AutoAssign || logs.Any(l => l.Event is "Assignment" or "Rerun"));
            var project = Projects().Single(p => p.Id == goal.ProjectId);
            var implementation = TaskIntent.RequiresImplementation(goal.Request, project) && !TaskIntent.IsReadOnly(goal.Request);
            var session = goal.SessionId == null ? null : Sessions().Single(s => s.Id == goal.SessionId);
            if (session?.State is SessionState.Running or SessionState.Planning)
                return false;
            var workers = session == null ? Array.Empty<WorkItem>() : WorkItems().Where(w => w.SessionId == session.Id && !w.IsGoal && !w.IsCoordinator).ToArray();
            var changed = automatic && implementation && grant == AccessLevel.WriteProject && (goal.Kind != WorkKind.Coding || goal.Access != AccessLevel.WriteProject);
            goal.GrantedAccess = approved;
            goal.AutomaticIntent = automatic;
            if (changed)
            {
                goal.Kind = WorkKind.Coding;
                goal.Access = AccessLevel.WriteProject;
                goal.AutoAssign = false;
                goal.RoutingApproved = false;
                goal.ProviderId = null;
                goal.ModelId = null;
            }

            if (!explicitGrant && !TaskAccessProposal.HasWriteRequest(goal, Projects().Single(p => p.Id == goal.ProjectId)) && TaskIntent.IsReadOnly(goal.Request) && goal.Access > AccessLevel.ReadProject)
            {
                goal.Access = AccessLevel.ReadProject;
                goal.RoutingApproved = false;
                changed = true;
            }

            var adjusted = new List<WorkItem>();
            foreach (var worker in workers.Where(w => w.State is WorkState.Queued or WorkState.Paused or WorkState.WaitingForProvider or WorkState.WaitingForPermission or WorkState.WrongAccess or WorkState.WrongProject))
            {
                if (worker.Tool != null || workers.Any(w => w.Tool?.InputFrom == worker.Id))
                    continue;
                if (grant != AccessLevel.WriteProject || !automatic || !TaskIntent.RequiresImplementation(worker.Request, project) || TaskIntent.IsReadOnly(worker.Request))
                    continue;
                if (worker.Kind is WorkKind.Coding or WorkKind.Research && worker.Access == AccessLevel.WriteProject)
                    continue;
                worker.Kind = worker.Kind == WorkKind.Research ? WorkKind.Research : WorkKind.Coding;
                worker.Access = AccessLevel.WriteProject;
                worker.RoutingApproved = false;
                worker.State = WorkState.Queued;
                worker.ProviderId = null;
                worker.ModelId = null;
                worker.UpdatedAt = DateTimeOffset.UtcNow;
                adjusted.Add(worker);
            }

            var invalidDecisions = session == null ? Array.Empty<PendingDecision>() : Decisions(session.Id).Where(d => d.State == DecisionState.Pending && d.Kind == "Approval" && ((changed && d.WorkId == goal.Id) || adjusted.Any(w => w.Id == d.WorkId))).ToArray();
            if (changed || adjusted.Count > 0)
                goal.UpdatedAt = DateTimeOffset.UtcNow;
            using var transaction = connection.BeginTransaction();
            void Run(string sql, params (string, object? )[] values)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = sql;
                foreach (var(key, value)in values)
                    command.Parameters.AddWithValue(key, value ?? DBNull.Value);
                command.ExecuteNonQuery();
            }

            Run("UPDATE work SET data=$data WHERE id=$id", ("$data", WorkJson(goal, transaction)), ("$id", goal.Id));
            foreach (var worker in adjusted)
                Run("UPDATE work SET data=$data WHERE id=$id", ("$data", WorkJson(worker, transaction)), ("$id", worker.Id));
            foreach (var decision in invalidDecisions)
            {
                decision.State = DecisionState.Rejected;
                Run("UPDATE decisions SET data=$data WHERE id=$id", ("$data", JsonSerializer.Serialize(decision)), ("$id", decision.Id));
            }

            if (session != null && automatic && session.AccessCeiling != grant)
            {
                session.AccessCeiling = grant;
                Run("UPDATE sessions SET data=$data WHERE id=$id", ("$data", JsonSerializer.Serialize(session)), ("$id", session.Id));
            }

            if (changed || adjusted.Count > 0)
                Run("INSERT INTO logs(work_id,at,event,message) VALUES($id,$at,'ExecutionScopeRepair',$message)", ("$id", goal.Id), ("$at", DateTimeOffset.UtcNow.ToString("O")), ("$message", "Restored implementation routing within the user's existing " + grant + " grant. Finished results, project, budgets and checkpoints retained; adjusted pending workers: " + adjusted.Count));
            transaction.Commit();
            return changed || adjusted.Count > 0;
        }
    }
}
