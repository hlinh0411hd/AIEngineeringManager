using System.Text.Json;
using Employee.Core;

namespace Employee.Infrastructure;

// Supplied by the Desktop. Each delegate returns a one-line result for the user, or throws
// InvalidOperationException with a readable message. FindByShortId returns null when zero or several tasks match.
public sealed record TelegramActions(
    Func<string, string?> FindByShortId,
    Func<string, Task<string>> Describe,
    Func<string, string, Task<string>> Comment,
    Func<string, Task<string>> Complete,
    Func<string, string, Task<string>> Close,
    Func<string, Task<string>> Run,
    Func<Task<string>>? ListProjects = null,
    Func<string, Task<string>>? NewTask = null);

// Holds the next getUpdates offset in telegram-state.json, outside ManagerSettings so the poll loop never races SaveSettings.
public sealed class TelegramOffsetStore(string path)
{
    private readonly object gate = new();

    public static TelegramOffsetStore InFolder(string dataFolder) => new(Path.Combine(dataFolder, "telegram-state.json"));

    private sealed record State(long NextOffset);

    public long Load()
    {
        lock (gate)
        {
            try
            {
                return File.Exists(path) ? JsonSerializer.Deserialize<State>(File.ReadAllText(path))?.NextOffset ?? 0 : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }

    public void Save(long nextOffset)
    {
        lock (gate)
        {
            var folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(new State(nextOffset)));
            File.Move(path + ".tmp", path, true);
        }
    }
}

// Inbound side of Telegram: long-polls for messages from the paired chat and turns them into task actions.
public sealed class TelegramCommandService
{
    public const string DefaultCloseReason = "Closed from Telegram";
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(3);

    private readonly Func<ManagerSettings> settings;
    private readonly TelegramMessageMap map;
    private readonly TelegramOffsetStore offsets;
    private readonly TelegramActions actions;
    private readonly Action<string> status;
    private readonly Func<string, TelegramBotClient> clientFor;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly object gate = new();
    private CancellationTokenSource? cts;
    private Task? loop;

    public TelegramCommandService(Func<ManagerSettings> settings, TelegramMessageMap map, TelegramOffsetStore offsets, TelegramActions actions, Action<string> status, Func<string, TelegramBotClient>? clientFactory = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        this.delay = delay ?? Task.Delay;
        this.settings = settings;
        this.map = map;
        this.offsets = offsets;
        this.actions = actions;
        this.status = status;
        clientFor = clientFactory ?? (token => new TelegramBotClient(token));
    }

    public static string HelpText =>
        "Reply to a task notification, or add #id (the 8 characters after #) to a command:\n" +
        "text - add a comment and requeue the task\n" +
        "/redo <text> - ask for changes\n" +
        "/continue [text] - keep working on the task (with text: add it as a comment; without: run it again)\n" +
        "/done - mark the task completed\n" +
        "/close [reason] - close the task\n" +
        "/run - run the task again\n" +
        "/status - show the task state\n" +
        "/projects - list the projects (numbered)\n" +
        "/new <project> <request> - create a task in a project (project = number, name or alias)\n" +
        "/help - this list";

    public void Start()
    {
        lock (gate)
        {
            if (loop is { IsCompleted: false })
                return;
            cts = new CancellationTokenSource();
            var token = cts.Token;
            loop = Task.Run(() => RunLoopAsync(token));
        }
    }

    public void Stop()
    {
        CancellationTokenSource? source;
        lock (gate)
        {
            source = cts;
            cts = null;
        }

        try
        {
            source?.Cancel();
        }
        catch (Exception)
        {
        }
    }

    // Test entry point and the body of the loop: handles each update in order and saves the offset after each one.
    public async Task ProcessUpdatesAsync(IReadOnlyList<TelegramMessage> updates, TelegramBotClient client, CancellationToken cancel)
    {
        foreach (var update in updates.OrderBy(u => u.UpdateId))
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                var allowed = settings().Telegram.AllowedChatId;
                // Not the paired chat (or no pairing): drop silently. No action, no reply.
                if (update.CallbackQueryId != null)
                {
                    if (allowed != 0 && update.ChatId == allowed)
                        await HandleCallbackAsync(update, client, cancel);
                    else
                        await AnswerAsync(client, update.CallbackQueryId, "", cancel);
                }
                else if (allowed != 0 && update.ChatId == allowed)
                    await HandleAsync(update, client, cancel);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Report("Telegram command failed: ", ex);
            }

