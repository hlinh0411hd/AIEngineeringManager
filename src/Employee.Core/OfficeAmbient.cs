namespace Employee.Core;

/// <summary>Where an office worker is in a random ambient activity. Pure presentation state, never persisted.</summary>
public enum AmbientState { AtDesk, MovingTo, ReportingToManager, VisitingColleague, HallwayStroll, Returning }

public enum AmbientKind { ReportToManager, VisitColleague, HallwayStroll }

/// <summary>Tunable constants of the ambient office activity.</summary>
public sealed record AmbientOptions
{
    /// <summary>The animation timer period; tick based values below are in these.</summary>
    public const int TickMilliseconds = 33;
    /// <summary>MaxAway can be raised to at most this many people.</summary>
    public const int AbsoluteMaxAway = 4;

    public bool Enabled { get; init; } = true;
    /// <summary>People away from their desk at once (office wide, task movements included). 3 by default, 4 at most.</summary>
    public int MaxAway { get; init; } = 3;
    /// <summary>People that must stay at their desk in every department.</summary>
    public int MinPresentPerZone { get; init; } = 1;
    /// <summary>How often the scheduler looks for a new activity.</summary>
    public int DecisionIntervalTicks { get; init; } = 45;
    /// <summary>Chance that a look starts an activity.</summary>
    public double ChancePerDecision { get; init; } = 0.25;
    public double ReportWeight { get; init; } = 3;
    public double VisitWeight { get; init; } = 4;
    public double HallwayWeight { get; init; } = 3;
    /// <summary>Factor on the chance to visit a desk of one's own department.</summary>
    public double SameRoomVisitWeight { get; init; } = 0.3;
    public int ActorCooldownMinSeconds { get; init; } = 20;
    public int ActorCooldownMaxSeconds { get; init; } = 45;
    public int GlobalCooldownSeconds { get; init; } = 3;
    public int PairCooldownSeconds { get; init; } = 60;
    public int ReportDwellMinTicks { get; init; } = 90;
    public int ReportDwellMaxTicks { get; init; } = 150;
    public int VisitDwellMinTicks { get; init; } = 120;
    public int VisitDwellMaxTicks { get; init; } = 240;
    public int HallwayWaypointMin { get; init; } = 2;
    public int HallwayWaypointMax { get; init; } = 3;
    public int HallwayDwellMinTicks { get; init; } = 15;
    public int HallwayDwellMaxTicks { get; init; } = 45;
    /// <summary>An activity that lasts longer than this is cut short and the worker is sent back.</summary>
    public int MaxActivitySeconds { get; init; } = 25;

    /// <summary>The options with every value brought into a safe range.</summary>
    public AmbientOptions Validate()
    {
        static int Order(int low, int high) => Math.Max(low, high);
        var report = Math.Max(0, ReportDwellMinTicks);
        var visit = Math.Max(0, VisitDwellMinTicks);
        var hall = Math.Max(0, HallwayDwellMinTicks);
        var cool = Math.Max(0, ActorCooldownMinSeconds);
        var way = Math.Max(1, HallwayWaypointMin);
        return this with
        {
            MaxAway = Math.Clamp(MaxAway, 0, AbsoluteMaxAway),
            MinPresentPerZone = Math.Max(1, MinPresentPerZone),
            DecisionIntervalTicks = Math.Max(1, DecisionIntervalTicks),
            ChancePerDecision = Math.Clamp(ChancePerDecision, 0, 1),
            ReportWeight = Math.Max(0, ReportWeight), VisitWeight = Math.Max(0, VisitWeight), HallwayWeight = Math.Max(0, HallwayWeight),
            SameRoomVisitWeight = Math.Clamp(SameRoomVisitWeight, 0, 1),
            ActorCooldownMinSeconds = cool, ActorCooldownMaxSeconds = Order(cool, ActorCooldownMaxSeconds),
            GlobalCooldownSeconds = Math.Max(0, GlobalCooldownSeconds), PairCooldownSeconds = Math.Max(0, PairCooldownSeconds),
            ReportDwellMinTicks = report, ReportDwellMaxTicks = Order(report, ReportDwellMaxTicks),
            VisitDwellMinTicks = visit, VisitDwellMaxTicks = Order(visit, VisitDwellMaxTicks),
            HallwayWaypointMin = way, HallwayWaypointMax = Order(way, HallwayWaypointMax),
            HallwayDwellMinTicks = hall, HallwayDwellMaxTicks = Order(hall, HallwayDwellMaxTicks),
            MaxActivitySeconds = Math.Max(5, MaxActivitySeconds)
        };
    }
}

