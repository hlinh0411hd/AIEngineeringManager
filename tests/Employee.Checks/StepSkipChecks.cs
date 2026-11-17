using Employee.Core;
using Employee.Infrastructure;

static class StepSkipChecks
{
    public static async Task<int> Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "EmployeeSkipChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "checks.db");
        using var db = new SqliteStore(path);
        db.SaveProject(new("p", "Fixture", folder));
        var runner = new NullRunner();
        var flow = new UnifiedTaskService(db, db, new Orchestrator(db, [], runner), runner);
        var goal = new WorkItem
        {
            ProjectId = "p",
            IsGoal = true,
            State = WorkState.Paused,
            Request = "Original goal",
            Access = AccessLevel.WriteProject
        };
        var session = new WorkSession
        {
            RootWorkId = goal.Id,
            Goal = goal.Request,
            ProjectIds = ["p"],
            State = SessionState.WaitingForUser,
            MaxTasks = 5,
            MaxCalls = 8,
            BaseCallsUsed = 2,
            CallsUsed = 2,
            RunTestsAtBatchEnd = true
        };
        goal.SessionId = session.Id;
        db.SaveSession(session);
        db.SaveWork(goal);
        var blocked = new WorkItem
        {
            ProjectId = "p",
            SessionId = session.Id,
            ParentTaskId = goal.Id,
            State = WorkState.WaitingForPermission,
            Attempts = 2,
            Verification = VerificationState.Untested,
            Notes = "Test permission denied"
        };
        var next = new WorkItem
        {
            ProjectId = "p",
            SessionId = session.Id,
            ParentTaskId = goal.Id,
            DependsOn = [blocked.Id]
        };
        db.SaveWork(blocked);
        db.SaveWork(next);
        var decision = new PendingDecision
        {
            SessionId = session.Id,
            WorkId = blocked.Id,
            Kind = "TestReview"
        };
        db.SaveDecision(decision);
        var other = new PendingDecision
        {
            SessionId = session.Id,
            WorkId = next.Id,
            Kind = "Approval"
        };
        db.SaveDecision(other);
        void Assert(bool condition, string message)
        {
            if (!condition)
                throw new Exception(message);
        }

        Assert(!TaskDependencies.Ready(next, flow.Steps(goal)), "Dependency should block initially");
        // The budget counts calls the steps really made; skipping must not change it.
        flow.Sync(goal.Id);
        var budget = (flow.SessionFor(goal)!.CallsUsed, flow.SessionFor(goal)!.MaxCalls);
        flow.SkipStep(goal, blocked.Id);
        var saved = db.WorkItems().Single(w => w.Id == blocked.Id);
        Assert(saved.State == WorkState.Skipped && saved.Attempts == 2 && saved.Verification == VerificationState.Untested, "Skip changed results or attempts");
        Assert(TaskDependencies.Ready(next, flow.Steps(goal)), "Skipped dependency still blocks");
        Assert(db.Decisions(session.Id).Single(d => d.Id == decision.Id).State == DecisionState.Rejected, "Step decision still pending");
        Assert(db.Decisions(session.Id).Single(d => d.Id == other.Id).State == DecisionState.Pending, "Other decision changed");
        Assert((flow.SessionFor(goal)!.CallsUsed, flow.SessionFor(goal)!.MaxCalls) == budget, $"Budget changed: {budget} -> {flow.SessionFor(goal)?.CallsUsed}/{flow.SessionFor(goal)?.MaxCalls}");
        try
        {
            flow.SkipStep(goal, blocked.Id);
            throw new Exception("Duplicate skip accepted");
        }
        catch (InvalidOperationException)
        {
        }

        var foreign = new WorkItem
        {
            State = WorkState.Paused
        };
        db.SaveWork(foreign);
        try
        {
            flow.SkipStep(goal, foreign.Id);
            throw new Exception("Foreign step accepted");
        }
        catch (InvalidOperationException)
        {
        }

        next.State = WorkState.Running;
        db.SaveWork(next);
        try
        {
            flow.SkipStep(goal, next.Id);
            throw new Exception("Running step accepted");
        }
        catch (InvalidOperationException)
        {
        }

        next.State = WorkState.Paused;
        db.SaveWork(next);
        flow.SkipStep(goal, next.Id);
        Assert(flow.CanComplete(goal), "All skipped steps should allow explicit final review");
        var job = new TestJob
        {
            ProjectId = "p",
            WorkIds = [blocked.Id],
            State = TestJobState.Pending
        };
        db.SaveTestJob(job);
        await flow.Engine.RunAsync(session.Id, new ManagerSettings(), []);
        Assert(flow.SessionFor(goal)!.State == SessionState.Completed, $"Skipped session did not finish: {flow.SessionFor(goal)!.State} {flow.SessionFor(goal)!.Notes}");
        Assert(db.TestJobs().Single(j => j.Id == job.Id).State == TestJobState.Pending, "Skipped step test ran");
        var updated = flow.AddComment(goal, "New update after skipped tests", [], true);
        Assert(updated.State == WorkState.Queued && flow.Steps(updated).Count == 3, "New comment has no executable step");
        Assert(flow.Steps(updated).Count(w => w.State == WorkState.Skipped) == 2, "Comment revived skipped work");
        Assert(flow.Steps(updated).Single(w => w.State == WorkState.Queued).Access == goal.Access, "New step changed scope");
        using var reopened = new SqliteStore(path);
        Assert(reopened.WorkItems().Single(w => w.Id == blocked.Id).State == WorkState.Skipped, "Skipped state not persisted");
        Console.WriteLine("PASS skip persistence, dependency release, decisions, unchanged tests/budgets, running/foreign guards and new comments; no AI invoked.");
        return 0;
    }
}
