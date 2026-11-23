using System.Text.Json;
using System.Text.RegularExpressions;
using Employee.Core;

namespace Employee.Infrastructure;
public static class CliModelCatalog
{
    public static IReadOnlyList<ModelInfo> CommandCodeModels(string output)
    {
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(line => Regex.Replace(line, @"\x1b\[[0-9;]*m", "").Trim().TrimStart('•', '*', '-').Trim()).Where(id => Regex.IsMatch(id, @"^[a-zA-Z0-9][a-zA-Z0-9._:/-]{1,159}$") && (id.Contains('-') || id.Contains('/'))).Distinct(StringComparer.Ordinal).Select(id => new ModelInfo(id, Enum.GetValues<WorkKind>(), DiscoverySource: "CommandCode CLI catalog; access unverified")).ToArray();
    }

    private static string Plain(string line) => Regex.Replace(line, @"\x1b\[[0-9;]*m", "").TrimEnd();

    public static IReadOnlyList<ModelInfo> KiloModels(string output)
    {
        return output.Split('\n').Select(Plain).Select(line => line.Trim()).Where(id => id.Length > 1 && id.Contains('/') && !id.Any(char.IsWhiteSpace) && Regex.IsMatch(id, @"^[A-Za-z0-9][A-Za-z0-9._:~/+-]{1,159}$")).Distinct(StringComparer.Ordinal).Select(id => new ModelInfo(id, Enum.GetValues<WorkKind>(), DiscoverySource: "Kilo CLI catalog; gateway account access unverified")).ToArray();
    }

    public static long ClixadCredits(string wallet)
    {
        var match = Regex.Match(Plain(wallet), @"(?im)^\s*credits:\s*(?<n>[\d,]+)");
        return match.Success && long.TryParse(match.Groups["n"].Value.Replace(",", ""), out var credits) ? credits : 0;
    }

    // clixad prices every model and labels the free daily allowance per model. Locked premium
    // models, and budget models with no free turns left on a zero-credit balance, cannot run now.
    public static IReadOnlyList<ModelInfo> ClixadModels(string output, long credits, out int hidden)
    {
        var kept = new List<ModelInfo>();
        hidden = 0;
        foreach (var raw in output.Split('\n'))
        {
            var line = Plain(raw).Trim();
            var match = Regex.Match(line, @"^(?<id>[a-z0-9][a-z0-9._-]{1,79})\s{2,}(?<tier>budget|locked|premium)\b");
            if (!match.Success)
                continue;
            var free = Regex.IsMatch(line, @"\[free:\s*~\d+\s+turns?\s+left today\]");
            if (match.Groups["tier"].Value == "locked" || line.Contains("not enough", StringComparison.OrdinalIgnoreCase) || (!free && credits <= 0))
            {
                hidden++;
                continue;
            }

            kept.Add(new ModelInfo(match.Groups["id"].Value, Enum.GetValues<WorkKind>(), DiscoverySource: free ? "clixad: free turns left today" : "clixad: paid from credits"));
        }

        return kept;
    }

    public static bool DevinFreePlan(string authStatus) => Regex.IsMatch(authStatus, @"(?im)^\s*(?:Tier|Plan):\s*[^\r\n]*\bFree\b");

    // A Devin Free account can run only the models its listing tags Free; paid models need credits.
    public static IReadOnlyList<ModelInfo> DevinModels(string output, bool freeAccount, out int hidden)
    {
        var kept = new List<ModelInfo>();
        hidden = 0;
        foreach (var raw in output.Split('\n'))
        {
            var line = Plain(raw).Trim();
            var match = Regex.Match(line, @"^(?<id>[a-z0-9][a-z0-9._-]{1,79})\s{2,}\S");
            if (!match.Success)
                continue;
            var free = !line.Contains('$') && line.EndsWith("Free]", StringComparison.Ordinal);
            if (freeAccount && !free)
            {
                hidden++;
                continue;
            }

            kept.Add(new ModelInfo(match.Groups["id"].Value, Enum.GetValues<WorkKind>(), DiscoverySource: free ? "devin: free model" : "devin: paid from credits"));
        }

        return kept;
    }

