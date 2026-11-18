namespace Employee.Core;

// A short, rule-based "what was done, what was not, and why" for a task or milestone. It is built from the
// recorded step states and reasons only (no AI call), so it is free, instant and the same everywhere:
// the comment tab, the Telegram notice and /status.
public static class TaskSummary
{
    private const int MaxNames = 3;

    // Marks the text of a stored summary comment so it can be recognised without a separate field.
    public const string Heading = "Summary";

    public static string Build(WorkItem goal, IReadOnlyList<WorkItem> steps, WorkSession? session, IEnumerable<TaskLog> goalLogs, bool includeLastResult = false)
    {
        var lines = new List<string> { Headline(goal) };
        var work = steps.Where(s => s.VerifyScope == null && !s.IsCoordinator).ToList();
        string? milestone = null;
        if (session is { Phases.Count: > 0 })
        {
            var assigned = MilestoneView.Assign(session, steps, goalLogs);
            var groups = MilestoneView.Groups(session, steps, assigned);
            var shown = groups.FirstOrDefault(g => g.Status.Contains("waiting for your sign-off"))
                ?? groups.FirstOrDefault(g => !g.Status.StartsWith("accepted", StringComparison.Ordinal))
                ?? groups[^1];
            milestone = $"Milestone {shown.Index + 1}/{groups.Count} \"{Clip(session.Phases[shown.Index].Name, 50)}\": {shown.Status}";
            work = work.Where(s => assigned.TryGetValue(s.Id, out var k) && k == shown.Index).ToList();
            var accepted = groups.Count(g => g.Status.StartsWith("accepted", StringComparison.Ordinal));
            milestone += $" ({accepted}/{groups.Count} milestones accepted overall)";
        }

        if (milestone != null)
            lines.Add(milestone);
        if (work.Count > 0)
        {
            var done = work.Where(s => s.State == WorkState.Completed).ToList();
            var skipped = work.Count(s => s.State == WorkState.Skipped);
            var open = work.Where(s => s.State is not (WorkState.Completed or WorkState.Skipped)).ToList();
            lines.Add($"Done ({done.Count}/{work.Count - skipped}): " + (done.Count == 0 ? "nothing finished yet" : Names(done.Select(s => Clip(FirstLine(s.Request), 60)))));
            if (open.Count > 0)
                lines.Add($"Not done ({open.Count}): " + Names(open.Select(s => Clip(FirstLine(s.Request), 50) + " [" + Why(s) + "]")));
            if (skipped > 0)
                lines.Add($"Skipped (not needed): {skipped}");
        }
        else if (goal.State is WorkState.NeedsReview or WorkState.Completed)
            lines.Add("Done: the request was run as a single step.");
        else
            lines.Add("Done: nothing finished yet.");

        var reason = goal.ProblemReason;
        if (string.IsNullOrWhiteSpace(reason) && goal.State is WorkState.Cancelled or WorkState.Incomplete or WorkState.NotMet or WorkState.Completed)
            reason = goal.Notes;
        if (!string.IsNullOrWhiteSpace(reason))
            lines.Add("Why it stopped: " + Clip(OneLine(reason), 200));
        if (goal.Verification != VerificationState.Untested)
            lines.Add("Tests: " + goal.Verification);
        if (includeLastResult && goal.Comments.LastOrDefault(c => c.IsAssistant && !c.IsSummary) is { } last)
            lines.Add("AI result: " + Clip(OneLine(StripStatus(last.Text)), 250));
        return string.Join("\n", lines);
    }

    private static string Headline(WorkItem goal) => goal.State switch
    {
        WorkState.NeedsReview => "Finished its run; waiting for your review.",
        WorkState.Completed => "Completed.",
        WorkState.Failed => "Failed.",
        WorkState.Paused => "Paused before finishing.",
        WorkState.AwaitingApproval => "Waiting for your approval before it continues.",
        WorkState.WaitingForPermission => "Waiting for a permission.",
        WorkState.WaitingForProvider => "Waiting for an AI to become available.",
        WorkState.WrongAccess => "Blocked: the task access is wrong.",
        WorkState.WrongProject => "Blocked: the project is wrong.",
        WorkState.Running => "Still running.",
        WorkState.Queued => "Queued.",
        WorkState.Cancelled or WorkState.Incomplete or WorkState.NotMet => "Closed by you: " + TaskOutcomes.Label(goal.State) + ".",
        _ => goal.State + "."
    };

    // Short reason a step is not finished.
    private static string Why(WorkItem step)
    {
        var text = !string.IsNullOrWhiteSpace(step.ProblemReason) ? step.ProblemReason : step.Notes;
        text = Clip(OneLine(text), 90);
        return text.Length == 0 ? step.State.ToString() : step.State + ": " + text;
    }

    private static string Names(IEnumerable<string> names)
    {
        var all = names.ToList();
        var text = string.Join("; ", all.Take(MaxNames));
        return all.Count > MaxNames ? text + $" (+{all.Count - MaxNames} more)" : text;
    }

    private static string StripStatus(string text)
    {
        var at = text.LastIndexOf("\n\nStatus:", StringComparison.Ordinal);
        return at > 0 ? text[..at] : text;
    }

    private static string FirstLine(string text) => OneLine(text.Split('\n', 2)[0]);
    private static string OneLine(string? text) => System.Text.RegularExpressions.Regex.Replace(text ?? "", @"\s+", " ").Trim();
    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";
}
