namespace Employee.Core;

/// <summary>Presentation-only task projection and bounded transition coalescing for Virtual Office.</summary>
public static class VirtualOffice
{
    public static bool ShouldRender(bool enabled, bool tabVisible, bool minimized) => enabled && tabVisible && !minimized;

    public static string ZoneFor(WorkItem work)
    {
        if (work.Tool != null || work.ToolWorkflowId != null) return "External Tools";
        if (work.IsCoordinator || work.Kind == WorkKind.Planning) return "Manager";
        // UI, game design and image work has its own room, split from Development and Creative.
        if (work.Kind is WorkKind.Coding or WorkKind.Content && WorkCapabilities.For(work) is WorkCapability.UiDesign or WorkCapability.GameDesign or WorkCapability.ImageGeneration)
            return "Design & Art";
        return work.Kind switch
        {
            WorkKind.Coding => "Development",
            // Research is the first step of a new task (it reports to the manager, who plans the work).
            WorkKind.Research => "Research",
            WorkKind.Classification or WorkKind.Extraction => "Data & Analysis",
            WorkKind.Content => "Creative",
            WorkKind.Review => "QA & Review",
            _ => "Manager"
        };
    }

    public static string ActivityFor(WorkState state) => state switch
    {
        WorkState.Running => "Working",
        WorkState.Delegated => "Delegation",
        WorkState.Paused => "Paused",
        WorkState.AwaitingApproval => "Waiting Approval",
        WorkState.WaitingForPermission or WorkState.WaitingForProvider or WorkState.NeedsReview => "Waiting",
        WorkState.Completed => "Completed",
        WorkState.Failed => "Failed",
        WorkState.WrongAccess or WorkState.WrongProject => "Blocked",
        WorkState.Saved => "Saved",
        WorkState.Skipped => "Skipped",
        WorkState.Incomplete or WorkState.NotMet or WorkState.Cancelled => "Closed",
        _ => state.ToString()
    };

    public static bool IsTerminal(WorkState state) => state is
        WorkState.Completed or WorkState.Failed or WorkState.WrongAccess or WorkState.WrongProject or WorkState.Saved or WorkState.Skipped
        or WorkState.Incomplete or WorkState.NotMet or WorkState.Cancelled;

    // Goal states that end with the responsible worker reporting to the Manager.
    public static bool IsReportable(WorkState state) => state is
        WorkState.Completed or WorkState.Failed or WorkState.WrongAccess or WorkState.WrongProject or WorkState.AwaitingApproval
        or WorkState.WaitingForPermission or WorkState.WaitingForProvider or WorkState.NeedsReview;

    // Where the work for a step that just started comes from, read from the task's own data: the
    // most recently finished step it depends on (or takes tool input from) in the same goal, or the
    // Manager when the step depends on nothing. Null when the input comes from the same zone.
    public static string? HandoffSource(WorkItem step, IReadOnlyCollection<WorkItem> goalSteps)
    {
        var inputs = step.DependsOn.Concat(step.Tool?.InputFrom is { } input ? [input] : Array.Empty<string>()).ToHashSet(StringComparer.Ordinal);
        if (inputs.Count == 0)
            return ZoneFor(step) == "Manager" ? null : "Manager";
        var producer = goalSteps.Where(w => inputs.Contains(w.Id) && IsTerminal(w.State)).OrderByDescending(w => w.UpdatedAt ?? w.CreatedAt).FirstOrDefault();
        var source = producer == null ? null : ZoneFor(producer);
        return source == null || source == ZoneFor(step) ? null : source;
    }

    // The department of the worker who walks for a transition. Delegation comes from the Manager,
    // who never leaves the desk: the worker receiving the new step walks to the Manager instead.
    public static string MoverZone(OfficeTransition transition)
    {
        if (transition.Source == null || transition.Target == null || transition.Source == transition.Target)
            return transition.Zone;
        return transition.Source == "Manager" ? transition.Target : transition.Source;
    }

    // The department of the receiving side. A delegation is received by the Manager at the management
    // desk; every other walk is received in its target department.
    public static string? ReceiverZone(OfficeTransition transition)
    {
        if (transition.Source == null || transition.Target == null || transition.Source == transition.Target)
            return null;
        return transition.Source == "Manager" ? "Manager" : transition.Target;
    }

    // The worker who carries a goal's report to the Manager: the step that finished last, outside the Manager's own zone.
    public static WorkItem? Messenger(IEnumerable<WorkItem> goalSteps) => goalSteps
        .Where(w => !w.IsGoal && !w.IsCoordinator && ZoneFor(w) != "Manager" && (IsTerminal(w.State) || IsReportable(w.State)))
        .OrderByDescending(w => w.UpdatedAt ?? w.CreatedAt)
        .FirstOrDefault();

