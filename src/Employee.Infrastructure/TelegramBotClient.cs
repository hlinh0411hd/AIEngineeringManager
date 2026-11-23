using Employee.Core;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Employee.Infrastructure;

public sealed record TelegramBot(long Id, string Username);
public sealed record TelegramMessage(long UpdateId, long ChatId, int MessageId, string Text, int? ReplyToMessageId, string? CallbackQueryId = null);

// Raised for any Telegram or network failure. The message never contains the bot token.
public sealed class TelegramApiException(string message, int code = 0, int? retryAfterSeconds = null) : Exception(message)
{
    public int Code { get; } = code;
    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
}

public sealed class TelegramBotClient : IDisposable
{
    public const int MaxTextLength = 4000;
    private readonly HttpClient http;
    private readonly string token;
    private readonly TimeSpan requestTimeout;

    public TelegramBotClient(string token, HttpMessageHandler? handler = null, TimeSpan? requestTimeout = null)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("Bot token is empty.", nameof(token));
        this.token = token.Trim();
        this.requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(30);
        // The per-call timeout is applied with a linked token so long polling is not cut short by HttpClient.Timeout.
        http = handler == null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) : new HttpClient(handler);
        http.Timeout = Timeout.InfiniteTimeSpan;
    }

    public async Task<TelegramBot> GetMeAsync(CancellationToken cancel)
    {
        var result = await CallAsync("getMe", null, requestTimeout, cancel);
        return new(result.GetProperty("id").GetInt64(), result.TryGetProperty("username", out var name) ? name.GetString() ?? "" : "");
    }

    // Sends plain task text. It is HTML-escaped and sent with parse_mode HTML so task content cannot break formatting.
    public async Task<int> SendMessageAsync(long chatId, string text, int? replyToMessageId, CancellationToken cancel, bool forceReply = false, IReadOnlyList<IReadOnlyList<TelegramButton>>? keyboard = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["chat_id"] = chatId,
            ["text"] = Escape(Truncate(text)),
            ["parse_mode"] = "HTML",
            ["disable_web_page_preview"] = true
        };
        if (forceReply)
            body["reply_markup"] = new Dictionary<string, object> { ["force_reply"] = true, ["selective"] = true };
        else if (keyboard != null)
            body["reply_markup"] = Keyboard(keyboard);
        if (replyToMessageId != null)
        {
            body["reply_to_message_id"] = replyToMessageId;
            body["allow_sending_without_reply"] = true;
        }

        var result = await CallAsync("sendMessage", body, requestTimeout, cancel);
        return result.GetProperty("message_id").GetInt32();
    }

    private static Dictionary<string, object> Keyboard(IReadOnlyList<IReadOnlyList<TelegramButton>> rows) => new()
    {
        ["inline_keyboard"] = rows.Select(row => row.Select(b => new Dictionary<string, string> { ["text"] = b.Label, ["callback_data"] = b.Data }).ToArray()).ToArray()
    };

    public async Task<int> SendMediaAsync(long chatId, WorkAttachment attachment, int replyTo, CancellationToken cancel)
    {
        if (!TaskResultMedia.IsMedia(attachment.Name) || attachment.Content.Length is 0 or > TaskResultMedia.MaxBytes)
            throw new ArgumentException("Unsupported or oversized result media.");
        var extension = Path.GetExtension(attachment.Name).ToLowerInvariant();
        var field = extension is ".jpg" or ".jpeg" or ".png" ? "photo" : extension == ".mp4" ? "video" : extension is ".mp3" or ".m4a" ? "audio" : "document";
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(chatId.ToString(System.Globalization.CultureInfo.InvariantCulture)), "chat_id");
        form.Add(new StringContent(replyTo.ToString(System.Globalization.CultureInfo.InvariantCulture)), "reply_to_message_id");
        form.Add(new ByteArrayContent(attachment.Content), field, Path.GetFileName(attachment.Name));
        var result = await CallAsync("send" + char.ToUpperInvariant(field[0]) + field[1..], null, TimeSpan.FromMinutes(2), cancel, form);
        return result.GetProperty("message_id").GetInt32();
    }

    public async Task AnswerCallbackQueryAsync(string callbackQueryId, string text, CancellationToken cancel)
    {
        var body = new Dictionary<string, object?> { ["callback_query_id"] = callbackQueryId };
        if (!string.IsNullOrEmpty(text))
            body["text"] = text.Length > 200 ? text[..199] + "…" : text;
        await CallAsync("answerCallbackQuery", body, requestTimeout, cancel);
    }

    // A null keyboard removes the buttons.
    public async Task EditMessageReplyMarkupAsync(long chatId, int messageId, IReadOnlyList<IReadOnlyList<TelegramButton>>? keyboard, CancellationToken cancel)
    {
        var body = new Dictionary<string, object?>
        {
            ["chat_id"] = chatId,
            ["message_id"] = messageId,
            ["reply_markup"] = keyboard == null ? new Dictionary<string, object> { ["inline_keyboard"] = Array.Empty<object>() } : Keyboard(keyboard)
        };
        await CallAsync("editMessageReplyMarkup", body, requestTimeout, cancel);
    }

    public async Task<IReadOnlyList<TelegramMessage>> GetUpdatesAsync(long offset, int timeoutSeconds, CancellationToken cancel)
    {
        timeoutSeconds = Math.Clamp(timeoutSeconds, 0, 50);
        var body = new Dictionary<string, object?>
        {
            ["offset"] = offset,
            ["timeout"] = timeoutSeconds,
            ["allowed_updates"] = new[] { "message", "callback_query" }
        };
        var result = await CallAsync("getUpdates", body, TimeSpan.FromSeconds(timeoutSeconds) + requestTimeout, cancel);
        var messages = new List<TelegramMessage>();
        foreach (var update in result.EnumerateArray())
        {
            if (update.TryGetProperty("callback_query", out var query))
            {
                // The callback is surfaced as a message: Text is the callback_data, MessageId the message that carries the buttons.
                if (query.TryGetProperty("id", out var queryId) && query.TryGetProperty("data", out var data) && query.TryGetProperty("message", out var source)
                    && source.TryGetProperty("chat", out var sourceChat) && source.TryGetProperty("message_id", out var sourceId))
                    messages.Add(new(update.GetProperty("update_id").GetInt64(), sourceChat.GetProperty("id").GetInt64(), sourceId.GetInt32(), data.GetString() ?? "", null, queryId.GetString()));
                continue;
            }

            if (!update.TryGetProperty("message", out var message) || !message.TryGetProperty("chat", out var chat) || !message.TryGetProperty("text", out var text))
                continue;
            int? reply = message.TryGetProperty("reply_to_message", out var replied) && replied.TryGetProperty("message_id", out var repliedId) ? repliedId.GetInt32() : null;
            messages.Add(new(update.GetProperty("update_id").GetInt64(), chat.GetProperty("id").GetInt64(), message.GetProperty("message_id").GetInt32(), text.GetString() ?? "", reply));
        }

        return messages;
    }

    public static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    public static string Truncate(string text) => text.Length <= MaxTextLength ? text : text[..(MaxTextLength - 1)] + "…";
    // Removes the exact token, its URL-escaped form, and anything shaped like a bot URL segment (bot<id>:<secret>).
    public static string Redact(string text, string token)
    {
        if (!string.IsNullOrEmpty(token))
            text = text.Replace(token, "***").Replace(Uri.EscapeDataString(token), "***");
        return BotUrlSecret.Replace(text, "bot***");
    }

    private static readonly System.Text.RegularExpressions.Regex BotUrlSecret = new(@"bot\d+(?::|%3A)[A-Za-z0-9_\-]+", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private async Task<JsonElement> CallAsync(string method, object? body, TimeSpan timeout, CancellationToken cancel, HttpContent? content = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.telegram.org/bot{token}/{method}")
            {
                Content = content ?? new StringContent(JsonSerializer.Serialize(body ?? new { }), Encoding.UTF8, "application/json")
            };
            using var response = await http.SendAsync(request, deadline.Token);
            var json = await response.Content.ReadAsStringAsync(deadline.Token);
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(json);
            }
            catch (JsonException)
            {
                throw new TelegramApiException($"Telegram {method} returned an unreadable response (HTTP {(int)response.StatusCode}).", (int)response.StatusCode);
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True && root.TryGetProperty("result", out var result))
                    return result.Clone();
                var code = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error_code", out var c) && c.TryGetInt32(out var parsed) ? parsed : (int)response.StatusCode;
                var description = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("description", out var d) ? d.GetString() : null;
                int? retry = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("parameters", out var p) && p.TryGetProperty("retry_after", out var r) && r.TryGetInt32(out var seconds) ? seconds : null;
                throw new TelegramApiException($"Telegram {method} failed ({code}): {Redact(description ?? ((HttpStatusCode)code).ToString(), token)}", code, retry);
            }
        }
        catch (TelegramApiException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new TelegramApiException($"Telegram {method} timed out.");
        }
        catch (Exception ex)
        {
            // The inner exception is deliberately not chained: transport errors can carry the request URL, which contains the token.
            throw new TelegramApiException($"Telegram {method} failed: {Redact(ex.Message, token)}");
        }
    }

    public void Dispose() => http.Dispose();
}
