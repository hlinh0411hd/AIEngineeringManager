using Employee.Core;
using Employee.Infrastructure;

static class TaskAccessChecks
{
    public static int Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "TaskAccessChecks", Guid.NewGuid().ToString("N"));
        using var store = new SqliteStore(Path.Combine(folder, "checks.db"));
        store.SaveProject(new("a", "A", folder));
        store.SaveProject(new("b", "B", folder));
        void Assert(bool ok)
        {
            if (!ok)
                throw new Exception("Task access assertion failed.");
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

            throw new Exception("Unsafe edit accepted.");
        }

        WorkItem Read(string id) => store.WorkItems().Single(w => w.Id == id);
        var goal = new WorkItem
        {
            IsGoal = true,
            ProjectId = "a",
            Request = "Original request",
            Access = AccessLevel.TextOnly,
            State = WorkState.Paused,
            AutoAssign = true,
            Attempts = 4,
            Switches = 3,
            Notes = "Old result",
            BlockReason = "Old block",
            ProviderId = "old",
            ModelId = "old",
            RouteHistory = ["old"],
            RoutingApproved = true
        };
        goal.Comments.Add(new("User input", [], DateTimeOffset.UtcNow));
        goal.Comments.Add(new("Old answer", [], DateTimeOffset.UtcNow) { IsAssistant = true });
        var session = new WorkSession
        {
            RootWorkId = goal.Id,
            ProjectIds = ["a"],
            CallsUsed = 3,
            State = SessionState.Paused
        };
        goal.SessionId = session.Id;
        store.SaveSession(session);
        store.SaveWork(goal);
        var step = new WorkItem
        {
            SessionId = session.Id,
            ParentTaskId = goal.Id,
            ProjectId = "a",
            State = WorkState.Completed
        };
        store.SaveWork(step);
        var decision = new PendingDecision
        {
            SessionId = session.Id,
            WorkId = step.Id
        };
        store.SaveDecision(decision);
        store.Log(goal.Id, "Execution", "Old execution");
        store.SaveHandoff(new(goal.Id, DateTimeOffset.UtcNow, "Stopped", goal.Request, folder, "", "Old progress", "Continue old work", "", "", "", ""));
        store.UpdateTaskAccess(goal.Id, "b", AccessLevel.WriteProject);
        var saved = Read(goal.Id);
        var archivedSession = store.Sessions().Single();
        Assert(saved.ProjectId == "b" && saved.Access == AccessLevel.WriteProject && saved.GrantedAccess == AccessLevel.WriteProject);
        Assert(saved.State == WorkState.Saved && saved.SessionId == null && !saved.AutoAssign && saved.Attempts == 0 && saved.Switches == 0);
        Assert(saved.ProviderId == null && saved.ModelId == null && saved.RouteHistory.Count == 0 && !saved.RoutingApproved);
        Assert(saved.Notes == "" && saved.BlockReason == "" && saved.Comments.Count == 1 && saved.Request == goal.Request);
        Assert(store.LatestHandoff(goal.Id) == null && !store.Logs(goal.Id).Any(l => l.Event == "Execution"));
        var archive = Read(archivedSession.RootWorkId!);
        Assert(archive.Id != goal.Id && archive.ProjectId == "a" && archive.Attempts == 4 && archivedSession.CallsUsed == 3);
        Assert(Read(step.Id).ParentTaskId == archive.Id && Read(step.Id).ProjectId == "a" && Read(step.Id).State == WorkState.Completed);
        Assert(store.LatestHandoff(archive.Id)?.WorkId == archive.Id && store.Logs(archive.Id).Any(l => l.Event == "Execution"));
        Assert(store.Decisions(session.Id).Single().State == DecisionState.Rejected);
        saved.State = WorkState.Completed;
        saved.Attempts = 1;
        store.SaveWork(saved);
        store.UpdateTaskAccess(goal.Id, "b", AccessLevel.WriteProject);
        Assert(Read(goal.Id).State == WorkState.Completed);
        store.UpdateTaskAccess(goal.Id, "b", AccessLevel.ReadProject);
        Assert(Read(goal.Id).State == WorkState.Saved && Read(goal.Id).Attempts == 0);
        store.UpdateTaskAccess(goal.Id, "a", AccessLevel.ReadProject);
        Assert(Read(goal.Id).ProjectId == "a" && Read(goal.Id).SessionId == null);
        Reject(() => store.UpdateTaskAccess(goal.Id, "missing", AccessLevel.ReadProject));
        // An executing task must not lock unrelated tasks, even in the same project.
        var running = new WorkItem
        {
            IsGoal = true,
            ProjectId = "a",
            State = WorkState.Running
        };
        store.SaveWork(running);
        using (var lease = store.ProtectTask(running.Id))
        {
            store.UpdateTaskAccess(goal.Id, "b", AccessLevel.WriteProject);
            Assert(Read(goal.Id).ProjectId == "b" && Read(goal.Id).Access == AccessLevel.WriteProject);
            var disposable = new WorkItem
            {
                IsGoal = true,
                ProjectId = "a",
                State = WorkState.Saved
            };
            store.SaveWork(disposable);
            Assert(store.DeleteTask(disposable.Id).Contains(disposable.Id));
            Reject(() => store.UpdateTaskAccess(running.Id, "b", AccessLevel.TextOnly));
            Reject(() => store.DeleteTask(running.Id));
        }

        // The execution lease also covers startup before the Running state is persisted.
        using (var lease = store.ProtectTask(goal.Id))
        {
            Reject(() => store.UpdateTaskAccess(goal.Id, "a", AccessLevel.ReadProject));
            Reject(() => store.CorrectTaskIntent(goal.Id, WorkKind.Review, AccessLevel.ReadProject));
        }

        saved = Read(goal.Id);
        saved.State = WorkState.Running;
        store.SaveWork(saved);
        Reject(() => store.UpdateTaskAccess(goal.Id, "b", AccessLevel.TextOnly));
        Console.WriteLine("PASS fresh project/access execution, archived history, no-op save, finished reset and running guard; no AI invoked.");
        return 0;
    }
}