            SaveOffset(update.UpdateId + 1);
        }
    }

    private async Task HandleAsync(TelegramMessage update, TelegramBotClient client, CancellationToken cancel)
    {
        var command = TelegramCommand.Parse(update.Text);
        if (command.Kind is TelegramCommandKind.Help or TelegramCommandKind.Start)
        {
            await ReplyAsync(client, update, HelpText, cancel);
            return;
        }

        if (command.Kind == TelegramCommandKind.Unknown)
        {
            await ReplyAsync(client, update, "Unknown command.\n" + HelpText, cancel);
            return;
        }

        if (command.Kind is TelegramCommandKind.Projects or TelegramCommandKind.New)
        {
            await ReplyAsync(client, update, await RunProjectCommandAsync(command, cancel), cancel);
            return;
        }

        string? workId = null;
        if (update.ReplyToMessageId is int replied)
            workId = map.WorkIdFor(update.ChatId, replied);
        if (workId == null && !string.IsNullOrEmpty(command.ShortId))
            workId = actions.FindByShortId(command.ShortId);
        if (workId == null)
        {
            await ReplyAsync(client, update, "I could not tell which task you mean. Reply to a task notification, or include #id (for example #a1b2c3d4).", cancel);
            return;
        }

        // A tapped /redo_<id> has no text yet: ask for it with a force-reply prompt tied to the task, so the user's answer becomes the comment.
        if (command.Kind == TelegramCommandKind.Comment && command.Text.Length == 0)
        {
            try
            {
                var prompt = await client.SendMessageAsync(update.ChatId, "What should change? Reply to this message with the details (#" + workId[..Math.Min(8, workId.Length)] + ").", update.MessageId, cancel, forceReply: true);
                map.Record(update.ChatId, prompt, workId);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Report("Telegram reply failed: ", ex);
            }

            return;
        }

        var result = await ExecuteAsync(command, workId, cancel);
        await ReplyAsync(client, update, string.IsNullOrWhiteSpace(result) ? "Done." : result, cancel);
    }

    // Shared by typed commands and button taps: runs the action and returns the one-line result.
    private async Task<string> ExecuteAsync(TelegramCommand command, string workId, CancellationToken cancel)
    {
        string result;
        try
        {
            result = command.Kind switch
            {
                TelegramCommandKind.Comment or TelegramCommandKind.Redo => command.Text.Length == 0
                    ? throw new InvalidOperationException("Add the comment text, for example: /redo make the button larger.")
                    : await actions.Comment(workId, command.Text),
                TelegramCommandKind.Continue => command.Text.Length == 0
                    ? await actions.Run(workId)
                    : await actions.Comment(workId, command.Text),
                TelegramCommandKind.Done => await actions.Complete(workId),
                TelegramCommandKind.Close => await actions.Close(workId, command.Text.Length == 0 ? DefaultCloseReason : command.Text),
                TelegramCommandKind.Run => await actions.Run(workId),
                TelegramCommandKind.Status => await actions.Describe(workId),
                _ => throw new InvalidOperationException("Unsupported command.")
            };
        }
        catch (InvalidOperationException ex)
        {
            result = Redact(ex.Message);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Report("Telegram action failed: ", ex);
            result = "That action failed. See the app for details.";
        }

        return result;
    }

    private async Task HandleCallbackAsync(TelegramMessage update, TelegramBotClient client, CancellationToken cancel)
    {
        var queryId = update.CallbackQueryId!;
        var callback = TelegramCommand.ParseCallback(update.Text);
        if (callback == null)
        {
            await AnswerAsync(client, queryId, "Unknown button.", cancel);
            return;
        }

        var workId = actions.FindByShortId(callback.ShortId) ?? map.WorkIdFor(update.ChatId, update.MessageId);
        if (workId == null)
        {
            await AnswerAsync(client, queryId, "Task not found.", cancel);
            await ReplyAsync(client, update, "I could not find that task any more.", cancel);
            return;
        }

        var shortId = callback.ShortId;
        if (callback.Stage == TelegramCallbackStage.Cancel)
        {
            await EditButtonsAsync(client, update, TelegramCommand.Buttons(WorkState.Queued, shortId), cancel);
            await AnswerAsync(client, queryId, "Cancelled.", cancel);
            await ReplyAsync(client, update, "Cancelled. Nothing was changed.", cancel);
            return;
        }

        if (callback.Kind is TelegramCommandKind.Done or TelegramCommandKind.Close && callback.Stage == TelegramCallbackStage.Action)
        {
            // First tap: ask for confirmation by swapping the buttons. Nothing runs yet.
            await EditButtonsAsync(client, update, TelegramCommand.ConfirmButtons(callback.Kind, shortId), cancel);
            await AnswerAsync(client, queryId, "Tap Confirm " + (callback.Kind == TelegramCommandKind.Close ? "close" : "done") + " to continue.", cancel);
            return;
        }

        if (callback.Kind == TelegramCommandKind.Redo)
        {
            try
            {
                var prompt = await client.SendMessageAsync(update.ChatId, "What should change? Reply to this message with the details (#" + shortId + ").", update.MessageId, cancel, forceReply: true);
                map.Record(update.ChatId, prompt, workId);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Report("Telegram reply failed: ", ex);
            }

            await AnswerAsync(client, queryId, "Reply with what should change.", cancel);
            return;
        }

        var command = new TelegramCommand(callback.Kind, shortId, "");
        var result = await ExecuteAsync(command, workId, cancel);
        if (callback.Stage == TelegramCallbackStage.Confirm)
            await EditButtonsAsync(client, update, null, cancel);
        if (string.IsNullOrWhiteSpace(result))
            result = "Done.";
        await AnswerAsync(client, queryId, FirstLine(result), cancel);
        await ReplyAsync(client, update, result, cancel);
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length > 180 ? line[..179] + "…" : line;
    }

    // Failures here are reported but never stop the loop.
    private async Task AnswerAsync(TelegramBotClient client, string queryId, string text, CancellationToken cancel)
    {
        try
        {
            await client.AnswerCallbackQueryAsync(queryId, Redact(text), cancel);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Report("Telegram answerCallbackQuery failed: ", ex);
        }
    }

    private async Task EditButtonsAsync(TelegramBotClient client, TelegramMessage update, IReadOnlyList<IReadOnlyList<TelegramButton>>? keyboard, CancellationToken cancel)
    {
        try
        {
            await client.EditMessageReplyMarkupAsync(update.ChatId, update.MessageId, keyboard, cancel);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Report("Telegram editMessageReplyMarkup failed: ", ex);
        }
    }

    private async Task<string> RunProjectCommandAsync(TelegramCommand command, CancellationToken cancel)
    {
        try
        {
            if (command.Kind == TelegramCommandKind.Projects)
                return actions.ListProjects == null ? "Projects are not available." : await actions.ListProjects();
            if (command.Text.Length == 0)
                return "Usage: /new <project> <request>\nExample: /new 1 fix the login button\nSend /projects to see the numbers.";
            return actions.NewTask == null ? "Creating tasks is not available." : await actions.NewTask(command.Text);
        }
        catch (InvalidOperationException ex)
        {
            return Redact(ex.Message);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Report("Telegram action failed: ", ex);
            return "That action failed. See the app for details.";
        }
    }

    private async Task ReplyAsync(TelegramBotClient client, TelegramMessage update, string text, CancellationToken cancel)
    {
        try
        {
            await client.SendMessageAsync(update.ChatId, text, update.MessageId, cancel);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Report("Telegram reply failed: ", ex);
        }
    }

    private async Task RunLoopAsync(CancellationToken cancel)
    {
        var backoff = MinBackoff;
        var tokenRejectedReported = false;
        TelegramBotClient? client = null;
        var clientToken = "";
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                try
                {
                    var config = settings().Telegram;
                    if (!config.Enabled || config.AllowedChatId == 0 || !TelegramSecrets.TryGetToken(config, out var token, out _))
                    {
                        client?.Dispose();
                        client = null;
                        clientToken = "";
                        await delay(IdleDelay, cancel);
                        continue;
                    }

                    if (client == null || clientToken != token)
                    {
                        client?.Dispose();
                        client = clientFor(token);
                        clientToken = token;
                    }

                    var updates = await client.GetUpdatesAsync(offsets.Load(), 25, cancel);
                    // A good poll resets the backoff and re-arms the one-time "token rejected" report.
                    backoff = MinBackoff;
                    tokenRejectedReported = false;
                    if (updates.Count > 0)
                        await ProcessUpdatesAsync(updates, client, cancel);
                }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    var wait = backoff;
                    if (ex is TelegramApiException { Code: 401 or 404 })
                    {
                        // Bad or revoked token: stop hammering the API, and say so once instead of once per attempt.
                        if (!tokenRejectedReported)
                        {
                            tokenRejectedReported = true;
                            Say("Telegram rejected the bot token; command polling is slowed down. Enter a valid token in the Telegram settings.");
                        }

                        backoff = MaxBackoff;
                        wait = MaxBackoff;
                    }
                    else if (ex is TelegramApiException { Code: 429, RetryAfterSeconds: int retry })
                    {
                        Report("Telegram polling failed: ", ex);
                        wait = TimeSpan.FromSeconds(Math.Max(1, retry));
                    }
                    else
                    {
                        Report("Telegram polling failed: ", ex);
                        backoff = TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, backoff.TotalSeconds * 2));
                    }

                    try
                    {
                        await delay(wait, cancel);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            client?.Dispose();
        }
    }

    private void SaveOffset(long next)
    {
        try
        {
            offsets.Save(next);
        }
        catch (Exception ex)
        {
            Report("Telegram offset could not be saved: ", ex);
        }
    }

    private string Redact(string text)
    {
        try
        {
            return TelegramSecrets.TryGetToken(settings().Telegram, out var token, out _) ? TelegramBotClient.Redact(text, token) : text;
        }
        catch (Exception)
        {
            return text;
        }
    }

    private void Say(string text)
    {
        try
        {
            status(text);
        }
        catch (Exception)
        {
        }
    }

    private void Report(string prefix, Exception ex)
    {
        try
        {
            status(prefix + Redact(ex.Message));
        }
        catch (Exception)
        {
        }
    }
}
