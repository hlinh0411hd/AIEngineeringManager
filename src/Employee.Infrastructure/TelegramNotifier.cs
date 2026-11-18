using System.Text.Json;
using Employee.Core;

namespace Employee.Infrastructure;

// The only place the bot token is protected and unprotected. Core stores just the DPAPI blob.
public static class TelegramSecrets
{
    public static void SetToken(TelegramSettings settings, string token) => settings.ProtectedBotToken = SecretProtection.Protect(token.Trim());
    public static bool TryGetToken(TelegramSettings settings, out string token, out string error)
    {
        token = "";
        error = "";
        if (!settings.HasToken)
        {
            error = "No bot token saved.";
            return false;
        }

        try
        {
            token = SecretProtection.Unprotect(settings.ProtectedBotToken);
        }
        catch (Exception)
        {
            error = "Telegram bot token cannot be decrypted; enter it again.";
            return false;
        }

        if (token.Length > 0)
            return true;
        error = "No bot token saved.";
        return false;
    }
}

// Remembers which Telegram message announced which task, so a reply can be routed back to its task.
public sealed class TelegramMessageMap(string path)
{
    public sealed record Entry(long ChatId, int MessageId, string WorkId, DateTimeOffset At);
    private const int MaxEntries = 1000;
    private readonly object gate = new();

    public void Record(long chatId, int messageId, string workId)
    {
        lock (gate)
        {
            var entries = Load();
            entries.RemoveAll(e => e.ChatId == chatId && e.MessageId == messageId);
            entries.Add(new(chatId, messageId, workId, DateTimeOffset.UtcNow));
            if (entries.Count > MaxEntries)
                entries.RemoveRange(0, entries.Count - MaxEntries);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(entries));
            File.Move(path + ".tmp", path, true);
        }
    }

    public string? WorkIdFor(long chatId, int messageId)
    {
        lock (gate)
            return Load().LastOrDefault(e => e.ChatId == chatId && e.MessageId == messageId)?.WorkId;
    }

    private List<Entry> Load()
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path)) ?? [] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

// Sends one Telegram message per new stop notice. It never throws into task processing:
// every failure is reported through the status callback instead. Transient failures get a small bounded retry.
public sealed class TelegramNotifier
{
    public const int MaxAttempts = 3;
    private static readonly TimeSpan[] DefaultBackoff = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(60);

    private readonly Func<TelegramSettings> settings;
    private readonly Action<string> status;
    private readonly Func<string, TelegramBotClient> clientFor;
    private readonly TimeSpan[] backoff;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly SemaphoreSlim order = new(1, 1);
    private string lastPermanentHint = "";
    public TelegramMessageMap Messages { get; }

    // backoff: waits before attempt 2 and attempt 3 (default 2 s, then 5 s). Tests pass zero waits.
    public TelegramNotifier(string dataFolder, Func<TelegramSettings> settings, Action<string> status, Func<string, TelegramBotClient>? clientFactory = null, TimeSpan[]? backoff = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        this.delay = delay ?? Task.Delay;
        this.settings = settings;
        this.status = status;
        clientFor = clientFactory ?? (token => new TelegramBotClient(token));
        this.backoff = backoff ?? DefaultBackoff;
        Messages = new(Path.Combine(dataFolder, "telegram-messages.json"));
    }