/// <summary>A worker as the scheduler sees them.</summary>
/// <param name="Present">At the own desk (not walking, not on an ambient activity).</param>
/// <param name="Busy">Holds work, has a queued or running office action, takes part in a handoff, or is moving.</param>
/// <param name="Hosting">Has a colleague standing at the desk right now.</param>
/// <param name="HoldsWork">Holds a work item, so the worker cannot take another task even while seated.</param>
/// <param name="WaitingWork">Belongs to a department that has work waiting for a free desk worker.</param>
public readonly record struct AmbientSeat(string Key, string Zone, bool IsManager, bool Present, bool Busy, bool Hosting, bool HoldsWork = false, bool WaitingWork = false);

public sealed record AmbientPlan(AmbientKind Kind, string ActorKey, string? TargetKey, int DwellTicks, int[] WaypointIndexes);

/// <summary>The invariants of the ambient activity, checked before somebody is picked to leave the desk.</summary>
public static class OfficeAmbientRules
{
    public static int AwayCount(IReadOnlyList<AmbientSeat> seats) => seats.Count(s => !s.Present);
    public static int PresentCount(string zone, IReadOnlyList<AmbientSeat> seats) => seats.Count(s => s.Present && s.Zone == zone);

    /// <summary>Seated workers in a department who can take a new task (they do not already hold one).</summary>
    public static int FreePresentCount(string zone, IReadOnlyList<AmbientSeat> seats) =>
        seats.Count(s => s.Present && !s.HoldsWork && s.Zone == zone);

    /// <summary>Free seated workers left in the mover's department after that worker leaves.</summary>
    public static int FreePresentAfterLeaving(string moverKey, IReadOnlyList<AmbientSeat> seats)
    {
        var found = false;
        var mover = default(AmbientSeat);
        foreach (var seat in seats)
            if (seat.Key == moverKey) { mover = seat; found = true; break; }
        if (!found) return -1;
        var free = FreePresentCount(mover.Zone, seats);
        return mover.Present && !mover.HoldsWork ? free - 1 : free;
    }

    /// <summary>Whether the department can spare the mover and still keep MinPresentPerZone free workers.</summary>
    public static bool RoomCanSpare(string moverKey, IReadOnlyList<AmbientSeat> seats, AmbientOptions options) =>
        FreePresentAfterLeaving(moverKey, seats) >= options.MinPresentPerZone;

    /// <summary>Whether another worker may leave without exceeding the office-wide away cap.</summary>
    public static bool AwayBelowCap(IReadOnlyList<AmbientSeat> seats, AmbientOptions options) =>
        AwayCount(seats) < options.MaxAway;

    /// <summary>
    /// Whether the worker may leave the desk: never a manager, never somebody busy or hosting, fewer than MaxAway
    /// people away already, no waiting work in the department, and at least MinPresentPerZone free workers left.
    /// </summary>
    public static bool CanLeave(AmbientSeat seat, IReadOnlyList<AmbientSeat> all, AmbientOptions options) =>
        options.Enabled && !seat.IsManager && seat.Present && !seat.Busy && !seat.Hosting && !seat.HoldsWork && !seat.WaitingWork
        && AwayBelowCap(all, options)
        && FreePresentAfterLeaving(seat.Key, all) >= options.MinPresentPerZone;

    /// <summary>A desk a visitor may stand behind: not the manager's, not their own seat, and not taken already.</summary>
    public static bool IsValidHost(AmbientSeat visitor, AmbientSeat host) =>
        host.Key != visitor.Key && !host.IsManager && host.Zone != "Manager" && host.Present && !host.Busy && !host.Hosting;
}

