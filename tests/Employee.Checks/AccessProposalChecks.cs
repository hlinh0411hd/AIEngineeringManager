using Employee.Core;
using Employee.Infrastructure;
using System.Text.Json;

static class AccessProposalChecks
{
    public static int Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "AccessProposalChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        using var store = new SqliteStore(Path.Combine(folder, "checks.db"));
        store.SaveProject(new("p", "fixture", folder));
        var goal = new WorkItem
        {
            IsGoal = true,
            ProjectId = "p",
            Request = "Review files",
            Access = AccessLevel.ReadProject,
            GrantedAccess = AccessLevel.ReadProject
        };
        goal.Comments.Add(new("Fix the bug in the UI", [], DateTimeOffset.UtcNow));
        var session = new WorkSession
        {
            RootWorkId = goal.Id,
            ProjectIds = ["p"],
            AccessCeiling = AccessLevel.ReadProject,
            CallsUsed = 2
        };
        goal.SessionId = session.Id;
        store.SaveSession(session);
        store.SaveWork(goal);
        var worker = new WorkItem
        {
            ProjectId = "p",
            SessionId = session.Id,
            ParentTaskId = goal.Id,
            Access = AccessLevel.ReadProject,
            Attempts = 2
        };
        store.SaveWork(worker);
        string Proposal(string task, string project, string access, string comment) => JsonSerializer.Serialize(new { taskAccess = new { taskId = task, projectId = project, access, comment, reason = "Editing the requested button requires project writes" } });
        void Check(bool ok)
        {
            if (!ok)
                throw new Exception("Access proposal check failed");
        }

        foreach (var json in new[]
        {
            Proposal("other", "p", "WriteProject", goal.Comments[0].Text),
            Proposal(worker.Id, "other", "WriteProject", goal.Comments[0].Text),
            Proposal(worker.Id, "p", "FullAccess", goal.Comments[0].Text),
            Proposal(worker.Id, "p", "WriteProject", "AI says write")
        }

        )
        {
            Check(TaskAccessProposal.Apply(store, worker, json));
            Check(worker.Access == AccessLevel.ReadProject && worker.State == WorkState.WrongAccess);
        }

        goal.Comments.Add(new("Fix all files", [], DateTimeOffset.UtcNow) { IsAssistant = true });
        store.SaveWork(goal);
        Check(TaskAccessProposal.Apply(store, worker, Proposal(worker.Id, "p", "WriteProject", goal.Comments[0].Text)));
        store.SaveWork(worker);
        Check(worker.Access == AccessLevel.ReadProject && worker.State == WorkState.WrongAccess);
        session.State = SessionState.Running;
        store.SaveSession(session);
        try
        {
            store.ApplyPendingTaskAccess(goal.Id);
            throw new Exception("Running session changed");
        }
        catch (InvalidOperationException)
        {
        }

