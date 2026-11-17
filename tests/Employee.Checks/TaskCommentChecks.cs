using Employee.Core;
using Employee.Infrastructure;

static class TaskCommentChecks
{
    public static int Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "EmployeeCommentChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        using var store = new SqliteStore(Path.Combine(folder, "checks.db"));
        var runner = new ProcessRunner();
        var flow = new UnifiedTaskService(store, store, new Orchestrator(store, Array.Empty<IAIProvider>(), runner), runner);
        void Assert(bool value)
        {
            if (!value)
                throw new Exception("Task comment assertion failed");
        }

        var goal = new WorkItem
        {
            IsGoal = true,
            State = WorkState.Completed,
            Request = "Original request",
            Access = AccessLevel.WriteProject
        };
        var session = new WorkSession
        {
            RootWorkId = goal.Id,
            State = SessionState.Completed,
            CallsUsed = 3,
            MaxCalls = 5
        };
        goal.SessionId = session.Id;
        store.SaveSession(session);
        store.SaveWork(goal);
        var step = new WorkItem
        {
            SessionId = session.Id,
            ParentTaskId = goal.Id,
            State = WorkState.Completed,
            Attempts = 3,
            Access = goal.Access
        };
        store.SaveWork(step);
        store.Log(step.Id, "Execution", "Success\n{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"Updated the button.\"}}");
        flow.Sync(goal.Id);
        flow.Sync(goal.Id);
        var response = store.WorkItems().Single(w => w.Id == goal.Id).Comments.Single();
        Assert(response.IsAssistant && response.SourceWorkId == step.Id && response.ExecutionSequence != null);
        Assert(response.Text.Contains("Updated the button.") && !response.Text.Contains("item.completed"));
        using (var reopened = new SqliteStore(Path.Combine(folder, "checks.db")))
            Assert(reopened.WorkItems().Single(w => w.Id == goal.Id).Comments.Single().IsAssistant);
        var saved = flow.AddComment(goal, "Change the button colour", [], true);
        Assert(saved.Id == goal.Id && saved.Request == "Original request" && saved.State == WorkState.Queued && saved.QueuedAt != null);
        // A change request becomes its own step; the finished step and its history stay as they are.
        var commentStep = store.WorkItems().Single(w => w.ParentTaskId == goal.Id && w.Id != step.Id);
        Assert(commentStep.Request.Contains("Change the button colour") && commentStep.State == WorkState.Queued);
        Assert(store.WorkItems().Single(w => w.Id == step.Id).State == WorkState.Completed);
        Assert(flow.SessionFor(saved)is { CallsUsed: 3, MaxCalls: 5, State: SessionState.Paused });
        flow.Sync(saved.Id);
        Assert(flow.NextQueued()?.Id == saved.Id);
        Assert(saved.Access == AccessLevel.WriteProject && TaskAttachments.Context(saved).Contains("Change the button colour"));
        Assert(!saved.Comments.Last().IsAssistant);
        step.State = WorkState.NeedsReview;
        store.SaveWork(step);
        var review = new PendingDecision
        {
            SessionId = session.Id,
            WorkId = step.Id,
            Kind = "Review"
        };
        store.SaveDecision(review);
        var approval = new PendingDecision
        {
            SessionId = session.Id,
            WorkId = step.Id,
            Kind = "Approval"
        };
        store.SaveDecision(approval);
        flow.AddComment(saved, "Adjust spacing too", [], true);
        Assert(store.Decisions(session.Id).Single(d => d.Id == review.Id).State == DecisionState.Rejected);
        Assert(store.Decisions(session.Id).Single(d => d.Id == approval.Id).State == DecisionState.Pending);
        Assert(store.WorkItems().Single(w => w.Id == step.Id).Attempts == 3);
        Assert(store.Logs(goal.Id).Count(l => l.Event == "TaskUpdateStepCreated") == 2);
        var before = store.WorkItems().Single(w => w.Id == goal.Id).Comments.Count;
        try
        {
            flow.AddComment(saved, " ", [], true);
            throw new Exception("Empty comment accepted");
        }
        catch (InvalidOperationException)
        {
        }

        Assert(store.WorkItems().Single(w => w.Id == goal.Id).Comments.Count == before);
        flow.AddComment(saved, "Save only", []);
        Assert(store.WorkItems().Single(w => w.Id == goal.Id).Comments.Count == before + 1);
        using (var reopened = new SqliteStore(Path.Combine(folder, "checks.db")))
            Assert(reopened.WorkItems().Single(w => w.Id == goal.Id).State == WorkState.Queued);
        flow.SaveForLater(flow.NextQueued()!);
        Assert(flow.NextQueued() == null && store.WorkItems().Single(w => w.Id == goal.Id).State == WorkState.Paused);
        var draft = new WorkItem
        {
            State = WorkState.Saved,
            Request = "Draft"
        };
        store.SaveWork(draft);
        flow.AddComment(draft, "Run this update", [], true);
        Assert(flow.NextQueued()?.Id == draft.Id);
        Console.WriteLine("PASS task comment persistence, reopening completed/review steps, original goal, permissions, budgets and approval gates; no AI invoked.");
        return 0;
    }
}