    // Fixed layout so every message reads the same: STATUS, ID, Task, Result, Tested, Done, Next.
    public static string Format(StopNotice notice)
    {
        var id = ShortId(notice.WorkId);
        var lines = new List<string>
        {
            StatusIcon(notice.State) + " " + StatusLabel(notice.State).ToUpperInvariant(),
            "ID: #" + id,
            "Task: " + notice.RequestExcerpt
        };
        if (!string.IsNullOrWhiteSpace(notice.LastComment))
            lines.Add("Comment: " + Clip(notice.LastComment, 120));

        string done = "", notDone = "", why = "", tests = "", milestone = "", ai = "";
        foreach (var raw in (notice.Summary ?? "").Split('\n'))
        {
            var line = raw.Trim();
            string Tail(string prefix) => line[prefix.Length..].Trim();
            if (line.StartsWith("Done", StringComparison.Ordinal)) done = line.Contains(':') ? line[(line.IndexOf(':') + 1)..].Trim() : "";
            else if (line.StartsWith("Not done", StringComparison.Ordinal)) notDone = line[(line.IndexOf(':') + 1)..].Trim();
            else if (line.StartsWith("Why it stopped:", StringComparison.Ordinal)) why = Tail("Why it stopped:");
            else if (line.StartsWith("Tests:", StringComparison.Ordinal)) tests = Tail("Tests:");
            else if (line.StartsWith("Milestone", StringComparison.Ordinal)) milestone = line;
            else if (line.StartsWith("AI result:", StringComparison.Ordinal)) ai = Tail("AI result:");
        }

        lines.Add("Result: " + ResultLine(notice, notDone, why));
        lines.Add("Tested: " + (tests.Length == 0 ? "not tested" : tests));
        if (milestone.Length > 0)
            lines.Add(milestone);
        if (done.Length > 0 && !done.StartsWith("nothing finished", StringComparison.OrdinalIgnoreCase))
        {
            lines.Add("");
            lines.Add(notice.LastComment is { Length: > 0 } ? "Done for this comment:" : "Done:");
            foreach (var item in done.Split("; ", StringSplitOptions.RemoveEmptyEntries))
                lines.Add(" - " + item);
        }

        if (notDone.Length > 0)
        {
            lines.Add("");
            lines.Add("Not done / problem:");
            foreach (var item in notDone.Split("; ", StringSplitOptions.RemoveEmptyEntries))
                lines.Add(" - " + item);
        }

        if (ai.Length > 0)
        {
            lines.Add("");
            lines.Add("AI says:");
            foreach (var point in AiPoints(ai))
                lines.Add(" - " + point);
        }

        return string.Join("\n", lines).TrimEnd();
    }

    static string ResultLine(StopNotice notice, string notDone, string why) => notice.State switch
    {
        WorkState.Completed => "finished",
        WorkState.NeedsReview => notDone.Length > 0 ? "run finished, some steps not done - needs your review" : "finished - needs your review",
        WorkState.Failed => "FAILED" + (why.Length > 0 ? ": " + why : ""),
        WorkState.Paused => "paused" + (why.Length > 0 ? ": " + why : ""),
        _ => notice.Title + (why.Length > 0 ? ": " + why : "")
    };

    static string StatusLabel(WorkState state) => state switch
    {
        WorkState.Completed => "Completed",
        WorkState.NeedsReview => "Needs review",
        WorkState.Failed => "Failed",
        WorkState.Paused => "Paused",
        WorkState.AwaitingApproval => "Awaiting approval",
        WorkState.WaitingForPermission => "Permission needed",
        WorkState.WaitingForProvider => "Waiting for AI",
        WorkState.WrongAccess => "Wrong access",
        WorkState.WrongProject => "Wrong project",
        _ => state.ToString()
    };

    static string StatusIcon(WorkState state) => state switch
    {
        WorkState.Completed => "✅",
        WorkState.NeedsReview => "🔍",
        WorkState.Failed => "❌",
        WorkState.Paused => "⏸",
        _ => "⚠️"
    };

    static string Clip(string text, int max)
    {
        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= max ? text : text[..(max - 1)] + "…";
    }

    // Splits the AI summary into short bullet points (by line, ';' or sentence end), capped so the message stays compact.
    static IEnumerable<string> AiPoints(string text)
    {
        var parts = System.Text.RegularExpressions.Regex.Split(text.Replace("\r", ""), @"\n+|;\s+|(?<=[.!?])\s+(?=\p{Lu})")
            .Select(p => p.Trim().TrimStart('-', '*', '•', ' ').Trim())
            .Where(p => p.Length > 0)
            .ToList();
        foreach (var p in parts.Take(5))
            yield return Clip(p, 160);
        if (parts.Count > 5)
            yield return "…";
    }

    public static string ShortId(string workId) => workId[..Math.Min(8, workId.Length)];

