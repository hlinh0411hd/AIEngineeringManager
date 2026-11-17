using Employee.Core;
using Employee.Infrastructure;

static class TaskLifecycleChecks
{
    public static int Run()
    {
        var path = Path.Combine(Path.GetTempPath(), "task-lifecycle-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            return RunFixture(path);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var file in new[]
            {
                path,
                path + "-wal",
                path + "-shm"
            }

            )
                if (File.Exists(file))
                    File.Delete(file);
        }
    }

    private static int RunFixture(string path)
    {
        using var store = new SqliteStore(path);
        void Check(bool ok)
        {
            if (!ok)
                throw new Exception("Task lifecycle assertion failed");
        }

        void Reject(Action action)
        {
            try
            {
                action();
            }
            catch (InvalidOperationException)
            {
                return;
            }

            throw new Exception("Expected rejection");
        }

        var at = DateTimeOffset.UtcNow.AddDays(-10);
        store.SaveProject(new("first", "Zulu", Path.GetTempPath()));
        store.SaveProject(new("second", "alpha", Path.GetTempPath()));
        var a = new WorkItem
        {
            Id = "a",
            ProjectId = "first",
            Request = "same",
            CreatedAt = at,
            State = WorkState.Saved
        };
        var b = new WorkItem
        {
            Id = "b",
            ProjectId = "second",
            Request = "same",
            CreatedAt = at,
            State = WorkState.Completed
        };
        store.SaveWork(b);
        store.SaveWork(a);
        foreach (var direction in Enum.GetValues<TaskSortDirection>())
            Check(store.QueryTasks(TaskSortField.Name, direction)[0].Id == "a");
        Check(store.QueryTasks(TaskSortField.State, TaskSortDirection.Ascending)[0].Id == "b");
        Check(store.QueryTasks(TaskSortField.Project, TaskSortDirection.Ascending)[0].Id == "b");
        Check(store.QueryTasks(TaskSortField.Project, TaskSortDirection.Descending)[0].Id == "a");
        Check(store.WorkItems().Single(w => w.Id == a.Id).UpdatedAt >= at);
        using (var lease = store.ProtectTask(a.Id))
        {
            Reject(() => store.DeleteTasks(["a", "b"]));
            Check(store.WorkItems().Count == 2);
        }

        var session = new WorkSession
        {
            RootWorkId = a.Id
        };
        store.SaveSession(session);
        a.SessionId = session.Id;
        store.SaveWork(a);
        var child = new WorkItem
        {
            ParentTaskId = a.Id,
            SessionId = session.Id
        };
        store.SaveWork(child);
        Reject(() => store.DeleteTasks([child.Id]));
        var job = new TestJob
        {
            WorkIds = [a.Id, b.Id],
            State = TestJobState.Running
        };
        store.SaveTestJob(job);
        Reject(() => store.DeleteTasks([a.Id]));
        job.State = TestJobState.Passed;
        store.SaveTestJob(job);
        store.Log(a.Id, "fixture", "fixture");
        var removed = store.DeleteTasks([a.Id, a.Id, "missing"]);
        Check(removed.Count == 2 && store.Sessions().Count == 0 && store.Logs(a.Id).Count == 0);
        Check(store.TestJobs().Single().WorkIds.SequenceEqual(new[] { b.Id }));
        Check(store.DeleteTasks([b.Id]).Count == 1 && store.WorkItems().Count == 0);
        using var reopened = new SqliteStore(path);
        Check(reopened.WorkItems().Count == 0);
        var now = DateTimeOffset.UtcNow;
        Check(store.LoadSettings().TaskRetentionDays == 7);
        foreach (var invalid in new[]
        {
            0,
            -1,
            3651
        }

        )
        {
            try
            {
                _ = new ManagerSettings
                {
                    TaskRetentionDays = invalid
                };
                throw new Exception("Invalid retention accepted");
            }
            catch (ArgumentOutOfRangeException)
            {
            }
        }

        var expired = new WorkItem
        {
            CreatedAt = now.AddDays(-8),
            State = WorkState.Completed
        };
        var boundary = new WorkItem
        {
            CreatedAt = now.AddDays(-7),
            State = WorkState.Completed
        };
        var running = new WorkItem
        {
            CreatedAt = now.AddDays(-20),
            State = WorkState.Running
        };
        var protectedWork = new WorkItem
        {
            CreatedAt = now.AddDays(-20),
            State = WorkState.Saved
        };
        foreach (var w in new[]
        {
            expired,
            boundary,
            running,
            protectedWork
        }

        )
            store.SaveWork(w);
        using (var lease = store.ProtectTask(protectedWork.Id))
        {
            Check(store.CleanupExpiredTasks(now) == 1);
            Check(store.WorkItems().Count == 3 && store.Logs("task-retention").Any(l => l.Event == "TaskRetentionError"));
        }

        var config = store.LoadSettings();
        config.TaskRetentionDays = 30;
        store.SaveSettings(config);
        using (var reload = new SqliteStore(path))
            Check(reload.LoadSettings().TaskRetentionDays == 30);
        Check(store.CleanupExpiredTasks(now) == 0);
        config.TaskRetentionDays = 1;
        store.SaveSettings(config);
        Check(store.CleanupExpiredTasks(now) == 2);
        Check(store.WorkItems().Single().Id == running.Id);
        Console.WriteLine("PASS task sorting, deterministic ties, atomic deletion, execution leases, session children, shared test jobs and persistence; no AI invoked.");
        return 0;
    }
}