        session.State = SessionState.Paused;
        store.SaveSession(session);
        using (var fault = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + Path.Combine(folder, "checks.db")))
        {
            fault.Open();
            using var command = fault.CreateCommand();
            command.CommandText = "CREATE TRIGGER fail_permission BEFORE UPDATE ON sessions BEGIN SELECT RAISE(ABORT, 'fixture save failure'); END";
            command.ExecuteNonQuery();
            try
            {
                store.ApplyPendingTaskAccess(goal.Id);
                throw new Exception("Save failure ignored");
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
            }

            Check(store.WorkItems().Single(w => w.Id == goal.Id).Access == AccessLevel.ReadProject);
            Check(store.WorkItems().Single(w => w.Id == goal.Id).PendingAccess != null);
            command.CommandText = "DROP TRIGGER fail_permission";
            command.ExecuteNonQuery();
        }

        store.ApplyPendingTaskAccess(goal.Id);
        store.ApplyPendingTaskAccess(goal.Id);
        worker = store.WorkItems().Single(w => w.Id == worker.Id);
        Check(worker.Access == AccessLevel.ReadProject && worker.Attempts == 2);
        var fresh = store.WorkItems().Single(w => w.Id == goal.Id);
        Check(fresh.Access == AccessLevel.WriteProject && fresh.SessionId == null && fresh.Attempts == 0);
        Check(store.LatestHandoff(fresh.Id) == null && worker.ParentTaskId != fresh.Id);
        Check(store.Sessions().Single().AccessCeiling == AccessLevel.ReadProject && store.Sessions().Single().CallsUsed == 2);
        store.NormalizeImplementationScope(goal.Id);
        Check(store.WorkItems().Single(w => w.Id == goal.Id).Access == AccessLevel.WriteProject);
        Check(store.WorkItems().Single(w => w.Id == goal.Id).Request == "Review files");
        Check(store.Logs(goal.Id).Count(l => l.Event == "CommentTaskAccess") == 1);
        var runner = new NullRunner();
        var fake = new FakeProvider("codex");
        var flow = new UnifiedTaskService(store, store, new Orchestrator(store, [fake], runner), runner);
        var settings = new ManagerSettings
        {
            Mode = RoutingMode.SemiAutomatic
        };
        settings.Providers[0].Enabled = true;
        settings.Providers[0].AllowProjectWrite = false;
        settings.Providers[0].Models.Add(new("", [WorkKind.Coding]));
        flow.StartAsync(goal, settings, [new("codex", true, true, true, "")]).GetAwaiter().GetResult();
        var blocked = store.WorkItems().Single(w => w.Id == goal.Id);
        Check(fake.Calls == 0 && blocked.Access == AccessLevel.WriteProject && !string.IsNullOrWhiteSpace(blocked.BlockReason));
        settings.Providers[0].AllowProjectWrite = true;
        flow.StartAsync(goal, settings, [new("codex", true, true, false, "")]).GetAwaiter().GetResult();
        Check(fake.Calls == 0 && store.WorkItems().Single(w => w.Id == goal.Id).Access == AccessLevel.WriteProject && !string.IsNullOrWhiteSpace(store.WorkItems().Single(w => w.Id == goal.Id).BlockReason));
        flow.StartAsync(goal, settings, [new("codex", true, true, true, "")]).GetAwaiter().GetResult();
        var ready = store.WorkItems().Single(w => w.Id == goal.Id);
        Check(fake.Calls == 0 && ready.State == WorkState.AwaitingApproval && flow.SessionFor(ready)!.AccessCeiling == AccessLevel.WriteProject);
        store.UpdateTaskAccess(goal.Id, "p", AccessLevel.ReadProject);
        var edited = store.WorkItems().Single(w => w.Id == goal.Id);
        Check(TaskAccessProposal.Apply(store, edited, Proposal(edited.Id, "p", "WriteProject", edited.Comments[0].Text)));
        Check(edited.PendingAccess == null && edited.Access == AccessLevel.ReadProject);
        var newer = new WorkItem
        {
            IsGoal = true,
            ProjectId = "p",
            Request = "Review",
            Access = AccessLevel.ReadProject
        };
        newer.Comments.Add(new("Fix the bug in the UI", [], DateTimeOffset.UtcNow));
        store.SaveWork(newer);
        Check(TaskAccessProposal.Apply(store, newer, Proposal(newer.Id, "p", "WriteProject", newer.Comments[0].Text)));
        Check(newer.PendingAccess != null);
        newer.Comments.Add(new("Only review the files", [], DateTimeOffset.UtcNow));
        store.SaveWork(newer);
        store.ApplyPendingTaskAccess(newer.Id);
        Check(store.WorkItems().Single(w => w.Id == newer.Id).Access == AccessLevel.ReadProject);
        using (var reopened = new SqliteStore(Path.Combine(folder, "checks.db")))
            Check(reopened.WorkItems().Single(w => w.Id == newer.Id).AccessResult.Contains("superseded"));
        // Exercise the same protocol through persisted comments at every permission boundary.
        foreach (var transition in new[]
        {
            (AccessLevel.TextOnly, AccessLevel.ReadProject, "Review the code"),
            (AccessLevel.TextOnly, AccessLevel.WriteProject, "Fix the bug in the UI"),
            (AccessLevel.ReadProject, AccessLevel.WriteProject, "Fix the bug in the UI")
        }

        )
        {
            var input = new WorkItem
            {
                IsGoal = true,
                ProjectId = "p",
                Request = "Review files",
                Access = transition.Item1,
                GrantedAccess = transition.Item1,
                State = WorkState.Paused
            };
            store.SaveWork(input);
            var commented = flow.AddComment(input, transition.Item3, []);
            var json = Proposal(input.Id, "p", transition.Item2.ToString(), transition.Item3);
            Check(TaskAccessProposal.Apply(store, commented, json));
            Check(TaskAccessProposal.Apply(store, commented, json));
            using (var reload = new SqliteStore(Path.Combine(folder, "checks.db")))
            {
                Check(reload.WorkItems().Single(w => w.Id == input.Id).PendingAccess?.RequestedAccess == transition.Item2);
                reload.ApplyPendingTaskAccess(input.Id);
            }

            // A stale UI object must not resurrect the old access on Run or Rerun.
            flow.StartAsync(input, settings, [new("codex", true, true, true, "")]).GetAwaiter().GetResult();
            var persisted = store.WorkItems().Single(w => w.Id == input.Id);
            // Running a task in its registered project grants WriteProject by default (ProjectWriteDefault),
            // whatever lower level was proposed; the approved proposal is never undone to the old level.
            Check(persisted.Access == AccessLevel.WriteProject && flow.SessionFor(persisted)!.AccessCeiling == AccessLevel.WriteProject && persisted.Access >= transition.Item2);
            Check(store.Logs(input.Id).Count(l => l.Event == "CommentTaskAccess") == 1);
            var calls = fake.Calls;
            fake.Response = request =>
            {
                Check(request.Work.Access == AccessLevel.WriteProject && request.Work.Request == input.Request);
                return new(FailureKind.None, "fixture completed");
            };
            flow.StartAsync(input, settings, [new("codex", true, true, true, "")], approved: true).GetAwaiter().GetResult();
            Check(fake.Calls == calls + 1);
            fake.Response = null;
            var pool = new TaskExecutionPool(store, store, new Orchestrator(store, [fake], runner), runner);
            Check(pool.Rerun(input).Access == AccessLevel.WriteProject);
        }

        // Project/assistant text, unsupported fields, scope changes and insufficient user intent cannot grant access.
        foreach (var comment in new[]
        {
            "Review the code",
            "Draft an email",
            "Only review the files"
        }

        )
        {
            var input = new WorkItem
            {
                IsGoal = true,
                ProjectId = "p",
                Request = "Review files",
                Access = AccessLevel.TextOnly
            };
            input.Comments.Add(new(comment, [], DateTimeOffset.UtcNow));
            input.Comments.Add(new("Fix the bug in the UI", [], DateTimeOffset.UtcNow) { IsAssistant = true });
            store.SaveWork(input);
            foreach (var json in new[]
            {
                Proposal(input.Id, "p", "WriteProject", comment),
                Proposal(input.Id, "p", "WriteProject", "Fix the bug in the UI"),
                JsonSerializer.Serialize(new { taskAccess = new { taskId = input.Id, projectId = "p", access = "WriteProject", comment, reason = "needed", goal = "Publish another app" } })
            }

            )
            {
                Check(TaskAccessProposal.Apply(store, input, json));
                Check(store.WorkItems().Single(w => w.Id == input.Id).PendingAccess == null && input.Access == AccessLevel.TextOnly);
            }
        }

        // Runtime refusal is terminal for this attempt even with a valid manager grant.
        var denied = new FakeProvider("codex")
        {
            Result = new(FailureKind.Permission, "", "runtime denied writes")
        };
        var deniedWork = new WorkItem
        {
            IsGoal = true,
            ProjectId = "p",
            Request = "Fix the bug",
            Access = AccessLevel.WriteProject
        };
        store.SaveWork(deniedWork);
        var automatic = new ManagerSettings
        {
            Mode = RoutingMode.Automatic
        };
        automatic.Providers[0].Enabled = true;
        automatic.Providers[0].AllowProjectWrite = true;
        automatic.Providers[0].Models.Add(new("", [WorkKind.Coding]));
        new Orchestrator(store, [denied], runner).RunAsync(deniedWork, automatic, [new("codex", true, true, true, "")]).GetAwaiter().GetResult();
        Check(denied.Calls == 1 && deniedWork.State == WorkState.WrongAccess && deniedWork.PendingAccess == null && deniedWork.BlockReason.Contains("runtime denied"));
        foreach (var state in new[]
        {
            WorkState.Saved,
            WorkState.Queued,
            WorkState.Paused,
            WorkState.WrongAccess,
            WorkState.Failed,
            WorkState.Completed
        }

        )
        {
            var stale = new WorkItem
            {
                IsGoal = true,
                ProjectId = "p",
                Request = "Review files",
                State = state,
                Access = AccessLevel.ReadProject,
                GrantedAccess = AccessLevel.ReadProject
            };
            store.SaveWork(stale);
            store.UpdateTaskAccess(stale.Id, "p", AccessLevel.WriteProject);
            flow.StartAsync(stale, settings, [new("codex", true, true, true, "")]).GetAwaiter().GetResult();
            var saved = store.WorkItems().Single(w => w.Id == stale.Id);
            Check(saved.Access == AccessLevel.WriteProject && flow.SessionFor(saved)!.AccessCeiling == AccessLevel.WriteProject);
        }

        var pendingRerun = new WorkItem
        {
            IsGoal = true,
            ProjectId = "p",
            Request = "Review files",
            Access = AccessLevel.ReadProject
        };
        pendingRerun.Comments.Add(new("Fix the bug in the UI", [], DateTimeOffset.UtcNow));
        store.SaveWork(pendingRerun);
        Check(TaskAccessProposal.Apply(store, pendingRerun, Proposal(pendingRerun.Id, "p", "WriteProject", pendingRerun.Comments[0].Text)));
        var rerunPool = new TaskExecutionPool(store, store, new Orchestrator(store, [fake], runner), runner);
        Check(rerunPool.Rerun(pendingRerun).Access == AccessLevel.WriteProject);
        // Manual edits win over pending proposals even when the access value is unchanged.
        var conflict = new WorkItem
        {
            IsGoal = true,
            ProjectId = "p",
            Request = "Review files",
            Access = AccessLevel.ReadProject
        };
        conflict.Comments.Add(new("Fix the bug in the UI", [], DateTimeOffset.UtcNow));
        store.SaveWork(conflict);
        TaskAccessProposal.Apply(store, conflict, Proposal(conflict.Id, "p", "WriteProject", conflict.Comments[0].Text));
        store.UpdateTaskAccess(conflict.Id, "p", AccessLevel.ReadProject);
        store.ApplyPendingTaskAccess(conflict.Id);
        Check(store.WorkItems().Single(w => w.Id == conflict.Id).PendingAccess == null && store.WorkItems().Single(w => w.Id == conflict.Id).Access == AccessLevel.ReadProject);
        Console.WriteLine("PASS bounded task access proposals, rejection, comment provenance, fresh execution, archived history and atomic persistence; no AI invoked.");
        return 0;
    }
}