    // True for network errors, timeouts, HTTP 5xx and 429. Everything else (400, 401, 403, 404...) is permanent.
    public static bool IsTransient(Exception ex) => ex switch
    {
        TelegramApiException api => api.Code == 0 || api.Code == 429 || api.Code >= 500,
        HttpRequestException => true,
        TaskCanceledException => true,
        _ => false
    };

    // A clear, token-free hint for errors that retrying cannot fix; null for other errors.
    public static string? PermanentHint(Exception ex)
    {
        if (ex is not TelegramApiException api)
            return null;
        if (api.Code is 401 or 404)
            return "Telegram rejected the bot token. Enter a valid token in the Telegram settings.";
        if (api.Code == 403 || (api.Code == 400 && api.Message.Contains("chat not found", StringComparison.OrdinalIgnoreCase)))
            return "Bot cannot message this chat; send /start to the bot, then check the allowed chat ID.";
        return null;
    }

    // Returns true when a message was sent. Never throws.
    public async Task<bool> NotifyAsync(StopNotice notice, CancellationToken cancel = default)
    {
        string token = "";
        try
        {
            var config = settings();
            if (!config.Enabled || config.AllowedChatId == 0 || (notice.Kind == StopNoticeKind.Completed && !config.NotifyCompleted))
                return false;
            if (!TelegramSecrets.TryGetToken(config, out token, out var error))
            {
                status(error);
                return false;
            }

            await order.WaitAsync(cancel);
            try
            {
                Exception? last = null;
                for (var attempt = 1; attempt <= MaxAttempts; attempt++)
                {
                    try
                    {
                        using var client = clientFor(token);
                        var id = await client.SendMessageAsync(config.AllowedChatId, Format(notice), null, cancel, keyboard: TelegramCommand.Buttons(notice.State, ShortId(notice.WorkId)));
                        Messages.Record(config.AllowedChatId, id, notice.WorkId);
                        lastPermanentHint = "";
                        return true;
                    }
                    catch (OperationCanceledException) when (cancel.IsCancellationRequested)
                    {
                        return false;
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                        if (!IsTransient(ex) || attempt == MaxAttempts)
                            break;
                        var wait = ex is TelegramApiException { Code: 429, RetryAfterSeconds: int retry }
                            ? TimeSpan.FromSeconds(Math.Clamp(retry, 1, MaxRetryAfter.TotalSeconds))
                            : backoff.Length == 0 ? TimeSpan.Zero : backoff[Math.Min(attempt - 1, backoff.Length - 1)];
                        if (wait > TimeSpan.Zero)
                            await delay(wait, cancel);
                    }
                }

                ReportFailure(last!, token);
                return false;
            }
            finally
            {
                order.Release();
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            Report("Telegram notification failed: " + TelegramBotClient.Redact(ex.Message, token));
            return false;
        }
    }

    private void ReportFailure(Exception ex, string token)
    {
        var hint = PermanentHint(ex);
        if (hint != null)
        {
            // The same permanent problem is reported once, until a send succeeds again.
            if (hint == lastPermanentHint)
                return;
            lastPermanentHint = hint;
            Report(hint);
            return;
        }

        Report("Telegram notification failed: " + TelegramBotClient.Redact(ex.Message, token));
    }

    private void Report(string text)
    {
        try
        {
            status(text);
        }
        catch (Exception)
        {
        }
    }

    // For the settings Test button. Never throws; returns the text to show.
    public async Task<string> TestAsync(CancellationToken cancel = default)
    {
        var token = "";
        try
        {
            if (!TelegramSecrets.TryGetToken(settings(), out token, out var error))
                return error;
            using var client = clientFor(token);
            var bot = await client.GetMeAsync(cancel);
            var chat = settings().AllowedChatId;
            if (chat == 0)
                return "Connected as @" + bot.Username + ". Enter the allowed chat ID to send a test message.";
            await client.SendMessageAsync(chat, "AI Employee Manager: Telegram test message.", null, cancel);
            return "Connected as @" + bot.Username + "; test message sent.";
        }
        catch (Exception ex)
        {
            return "Telegram test failed: " + TelegramBotClient.Redact(ex.Message, token);
        }
    }
}
