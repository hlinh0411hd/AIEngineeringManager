using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Employee.Core;

namespace Employee.Infrastructure;
public sealed partial class SqliteStore : IIntentLearningStore
{
    public IReadOnlyList<IntentExample> IntentExamples(string projectId) => Read("SELECT data FROM intent_examples WHERE project_id=$project ORDER BY rowid DESC LIMIT 500", Decode<IntentExample>, ("$project", projectId));
    public void LearnIntent(IntentExample example)
    {
        if (string.IsNullOrWhiteSpace(example.Request) || example.Request.Length > 12000 || !Enum.IsDefined(example.Kind) || example.Kind == WorkKind.Planning || !Enum.IsDefined(example.Access) || !Enum.IsDefined(example.Source))
            throw new InvalidOperationException("Invalid confirmed task example.");
        lock (gate)
        {
            if (!Projects().Any(p => p.Id == example.ProjectId))
                throw new InvalidOperationException("Choose a registered project.");
            var normalized = TaskIntent.Normalize(example.Request);
            if (normalized.Length < 3)
                throw new InvalidOperationException("Task example is too short.");
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(example.ProjectId + "\n" + normalized)));
            var previous = Read("SELECT data FROM intent_examples WHERE id=$id", Decode<IntentExample>, ("$id", key)).FirstOrDefault();
            if (previous != null && previous.Source > example.Source)
                return;
            example = example with
            {
                Id = key,
                At = DateTimeOffset.UtcNow
            };
            Execute("INSERT INTO intent_examples(id,project_id,data) VALUES($id,$project,$data) ON CONFLICT(id) DO UPDATE SET data=$data", ("$id", key), ("$project", example.ProjectId), ("$data", JsonSerializer.Serialize(example)));
            Execute("DELETE FROM intent_examples WHERE project_id=$project AND id NOT IN (SELECT id FROM intent_examples WHERE project_id=$project ORDER BY json_extract(data,'$.Source') DESC,json_extract(data,'$.At') DESC LIMIT 500)", ("$project", example.ProjectId));
        }
    }

    public void ForgetIntent(string id) => Execute("DELETE FROM intent_examples WHERE id=$id", ("$id", id));
    public int ImportReviewedIntents(string projectId)
    {
        var all = WorkItems();
        var imported = 0;
        foreach (var goal in all.Where(w => w.ProjectId == projectId && w.IsGoal && w.State == WorkState.Completed && w.ToolWorkflowId == null).OrderByDescending(w => w.CreatedAt).Take(200))
        {
            if (TaskIntent.Normalize(goal.Request).Length < 3 || goal.Kind == WorkKind.Planning || !Logs(goal.Id).Any(l => l.Event == "UserReview") || goal.SessionId != null && all.Any(w => w.SessionId == goal.SessionId && !w.IsCoordinator && !w.IsGoal && w.State != WorkState.Completed))
                continue;
            LearnIntent(new("", goal.ProjectId, goal.Request, goal.Kind, goal.Access, IntentExampleSource.ReviewedCompletion, goal.Id, DateTimeOffset.UtcNow));
            imported++;
        }

        return imported;
    }

    public void CorrectTaskIntent(string id, WorkKind kind, AccessLevel access)
    {
        if (!Enum.IsDefined(kind) || kind == WorkKind.Planning || !Enum.IsDefined(access))
            throw new InvalidOperationException("Choose a task type and access.");
        lock (gate)
        {
            var goal = WorkItems().Single(w => w.Id == id);
            if (protectedTasks.ContainsKey(goal.Id) || !goal.IsGoal || goal.ToolWorkflowId != null || goal.State is WorkState.Running or WorkState.Completed)
                throw new InvalidOperationException("Stop the task first. Completed/tool workflows cannot be reclassified in place.");
            var steps = goal.SessionId == null ? Array.Empty<WorkItem>() : WorkItems().Where(w => w.SessionId == goal.SessionId && !w.IsGoal && !w.IsCoordinator).ToArray();
            if (steps.Any(w => w.Attempts > 0 || w.State is not (WorkState.Queued or WorkState.Paused or WorkState.WaitingForProvider)) || goal.SessionId != null && Plans(goal.SessionId).Count > 0)
                throw new InvalidOperationException("Task already has execution or a plan. Save the learning example and create a new task instead.");
            var session = goal.SessionId == null ? null : Sessions().Single(s => s.Id == goal.SessionId);
            if (session?.State is SessionState.Running or SessionState.Planning || WorkItems().Any(w => w.SessionId == goal.SessionId && goal.SessionId != null && w.State == WorkState.Running))
                throw new InvalidOperationException("Stop the running session first.");
            var pending = session == null ? Array.Empty<PendingDecision>() : Decisions(session.Id).Where(d => d.State == DecisionState.Pending).ToArray();
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
                step.RoutingApproved = false;
                step.ProviderId = null;
                step.ModelId = null;
                Run("UPDATE work SET data=$data WHERE id=$id", ("$data", WorkJson(step, transaction)), ("$id", step.Id));
            }

            foreach (var decision in pending)
            {
                decision.State = DecisionState.Rejected;
                Run("UPDATE decisions SET data=$data WHERE id=$id", ("$data", JsonSerializer.Serialize(decision)), ("$id", decision.Id));
            }

            goal.Kind = kind;
            goal.Access = access;
            goal.GrantedAccess = access;
            goal.AutomaticIntent = false;
            goal.AutoAssign = false;
            goal.RoutingApproved = false;
            goal.State = session == null ? WorkState.Saved : WorkState.Paused;
            goal.QueuedAt = null;
            goal.ProviderId = null;
            goal.ModelId = null;
            goal.Verification = VerificationState.Untested;
            goal.Notes = "User corrected task classification to " + kind + " / " + access + ". Resume explicitly; budgets and history preserved.";
            Run("UPDATE work SET data=$data WHERE id=$id", ("$data", WorkJson(goal, transaction)), ("$id", goal.Id));
            if (session != null)
            {
                session.AccessCeiling = access;
                session.State = SessionState.Paused;
                session.Notes = goal.Notes;
                Run("UPDATE sessions SET data=$data WHERE id=$id", ("$data", JsonSerializer.Serialize(session)), ("$id", session.Id));
            }

            Run("INSERT INTO logs(work_id,at,event,message) VALUES($id,$at,'UserClassification',$message)", ("$id", goal.Id), ("$at", DateTimeOffset.UtcNow.ToString("O")), ("$message", goal.Notes));
            transaction.Commit();
        }
    }
}