/// <summary>Picks, at random and within the rules, who leaves the desk and for what. Holds only cooldowns.</summary>
public sealed class AmbientScheduler(AmbientOptions? options = null, Random? random = null)
{
    private readonly AmbientOptions o = (options ?? new AmbientOptions()).Validate();
    private readonly Random rng = random ?? new Random();
    private readonly Dictionary<string, DateTimeOffset> actorCooldown = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Visitor, string Host), DateTimeOffset> pairCooldown = [];
    private DateTimeOffset nextDecision, nextStart;

    public AmbientOptions Options => o;

    public bool OnCooldown(string actorKey, DateTimeOffset now) => actorCooldown.TryGetValue(actorKey, out var until) && now < until;

    /// <summary>Called when a worker is back at the desk: starts that worker's cooldown.</summary>
    public void NotifyReturned(string actorKey, DateTimeOffset now) =>
        actorCooldown[actorKey] = now.AddSeconds(rng.Next(o.ActorCooldownMinSeconds, o.ActorCooldownMaxSeconds + 1));

    public void Reset() { actorCooldown.Clear(); pairCooldown.Clear(); nextDecision = nextStart = default; }

    /// <summary>One look at the office. Returns a plan to carry out, or null (nothing to do right now).</summary>
    public AmbientPlan? TryPlan(DateTimeOffset now, IReadOnlyList<AmbientSeat> seats, int freeReportSpots, int waypointCount)
    {
        if (!o.Enabled || now < nextDecision) return null;
        nextDecision = now.AddMilliseconds(o.DecisionIntervalTicks * AmbientOptions.TickMilliseconds);
        if (now < nextStart || AwayCount(seats) >= o.MaxAway || rng.NextDouble() >= o.ChancePerDecision) return null;
        var candidates = seats.Where(s => !OnCooldown(s.Key, now) && OfficeAmbientRules.CanLeave(s, seats, o)).ToArray();
        if (candidates.Length == 0) return null;
        var actor = candidates[rng.Next(candidates.Length)];

        var hosts = seats.Where(h => OfficeAmbientRules.IsValidHost(actor, h) && !(pairCooldown.TryGetValue((actor.Key, h.Key), out var until) && now < until)).ToArray();
        var kinds = new List<(AmbientKind Kind, double Weight)>();
        if (freeReportSpots > 0 && o.ReportWeight > 0) kinds.Add((AmbientKind.ReportToManager, o.ReportWeight));
        if (hosts.Length > 0 && o.VisitWeight > 0) kinds.Add((AmbientKind.VisitColleague, o.VisitWeight));
        if (waypointCount > 0 && o.HallwayWeight > 0) kinds.Add((AmbientKind.HallwayStroll, o.HallwayWeight));
        if (kinds.Count == 0) return null;

        var kind = Pick(kinds, k => k.Weight).Kind;
        AmbientPlan plan;
        switch (kind)
        {
            case AmbientKind.ReportToManager:
                plan = new(kind, actor.Key, null, rng.Next(o.ReportDwellMinTicks, o.ReportDwellMaxTicks + 1), []);
                break;
            case AmbientKind.VisitColleague:
                var host = Pick(hosts, h => h.Zone == actor.Zone ? o.SameRoomVisitWeight : 1);
                pairCooldown[(actor.Key, host.Key)] = now.AddSeconds(o.PairCooldownSeconds);
                plan = new(kind, actor.Key, host.Key, rng.Next(o.VisitDwellMinTicks, o.VisitDwellMaxTicks + 1), []);
                break;
            default:
                var count = Math.Min(waypointCount, rng.Next(o.HallwayWaypointMin, o.HallwayWaypointMax + 1));
                var indexes = Enumerable.Range(0, waypointCount).OrderBy(_ => rng.Next()).Take(count).ToArray();
                plan = new(kind, actor.Key, null, rng.Next(o.HallwayDwellMinTicks, o.HallwayDwellMaxTicks + 1), indexes);
                break;
        }
        nextStart = now.AddSeconds(o.GlobalCooldownSeconds);
        return plan;
    }

    private static int AwayCount(IReadOnlyList<AmbientSeat> seats) => OfficeAmbientRules.AwayCount(seats);

    private T Pick<T>(IReadOnlyList<T> items, Func<T, double> weight)
    {
        var total = items.Sum(weight);
        if (total <= 0) return items[rng.Next(items.Count)];
        var roll = rng.NextDouble() * total;
        foreach (var item in items)
            if ((roll -= weight(item)) < 0) return item;
        return items[^1];
    }
}
