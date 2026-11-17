namespace Employee.Core;
public enum IntentExampleSource
{
    ReviewedCompletion = 1,
    ManualCreation = 2,
    UserCorrection = 3
}

public sealed record IntentExample(string Id, string ProjectId, string Request, WorkKind Kind, AccessLevel Access, IntentExampleSource Source, string WorkId, DateTimeOffset At);
public sealed record LearnedIntent(WorkKind Kind, AccessLevel Access, string Reason);
public interface IIntentLearningStore
{
    IReadOnlyList<IntentExample> IntentExamples(string projectId);
    void LearnIntent(IntentExample example);
    void ForgetIntent(string id);
}

public static class IntentLearning
{
    public static IReadOnlyList<IntentExample> Hints(string request, IReadOnlyList<IntentExample> examples)
    {
        var words = TaskIntent.Normalize(request).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        return examples.Where(e => e.Request.Length <= 400 && TaskIntent.IsReadOnly(e.Request) == TaskIntent.IsReadOnly(request)).Select(e =>
        {
            var tokens = TaskIntent.Normalize(e.Request).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
            return new
            {
                Example = e,
                Score = 2.0 * words.Intersect(tokens).Count() / Math.Max(1, words.Count + tokens.Count)
            };
        }).Where(e => e.Score >= 0.45).OrderByDescending(e => e.Score).ThenByDescending(e => e.Example.Source).Take(3).Select(e => e.Example).ToArray();
    }

    public static LearnedIntent? Match(string request, IReadOnlyList<IntentExample> examples, bool exactOnly = false)
    {
        var normalized = TaskIntent.Normalize(request);
        var safe = examples.Where(e => Enum.IsDefined(e.Kind) && e.Kind != WorkKind.Planning && Enum.IsDefined(e.Access) && (!TaskIntent.IsReadOnly(request) || e.Access != AccessLevel.WriteProject)).ToArray();
        var exact = safe.Where(e => TaskIntent.Normalize(e.Request) == normalized).OrderByDescending(e => e.Source).ThenByDescending(e => e.At).FirstOrDefault();
        if (exact != null)
            return new(exact.Kind, exact.Access, "Confirmed exact example: " + exact.Id + " / " + exact.Source);
        if (exactOnly)
            return null;
        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        if (words.Count < 6)
            return null;
        static string Signature(string value) => string.Join(" ", TaskIntent.Normalize(value).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Any(char.IsDigit) || new[] { "khong", "chua", "dung", "not", "no", "never", "only", "chi" }.Contains(w)));
        var candidates = safe.Where(e => Signature(e.Request) == Signature(request) && TaskIntent.IsReadOnly(e.Request) == TaskIntent.IsReadOnly(request) && TaskIntent.IsTextDeliverable(e.Request) == TaskIntent.IsTextDeliverable(request)).Select(e =>
        {
            var tokens = TaskIntent.Normalize(e.Request).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
            var score = 2.0 * words.Intersect(tokens).Count() / Math.Max(1, words.Count + tokens.Count);
            return new
            {
                Example = e,
                Score = score
            };
        }).Where(x => x.Score >= 0.9).ToArray();
        if (candidates.Length < 2)
            return null;
        var groups = candidates.GroupBy(x => (x.Example.Kind, x.Example.Access)).ToArray();
        if (groups.Length != 1 || candidates.Select(x => TaskIntent.Normalize(x.Example.Request)).Distinct().Count() < 2 || candidates.Select(x => x.Example.WorkId).Distinct().Count() < 2)
            return null;
        var label = groups[0].Key;
        return new(label.Kind, label.Access, "Consensus of " + candidates.Length + " confirmed project examples; token overlap >= 0.90. Heuristic, not a probability.");
    }
}