    // A department's arriving task goes to any worker who is at their desk right now and holds no
    // work yet — never a fixed worker or seat. Null when nobody is free at a desk of the department
    // (rare: a desk worker always remains): the task then queues until somebody is back at a desk.
    public static string? PickAssignee(IEnumerable<(string Key, bool AtDesk, bool HoldsWork)> workers, Random? random = null)
    {
        var free = workers.Where(w => w.AtDesk && !w.HoldsWork).Select(w => w.Key).ToArray();
        return free.Length == 0 ? null : free[(random ?? Random.Shared).Next(free.Length)];
    }
}

public sealed record OfficeTransition(string WorkId, string Zone, WorkState State, string Activity, string? Source = null, string? Target = null, string? GoalId = null, WorkState? GoalState = null);

/// <summary>Coalesces updates per task; stale intermediate transitions are discarded against current state.</summary>
public sealed class OfficeTransitionQueue(int capacity = 48)
{
    private readonly LinkedList<OfficeTransition> pending = [];
    private readonly Dictionary<string, LinkedListNode<OfficeTransition>> byWork = new(StringComparer.Ordinal);

    public int Count => pending.Count;

    public void Enqueue(OfficeTransition transition)
    {
        if (byWork.TryGetValue(transition.WorkId, out var old))
        {
            pending.Remove(old);
            byWork.Remove(transition.WorkId);
        }
        var node = pending.AddLast(transition);
        byWork[transition.WorkId] = node;
        while (pending.Count > Math.Max(1, capacity))
        {
            var first = pending.First!;
            pending.RemoveFirst();
            byWork.Remove(first.Value.WorkId);
        }
    }

    // The oldest transition that is still current and that canStart accepts (for example, its actor
    // is free). Stale transitions are dropped; current ones that cannot start yet stay queued.
    public OfficeTransition? TakeNext(IReadOnlyDictionary<string, (string Zone, WorkState State)> current, IReadOnlyDictionary<string, WorkState>? goals = null, Func<OfficeTransition, bool>? canStart = null)
    {
        for (var node = pending.First; node != null;)
        {
            var next = node.Next;
            var item = node.Value;
            var valid = IsCurrent(item, current, goals);
            if (!valid || canStart == null || canStart(item))
            {
                pending.Remove(node);
                byWork.Remove(item.WorkId);
                if (valid)
                    return item;
            }

            node = next;
        }

        return null;
    }

    public void Clear()
    {
        pending.Clear();
        byWork.Clear();
    }

    // Whether a transition still matches the task's (and its goal's) present state.
    public static bool IsCurrent(OfficeTransition item, IReadOnlyDictionary<string, (string Zone, WorkState State)> current, IReadOnlyDictionary<string, WorkState>? goals = null) =>
        current.TryGetValue(item.WorkId, out var now) && now.Zone == item.Zone && now.State == item.State
        && (item.GoalId == null || (goals != null && goals.TryGetValue(item.GoalId, out var goalState) && goalState == item.GoalState));
}

/// <summary>One queued visual action for one character (seat).</summary>
public sealed record OfficeAction(string Id, string Seat, OfficeTransition Transition, DateTimeOffset EnqueuedAt, DateTimeOffset? StartedAt = null);

/// <summary>Persisted form of an action; Position is its order in the seat's lane (a current action is first).</summary>
public sealed record OfficeActionRecord(string Id, string Seat, int Position, bool Current, OfficeTransition Transition, DateTimeOffset EnqueuedAt);

public interface IOfficeActionStore
{
    void SaveOfficeActions(IReadOnlyList<OfficeActionRecord> records);
    IReadOnlyList<OfficeActionRecord> OfficeActions();
}

