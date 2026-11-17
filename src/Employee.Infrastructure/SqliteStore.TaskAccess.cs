using System.Text.Json;
using Employee.Core;

namespace Employee.Infrastructure;
public sealed partial class SqliteStore
{
    public void UpdateTaskAccess(string id, string projectId, AccessLevel access) => ResetTaskAccess(id, projectId, access, null);
    private void ResetTaskAccess(string id, string projectId, AccessLevel access, string? commentReason)
    {
        if (!Enum.IsDefined(access))
            throw new InvalidOperationException("Choose a valid access level.");
        lock (gate)
        {
            if (!Projects().Any(p => p.Id == projectId))
                throw new InvalidOperationException("Choose a registered project.");
            var all = WorkItems();
            var goal = all.Single(w => w.Id == id);
            if (!goal.IsGoal || goal.IsCoordinator || goal.ParentTaskId != null || goal.ToolWorkflowId != null || goal.State == WorkState.Running)
                throw new InvalidOperationException("Stop the task first. Tool workflows cannot be changed here.");
            var session = goal.SessionId == null ? null : Sessions().Single(s => s.Id == goal.SessionId);
            var related = session == null ? new[]
            {
                goal
            }

            : all.Where(w => w.SessionId == session.Id).ToArray();
            if (protectedTasks.ContainsKey(goal.Id) || session?.State is SessionState.Running or SessionState.Planning || related.Any(w => w.State == WorkState.Running || protectedTasks.ContainsKey(w.Id)) || TestJobs().Any(j => j.State == TestJobState.Running && j.WorkIds.Any(id => related.Any(w => w.Id == id))))
                throw new InvalidOperationException("Stop the session and wait for tests before changing project access.");
            if (session != null && (session.RootWorkId != goal.Id || session.ProjectIds.Any(p => p != goal.ProjectId) || related.Any(w => w.State is not (WorkState.Completed or WorkState.Skipped) && w.ProjectId != goal.ProjectId)))
                throw new InvalidOperationException("A shared session must be edited separately; this editor supports single-project tasks.");
            if (goal.ProjectId == projectId && goal.Access == access && goal.PendingAccess == null)
                return;
            var previous = $"{goal.ProjectId} / {goal.Access}";
            var pending = session == null ? [] : Decisions(session.Id).Where(d => d.State == DecisionState.Pending).ToArray();
            var plans = session == null ? [] : Plans(session.Id).Where(p => p.State == DecisionState.Pending).ToArray();
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

            // Keep the old execution under a separate root. The editable task keeps its ID,
            // but must have no session, handoff, result or exhausted execution budget to resume.
            if (session != null || goal.Attempts > 0 || LatestHandoff(goal.Id) != null)
            {
                var archived = JsonSerializer.Deserialize<WorkItem>(JsonSerializer.Serialize(goal))!;
                archived.Id = Guid.NewGuid().ToString("N");
                archived.QueuedAt = null;
                archived.PendingAccess = null;
                if (archived.State == WorkState.Queued)
                    archived.State = WorkState.Paused;
                archived.Notes = "Previous execution before project/access change.\n" + archived.Notes;
                Run("INSERT INTO work(id,data) VALUES($id,$data)", ("$id", archived.Id), ("$data", WorkJson(archived, transaction)));
                Run("UPDATE logs SET work_id=$archive WHERE work_id=$id", ("$archive", archived.Id), ("$id", goal.Id));
                Run("UPDATE handoffs SET work_id=$archive,data=json_set(data,'$.WorkId',$archive) WHERE work_id=$id", ("$archive", archived.Id), ("$id", goal.Id));
                foreach (var work in related.Where(w => w.Id != goal.Id && w.ParentTaskId == goal.Id))
                {
                    work.ParentTaskId = archived.Id;
                    Run("UPDATE work SET data=$data WHERE id=$id", ("$data", WorkJson(work, transaction)), ("$id", work.Id));
                }

                if (session != null)
                    session.RootWorkId = archived.Id;
            }

            goal.AccessEditedAt = DateTimeOffset.UtcNow;
            goal.PendingAccess = null;
            goal.AccessResult = commentReason ?? "Manual project/access saved; older comment proposals superseded.";
            goal.ProjectId = projectId;
            goal.Access = access;
            goal.GrantedAccess = access;
            if (commentReason != null)
                goal.Kind = access == AccessLevel.WriteProject ? WorkKind.Coding : WorkKind.Review;
            goal.SessionId = null;
            goal.State = WorkState.Saved;
            goal.AutoAssign = false;
            goal.Attempts = 0;
            goal.Switches = 0;
            goal.RouteHistory = [];
            goal.PreferredRoute = null;
            goal.ProviderId = null;
            goal.ModelId = null;
            goal.RoutingApproved = false;
            goal.RouteIsAdvisory = false;
            goal.Notes = "";
            goal.BlockReason = "";
            goal.QueuedAt = null;
            goal.ScheduledAt = null;
            goal.Verification = VerificationState.Untested;
            goal.UpdatedAt = DateTimeOffset.UtcNow;
            goal.Comments = goal.Comments.Where(c => !c.IsAssistant).ToList();
            Run("UPDATE work SET data=$data WHERE id=$id", ("$data", WorkJson(goal, transaction)), ("$id", goal.Id));
            foreach (var decision in pending)
            {
                decision.State = DecisionState.Rejected;
                Run("UPDATE decisions SET data=$data WHERE id=$id", ("$data", JsonSerializer.Serialize(decision)), ("$id", decision.Id));
            }

            foreach (var plan in plans)
            {
                plan.State = DecisionState.Rejected;
                Run("UPDATE plans SET data=$data WHERE id=$id", ("$data", JsonSerializer.Serialize(plan)), ("$id", plan.Id));
            }

            if (session != null)
            {
                if (session.State != SessionState.Completed)
                    session.State = SessionState.Paused;
                Run("UPDATE sessions SET data=$data WHERE id=$id", ("$data", JsonSerializer.Serialize(session)), ("$id", session.Id));
            }

            Run("INSERT INTO logs(work_id,at,event,message) VALUES($id,$at,'UserProjectAccess',$message)", ("$id", goal.Id), ("$at", DateTimeOffset.UtcNow.ToString("O")), ("$message", $"User changed {previous} to {projectId} / {access}. Run will start from the beginning. Previous execution archived; pending approvals invalidated."));
            if (commentReason != null)
                Run("INSERT INTO logs(work_id,at,event,message) VALUES($id,$at,'CommentTaskAccess',$message)", ("$id", goal.Id), ("$at", DateTimeOffset.UtcNow.ToString("O")), ("$message", commentReason));
            transaction.Commit();
        }
    }
}
