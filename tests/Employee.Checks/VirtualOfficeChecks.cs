using Employee.Core;

static class VirtualOfficeChecks
{
    public static int Run()
    {
        var examples = new[]
        {
            (new WorkItem { Kind = WorkKind.Planning }, "Manager"),
            (new WorkItem { Kind = WorkKind.Coding }, "Development"),
            (new WorkItem { Kind = WorkKind.Research }, "Research"),
            (new WorkItem { Kind = WorkKind.Classification }, "Data & Analysis"),
            (new WorkItem { Kind = WorkKind.Extraction }, "Data & Analysis"),
            (new WorkItem { Kind = WorkKind.Coding, CapabilityHint = "UiDesign" }, "Design & Art"),
            (new WorkItem { Kind = WorkKind.Content, CapabilityHint = "GameDesign" }, "Design & Art"),
            (new WorkItem { Kind = WorkKind.Coding, CapabilityHint = "Debugging" }, "Development"),
            (new WorkItem { Kind = WorkKind.Coding, IsCoordinator = true }, "Manager"),
            (new WorkItem { Kind = WorkKind.Content }, "Creative"),
            (new WorkItem { Kind = WorkKind.Review }, "QA & Review"),
            (new WorkItem { Kind = WorkKind.Coding, Tool = new ToolTaskSpec("tool", "skill", "run") }, "External Tools"),
            (new WorkItem { Kind = WorkKind.Coding, ToolWorkflowId = "flow" }, "External Tools")
        };
        foreach (var (work, expected) in examples)
            if (VirtualOffice.ZoneFor(work) != expected)
                throw new Exception($"{work.Kind} mapped to {VirtualOffice.ZoneFor(work)}, expected {expected}");

        var terminal = new[] { WorkState.Completed, WorkState.Failed, WorkState.WrongAccess, WorkState.WrongProject, WorkState.Saved, WorkState.Skipped, WorkState.Incomplete, WorkState.NotMet, WorkState.Cancelled };
        if (terminal.Any(state => !VirtualOffice.IsTerminal(state)) || VirtualOffice.IsTerminal(WorkState.Paused) || VirtualOffice.IsTerminal(WorkState.Running))
            throw new Exception("Terminal and nonterminal task states were misclassified");
        var activities = new Dictionary<WorkState, string>
        {
            [WorkState.Queued] = "Queued", [WorkState.Running] = "Working",
            [WorkState.Delegated] = "Delegation", [WorkState.Paused] = "Paused",
            [WorkState.AwaitingApproval] = "Waiting Approval", [WorkState.WaitingForPermission] = "Waiting",
            [WorkState.WaitingForProvider] = "Waiting", [WorkState.NeedsReview] = "Waiting",
            [WorkState.Completed] = "Completed", [WorkState.Failed] = "Failed",
            [WorkState.WrongAccess] = "Blocked", [WorkState.WrongProject] = "Blocked",
            [WorkState.Saved] = "Saved", [WorkState.Skipped] = "Skipped",
            [WorkState.Incomplete] = "Closed", [WorkState.NotMet] = "Closed", [WorkState.Cancelled] = "Closed"
        };
        foreach (var state in Enum.GetValues<WorkState>())
            if (!activities.TryGetValue(state, out var expected) || VirtualOffice.ActivityFor(state) != expected ||
                VirtualOffice.IsTerminal(state) != terminal.Contains(state))
                throw new Exception($"Incorrect office activity or terminal classification for {state}");
        if (!VirtualOffice.ShouldRender(true, true, false) || VirtualOffice.ShouldRender(false, true, false) || VirtualOffice.ShouldRender(true, false, false) || VirtualOffice.ShouldRender(true, true, true))
            throw new Exception("Office rendering did not stop for Off, hidden tab or minimized window");
        foreach (var enabled in new[] { false, true })
        foreach (var visible in new[] { false, true })
        foreach (var minimized in new[] { false, true })
            if (VirtualOffice.ShouldRender(enabled, visible, minimized) != (enabled && visible && !minimized))
                throw new Exception($"Incorrect render gate: enabled={enabled}, visible={visible}, minimized={minimized}");

        var queue = new OfficeTransitionQueue(capacity: 4);
        queue.Enqueue(new("a", "Development", WorkState.Running, "Working"));
        queue.Enqueue(new("b", "Research", WorkState.Running, "Working"));
        queue.Enqueue(new("a", "Development", WorkState.Completed, "Completed"));
        if (queue.Count != 2)
            throw new Exception("Concurrent task updates were not retained and coalesced per task");
        var current = new Dictionary<string, (string Zone, WorkState State)>
        {
            ["a"] = ("Development", WorkState.Completed),
            ["b"] = ("Research", WorkState.Running)
        };
        var first = queue.TakeNext(current);
        var second = queue.TakeNext(current);
        if (first?.WorkId != "b" || second?.WorkId != "a" || second.Activity != "Completed" || queue.Count != 0)
            throw new Exception("Concurrent transitions or current terminal state were not preserved");

        queue.Enqueue(new("c", "Creative", WorkState.Running, "Working"));
        var stale = queue.TakeNext(new Dictionary<string, (string Zone, WorkState State)> { ["c"] = ("Creative", WorkState.Paused) });
        if (stale != null || queue.Count != 0)
            throw new Exception("Stale animation was replayed after task state advanced");

        queue.Enqueue(new("c", "Creative", WorkState.Running, "Delegation", "Manager", "Creative", "goal", WorkState.Running));
        var changedGoal = queue.TakeNext(
            new Dictionary<string, (string Zone, WorkState State)> { ["c"] = ("Creative", WorkState.Running) },
            new Dictionary<string, WorkState> { ["goal"] = WorkState.Paused });
        if (changedGoal != null || queue.Count != 0)
            throw new Exception("An obsolete movement survived a change to its owning goal");

        queue.Enqueue(new("c", "Creative", WorkState.Running, "Working"));
        queue.Enqueue(new("d", "Creative", WorkState.Running, "Working"));
        var moved = queue.TakeNext(new Dictionary<string, (string Zone, WorkState State)>
        {
            ["c"] = ("Research", WorkState.Running), ["d"] = ("Creative", WorkState.Running)
        });
        if (moved?.WorkId != "d" || queue.Count != 0)
            throw new Exception("A stale zone update prevented a current concurrent task from being shown");
        queue.Enqueue(new("removed", "Manager", WorkState.Running, "Working"));
        if (queue.TakeNext(new Dictionary<string, (string Zone, WorkState State)>()) != null)
            throw new Exception("A removed task's transition was replayed");

        for (var i = 0; i < 8; i++) queue.Enqueue(new(i.ToString(), "Manager", WorkState.Running, "Working"));
        if (queue.Count != 4)
            throw new Exception("Transition queue exceeded its configured bound");
        queue.Clear();
        if (queue.Count != 0 || queue.TakeNext(current) != null)
            throw new Exception("Clearing the queue did not discard pending transitions");
        // A resumed office must use a fresh snapshot, not queued history.
        queue.Enqueue(new("resume", "Manager", WorkState.Running, "Working"));
        queue.Clear();
        if (queue.TakeNext(new Dictionary<string, (string Zone, WorkState State)> { ["resume"] = ("Manager", WorkState.Running) }) != null)
            throw new Exception("Cleared history was replayed when the office resumed");

        var sameZone = new OfficeTransitionQueue();
        var concurrent = new Dictionary<string, (string Zone, WorkState State)>();
        for (var i = 0; i < 32; i++)
        {
            var id = $"parallel-{i}";
            concurrent[id] = ("Development", WorkState.Running);
            sameZone.Enqueue(new(id, "Development", WorkState.Running, "Working"));
        }
        var seen = new HashSet<string>();
        while (sameZone.TakeNext(concurrent) is { } next) seen.Add(next.WorkId);
        if (seen.Count != concurrent.Count)
            throw new Exception("Concurrent tasks in the same zone were lost");
        // Handoffs come from the task's own data: the dependency that finished last, or the Manager.
        var t0 = DateTimeOffset.UtcNow;
        var research = new WorkItem { Kind = WorkKind.Research, State = WorkState.Completed, UpdatedAt = t0 };
        var review = new WorkItem { Kind = WorkKind.Review, State = WorkState.Completed, UpdatedAt = t0.AddMinutes(1) };
        var coding = new WorkItem { Kind = WorkKind.Coding, State = WorkState.Running, DependsOn = [research.Id, review.Id] };
        var opening = new WorkItem { Kind = WorkKind.Research, State = WorkState.Running };
        var earlierCode = new WorkItem { Kind = WorkKind.Coding, State = WorkState.Completed, UpdatedAt = t0 };
        var follow = new WorkItem { Kind = WorkKind.Coding, State = WorkState.Running, DependsOn = [earlierCode.Id] };
        var goalSteps = new[] { research, review, coding, opening, earlierCode, follow };
        if (VirtualOffice.HandoffSource(coding, goalSteps) != "QA & Review")
            throw new Exception("A started step did not receive its work from the dependency that finished last");
        if (VirtualOffice.HandoffSource(opening, goalSteps) != "Manager")
            throw new Exception("A step without dependencies was not delegated by the Manager");
        if (VirtualOffice.HandoffSource(follow, goalSteps) != null)
            throw new Exception("Work passed within one zone was drawn as a walk between desks");
        var failed = new WorkItem { Kind = WorkKind.Content, State = WorkState.Failed, UpdatedAt = t0.AddMinutes(5) };
        var planner = new WorkItem { Kind = WorkKind.Planning, IsCoordinator = true, State = WorkState.Completed, UpdatedAt = t0.AddMinutes(9) };
        if (VirtualOffice.Messenger([research, review, failed, planner])?.Id != failed.Id)
            throw new Exception("The goal report was not carried by the step that finished last outside the Manager's zone");
        if (!VirtualOffice.IsReportable(WorkState.NeedsReview) || VirtualOffice.IsReportable(WorkState.Running))
            throw new Exception("Reportable goal states are wrong");

        // Several actors move at once: a transition waits only for the actors it needs.
        var busy = new OfficeTransitionQueue();
        busy.Enqueue(new("x", "Development", WorkState.Running, "Handoff", "Research", "Development"));
        busy.Enqueue(new("y", "Creative", WorkState.Running, "Delegation", "Manager", "Creative"));
        var running = new Dictionary<string, (string Zone, WorkState State)> { ["x"] = ("Development", WorkState.Running), ["y"] = ("Creative", WorkState.Running) };
        var started = busy.TakeNext(running, null, t => t.Source != "Research");
        if (started?.WorkId != "y" || busy.Count != 1)
            throw new Exception("A transition for free actors waited behind one whose actor was busy");
        if (busy.TakeNext(running, null, _ => true)?.WorkId != "x")
            throw new Exception("A transition that waited for a busy actor was lost");
        // Per-character FIFO lanes: a new action waits for the current one, with no abort.
        var lanes = new ActorActionQueue(capacityPerSeat: 3, timeout: TimeSpan.FromSeconds(10));
        var clock = DateTimeOffset.UtcNow;
        var live = new Dictionary<string, (string Zone, WorkState State)>();
        for (var i = 0; i < 6; i++) live[$"w{i}"] = ("Development", WorkState.Running);
        var a1 = lanes.Enqueue("seat:A", new("w0", "Development", WorkState.Running, "Working"), clock);
        var a2 = lanes.Enqueue("seat:A", new("w1", "Development", WorkState.Running, "Working"), clock);
        lanes.Enqueue("seat:B", new("w2", "Development", WorkState.Running, "Working"), clock);
        bool Valid(OfficeAction a) => OfficeTransitionQueue.IsCurrent(a.Transition, live);
        if (lanes.TryStartNext("seat:A", clock, Valid)?.Id != a1.Id)
            throw new Exception("The first queued action did not start first");
        if (lanes.TryStartNext("seat:A", clock, Valid) != null || lanes.Current("seat:A")?.Id != a1.Id)
            throw new Exception("A new action started while the character's current action was still running");
        var b1 = lanes.TryStartNext("seat:B", clock, Valid);
        if (b1?.Transition.WorkId != "w2")
            throw new Exception("One character's running action blocked another character");
        lanes.Complete("seat:B", b1.Id);
        if (lanes.Complete("seat:A", a2.Id) || !lanes.Complete("seat:A", a1.Id))
            throw new Exception("Completion was accepted for an action that is not the current one");
        if (lanes.TryStartNext("seat:A", clock, Valid)?.Id != a2.Id)
            throw new Exception("The next action did not start after the previous one completed");
        // Bound and timeout.
        lanes.Complete("seat:A", a2.Id);
        for (var i = 0; i < 5; i++) lanes.Enqueue("seat:A", new($"w{i}", "Development", WorkState.Running, "Working"), clock);
        if (lanes.PendingCount("seat:A") != 3)
            throw new Exception("A character's action queue exceeded its bound");
        lanes.TryStartNext("seat:A", clock, Valid);
        if (lanes.Expired(clock.AddSeconds(5)).Count != 0 || lanes.Expired(clock.AddSeconds(11)).Count != 1)
            throw new Exception("The safety timeout did not flag a stuck action");
        // Persisted form: the current action and the queue survive a restart, in order.
        var saved = lanes.Snapshot();
        var restored = new ActorActionQueue(capacityPerSeat: 3);
        restored.Restore(saved);
        if (restored.PendingCount("seat:A") != 3 || restored.TryStartNext("seat:A", clock, Valid)?.Id != saved.First(r => r.Seat == "seat:A" && r.Current).Id)
            throw new Exception("The queue and current action were not restored in order");
        lanes.InterruptAll();
        if (lanes.Current("seat:A") != null || lanes.PendingCount("seat:A") != 3)
            throw new Exception("Interrupting kept the running action out of its lane");
        Console.WriteLine("PASS Virtual Office per-character action queue is FIFO, never aborts the current action, is bounded, times out and restores");
        Console.WriteLine("PASS Virtual Office handoffs follow real step dependencies, reports come from the last finished step, and actors move independently");
        AmbientChecks();
        AmbientRuleChecks();
        AssigneeChecks();
        Console.WriteLine("PASS Virtual Office zone mapping, terminal/wait states, concurrent tasks, per-task coalescing, stale transition skipping and bounded queue");
        return 0;
    }
    static void AmbientRuleChecks()
    {
        var options = new AmbientOptions { MaxAway = 3, MinPresentPerZone = 1 };
        var twoFree = new[]
        {
            new AmbientSeat("a", "Development", false, true, false, false),
            new AmbientSeat("b", "Development", false, true, false, false)
        };
        if (!OfficeAmbientRules.CanLeave(twoFree[0], twoFree, options))
            throw new Exception("A free worker in a room with another free worker could not leave");
        var oneFreeOneBusy = new[]
        {
            new AmbientSeat("a", "Development", false, true, false, false),
            new AmbientSeat("b", "Development", false, true, false, false, true)
        };
        if (OfficeAmbientRules.FreePresentCount("Development", oneFreeOneBusy) != 1)
            throw new Exception("A worker holding work was counted as free presence");
        if (OfficeAmbientRules.CanLeave(oneFreeOneBusy[0], oneFreeOneBusy, options))
            throw new Exception("The last worker without work left their department");
        var oneSeat = new[] { new AmbientSeat("only", "Research", false, true, false, false) };
        if (OfficeAmbientRules.CanLeave(oneSeat[0], oneSeat, options))
            throw new Exception("The only worker of a one-seat room was allowed to leave");
        var waiting = new[]
        {
            new AmbientSeat("a", "Creative", false, true, false, false, false, true),
            new AmbientSeat("b", "Creative", false, true, false, false, false, true)
        };
        if (OfficeAmbientRules.CanLeave(waiting[0], waiting, options) || OfficeAmbientRules.CanLeave(waiting[1], waiting, options))
            throw new Exception("A worker left a department with unassigned work waiting for someone at a desk");
        var atCap = new[]
        {
            new AmbientSeat("a", "Research", false, false, false, false),
            new AmbientSeat("b", "Research", false, false, false, false),
            new AmbientSeat("c", "Research", false, false, false, false),
            new AmbientSeat("d", "Research", false, true, false, false),
            new AmbientSeat("e", "Research", false, true, false, false)
        };
        if (OfficeAmbientRules.AwayBelowCap(atCap, options))
            throw new Exception("Ambient movement was allowed although MaxAway was reached");
        if (!OfficeAmbientRules.RoomCanSpare("d", atCap, options) ||
            OfficeAmbientRules.RoomCanSpare("e", [new AmbientSeat("e", "Research", false, true, false, false)], options))
            throw new Exception("RoomCanSpare mishandled the last free worker of a department");

        var delegation = new OfficeTransition("w", "Development", WorkState.Running, "Delegation", "Manager", "Development");
        if (VirtualOffice.MoverZone(delegation) != "Development" || VirtualOffice.ReceiverZone(delegation) != "Manager")
            throw new Exception("A delegation to a department was not moved by that department and received by the Manager");
        var handoff = new OfficeTransition("w", "Development", WorkState.Running, "Handoff", "Research", "Development");
        if (VirtualOffice.MoverZone(handoff) != "Research" || VirtualOffice.ReceiverZone(handoff) != "Development")
            throw new Exception("A handoff between departments did not move from source to target");
        var report = new OfficeTransition("w", "Development", WorkState.Completed, "Reporting", "Development", "Manager");
        if (VirtualOffice.MoverZone(report) != "Development" || VirtualOffice.ReceiverZone(report) != "Manager")
            throw new Exception("A report did not move from the department to the Manager");
        Console.WriteLine("PASS Virtual Office ambient rules keep one free worker per department, respect MaxAway and waiting work, and keep the Manager seated");
    }
    static void AssigneeChecks()
    {
        // A task goes to any worker at a desk of the department — never to a fixed seat or role.
        (string Key, bool AtDesk, bool HoldsWork)[] team =
        [
            ("dev:0", true, false), ("dev:1", true, true), ("dev:2", false, false), ("dev:3", true, false)
        ];
        var rng = new Random(7);
        var picks = Enumerable.Range(0, 64).Select(_ => VirtualOffice.PickAssignee(team, rng)).ToArray();
        if (picks.Any(p => p is not ("dev:0" or "dev:3")))
            throw new Exception("A task was given to a worker who is away from the desk or already holds work");
        if (picks.Distinct().Count() < 2)
            throw new Exception("Tasks were pinned to one fixed worker instead of any worker at a desk");
        var rng2 = new Random(7);
        var again = Enumerable.Range(0, 64).Select(_ => VirtualOffice.PickAssignee(team, rng2)).ToArray();
        if (!picks.SequenceEqual(again))
            throw new Exception("Seeded desk assignment was not reproducible");
        // A busy department queues the task; the first worker back at a desk takes it.
        if (VirtualOffice.PickAssignee([("dev:0", false, false), ("dev:1", true, true)], new Random(7)) != null)
            throw new Exception("A task was assigned although nobody in the department was free at a desk");
        if (VirtualOffice.PickAssignee([("dev:0", false, false), ("dev:1", true, true), ("dev:2", true, false)], new Random(7)) != "dev:2")
            throw new Exception("A waiting task did not go to the worker who came back to their desk");
        Console.WriteLine("PASS Virtual Office tasks go to any worker at a desk of the department and queue while nobody is there");
    }
    static void AmbientChecks()
    {
        string[] zones = ["Manager", "Research", "Development", "Data & Analysis", "Design & Art", "Creative", "QA & Review", "External Tools"];
        foreach (var maxAway in new[] { 3, 4 })
        foreach (var seed in new[] { 1, 7, 42 })
        {
            var options = new AmbientOptions { MaxAway = maxAway, ChancePerDecision = 1, DecisionIntervalTicks = 1, GlobalCooldownSeconds = 0, ActorCooldownMinSeconds = 0, ActorCooldownMaxSeconds = 0, PairCooldownSeconds = 0 };
            var scheduler = new AmbientScheduler(options, new Random(seed));
            var seats = zones.SelectMany(z => Enumerable.Range(0, z == "Manager" ? 1 : 4).Select(i => new AmbientSeat($"{z}:{i}", z, z == "Manager", true, false, false))).ToList();
            var now = DateTimeOffset.UtcNow;
            var left = new Dictionary<string, int>();
            for (var step = 0; step < 5000; step++)
            {
                now = now.AddMilliseconds(100);
                // Some workers come back, the rest stay out.
                foreach (var key in left.Where(kv => kv.Value <= step).Select(kv => kv.Key).ToArray())
                {
                    var i = seats.FindIndex(s => s.Key == key);
                    seats[i] = seats[i] with { Present = true };
                    left.Remove(key);
                    scheduler.NotifyReturned(key, now);
                }
                if (scheduler.TryPlan(now, seats, 2, 5) is not { } plan) continue;
                var actor = seats.First(s => s.Key == plan.ActorKey);
                if (actor.IsManager || actor.Busy || !actor.Present) throw new Exception("Ambient picked a manager, a busy or an absent worker");
                if (OfficeAmbientRules.AwayCount(seats) + 1 > maxAway) throw new Exception("Ambient exceeded MaxAway");
                if (OfficeAmbientRules.PresentCount(actor.Zone, seats) < 2) throw new Exception("Ambient took the last worker of a department");
                if (plan.TargetKey is { } host && (host.StartsWith("Manager") || host == actor.Key)) throw new Exception("Ambient visited the manager or oneself");
                seats[seats.FindIndex(s => s.Key == actor.Key)] = actor with { Present = false };
                left[actor.Key] = step + 20 + seed % 5;
                if (zones.Any(z => OfficeAmbientRules.PresentCount(z, seats) < 1)) throw new Exception("A department was left empty");
            }
        }
        var busy = new[] { new AmbientSeat("a", "Research", false, true, true, false), new AmbientSeat("b", "Research", false, true, false, false), new AmbientSeat("c", "Research", false, true, false, false) };
        if (OfficeAmbientRules.CanLeave(busy[0], busy, new AmbientOptions()) || !OfficeAmbientRules.CanLeave(busy[1] with { Present = true }, busy, new AmbientOptions()))
            throw new Exception("CanLeave mishandled a busy worker");
        if (new AmbientOptions { MaxAway = 9, MinPresentPerZone = 0 }.Validate() is not { MaxAway: 4, MinPresentPerZone: 1 })
            throw new Exception("Ambient options were not clamped");
        Console.WriteLine("PASS Virtual Office ambient activity keeps managers seated, one worker per department and fewer than MaxAway people away");
    }
}
