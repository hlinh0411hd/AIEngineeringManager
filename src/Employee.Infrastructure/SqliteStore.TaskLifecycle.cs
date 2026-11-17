using Employee.Core;

namespace Employee.Infrastructure;
public sealed partial class SqliteStore : ITaskLifecycleStore
{
    // Keep selection and deletion under the same gate so a concurrent save cannot change eligibility.
    public int CleanupExpiredTasks(DateTimeOffset now)
    {
        lock (gate)
        {
            var cutoff = now.AddDays(-LoadSettings().TaskRetentionDays);
            var candidates = WorkItems().Where(w => w.ParentTaskId == null && !w.IsCoordinator && w.CreatedAt < cutoff).OrderBy(w => w.CreatedAt).ThenBy(w => w.Id, StringComparer.Ordinal).ToArray();
            var count = 0;
            foreach (var task in candidates)
            {
                try
                {
                    if (DeleteTask(task.Id).Contains(task.Id))
                        count++;
                }
                catch (Exception ex)
                {
                    Log("task-retention", "TaskRetentionError", $"{task.Id}: {ex}");
                }
            }

            if (count > 0)
                Log("task-retention", "TaskRetentionCompleted", $"Deleted {count} expired root tasks; cutoff {cutoff:O} (CreatedAt).");
            return count;
        }
    }

    private readonly Dictionary<string, int> protectedTasks = new(StringComparer.Ordinal);
    public IDisposable ProtectTask(string id)
    {
        lock (gate)
        {
            if (!WorkItems().Any(w => w.Id == id))
                throw new InvalidOperationException("Task no longer exists.");
            protectedTasks[id] = protectedTasks.GetValueOrDefault(id) + 1;
            return new TaskLease(() =>
            {
                lock (gate)
                {
                    if (--protectedTasks[id] == 0)
                        protectedTasks.Remove(id);
                }
            });
        }
    }

    private sealed class TaskLease(Action release) : IDisposable
    {
        private Action? remaining = release;
        public void Dispose() => Interlocked.Exchange(ref remaining, null)?.Invoke();
    }

    public IReadOnlyList<WorkItem> QueryTasks(TaskSortField field, TaskSortDirection direction)
    {
        if (!Enum.IsDefined(field) || !Enum.IsDefined(direction))
            throw new ArgumentOutOfRangeException();
        var tasks = WorkItems();
        var projectNames = field == TaskSortField.Project ? Projects().ToDictionary(p => p.Id, p => p.Name) : null;
        IOrderedEnumerable<WorkItem> Sort<T>(Func<WorkItem, T> key, IComparer<T>? comparer = null) => direction == TaskSortDirection.Ascending ? tasks.OrderBy(key, comparer) : tasks.OrderByDescending(key, comparer);
        var ordered = field switch
        {
            TaskSortField.Name => Sort(w => w.Request, StringComparer.OrdinalIgnoreCase),
            TaskSortField.CreatedAt => Sort(w => w.CreatedAt),
            TaskSortField.UpdatedAt => Sort(w => w.UpdatedAt ?? w.CreatedAt),
            TaskSortField.State => Sort(w => w.State),
            TaskSortField.Project => Sort(w => projectNames!.GetValueOrDefault(w.ProjectId, w.ProjectId), StringComparer.OrdinalIgnoreCase),
            _ => throw new ArgumentOutOfRangeException(nameof(field))};
        // Equal keys: creation time follows the chosen direction (newest first when descending); the
        // final Id tie-break stays fixed so equal tasks keep a stable order.
        return (direction == TaskSortDirection.Ascending
            ? ordered.ThenBy(w => w.CreatedAt).ThenBy(w => w.Id, StringComparer.Ordinal)
            : ordered.ThenByDescending(w => w.CreatedAt).ThenBy(w => w.Id, StringComparer.Ordinal)).ToArray();
    }

    // A root owns its descendants and its session. Internal steps cannot be removed independently.
    // Usage/performance records and project memory remain independent historical records.
    public IReadOnlyList<string> DeleteTask(string id) => DeleteTasks([id]);
    public IReadOnlyList<string> DeleteTasks(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var requested = ids.ToHashSet(StringComparer.Ordinal);
        lock (gate)
        {
            var all = WorkItems();
            var sessions = Sessions();
            var jobs = TestJobs();
            var selected = all.Where(w => requested.Contains(w.Id)).ToArray();
            if (selected.Any(w => w.ParentTaskId != null || w.IsCoordinator || w.SessionId != null && !sessions.Any(s => s.Id == w.SessionId && s.RootWorkId == w.Id)))
                throw new InvalidOperationException("Delete the root task instead of an internal step.");
            var removed = selected.Select(w => w.Id).ToHashSet(StringComparer.Ordinal);
            var ownedSessions = sessions.Where(s => s.RootWorkId != null && removed.Contains(s.RootWorkId)).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            bool changed;
            do
            {
                changed = false;
                foreach (var w in all)
                    if ((w.ParentTaskId != null && removed.Contains(w.ParentTaskId) || w.SessionId != null && ownedSessions.Contains(w.SessionId)) && removed.Add(w.Id))
                        changed = true;
            }
            while (changed);
            if (all.Any(w => removed.Contains(w.Id) && (w.State == WorkState.Running || protectedTasks.ContainsKey(w.Id))) || sessions.Any(s => ownedSessions.Contains(s.Id) && s.State is SessionState.Running or SessionState.Planning) || jobs.Any(j => j.State == TestJobState.Running && j.WorkIds.Any(removed.Contains)))
                throw new InvalidOperationException("Stop and await running tasks and tests before deleting.");
            if (all.Any(w => !removed.Contains(w.Id) && w.DependsOn.Any(removed.Contains)))
                throw new InvalidOperationException("Another task depends on this task.");
            using var tx = connection.BeginTransaction();
            void Run(string sql, string id)
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }

            foreach (var session in ownedSessions)
                Run("DELETE FROM decisions WHERE session_id=$id; DELETE FROM plans WHERE session_id=$id; DELETE FROM sessions WHERE id=$id;", session);
            foreach (var id in removed)
                Run("DELETE FROM logs WHERE work_id=$id; DELETE FROM handoffs WHERE work_id=$id; DELETE FROM work WHERE id=$id;", id);
            foreach (var job in jobs.Where(j => j.WorkIds.Any(removed.Contains)))
            {
                job.WorkIds.RemoveAll(removed.Contains);
                using var update = connection.CreateCommand();
                update.Transaction = tx;
                update.CommandText = "UPDATE test_jobs SET data=$data WHERE id=$id";
                update.Parameters.AddWithValue("$id", job.Id);
                update.Parameters.AddWithValue("$data", System.Text.Json.JsonSerializer.Serialize(job));
                update.ExecuteNonQuery();
            }

            tx.Commit();
            return removed.Order(StringComparer.Ordinal).ToArray();
        }
    }
}
