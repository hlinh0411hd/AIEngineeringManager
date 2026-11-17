using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Employee.Core;
using Employee.Infrastructure;
using static Harness;

// Storage, persistence, attachments, maintenance and project data.
static class DataChecks
{
    public static async Task Run()
    {
        await Check("SQLite persistence, settings, UTF-8 and crash recovery", () =>
        {
            var path = Path.Combine(root, "persistence.db");
            var w = new WorkItem
            {
                Request = "Sửa nhiệm vụ tuần",
                State = WorkState.Running
            };
            using (var s = new SqliteStore(path))
            {
                s.SaveWork(w);
                var config = new ManagerSettings
                {
                    Mode = RoutingMode.Manual
                };
                s.SaveSettings(config);
                s.Log(w.Id, "Test", "Tiếng Việt");
            }

            using (var s = new SqliteStore(path))
            {
                s.RecoverInterruptedWork();
                Assert(s.WorkItems().Single().State == WorkState.Paused);
                Assert(s.LoadSettings().Mode == RoutingMode.Manual);
                Assert(s.Logs(w.Id)[0].Message == "Tiếng Việt", "Vietnamese log text round-trips");
                Assert(s.WorkItems().Single().Request == "Sửa nhiệm vụ tuần", "Vietnamese task text round-trips");
            }

            return Task.CompletedTask;
        });
        await Check("Project registration, metadata index, exclusions, FTS isolation", () =>
        {
            using var f = New();
            File.WriteAllText(Path.Combine(f.Project.Root, "WeeklyQuest.cs"), "hello");
            Directory.CreateDirectory(Path.Combine(f.Project.Root, "node_modules"));
            File.WriteAllText(Path.Combine(f.Project.Root, "node_modules", "Secret.cs"), "no");
            File.WriteAllText(Path.Combine(f.Project.Root, ".env"), "secret");
            var index = new ProjectIndexer(f.Store);
            Assert(index.Index(f.Project) == 1);
            Assert(f.Store.SearchFiles(f.Project.Id, "Weekly").Count == 1);
            Assert(f.Store.SearchFiles("other", "Weekly").Count == 0);
            File.Delete(Path.Combine(f.Project.Root, "WeeklyQuest.cs"));
            index.Index(f.Project);
            Assert(f.Store.SearchFiles(f.Project.Id, "Weekly").Count == 0);
            return Task.CompletedTask;
        });
        await Check("Model ratings update from a ratings file and from task results", () =>
        {
            try
            {
                var set = new RatingSet("test", DateTimeOffset.UtcNow, [new("gpt-6-luna", 5, new() { ["Debugging"] = 40 })]);
                ModelRatings.Apply(set);
                var luna = new ModelInfo("gpt-6-luna", []);
                Assert(ModelRatings.QualityFor("codex", luna, WorkCapability.Debugging) == 40, "A ratings file overrides the built-in value");
                Assert(ModelRatings.QualityFor("codex", luna, WorkCapability.Coding) == 77, "Capabilities the file omits keep the built-in value");
                Assert(luna.RatingSource.StartsWith("Ratings from test"));
                var bad = false;
                try
                {
                    ModelRatings.Apply(new("x", DateTimeOffset.UtcNow, [new("gpt-6-luna", 5, new() { ["Juggling"] = 40 })]));
                }
                catch (InvalidOperationException)
                {
                    bad = true;
                }

                Assert(bad, "Unknown work types are rejected");
            }
            finally
            {
                ModelRatings.Apply(null);
            }

            using var f = New();
            foreach (var i in Enumerable.Range(0, 4))
                f.Store.SaveModelRun(new($"run{i}", $"w{i}", "codex", "gpt-6-luna", WorkKind.Coding, FailureKind.None, 1000, false, DateTimeOffset.UtcNow, null, WorkCapability.Debugging));
            f.Store.RecordApproval(["w0", "w1", "w2", "w3"]);
            var router = ModelRouter.For(f.Store);
            var learn = new ManagerSettings();
            Assert(router.Learned("codex", "gpt-6-luna", WorkCapability.Debugging, learn) > 0, "Successful, approved runs raise the learned score for that work type");
            Assert(router.Learned("codex", "gpt-6-luna", WorkCapability.UiDesign, learn) == 0, "Learning stays with the work type it came from");
            var before = router.Learned("codex", "gpt-6-luna", WorkCapability.Debugging, learn);
            Assert(f.Store.RateModelResult("w0", false) && ModelRouter.For(f.Store).Learned("codex", "gpt-6-luna", WorkCapability.Debugging, learn) < before, "An explicit poor rating replaces the approval and lowers the learned score");
            return Task.CompletedTask;
        });
        await Check("Attachments are stored once outside task rows, and older rows migrate on open", () =>
        {
            var folder = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "legacy.db");
            var bytes = Enumerable.Range(0, 300_000).Select(i => (byte)(i % 251)).ToArray();
            using (var store = new SqliteStore(path))
            {
                // An earlier version wrote attachment bytes inside the task JSON.
                var legacy = new WorkItem
                {
                    Request = "Legacy task",
                    Attachments = [new("big.bin", bytes)]
                };
                legacy.Comments.Add(new("same file again", [new("copy.bin", bytes)], DateTimeOffset.UtcNow));
                using var raw = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + path + ";Pooling=False");
                raw.Open();
                using var insert = raw.CreateCommand();
                insert.CommandText = "INSERT INTO work VALUES($id,$data)";
                insert.Parameters.AddWithValue("$id", legacy.Id);
                insert.Parameters.AddWithValue("$data", JsonSerializer.Serialize(legacy));
                insert.ExecuteNonQuery();
            }

            using (var reopened = new SqliteStore(path))
            {
                var work = reopened.WorkItems().Single();
                Assert(work.Attachments.Single().Content.SequenceEqual(bytes) && work.Comments.Single().Attachments.Single().Content.SequenceEqual(bytes), "Contents round-trip after migration");
                work.Notes = "edited";
                reopened.SaveWork(work);
                Assert(reopened.WorkItems().Single().Attachments.Single().Content.Length == bytes.Length, "Contents survive a later save");
            }

            using var check = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + path + ";Pooling=False");
            check.Open();
            long Scalar(string sql)
            {
                using var command = check.CreateCommand();
                command.CommandText = sql;
                return Convert.ToInt64(command.ExecuteScalar());
            }

