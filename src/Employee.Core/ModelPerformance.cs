namespace Employee.Core;
public enum RoutingPreference
{
    Economy,
    Balanced,
    Performance
}

// How much usage routing may spend. Auto: lowest expected usage to an accepted result. Low/Medium/
// High/Highest: among models whose rating meets the step's bar, take the lowest, middle, upper or top one.
public enum UsageLevel
{
    Auto,
    Low,
    Medium,
    High,
    Highest
}

public sealed record ModelRun(string Id, string WorkId, string ProviderId, string ModelId, WorkKind Kind, FailureKind Failure, long Milliseconds, bool Coordinator, DateTimeOffset At, TokenUsage? Usage = null, WorkCapability? Capability = null, int? Difficulty = null);
public sealed record ModelPerformance(string ProviderId, string ModelId, WorkKind Kind, int Samples, int Succeeded, int Failed, int Good, int Poor, double AverageMilliseconds, WorkCapability Capability = default)
{
    // Rating points learned for this model and work type (see ModelLearning); filled for display.
    public double LearnedOffset { get; init; }
    public string ModelDisplay => string.IsNullOrEmpty(ModelId) ? "CLI default (exact model unknown)" : ModelId;
    public double SuccessPercent => Samples == 0 ? 0 : 100.0 * Succeeded / Samples;
    public double AverageSeconds => Math.Round(AverageMilliseconds / 1000, 1);
    // Small samples are shrunk toward neutral. Availability failures and cancellations are not quality failures.
    public double Adjustment => Samples < 3 ? 0 : Math.Clamp(24 * ((Succeeded + 2.0) / (Succeeded + Failed + 4) - 0.5) + 40 * ((Good + 1.0) / (Good + Poor + 2) - 0.5), -30, 30);
}

public interface IModelPerformanceStore
{
    void SaveModelRun(ModelRun run);
    IReadOnlyList<ModelPerformance> ModelPerformance();
    bool RateModelResult(string workId, bool good);
    // Approving a finished task counts as a good result for its successful runs the user has not rated.
    void RecordApproval(IEnumerable<string> workIds);
    // Sending an unapproved result back (change request, rerun) counts as a result that missed.
    void RecordRework(IEnumerable<string> workIds);
    // Individual judged results (ratings, approvals, send-backs, failed runs) with their difficulty.
    IReadOnlyList<ModelOutcome> ModelOutcomes();
}

public enum OutcomeSource
{
    Rating,
    Approval,
    Rework,
    Failure
}

public sealed record ModelOutcome(string ProviderId, string ModelId, WorkCapability Capability, int Difficulty, bool Accepted, OutcomeSource Source, DateTimeOffset At);
// Learns how far a model's real results sit above or below its rating, per work type. Each result
// is compared with what the rating predicted at that result's own difficulty, so easy wins add
// almost nothing and hard wins add a lot; misses on easy work cost a lot, misses on hard work little.
// Old results fade (half weight after 30 days), approvals and failed runs weigh half of an explicit
// rating or send-back, and the offset never exceeds ±12 rating points.
public static class ModelLearning
{
    public const double MaxOffset = 12;
    public const double HalfLifeDays = 30;
    private const double PseudoCount = 3, Scale = 25;
    public static double Weight(OutcomeSource source) => source is OutcomeSource.Approval or OutcomeSource.Failure ? 0.5 : 1;
    // Chance a result is accepted when the model's quality is this far from the step's bar.
    public static double Acceptance(double quality, int bar) => Math.Clamp(1 / (1 + Math.Exp(-(quality - bar) / 5.0)), 0.05, 0.95);
    public static double Offset(IEnumerable<ModelOutcome> outcomes, int rated, DateTimeOffset now, DateTimeOffset? since = null)
    {
        double residual = 0, total = 0;
        foreach (var outcome in outcomes)
        {
            if (since is { } start && outcome.At < start)
                continue;
            var age = Math.Max(0, (now - outcome.At).TotalDays);
            var weight = Weight(outcome.Source) * Math.Pow(0.5, age / HalfLifeDays);
            var expected = Acceptance(rated, ModelRatings.RequiredQuality(outcome.Difficulty, RoutingPreference.Balanced));
            residual += weight * ((outcome.Accepted ? 1 : 0) - expected);
            total += weight;
        }

        return Math.Clamp(Scale * residual / (total + PseudoCount), -MaxOffset, MaxOffset);
    }
}
