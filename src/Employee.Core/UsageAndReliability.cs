namespace Employee.Core;
public sealed record TokenUsage(long? Input, long? Output, long? CachedInput = null);
public sealed record UsageSummary(string ProviderId, int Runs, int ReportedRuns, long? InputTokens, long? OutputTokens, long? CachedInputTokens);
public sealed record ModelReliability(string ModelId, WorkKind Kind, int Samples, int Invalid, DateTimeOffset LastAt);
public sealed record AdvancedEvaluation(string Id, string ModelId, string Category, int Passed, int Total, long Milliseconds, long? MemoryDeltaMb, DateTimeOffset At);
public interface IAdvancedModelStore
{
    IReadOnlyList<UsageSummary> UsageSince(DateTimeOffset since);
    IReadOnlyList<ModelReliability> Reliability();
    void MarkAssignmentInvalid(string workId);
    void SaveAdvancedEvaluation(AdvancedEvaluation value);
    IReadOnlyList<AdvancedEvaluation> AdvancedEvaluations();
}
