using Employee.Core;
using Employee.Infrastructure;
using Microsoft.Data.Sqlite;

static class TaskLifecycleEdgeChecks
{
    public static void Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "EmployeeLifecycleEdges", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "fixture.db");
        void Check(bool ok, string message)
        {
            if (!ok)
                throw new Exception(message);
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

            throw new Exception("Expected deletion rejection");
        }

        try
        {
            using var store = new SqliteStore(path);
            void Raw(string sql, string json)
            {
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.Parameters.AddWithValue("$data", json);
                command.ExecuteNonQuery();
            }

            var now = DateTimeOffset.Parse("2026-11-02T00:00:00+00:00");
            var cutoff = now.AddDays(-7);
            var stamps = new[]
            {
                cutoff.AddTicks(-1).ToOffset(TimeSpan.FromHours(7)),
                cutoff.ToOffset(TimeSpan.FromHours(-8)),
                cutoff.AddTicks(1)
            };
            for (var i = 0; i < stamps.Length; i++)
                store.SaveWork(new() { Id = "boundary" + i, CreatedAt = stamps[i], State = WorkState.Completed });
            Check(store.CleanupExpiredTasks(now.ToOffset(TimeSpan.FromHours(7))) == 1, "Only strictly older than seven elapsed days expires, regardless of offset");
            Check(store.WorkItems().Select(w => w.Id).Order().SequenceEqual(new[] { "boundary1", "boundary2" }), "Exact boundary must survive");
            Check(store.CleanupExpiredTasks(now) == 0, "Cleanup is idempotent");
            store.DeleteTasks(store.WorkItems().Select(w => w.Id));
            Raw("INSERT INTO settings VALUES(1,$data)", "{\"Theme\":\"legacy\"}");
            Check(store.LoadSettings().TaskRetentionDays == 7, "Legacy settings default");
            Raw("INSERT INTO work VALUES('legacy',$data)", "{\"Id\":\"legacy\",\"Request\":\"old\",\"CreatedAt\":\"2026-10-01T00:00:00Z\"}");
            Check(store.WorkItems().Single().UpdatedAt == null, "Legacy modification timestamp remains absent");
            var modern = new WorkItem
            {
                Id = "modern",
                Request = "Alpha",
                CreatedAt = now,
                State = WorkState.Completed
            };
            store.SaveWork(modern);
            foreach (var field in Enum.GetValues<TaskSortField>())
            {
                var ascending = store.QueryTasks(field, TaskSortDirection.Ascending);
                var descending = store.QueryTasks(field, TaskSortDirection.Descending);
                Check(ascending[0].Id != descending[0].Id, "Both sort directions: " + field);
            }

            Check(store.QueryTasks(TaskSortField.UpdatedAt, TaskSortDirection.Ascending)[0].Id == "legacy", "Legacy sort falls back to creation time");
            var config = store.LoadSettings();
            config.TaskRetentionDays = 90;
            store.SaveSettings(config);
            using (var reopened = new SqliteStore(path))
                Check(reopened.LoadSettings().TaskRetentionDays == 90, "Settings persist after reopen");
            Check(store.CleanupExpiredTasks(now) == 0, "Increasing retention preserves old task");
            config.TaskRetentionDays = 7;
            store.SaveSettings(config);
            Check(store.CleanupExpiredTasks(now) == 1, "Decreasing retention applies at next cleanup to legacy data");
            store.DeleteTask(modern.Id);
            var root = new WorkItem
            {
                Id = "root",
                CreatedAt = cutoff.AddDays(-1)
            };
            store.SaveWork(root);
            var dependent = new WorkItem
            {
                Id = "dependent",
                DependsOn = [root.Id]
            };
            store.SaveWork(dependent);
            Reject(() => store.DeleteTask(root.Id));
            Check(store.WorkItems().Count == 2, "Dependency rejection leaves all rows intact");
            Check(store.DeleteTasks([root.Id, dependent.Id]).Count == 2, "Related roots can be deleted together");
            store.SaveWork(root);
            var session = new WorkSession
            {
                RootWorkId = root.Id
            };
            store.SaveSession(session);
            root.SessionId = session.Id;
            store.SaveWork(root);
            var child = new WorkItem
            {
                ParentTaskId = root.Id,
                SessionId = session.Id,
                State = WorkState.Running
            };
            store.SaveWork(child);
            Reject(() => store.DeleteTask(root.Id));
            child.State = WorkState.Completed;
            store.SaveWork(child);
            foreach (var state in new[]
            {
                SessionState.Running,
                SessionState.Planning
            }

            )
            {
                session.State = state;
                store.SaveSession(session);
                Reject(() => store.DeleteTask(root.Id));
            }

            session.State = SessionState.Completed;
            store.SaveSession(session);
            using (store.ProtectTask(child.Id))
                Reject(() => store.DeleteTask(root.Id));
            store.SavePlan(new() { SessionId = session.Id, SourceWorkId = root.Id });
            store.SaveDecision(new() { SessionId = session.Id, WorkId = child.Id });
            store.SaveHandoff(new(child.Id, now, "fixture", "", "", "", "", "", "", "", "", ""));
            store.Log(child.Id, "fixture", "fixture");
            Check(store.DeleteTask(root.Id).Count == 2, "Single root cascades to descendants");
            Check(store.Sessions().Count == 0 && store.Plans(session.Id).Count == 0 && store.Decisions(session.Id).Count == 0 && store.LatestHandoff(child.Id) == null && store.Logs(child.Id).Count == 0, "Related records removed");
            Check(store.DeleteTasks([]).Count == 0 && store.DeleteTask("missing").Count == 0, "Empty/missing deletion is harmless");
            var instant = DateTimeOffset.Parse("2026-11-02T00:30:00+07:00");
            Check(TaskCalendar.CreationDay(instant) == new DateTime(2026, 11, 1), "Grouping uses UTC across local midnight");
            Check(TaskCalendar.CreationDay(instant) == TaskCalendar.CreationDay(instant.ToOffset(TimeSpan.FromHours(-8))), "Equivalent instants share a group");
            Check(TaskCalendar.CreationDay(now.AddTicks(-1)) != TaskCalendar.CreationDay(now), "UTC midnight changes group");
            Console.WriteLine("PASS lifecycle edges: all sort fields/directions, legacy data, UTC grouping, exact seven-day boundary, retention changes, dependencies and active descendants/sessions.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(folder, true);
        }
    }
}
