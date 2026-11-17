using System.Text.Json;
using Employee.Core;

namespace Employee.Infrastructure;
public sealed partial class SqliteStore
{
    public void ApplyPendingTaskAccess(string id)
    {
        lock (gate)
        {
            var all = WorkItems();
            var goal = all.Single(w => w.Id == id);
            if (goal.PendingAccess is not { } request)
                return;
            var session = goal.SessionId == null ? null : Sessions().Single(s => s.Id == goal.SessionId);
            var related = session == null ? new[]
            {
                goal
            }

            : all.Where(w => w.SessionId == session.Id).ToArray();
            if (session?.State is SessionState.Running or SessionState.Planning || related.Any(w => w.State == WorkState.Running) || TestJobs().Any(j => j.State == TestJobState.Running && j.WorkIds.Any(wid => related.Any(w => w.Id == wid))))
                throw new InvalidOperationException("Stop the session and wait for tests before applying comment permissions.");
            var comment = goal.Comments.LastOrDefault(c => !c.IsAssistant);
            var valid = goal.ProjectId == request.ProjectId && goal.Access == request.PreviousAccess && comment != null && comment.Text == request.Comment && comment.At == request.CommentAt && request.RequestedAccess > goal.Access && Enum.IsDefined(request.RequestedAccess) && !(goal.AccessEditedAt >= comment.At) && TaskAccessProposal.AllowsRequest(goal, Projects().Single(p => p.Id == goal.ProjectId), request.RequestedAccess) && related.Any(w => w.Id == request.WorkId) && related.All(w => w.ProjectId == goal.ProjectId);
            goal.PendingAccess = null;
            goal.AccessResult = valid ? $"Applied {request.RequestedAccess} from comment. Reason: " + request.Reason : "Comment permission change superseded by a newer comment or manual permission edit; access unchanged. Reason: " + request.Reason;
            if (valid)
            {
                // Archive the prior execution atomically, including its checkpoints and approvals.
                // The next run must create a new session and route using the persisted grant.
                ResetTaskAccess(id, goal.ProjectId, request.RequestedAccess, goal.AccessResult);
                return;
            }

            var changed = new List<WorkItem>
            {
                goal
            };
            goal.UpdatedAt = DateTimeOffset.UtcNow;
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

            foreach (var work in changed)
                Run("UPDATE work SET data=$data WHERE id=$id", ("$id", work.Id), ("$data", WorkJson(work, transaction)));
            Run("INSERT INTO logs(work_id,at,event,message) VALUES($id,$at,'CommentTaskAccess',$message)", ("$id", goal.Id), ("$at", goal.UpdatedAt.Value.ToString("O")), ("$message", goal.AccessResult));
            transaction.Commit();
        }
    }
}
