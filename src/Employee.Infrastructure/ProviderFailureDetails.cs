using System.Text.Json;
using System.Text.RegularExpressions;
using Employee.Core;

namespace Employee.Infrastructure;
public static class ProviderFailureDetails
{
    public static string Describe(FailureKind failure, string output)
    {
        var evidence = "";
        foreach (var line in output.Split('\n').Reverse())
        {
            var candidate = line.Trim();
            try
            {
                using var doc = JsonDocument.Parse(candidate);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("error", out var error))
                    {
                        if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var errorMessage) && errorMessage.ValueKind == JsonValueKind.String)
                            candidate = errorMessage.GetString() ?? "";
                        else if (error.ValueKind == JsonValueKind.String)
                            candidate = error.GetString() ?? "";
                    }
                    else if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String)
                        candidate = result.GetString() ?? "";
                    else if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                        candidate = message.GetString() ?? "";
                }
            }
            catch (JsonException)
            {
            }

            if (ProviderErrors.Classify(1, candidate) != failure)
                continue;
            evidence = Regex.Replace(candidate, @"(?i)\b(?:sk-[a-z0-9_-]{12,}|gh[pousr]_[a-z0-9_]{12,}|bearer\s+\S+)", "[redacted]");
            evidence = evidence[..Math.Min(700, evidence.Length)];
            break;
        }

        var label = failure switch
        {
            FailureKind.RateLimit => Regex.IsMatch(evidence, @"(?i)usage|quota|credit|premium|hit.*limit") ? "Provider usage/quota exhausted" : "Rate limited",
            FailureKind.Authentication => "Provider not signed in or the session is invalid",
            FailureKind.ModelUnavailable => "Model overloaded, limited or unavailable",
            FailureKind.ModelRefused => "The model's safety filter declined this step",
            FailureKind.Unavailable => "CLI/provider currently unavailable",
            _ => failure.ToString()};
        return label + (evidence.Length == 0 ? ". The CLI gave no further detail." : ". CLI: " + evidence);
    }
}
