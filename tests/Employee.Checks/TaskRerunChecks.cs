using Employee.Core;
using Employee.Infrastructure;

internal static class TaskRerunChecks
{
    public static int Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "TaskRerunChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        using var db = new SqliteStore(Path.Combine(folder, "checks.db"));
        db.SaveProject(new("p", "fixture", folder));
        var runner = new NullRunner();
        var pool = new TaskExecutionPool(db, db, new Orchestrator(db, [], runner), runner);
        var original = new WorkItem
        {
            IsGoal = true,
            ProjectId = "p",
            Request = "Original request",
            State = WorkState.Completed,
            Access = AccessLevel.WriteProject,
            Attempts = 4,
            Switches = 2,
            RoutingApproved = true,
            Verification = VerificationState.Verified,
            Attachments = [new("input.txt", [1, 2])],
            Comments = [new("User update", [], DateTimeOffset.UtcNow), new("Old result", [], DateTimeOffset.UtcNow)
            {
                IsAssistant = true
            }

            ]
        };
        db.SaveWork(original);
        db.Log(original.Id, "Execution", "Old result");
        var fresh = pool.Rerun(original);
        if (fresh.Id == original.Id || fresh.SessionId != null || fresh.Attempts != 0 || fresh.Switches != 0 || fresh.RoutingApproved || fresh.State != WorkState.Saved || fresh.Verification != VerificationState.Untested || fresh.Request != original.Request || fresh.Access != original.Access || fresh.Comments.Count != 1 || fresh.Comments[0].Text != "User update" || !fresh.Attachments[0].Content.SequenceEqual(original.Attachments[0].Content))
            throw new Exception("Rerun did not start fresh with user inputs.");
        if (db.WorkItems().Single(w => w.Id == original.Id).State != WorkState.Completed || db.Logs(original.Id).Count != 1)
            throw new Exception("Original run was modified.");
        // Stale UI snapshots must not override a later saved permission edit.
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
                GrantedAccess = AccessLevel.ReadProject,
                AutoAssign = false,
                AutomaticIntent = true
            };
            db.SaveWork(stale);
            db.UpdateTaskAccess(stale.Id, "p", AccessLevel.WriteProject);
            var rerun = pool.Rerun(stale);
            db.NormalizeImplementationScope(rerun.Id);
            var persisted = db.WorkItems().Single(w => w.Id == rerun.Id);
            if (persisted.Access != AccessLevel.WriteProject || persisted.AutoAssign || persisted.SessionId != null)
                throw new Exception("Rerun lost the latest saved access for " + state);
        }

        var workflow = new ToolWorkflow
        {
            ProjectId = "p",
            Name = "workflow",
            State = ToolRunState.Completed,
            Steps = [new()
            {
                Key = "one",
                ToolId = "tool",
                Operation = "run",
                RunId = "old-run"
            }

            ]
        };
        db.SaveWorkflow(workflow);
        original.ToolWorkflowId = workflow.Id;
        db.SaveWork(original);
        var toolGoal = pool.Rerun(original);
        var cloned = db.Workflows().Single(w => w.Id == toolGoal.ToolWorkflowId);
        if (cloned.Id == workflow.Id || cloned.State != ToolRunState.Draft || cloned.Steps[0].RunId != null)
            throw new Exception("Rerun reused tool execution state.");
        original.State = WorkState.Running;
        db.SaveWork(original);
        var rejected = false;
        try
        {
            pool.Rerun(original);
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }

        if (!rejected)
            throw new Exception("Running task could be rerun.");
        Console.WriteLine("PASS fresh rerun, user inputs, history preservation, fresh workflow and running guard; no AI invoked");
        return 0;
    }
}