/// <summary>
/// A FIFO lane of actions per character. A new action waits while the character is busy and starts only
/// after the current one is completed; nothing is aborted. A safety timeout releases a stuck current
/// action, and each lane is bounded so a stalled character cannot grow it without limit.
/// </summary>
public sealed class ActorActionQueue(int capacityPerSeat = 12, TimeSpan? timeout = null)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(90);
    private sealed class Lane
    {
        public OfficeAction? Current;
        public readonly LinkedList<OfficeAction> Pending = [];
    }

    private readonly Dictionary<string, Lane> lanes = new(StringComparer.Ordinal);
    private readonly TimeSpan limit = timeout is { } t && t > TimeSpan.Zero ? t : DefaultTimeout;
    private readonly int capacity = Math.Max(1, capacityPerSeat);

    /// <summary>Increases on every change so callers can persist only when something changed.</summary>
    public int Version { get; private set; }
    public int TotalCount => lanes.Values.Sum(l => l.Pending.Count + (l.Current == null ? 0 : 1));
    public int PendingCount(string seat) => lanes.TryGetValue(seat, out var l) ? l.Pending.Count : 0;
    public OfficeAction? Current(string seat) => lanes.TryGetValue(seat, out var l) ? l.Current : null;

    private Lane LaneFor(string seat)
    {
        if (!lanes.TryGetValue(seat, out var lane)) lanes[seat] = lane = new Lane();
        return lane;
    }

    // Queues at the back of the seat's lane. A waiting action of the same task is replaced by the newer
    // one (the older one describes a state that has passed). When the lane is full the oldest waiting
    // action is dropped; the current one is never dropped.
    public OfficeAction Enqueue(string seat, OfficeTransition transition, DateTimeOffset now)
    {
        var lane = LaneFor(seat);
        for (var node = lane.Pending.First; node != null;)
        {
            var next = node.Next;
            if (node.Value.Transition.WorkId == transition.WorkId) lane.Pending.Remove(node);
            node = next;
        }
        var action = new OfficeAction(Guid.NewGuid().ToString("N"), seat, transition, now);
        lane.Pending.AddLast(action);
        while (lane.Pending.Count > capacity) lane.Pending.RemoveFirst();
        Version++;
        return action;
    }

    // Starts the next action of an idle seat. Strict FIFO: when the first valid action cannot start yet
    // (canStart is false) the seat keeps waiting; later ones do not overtake it. Stale actions are dropped.
    public OfficeAction? TryStartNext(string seat, DateTimeOffset now, Func<OfficeAction, bool> isValid, Func<OfficeAction, bool>? canStart = null)
    {
        if (!lanes.TryGetValue(seat, out var lane) || lane.Current != null) return null;
        while (lane.Pending.First is { } node)
        {
            var action = node.Value;
            if (!isValid(action)) { lane.Pending.RemoveFirst(); Version++; continue; }
            if (canStart != null && !canStart(action)) return null;
            lane.Pending.RemoveFirst();
            lane.Current = action with { StartedAt = now };
            Version++;
            return lane.Current;
        }
        return null;
    }

    // Reports that the current action finished (movement reached its target, or its duration elapsed).
    public bool Complete(string seat, string actionId)
    {
        if (!lanes.TryGetValue(seat, out var lane) || lane.Current?.Id != actionId) return false;
        lane.Current = null;
        Version++;
        return true;
    }

    // The current actions that have run longer than the safety timeout.
    public IReadOnlyList<OfficeAction> Expired(DateTimeOffset now) => lanes.Values
        .Where(l => l.Current is { StartedAt: { } started } && now - started >= limit)
        .Select(l => l.Current!).ToArray();

    // Puts every running action back at the front of its lane (used when drawing stops).
    public void InterruptAll()
    {
        foreach (var lane in lanes.Values)
            if (lane.Current is { } current)
            {
                lane.Pending.AddFirst(current with { StartedAt = null });
                lane.Current = null;
                Version++;
            }
    }

    public void RemoveSeats(Func<string, bool> remove)
    {
        foreach (var seat in lanes.Keys.Where(remove).ToArray())
        {
            lanes.Remove(seat);
            Version++;
        }
    }

    public void Clear()
    {
        lanes.Clear();
        Version++;
    }

    public IReadOnlyList<OfficeActionRecord> Snapshot()
    {
        var records = new List<OfficeActionRecord>();
        foreach (var (seat, lane) in lanes)
        {
            var position = 0;
            if (lane.Current is { } current) records.Add(new(current.Id, seat, position++, true, current.Transition, current.EnqueuedAt));
            foreach (var action in lane.Pending) records.Add(new(action.Id, seat, position++, false, action.Transition, action.EnqueuedAt));
        }
        return records;
    }

    // Rebuilds the lanes. A saved current action is not running after a restart, so it goes back to the
    // front of its lane and starts again when its (validated) turn comes.
    public void Restore(IEnumerable<OfficeActionRecord> records)
    {
        lanes.Clear();
        foreach (var group in records.GroupBy(r => r.Seat))
        {
            var lane = LaneFor(group.Key);
            foreach (var record in group.OrderBy(r => r.Position).Take(capacity))
                lane.Pending.AddLast(new OfficeAction(record.Id, record.Seat, record.Transition, record.EnqueuedAt));
        }
        Version++;
    }
}
