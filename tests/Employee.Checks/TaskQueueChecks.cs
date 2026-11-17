using Employee.Core;
using Employee.Infrastructure;

static class TaskQueueChecks
{
    public static async Task<int> Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "EmployeeQueueChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var store = new SqliteStore(Path.Combine(root, "checks.db"));
        store.SaveProject(new("fixture", "Queue fixture", root));
        var runner = new ProcessRunner();
        var flow = new UnifiedTaskService(store, store, new Orchestrator(store, Array.Empty<IAIProvider>(), runner), runner);
        void Assert(bool value)
        {
            if (!value)
                throw new Exception("Queue assertion failed");
        }

        var legacy = new WorkItem
        {
            Request = "legacy draft"
        };
        store.SaveWork(legacy);
        var step = new WorkItem
        {
            ParentTaskId = "parent"
        };
        store.SaveWork(step);
        flow.RecoverSavedTasks();
        Assert(store.WorkItems().Single(w => w.Id == legacy.Id).State == WorkState.Saved);
        Assert(store.WorkItems().Single(w => w.Id == step.Id).State == WorkState.Queued);
        Assert(flow.NextQueued() == null);
        flow.Enqueue(legacy);
        var first = flow.NextQueued()!;
        var later = new WorkItem
        {
            State = WorkState.Saved,
            Priority = 100,
            ProjectId = "fixture",
            Request = "Queued fixture"
        };
        store.SaveWork(later);
        flow.Enqueue(later);
        first.QueuedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        store.SaveWork(first);
        flow.Enqueue(first);
        Assert(flow.NextQueued()!.Id == later.Id);
        later = store.WorkItems().Single(w => w.Id == later.Id);
        later.Priority = first.Priority;
        store.SaveWork(later);
        Assert(flow.NextQueued()!.Id == legacy.Id);
        Assert(store.WorkItems().Single(w => w.Id == first.Id).QueuedAt == first.QueuedAt);
        flow.SaveForLater(first);
        Assert(flow.NextQueued()!.Id == later.Id);
        Assert(store.WorkItems().Single(w => w.Id == first.Id).State == WorkState.Saved);
        // No providers: queued work follows the normal Manual gate without inference.
        flow.SaveForLater(later);
        await flow.StartAsync(store.WorkItems().Single(w => w.Id == later.Id), new ManagerSettings { Mode = RoutingMode.Manual }, Array.Empty<ProviderHealth>());
        Assert(store.WorkItems().Single(w => w.Id == later.Id).State == WorkState.WaitingForProvider);
        Assert(flow.NextQueued() == null && !flow.IsBusy);
        try
        {
            flow.Enqueue(store.WorkItems().Single(w => w.Id == later.Id));
            throw new Exception("Started task was queued again");
        }
        catch (InvalidOperationException)
        {
        }

        using var reopened = new SqliteStore(Path.Combine(root, "checks.db"));
        Assert(reopened.WorkItems().Single(w => w.Id == first.Id).State == WorkState.Saved);
        Assert((int)WorkState.Delegated == 9 && (int)WorkState.Saved == 10);
        // Capacity waits apply to both a first run and an existing session, without inference.
        store.SaveProject(new("capacity", "Capacity fixture", root));
        var pool = new TaskExecutionPool(store, store, new Orchestrator(store, [], runner), runner);
        var settings = new ManagerSettings
        {
            Mode = RoutingMode.Manual,
            AdaptiveParallelism = false
        };
        settings.Automation.MaxParallelAgents = 1;
        var draft = new WorkItem
        {
            IsGoal = true,
            ProjectId = "capacity",
            Request = "Wait for capacity",
            State = WorkState.Saved
        };
        store.SaveWork(draft);
        using (var lease = pool.ReserveExternal([root], settings))
        {
            await pool.StartAsync(draft, settings, []);
            var queued = store.WorkItems().Single(w => w.Id == draft.Id);
            Assert(queued.State == WorkState.Queued && queued.QueuedAt != null && queued.SessionId == null);
            await pool.StartAsync(draft, settings, []);
            Assert(store.WorkItems().Single(w => w.Id == draft.Id).QueuedAt == queued.QueuedAt);
            Assert(pool.NextQueued(settings) == null && !pool.IsBusy);
        }

        Assert(pool.NextQueued(settings)?.Id == draft.Id);
        await pool.StartAsync(draft, settings, []);
        var resumed = store.WorkItems().Single(w => w.Id == draft.Id);
        Assert(resumed.SessionId != null && resumed.QueuedAt == null && resumed.State == WorkState.WaitingForProvider);
        var route = new Route("fixture", "model", "Explicit selection");
        using (var lease = pool.ReserveExternal([root], settings))
        {
            await pool.StartAsync(resumed, settings, [], route, true);
            var queued = store.WorkItems().Single(w => w.Id == draft.Id);
            Assert(queued.State == WorkState.Queued && queued.QueuedAt != null && queued.SessionId == resumed.SessionId);
            Assert(queued.PreferredRoute == route && queued.RoutingApproved && queued.Attempts == resumed.Attempts);
            pool.SaveForLater(queued);
            Assert(store.WorkItems().Single(w => w.Id == draft.Id).State == WorkState.Paused);
            await pool.StartAsync(resumed, settings, []);
        }

        Assert(pool.NextQueued(settings)?.Id == draft.Id);
        pool.SaveForLater(store.WorkItems().Single(w => w.Id == draft.Id));
        await pool.StartAsync(store.WorkItems().Single(w => w.Id == draft.Id), settings, []);
        Assert(store.WorkItems().Single(w => w.Id == draft.Id).SessionId == resumed.SessionId && !pool.IsBusy);
        Console.WriteLine("PASS queue priority then FIFO, duplicate enqueue, saved exclusion, legacy migration, persistence, normal routing gates and enum compatibility; no AI invoked.");
        return 0;
    }
}
