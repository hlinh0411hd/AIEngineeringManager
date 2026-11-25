namespace Employee.Core;

// A comment can say which AI to use: "sử dụng antigravity thay cho codex". The AI names are matched
// loosely (a typo of a letter or two still counts), since comments are typed fast.
public static class ProviderRequest
{
    // The reason stored on a route the user asked for, so the planner and the fallback can tell it
    // apart from a route the planner or the router chose.
    public const string ReasonPrefix = "User asked to use ";

    private static readonly string[] UseVerbs = ["dung", "xai", "use", "using", "chuyen", "switch", "sudung", "bang", "qua"];
    private static readonly string[] Negations = ["khong", "chua", "not", "never", "dont", "don", "t"];

    // Returns the AI the comment asks to use, and the one it asks to avoid ("instead of ...").
    public static (string? Prefer, string? Avoid) From(string text, IEnumerable<string> providerIds)
    {
        var ids = providerIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => TaskIntent.Normalize(id).Replace(" ", "")).Where(id => id.Length > 0).Distinct().ToArray();
        string? prefer = null, avoid = null;
        if (ids.Length == 0 || string.IsNullOrWhiteSpace(text))
            return (null, null);
        foreach (var clause in System.Text.RegularExpressions.Regex.Split(text, @"[\r\n.!?;]+"))
        {
            var words = TaskIntent.Normalize(clause).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // "thay cho", "thay vì", "instead of", "rather than": what follows is the AI to avoid.
            var pivot = -1;
            for (var i = 0; i < words.Length && pivot < 0; i++)
                if (words[i] is "thay" && i + 1 < words.Length && words[i + 1] is "cho" or "vi" || words[i] is "instead" or "rather" && i + 1 < words.Length && words[i + 1] is "of" or "than")
                    pivot = i;
            for (var j = 0; j < words.Length; j++)
            {
                var provider = ids.FirstOrDefault(id => Matches(words[j], id));
                if (provider == null)
                    continue;
                var useBefore = -1;
                for (var k = j - 1; k >= 0 && useBefore < 0; k--)
                    if (UseVerbs.Contains(words[k]))
                        useBefore = k;
                var positiveUse = useBefore >= 0 && !IsNegated(words, useBefore);
                if (pivot >= 0 && j > pivot || pivot < 0 && IsNegated(words, j))
                    avoid ??= provider;
                else if (positiveUse && (pivot < 0 || j < pivot || useBefore < pivot))
                    prefer ??= provider;
            }
        }
        return (prefer, avoid);
    }

    // Quick checks: the exact name, or a name one or two letters off for a longer name.
    internal static bool Matches(string word, string id)
    {
        if (word == id)
            return true;
        if (id.Length < 5 || word.Length < 4)
            return false;
        var allowed = id.Length >= 9 ? 2 : 1;
        return Distance(word, id) <= allowed;
    }

    private static bool IsNegated(string[] words, int index)
    {
        for (var k = Math.Max(0, index - 2); k < index; k++)
            if (Negations.Contains(words[k]))
                return true;
        return false;
    }

    private static int Distance(string a, string b)
    {
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var current = new int[b.Length + 1];
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            previous = current;
        }
        return previous[b.Length];
    }
}
