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
