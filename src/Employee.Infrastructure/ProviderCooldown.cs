using System.Globalization;
using System.Text.RegularExpressions;
using Employee.Core;

namespace Employee.Infrastructure;
// When a provider runs out of usage or is unavailable, it is skipped until it is expected to
// work again. Use the reset time the CLI states ("try again at 11:14 PM", "in 3 hours",
// "Retry-After: 120") when there is one, so the manager neither retries too early nor waits
// longer than needed.
public static partial class ProviderCooldown
{
    private static readonly TimeSpan Margin = TimeSpan.FromMinutes(1);
    public static DateTimeOffset RetryAt(FailureKind failure, string text, DateTimeOffset now)
    {
        if (Stated(text, now)is { } stated)
            return Clamp(stated + Margin, now);
        return now.AddMinutes(failure == FailureKind.RateLimit ? 15 : 5);
    }

    public static bool ResetStated(string text) => Stated(text, DateTimeOffset.Now) != null;

    // A tool that is out of image quota or capacity for now: the step waits for the reset instead of stopping for review.
    public static bool IsCapacityBlock(string text) => !string.IsNullOrWhiteSpace(text)
        && Regex.IsMatch(text, @"(?i)\b429\b|resourceexhausted|capacity exhausted|quota|usage limit|rate limit");
    private static DateTimeOffset Clamp(DateTimeOffset value, DateTimeOffset now) => value < now.AddMinutes(1) ? now.AddMinutes(1) : value > now.AddDays(7) ? now.AddDays(7) : value;
    private static DateTimeOffset? Stated(string text, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var iso = IsoTime().Match(text);
        if (iso.Success && DateTimeOffset.TryParse(iso.Groups["at"].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var absolute))
            return absolute;
        // "try again at Nov 10th, 2026 12:34 AM" (Codex): a written date, local time.
        var written = WrittenDate().Match(text);
        if (written.Success)
        {
            var value = $"{written.Groups["month"].Value} {written.Groups["day"].Value} {(written.Groups["year"].Success ? written.Groups["year"].Value : now.ToLocalTime().Year.ToString(CultureInfo.InvariantCulture))} {written.Groups["time"].Value}";
            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var date))
            {
                var at = new DateTimeOffset(date);
                return !written.Groups["year"].Success && at < now ? at.AddYears(1) : at;
            }
        }
        var clock = ClockTime().Match(text);
        if (clock.Success && DateTime.TryParse(clock.Groups["at"].Value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.NoCurrentDateDefault, out var time))
        {
            var local = now.ToLocalTime();
            var at = new DateTimeOffset(local.Date + time.TimeOfDay, local.Offset);
            return at <= local ? at.AddDays(1) : at;
        }

        var relative = Relative().Match(text);
        if (relative.Success)
        {
            var total = TimeSpan.Zero;
            foreach (Match part in Duration().Matches(relative.Groups["span"].Value))
            {
                var value = double.Parse(part.Groups["n"].Value, CultureInfo.InvariantCulture);
                total += part.Groups["unit"].Value.ToLowerInvariant()[0] switch
                {
                    'd' => TimeSpan.FromDays(value),
                    'h' => TimeSpan.FromHours(value),
                    'm' => TimeSpan.FromMinutes(value),
                    _ => TimeSpan.FromSeconds(value)};
            }

            if (total > TimeSpan.Zero)
                return now + total;
        }

        var header = RetryAfterSeconds().Match(text);
        if (header.Success)
            return now.AddSeconds(int.Parse(header.Groups["s"].Value, CultureInfo.InvariantCulture));
        return null;
    }

    [GeneratedRegex(@"(?i)(?:try again|retry|resets?|available again|reset)\s+(?:at|after|on)\s+(?<at>\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(?::\d{2})?(?:\.\d+)?(?:Z|[+-]\d{2}:?\d{2})?)")]
    private static partial Regex IsoTime();
    [GeneratedRegex(@"(?i)(?:try again|retry|resets?|available again)\s+(?:at|on|after)\s+(?<month>jan|feb|mar|apr|may|jun|jul|aug|sep|sept|oct|nov|dec)[a-z]*\.?\s+(?<day>\d{1,2})(?:st|nd|rd|th)?,?\s+(?:(?<year>\d{4}),?\s+)?(?:at\s+)?(?<time>\d{1,2}:\d{2}(?:\s*[ap]\.?m\.?)?)")]
    private static partial Regex WrittenDate();
    // "try again at 8:20 PM", and Claude Code's "resets 8:20pm" / "resets 8pm" (no "at").
    [GeneratedRegex(@"(?i)(?:try again|retry|resets?|available again)\s+(?:at\s+)?(?<at>\d{1,2}(?::\d{2})?\s*[ap]\.?m\.?|\d{1,2}:\d{2})(?![\d:])")]
    private static partial Regex ClockTime();
    // "resets in ~3h15m": an optional "~", and units that touch the next number ("3h15m") still count.
    [GeneratedRegex(@"(?i)(?:try again|retry|resets?|available again|retry after)\s+(?:in|after)\s+~?\s*(?<span>(?:\d+(?:\.\d+)?\s*(?:days?|d|hours?|hrs?|h|minutes?|mins?|m|seconds?|secs?|s)(?![a-z])[\s,and]*)+)")]
    private static partial Regex Relative();
    [GeneratedRegex(@"(?i)(?<n>\d+(?:\.\d+)?)\s*(?<unit>days?|d|hours?|hrs?|h|minutes?|mins?|m|seconds?|secs?|s)(?![a-z])")]
    private static partial Regex Duration();
    [GeneratedRegex(@"(?i)retry-after:\s*(?<s>\d{1,6})")]
    private static partial Regex RetryAfterSeconds();
}