    public static IReadOnlyList<ModelInfo> HelpModels(string provider, string help)
    {
        var section = Regex.Match(help, @"(?ms)^[ \t]+(?:-m,[ \t]*)?--model\b(?<body>.*?)(?=^[ \t]+-[A-Za-z-]|\z)").Groups["body"].Value;
        if (section.Length > 6000)
            return[];
        if (provider == "copilot")
        {
            var choices = Regex.Match(section, @"(?is)choices:\s*(?<values>.*?)\)");
            if (!choices.Success)
                return[];
            section = choices.Groups["values"].Value;
        }

        if (provider is not ("claude" or "copilot"))
            return[];
        return Regex.Matches(section, "['\"](?<id>[a-zA-Z][a-zA-Z0-9._/-]{1,120})['\"]").Select(m => m.Groups["id"].Value).Distinct(StringComparer.Ordinal).Select(id => new ModelInfo(id, Enum.GetValues<WorkKind>(), DiscoverySource: provider == "claude" ? "Installed CLI help alias; account access unverified" : "Installed CLI help choice; account access unverified")).ToArray();
    }

    public static IReadOnlyList<ModelInfo> FreebuffSettings(out string detail, string? settingsPath = null)
    {
        var file = settingsPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "manicode", "settings.json");
        detail = "No model recorded in local Freebuff settings, and the CLI has no noninteractive model-list command. Open Freebuff, pick a model, then detect again, or add an exact ID manually.";
        try
        {
            if (!File.Exists(file) || new FileInfo(file).Length > 200_000)
                return[];
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var root = document.RootElement;
            var selected = root.TryGetProperty("freebuffModel", out var model) && model.ValueKind == JsonValueKind.String ? model.GetString() : null;
            var fallback = root.TryGetProperty("freebuffModelDefaultMigration", out var migrated) && migrated.ValueKind == JsonValueKind.String ? migrated.GetString() : null;
            var id = !string.IsNullOrWhiteSpace(selected) ? selected : fallback;
            if (string.IsNullOrWhiteSpace(id) || !Regex.IsMatch(id, @"^[A-Za-z0-9][A-Za-z0-9._/-]{0,159}$"))
                return[];
            var recorded = string.IsNullOrWhiteSpace(selected) ? "default" : "selected";
            detail = $"1 {recorded} model recorded by local Freebuff CLI settings; account access is unverified, the CLI lists no models noninteractively and execution remains blocked.";
            return[new(id, Enum.GetValues<WorkKind>(), DiscoverySource: $"Local Freebuff CLI settings {recorded} model; execution blocked")];
        }
        catch (Exception e)when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            detail = "Freebuff settings could not be read: " + e.Message;
            return[];
        }
    }

    public static IReadOnlyList<ModelInfo> CodexCache(out string detail)
    {
        var home = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(home))
            home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        var file = Path.Combine(home, "models_cache.json");
        detail = "No local Codex model cache. Use CLI default or add an exact ID manually; no paid API probe.";
        try
        {
            if (!File.Exists(file) || new FileInfo(file).Length > 8_000_000)
                return[];
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var root = document.RootElement;
            if (!root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
                return[];
            var date = root.TryGetProperty("fetched_at", out var fetched) ? fetched.ToString() : "Unknown";
            var found = models.EnumerateArray().Where(m => m.ValueKind == JsonValueKind.Object && m.TryGetProperty("slug", out var slug) && slug.ValueKind == JsonValueKind.String && (!m.TryGetProperty("visibility", out var visibility) || visibility.GetString() == "list")).Select(m => m.GetProperty("slug").GetString() ?? "").Where(id => Regex.IsMatch(id, @"^[A-Za-z0-9][A-Za-z0-9._/-]{0,159}$")).Distinct().Take(200).Select(id => new ModelInfo(id, Enum.GetValues<WorkKind>(), DiscoverySource: "Local Codex cache (" + date + "); account access unverified")).ToArray();
            detail = $"{found.Length} model IDs from local CLI cache, fetched {date}. Cache may be stale or belong to a different CLI login; no live account availability claim.";
            return found;
        }
        catch (Exception e)when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            detail = "Codex model cache could not be read: " + e.Message;
            return[];
        }
    }
}
