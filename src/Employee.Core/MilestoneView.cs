namespace Employee.Core;

// Which milestone each step of a large task belongs to, and how each milestone stands, for showing
// the steps grouped by milestone ("milestone 1 does this, with these steps").
public static class MilestoneView
{
    public sealed record Group(int Index, string Title, string Goal, string Status, int Done, int Total);

    // The milestone a step created now belongs to: the first one not accepted yet (the last when all are).
    public static int? Current(WorkSession session) =>
        session.Phases.Count == 0 ? null : AcceptanceCheck.OpenPhase(session) ?? session.Phases.Count - 1;

    // A plan for a large task covers the open milestone only. Steps the planner wrote for a later milestone
    // (its own "phase" number above the open one, or the plan's phase) are left out here and planned when
    // that milestone is the open one, after the tester has accepted the milestones before it. A step that
    // depends on a left-out step is left out too. Steps without a phase stay in the open milestone.
    public static WorkPlan KeepOpenMilestone(WorkSession session, WorkPlan plan)
    {
        if (session.Phases.Count == 0 || plan.Tasks == null)
            return plan;
        var open = AcceptanceCheck.OpenPhase(session) ?? session.Phases.Count - 1;
        // While the open milestone is being fixed after a failed test, a replan is for that milestone itself:
        // its steps are not later milestones, so none are left out.
        if (session.VerifyRounds > 0 && !session.AcceptedScopes.Contains("phase:" + open))
            return plan;
        var openNumber = open + 1;
        bool Later(PlannedTask task) => (task.Phase ?? plan.Phase ?? openNumber) > openNumber;
        var left = new HashSet<string>(plan.Tasks.Where(Later).Select(t => t.Key), StringComparer.Ordinal);
        if (left.Count == 0)
            return plan;
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var task in plan.Tasks)
                if (!left.Contains(task.Key) && (task.DependsOn.Any(left.Contains) || (task.Tool?.InputFrom is { } from && left.Contains(from))))
                {
                    left.Add(task.Key);
                    changed = true;
                }
        }
        // A plan with nothing for the open milestone is kept as it is: it would leave the goal with no steps.
        var kept = plan.Tasks.Where(t => !left.Contains(t.Key)).ToArray();
        return kept.Length == 0 ? plan : plan with { Tasks = kept };
    }

    // Steps carry their milestone; older steps (made before it was recorded) are placed by when their
    // milestone was accepted: a step made before milestone k passed belongs to the first such milestone.
    public static IReadOnlyDictionary<string, int> Assign(WorkSession session, IReadOnlyList<WorkItem> steps, IEnumerable<TaskLog> goalLogs)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (session.Phases.Count == 0)
            return result;
        var passedAt = new DateTimeOffset?[session.Phases.Count];
        foreach (var log in goalLogs.Where(l => l.Event == "AcceptancePassed" && l.Message.StartsWith("milestone ", StringComparison.Ordinal)))
        {
            var number = log.Message["milestone ".Length..].Split('/')[0];
            if (int.TryParse(number, out var k) && k >= 1 && k <= passedAt.Length)
                passedAt[k - 1] = log.At;
        }
        var last = Current(session) ?? 0;
        foreach (var step in steps)
        {
            int index;
            if (step.Milestone is { } recorded)
                index = recorded;
            else if (step.VerifyScope is { } scope && scope.StartsWith("phase:", StringComparison.Ordinal) && int.TryParse(scope[6..], out var tested))
                index = tested;
            else
            {
                index = last;
                for (var k = 0; k < passedAt.Length; k++)
                    if (passedAt[k] is { } at && step.CreatedAt <= at)
                    {
                        index = k;
                        break;
                    }
            }
            result[step.Id] = Math.Clamp(index, 0, session.Phases.Count - 1);
        }
        return result;
    }

    public static IReadOnlyList<Group> Groups(WorkSession session, IReadOnlyList<WorkItem> steps, IReadOnlyDictionary<string, int> assigned)
    {
        var open = AcceptanceCheck.OpenPhase(session);
        return session.Phases.Select((phase, i) =>
        {
            var mine = steps.Where(s => assigned.TryGetValue(s.Id, out var k) && k == i).ToArray();
            var accepted = session.AcceptedScopes.Contains("phase:" + i);
            var status = accepted && session.SignedOffScopes.Contains("phase:" + i) ? "accepted and signed off"
                : accepted && i < session.Phases.Count - 1 && session.SignOffMilestones ? "accepted by the tester, waiting for your sign-off"
                : accepted ? "accepted by the tester"
                : session.VerifyScope == "phase:" + i && session.AcceptanceStalled != null ? "testing stopped: still failing"
                : mine.Length > 0 || open == i ? "in progress"
                : "not started";
            return new Group(i, $"Milestone {i + 1}/{session.Phases.Count} · {phase.Name}", phase.Goal, status,
                mine.Count(s => s.State is WorkState.Completed or WorkState.Skipped), mine.Length);
        }).ToArray();
    }
}
