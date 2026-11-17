namespace Employee.Core;
public static class WorkComplexity
{
    private static readonly string[] HeavySignals = Vocabulary.HeavyWork;
    private static readonly string[] LightSignals = Vocabulary.LightWork;
    // Raises difficulty without implying a whole subsystem: reasoning-heavy or risky work.
    private static readonly string[] HarderSignals = Vocabulary.HarderWork;
    public const int LightBelow = 35;
    public const int HeavyFrom = 70;
    // 0-100 difficulty of one step. The planner's per-step rating is used when present;
    // otherwise a rule-based estimate. An explicit user effort always bounds the result.
    public static int Score(WorkItem work)
    {
        var request = " " + TaskIntent.Normalize(work.Request) + " ";
        bool Has(string[] signals) => signals.Any(signal => request.Contains(" " + signal + " ", StringComparison.Ordinal));
        int score;
        if (work.Difficulty is { } rated)
            score = rated;
        else
        {
            score = work.Kind switch
            {
                WorkKind.Classification or WorkKind.Extraction => 15,
                WorkKind.Content => 35,
                WorkKind.Research => 40,
                WorkKind.Review => 45,
                WorkKind.Planning => 75,
                _ => 50
            };
            var words = request.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            score += Math.Min(12, words / 40);
            score += Math.Min(15, 5 * HarderSignals.Count(signal => request.Contains(" " + signal + " ", StringComparison.Ordinal)));
            if (work.Access == AccessLevel.WriteProject)
                score += 5;
            score += Math.Min(6, 2 * work.DependsOn.Count);
            if (work.Attachments.Count > 0)
                score += 4;
            if (Has(HeavySignals))
                score = Math.Max(score, 78);
            else if (Has(LightSignals))
                score = Math.Min(score, 25);
        }

        if (work.Kind == WorkKind.Planning)
            score = Math.Max(score, 75);
        score = work.Effort switch
        {
            WorkEffort.Light => Math.Min(score, LightBelow - 1),
            WorkEffort.Standard => Math.Clamp(score, LightBelow, HeavyFrom - 1),
            WorkEffort.Heavy => Math.Max(score, HeavyFrom + 5),
            _ => score
        };
        return Math.Clamp(score, 0, 100);
    }

    public static WorkEffort Estimate(WorkItem work)
    {
        var score = Score(work);
        return score < LightBelow ? WorkEffort.Light : score >= HeavyFrom ? WorkEffort.Heavy : WorkEffort.Standard;
    }
}
