using System.Text.Json;
using Employee.Core;

namespace Employee.Infrastructure;
public static class ReportedUsage
{
    // Field names observed in local Codex turn.completed frames. Unknown vendors/frames remain unknown.
    public static TokenUsage? Parse(string provider, string output)
    {
        if (provider != "codex")
            return null;
        foreach (var line in output.Split('\n').Reverse())
        {
            try
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.GetString() != "turn.completed" || !root.TryGetProperty("usage", out var usage))
                    continue;
                long? Number(string key) => usage.TryGetProperty(key, out var value) && value.TryGetInt64(out var n) && n >= 0 ? n : null;
                var input = Number("input_tokens");
                var generated = Number("output_tokens");
                return input == null && generated == null ? null : new(input, generated, Number("cached_input_tokens"));
            }
            catch (JsonException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        return null;
    }
}