            Assert(Scalar("SELECT max(length(data)) FROM work") < 5000, "The task row no longer carries attachment bytes");
            Assert(Scalar("SELECT count(*) FROM attachment_blobs") == 1, "Identical files are stored once");
            return Task.CompletedTask;
        });
        await Check("Maintenance keeps recent history and only the newest backups", () =>
        {
            using var f = New();
            for (var i = 0; i < 8; i++)
                f.Store.SaveHandoff(new(f.Work.Id, DateTimeOffset.UtcNow.AddMinutes(i), $"checkpoint {i}", "", "", "", "", "", "", "", "", ""));
            var old = DateTimeOffset.UtcNow.AddDays(-40);
            using (var raw = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + f.Store.DatabasePath + ";Pooling=False"))
            {
                raw.Open();
                using var insert = raw.CreateCommand();
                insert.CommandText = "INSERT INTO logs(work_id,at,event,message) VALUES($w,$at,'Execution',$m),($w,$now,'Execution',$m)";
                insert.Parameters.AddWithValue("$w", f.Work.Id);
                insert.Parameters.AddWithValue("$at", old.ToString("O"));
                insert.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                insert.Parameters.AddWithValue("$m", new string ('x', 20000));
                insert.ExecuteNonQuery();
            }

            f.Store.RunMaintenance();
            Assert(f.Store.LatestHandoff(f.Work.Id)?.Reason == "checkpoint 7", "The newest checkpoint is kept");
            var executions = f.Store.Logs(f.Work.Id).Where(l => l.Event == "Execution").ToArray();
            Assert(executions.Any(l => l.Message.Length == 20000) && executions.Any(l => l.Message.Length < 5000), "Only execution logs older than 30 days are trimmed");
            var backups = Path.Combine(Path.GetDirectoryName(f.Store.DatabasePath)!, "backups");
            for (var i = 0; i < 12; i++)
            {
                f.Store.Backup(Path.Combine(backups, $"b{i:00}.db"));
                File.SetLastWriteTimeUtc(Path.Combine(backups, $"b{i:00}.db"), DateTime.UtcNow.AddMinutes(i - 100));
            }

            f.Store.Backup(Path.Combine(backups, "b12.db"));
            Assert(Directory.GetFiles(backups, "*.db").Length == SqliteStore.BackupsKept, "Only the newest backups are kept");
            var elsewhere = Path.Combine(Path.GetDirectoryName(f.Store.DatabasePath)!, "staging");
            for (var i = 0; i < 12; i++)
                f.Store.Backup(Path.Combine(elsewhere, $"s{i:00}.db"));
            Assert(Directory.GetFiles(elsewhere, "*.db").Length == 12, "Copies outside the backups folder are never pruned");
            return Task.CompletedTask;
        });
        await Check("Attempt budget persists across resumes", async () =>
        {
            using var f = New();
            f.Settings.MaxAttempts = 1;
            await f.Manager.RunAsync(f.Work, f.Settings, f.Health);
            await f.Manager.RunAsync(f.Work, f.Settings, f.Health);
            Assert(f.First.Calls == 1 && f.Work.State == WorkState.Failed);
        });
        await Check("Reviewed file proposals enforce hashes, path bounds and all-or-nothing validation", () =>
        {
            using var f = New();
            var path = Path.Combine(f.Project.Root, "WeeklyQuest.cs");
            File.WriteAllText(path, "before");
            var service = new FileEditService();
            var original = File.ReadAllBytes(path);
            var hash = FileEditService.Hash(original);
            service.Apply(f.Project, new("safe edit", [new("WeeklyQuest.cs", hash, "after"), new("NewQuest.cs", "NEW", "new file")]));
            Assert(File.ReadAllText(path) == "after");
            bool stale = false;
            try
            {
                service.Apply(f.Project, new("stale", [new("WeeklyQuest.cs", hash, "bad")]));
            }
            catch (InvalidOperationException)
            {
                stale = true;
            }

            Assert(stale && File.ReadAllText(path) == "after");
            foreach (var unsafePath in new[]
            {
                "../escape.cs",
                "C:/escape.cs",
                ".git/config",
                "folder/../escape.cs",
                "NUL.txt",
                "secrets.json",
                "folder/file.cs:stream",
                "folder./a.cs"
            }

            )
            {
                bool rejected = false;
                try
                {
                    FileEditService.ResolveSafe(f.Project.Root, unsafePath);
                }
                catch (InvalidOperationException)
                {
                    rejected = true;
                }

                Assert(rejected, unsafePath);
            }

            var afterHash = FileEditService.Hash(File.ReadAllBytes(path));
            bool rejectedBatch = false;
            try
            {
                service.Apply(f.Project, new("bad batch", [new("WeeklyQuest.cs", afterHash, "should not change"), new("../escape.cs", "NEW", "bad")]));
            }
            catch (InvalidOperationException)
            {
                rejectedBatch = true;
            }

            Assert(rejectedBatch && File.ReadAllText(path) == "after");
            return Task.CompletedTask;
        });
        await Check("Task review presents persisted Codex response and project changes", () =>
        {
            using var f = New();
            f.Work.State = WorkState.NeedsReview;
            f.Work.ProviderId = "codex";
            f.Work.Access = AccessLevel.WriteProject;
            var now = DateTimeOffset.UtcNow;
            TaskLog[] logs = [new(1, f.Work.Id, now, "Route", "codex"), new(2, f.Work.Id, now, "Execution", "codex/default; None; exit=0\nnoise from stderr\n{\"type\":\"item.completed\",\"item\":{\"type\":\"command_execution\",\"aggregated_output\":\"internal command data\"}}\n{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"�� c?p nh?t giao di?n. H�y m? app d? xem.\"}}")];
            var checkpoint = new Handoff(f.Work.Id, now, "After execution: None", f.Work.Request, f.Project.Root, "", "raw", "remaining", " M MainWindow.xaml", "diff --git a/MainWindow.xaml b/MainWindow.xaml", "Not run", "decisions");
            var report = TaskReviewReport.Create(f.Work, f.Project, logs, checkpoint);
            Assert(report.Result == "�� c?p nh?t giao di?n. H�y m? app d? xem.");
            Assert(report.Changes.Contains("MainWindow.xaml"));
            Assert(report.Verification.Contains("Use Apply reviewed edits only"));
            Assert(!report.Result.Contains("internal command data"));
            return Task.CompletedTask;
        });
        await Check("V2 migration preserves V1 work and creates a consistent backup", () =>
        {
            var path = Path.Combine(root, "migration.db");
            string id;
            using (var s = new SqliteStore(path))
            {
                var w = new WorkItem
                {
                    Request = "Legacy task"
                };
                id = w.Id;
                s.SaveWork(w);
            }

            using (var c = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + path))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "PRAGMA user_version=1";
                cmd.ExecuteNonQuery();
            }

            using (var s = new SqliteStore(path))
            {
                Assert(s.WorkItems().Single().Id == id);
                Assert(s.Sessions().Count == 0);
                Assert(s.WorkItems().Single().DependsOn.Count == 0);
            }

            Assert(Directory.GetFiles(root, "migration.db.v1-*.bak").Length == 1);
            return Task.CompletedTask;
        });
        await Check("Dependency edits reject cycles without persisting the invalid graph", () =>
        {
            using var f = New();
            var session = SessionFor(f);
            var second = ExtraWork(f, session);
            var engine = EngineFor(f);
            engine.SetDependencies(session.Id, second.Id, [f.Work.Id]);
            bool rejected = false;
            try
            {
                engine.SetDependencies(session.Id, f.Work.Id, [second.Id]);
            }
            catch (InvalidOperationException)
            {
                rejected = true;
            }

            Assert(rejected && f.Store.WorkItems().Single(w => w.Id == f.Work.Id).DependsOn.Count == 0);
            return Task.CompletedTask;
        });
        await Check("Smart testing batches pending changes and uses file content to avoid stale success", async () =>
        {
            using var f = New();
            var profile = new ProjectTestProfile
            {
                ProjectId = f.Project.Id,
                Name = "Safe echo check",
                Executable = Environment.ProcessPath!,
                Arguments = ["--echo", "test passed"],
                Enabled = true,
                FullValidation = true
            };
            f.Store.SaveTestProfile(profile);
            var service = new SmartTestingService(f.Store, f.Store, f.Manager, new ProcessRunner());
            var path = Path.Combine(f.Project.Root, "Quest.cs");
            File.WriteAllText(path, "one");
            var first = service.Enqueue(f.Work, ["Quest.cs"], "snapshot1").Single();
            File.WriteAllText(path, "two");
            var next = service.Enqueue(f.Work, ["Quest.cs"], "snapshot2").Single();
            Assert(first.Id == next.Id && f.Store.TestJobs().Count == 1);
            await service.RunAsync(next, CancellationToken.None);
            Assert(f.Store.TestJobs().Single().State == TestJobState.Passed && f.Store.WorkItems().Single().Verification == VerificationState.Verified);
            Assert(f.Store.WorkItems().Single().State == WorkState.Queued);
            Assert(service.Enqueue(f.Work, ["Quest.cs"], "snapshot2").Single().Id == first.Id);
            File.WriteAllText(path, "three");
            Assert(service.Enqueue(f.Work, ["Quest.cs"], "snapshot2").Single().Id != first.Id);
            Assert(SmartTestingService.Classify(["Permissions.cs"]) == ChangeImpact.Critical && SmartTestingService.Classify(["README.md"]) == ChangeImpact.Minor);
        });
        await Check("Critical smart tests run from Codex file events even without Git", async () =>
        {
            using var f = New();
            f.Work.Access = AccessLevel.WriteProject;
            f.Settings.Providers.Single(p => p.Id == "codex").AllowProjectWrite = true;
            var session = SessionFor(f);
            f.Store.SaveTestProfile(new() { ProjectId = f.Project.Id, Name = "Critical fixture", Executable = Environment.ProcessPath!, Arguments = ["--echo", "critical passed"], Enabled = true, FullValidation = true });
            f.First.Result = new(FailureKind.None, "{\"type\":\"item.completed\",\"item\":{\"type\":\"file_change\",\"changes\":[{\"path\":\"Permissions.cs\",\"kind\":\"update\"}]}}\n{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"Updated permission logic\"}}");
            await EngineFor(f).RunAsync(session.Id, f.Settings, f.Health);
            Assert(f.Store.TestJobs().Single()is { Impact: ChangeImpact.Critical, State: TestJobState.Passed });
            Assert(f.Store.WorkItems().Single().Verification == VerificationState.Verified);
        });
        await Check("Provider resume: retry a persisted wait without losing checkpoints or usage", async () =>
        {
            using var f = New();
            f.First.Result = new(FailureKind.RateLimit, "quota exceeded");
            f.Settings.Providers.Single(p => p.Id == "gemini").Enabled = false;
            var flow = UnifiedFor(f);
            await flow.StartAsync(f.Work, f.Settings, f.Health);
            var goal = f.Store.WorkItems().Single(w => w.Id == f.Work.Id);
            var step = flow.Steps(goal).Single();
            Assert(step.State == WorkState.WaitingForProvider && f.First.Calls == 1, "Quota exhaustion must wait for a provider after exactly one execution.");
            Assert(flow.Decisions(goal).Any(d => d.Kind == "ProviderWait") && !flow.Decisions(goal).Any(d => d.Kind == "Approval"), "A provider wait must be distinguished from a generic approval.");
            var checkpoint = f.Store.LatestHandoff(step.Id);
            Assert(checkpoint != null);
            // Reopen with a future legacy cooldown: historical quota must not prevent a fresh request.
            var config = f.Store.LoadSettings();
            config.Providers.Single(p => p.Id == "codex").CooldownUntil = DateTimeOffset.UtcNow.AddDays(7);
            f.Store.SaveSettings(config);
            f.First.Result = new(FailureKind.None, "resumed");
            var manager = new Orchestrator(f.Store, [f.First, f.Second], new NullRunner());
            var resumed = new UnifiedTaskService(f.Store, f.Store, manager, new NullRunner());
            await resumed.StartAsync(goal, f.Settings, f.Health, new("codex", "a", "resume"));
            goal = f.Store.WorkItems().Single(w => w.Id == goal.Id);
            step = resumed.Steps(goal).Single();
            Assert(f.First.Calls == 2 && step.Id == checkpoint!.WorkId && step.Attempts == 2 && step.State == WorkState.Completed);
            Assert(resumed.SessionFor(goal)!.CallsUsed == 2 && resumed.Decisions(goal).Count == 0 && goal.State == WorkState.NeedsReview);
        });
        await Check("Data location is portable, persisted and explicit overrides stay isolated", () =>
        {
            var app = Path.Combine(root, "portable");
            Directory.CreateDirectory(app);
            File.WriteAllText(Path.Combine(app, "Run.bat"), "");
            File.WriteAllText(Path.Combine(app, "AIEmployeeManager.slnx"), "");
            Assert(DataLocationService.FindApplicationRoot(Path.Combine(app, "src", "bin")) == app);
            var locations = new DataLocationService(app);
            Assert(locations.Resolve() == Path.Combine(app, "Data"));
            Assert(!File.Exists(locations.ConfigPath));
            var chosen = Path.Combine(root, "chosen-data");
            using (var db = new SqliteStore(Path.Combine(chosen, "manager.db")))
                db.SaveWork(new() { Request = "keep me" });
            locations.Save(chosen);
            Assert(locations.Resolve() == chosen);
            Assert(locations.Resolve(Path.Combine(root, "override")) == Path.Combine(root, "override"));
            Assert(locations.Resolve() == chosen);
            var missing = Path.Combine(root, "missing-drive");
            locations.Save(missing);
            bool rejected = false;
            try
            {
                locations.Resolve();
            }
            catch (InvalidOperationException)
            {
                rejected = true;
            }

            Assert(rejected && !Directory.Exists(missing));
            return Task.CompletedTask;
        });
        await Check("Data relocation includes live WAL, checkpoints and settings without changing source", () =>
        {
            var source = Path.Combine(root, "data-source");
            var target = Path.Combine(root, "data-target");
            using var db = new SqliteStore(Path.Combine(source, "manager.db"));
            var task = new WorkItem
            {
                Request = "Persist relocation",
                Attempts = 2,
                State = WorkState.Paused
            };
            db.SaveWork(task);
            db.Log(task.Id, "Checkpoint", "persisted");
            var settings = new ManagerSettings();
            settings.Automation.DurationMinutes = 0;
            db.SaveSettings(settings);
            DataLocationService.CopyDatabase(source, target);
            using var copy = new SqliteStore(Path.Combine(target, "manager.db"));
            Assert(copy.WorkItems().Single().Id == task.Id && copy.Logs(task.Id).Single().Message == "persisted" && copy.LoadSettings().Automation.DurationMinutes == 0);
            db.Log(task.Id, "Later", "source kept");
            Assert(copy.Logs(task.Id).Count == 1 && db.Logs(task.Id).Count == 2);
            return Task.CompletedTask;
        });
        await Check("Data relocation refuses nonempty destinations and leaves location config unchanged", () =>
        {
            var app = Path.Combine(root, "location-failure");
            Directory.CreateDirectory(app);
            var locations = new DataLocationService(app);
            var source = Path.Combine(app, "source");
            using (var db = new SqliteStore(Path.Combine(source, "manager.db")))
                db.SaveWork(new() { Request = "original" });
            locations.Save(source);
            var target = Path.Combine(app, "target");
            Directory.CreateDirectory(target);
            var existing = Path.Combine(target, "important.txt");
            File.WriteAllText(existing, "original");
            bool rejected = false;
            try
            {
                DataLocationService.CopyDatabase(source, target);
            }
            catch (InvalidOperationException)
            {
                rejected = true;
            }

            Assert(rejected && File.ReadAllText(existing) == "original" && locations.Resolve() == source && !File.Exists(Path.Combine(target, "manager.db")));
            return Task.CompletedTask;
        });
        await Check("Data folder lease prevents another thread opening a second writer", () =>
        {
            var folder = Path.Combine(root, "leased");
            using var held = DataLocationService.Acquire(folder);
            bool rejected = false;
            var thread = new Thread(() =>
            {
                try
                {
                    using var other = DataLocationService.Acquire(folder);
                }
                catch (InvalidOperationException)
                {
                    rejected = true;
                }
            });
            thread.Start();
            thread.Join();
            Assert(rejected);
            return Task.CompletedTask;
        });
    }
}
