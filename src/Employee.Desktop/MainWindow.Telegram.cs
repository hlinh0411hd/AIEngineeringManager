using System.Windows;
using Employee.Core;
using Employee.Infrastructure;

namespace Employee.Desktop;
public partial class MainWindow
{
    // Shown in the password box when a token is already saved. The real token is never put back into the UI.
    private const string TelegramTokenMask = "••••••••••••";
    private TelegramNotifier? telegram;
    private bool telegramBusy;
    private TelegramCommandService? telegramCommands;

    private void InitializeTelegram()
    {
        telegram = new(dataPath, () => settings.Telegram, text => Dispatcher.BeginInvoke(() => Status.Text = text));
        ShowTelegramSettings();
        RestartTelegramCommands();
    }

    // Stops the poller, if any. Safe to call repeatedly.
    private void StopTelegramCommands()
    {
        telegramCommands?.Stop();
        telegramCommands = null;
    }

    // A new service instance each time, so a still-draining old loop can never block the start of the new one.
    private void RestartTelegramCommands()
    {
        StopTelegramCommands();
        var config = settings.Telegram;
        if (telegram == null || closing || !config.Enabled || !config.HasToken || config.AllowedChatId == 0)
            return;
        telegramCommands = new(() => settings, telegram.Messages, TelegramOffsetStore.InFolder(dataPath), BuildTelegramActions(), text => Dispatcher.BeginInvoke(() => Status.Text = text));
        telegramCommands.Start();
    }

    private Task<T> OnUi<T>(Func<T> action) => Dispatcher.InvokeAsync(() =>
    {
        if (closing || shuttingDown)
            throw new InvalidOperationException("The app is closing.");
        return action();
    }).Task;

    private WorkItem? FindTelegramTask(string id) => store.WorkItems().FirstOrDefault(w => w.Id == id && TelegramTaskActions.IsTopLevel(w));

    // The logic lives in Infrastructure so it can be checked headlessly; this only supplies the UI thread and the refresh hooks.
    private TelegramActions BuildTelegramActions() => new TelegramTaskActions(unified, store, OnUi, OnTelegramChanged, () => queueSuspended = false, () => shellAutonomous, PushCompletedTask).ToActions();

    private void OnTelegramChanged(WorkItem? created)
    {
        if (created == null)
        {
            RefreshTasks();
            return;
        }

        if (!selectedTaskStates.Contains(created.State))
            SetTaskStateFilter([..selectedTaskStates, created.State]);
        RefreshTasks(created.Id);
    }

    private void ShowTelegramSettings()
    {
        var config = settings.Telegram;
        TelegramEnabled.IsChecked = config.Enabled;
        TelegramNotifyCompleted.IsChecked = config.NotifyCompleted;
        TelegramChatId.Text = config.AllowedChatId == 0 ? "" : config.AllowedChatId.ToString();
        TelegramToken.Password = config.HasToken ? TelegramTokenMask : "";
        TelegramTokenHint.Text = config.HasToken ? "A token is saved (hidden). Type a new token to replace it." : "No token saved.";
    }

    private string SafeSummary(string workId)
    {
        try
        {
            return FindTelegramTask(workId) is { } work ? unified.Summary(work, true) : "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    private string SafeLastComment(string workId)
    {
        try
        {
            return FindTelegramTask(workId)?.Comments.LastOrDefault(c => !c.IsAssistant && !c.IsSummary)?.Text ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    // Sends fresh stop notices to Telegram without blocking the UI thread. NotifyAsync never throws.
    private void NotifyTelegram(IEnumerable<StopNotice> notices)
    {
        if (telegram == null || !settings.Telegram.Enabled)
            return;
        // The summary is read here, on the UI thread, where the task store is consistent.
        var batch = notices.Select(n => n with { Summary = SafeSummary(n.WorkId), LastComment = SafeLastComment(n.WorkId),
            Attachments = FindTelegramTask(n.WorkId) is { } work ? TaskResultMedia.ForNotice(work) : [] }).ToArray();
        if (batch.Length == 0)
            return;
        var notifier = telegram;
        _ = Task.Run(async () =>
        {
            foreach (var notice in batch)
                await notifier.NotifyAsync(notice);
        });
    }

    // Saves through the same settings store as every other section. Throws InvalidOperationException for bad input.
    private void PersistTelegramSettings()
    {
        long chat = 0;
        var chatText = TelegramChatId.Text.Trim();
        if (chatText.Length > 0 && !long.TryParse(chatText, out chat))
            throw new InvalidOperationException("Allowed chat ID must be a number (group chats are negative).");
        var fresh = store.LoadSettings();
        var config = fresh.Telegram;
        config.Enabled = TelegramEnabled.IsChecked == true;
        config.NotifyCompleted = TelegramNotifyCompleted.IsChecked == true;
        config.AllowedChatId = chat;
        var typed = TelegramToken.Password.Trim();
        if (typed.Length > 0 && typed != TelegramTokenMask)
            TelegramSecrets.SetToken(config, typed);
        store.SaveSettings(fresh);
        settings.Telegram = config;
        ShowTelegramSettings();
        RestartTelegramCommands();
    }

    private void SaveTelegramSettings(object sender, RoutedEventArgs e)
    {
        try
        {
            PersistTelegramSettings();
            Status.Text = settings.Telegram.Enabled && settings.Telegram.AllowedChatId == 0
                ? "Telegram settings saved. Enter the allowed chat ID before notifications can be sent."
                : "Telegram settings saved.";
        }
        catch (Exception ex)
        {
            Status.Text = ex.Message;
        }
    }

    private async void TestTelegram(object sender, RoutedEventArgs e)
    {
        if (telegramBusy || telegram == null)
            return;
        try
        {
            PersistTelegramSettings();
        }
        catch (Exception ex)
        {
            Status.Text = ex.Message;
            return;
        }

        telegramBusy = true;
        TelegramTestButton.IsEnabled = false;
        Status.Text = "Testing Telegram...";
        try
        {
            var notifier = telegram;
            // getMe and sendMessage run on a worker thread; the result text is already token-redacted.
            Status.Text = await Task.Run(() => notifier.TestAsync());
        }
        finally
        {
            telegramBusy = false;
            TelegramTestButton.IsEnabled = true;
        }
    }

    // Reads the latest message sent to the bot and fills the chat ID box. Order: stop the poller (two getUpdates
    // callers on one bot conflict), fetch once, then restart the poller. The offset file is not touched.
    private async void FindTelegramChatId(object sender, RoutedEventArgs e)
    {
        if (telegramBusy)
            return;
        var typed = TelegramToken.Password.Trim();
        if (typed.Length > 0 && typed != TelegramTokenMask)
        {
            try
            {
                PersistTelegramSettings();
            }
            catch (Exception ex)
            {
                Status.Text = ex.Message;
                return;
            }
        }

        if (!TelegramSecrets.TryGetToken(settings.Telegram, out var token, out var error))
        {
            Status.Text = error + " Enter the bot token first.";
            return;
        }

        telegramBusy = true;
        TelegramFindChatButton.IsEnabled = false;
        TelegramTestButton.IsEnabled = false;
        Status.Text = "Looking for your message... send /start to the bot if you have not yet.";
        StopTelegramCommands();
        try
        {
            using var client = new TelegramBotClient(token);
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            // offset -1 returns only the newest pending update.
            var updates = await Task.Run(() => client.GetUpdatesAsync(-1, 0, limit.Token));
            var latest = updates.OrderBy(u => u.UpdateId).LastOrDefault();
            if (latest == null)
                Status.Text = "No messages found. Send /start to your bot in Telegram, then click Find chat ID again.";
            else
            {
                TelegramChatId.Text = latest.ChatId.ToString();
                Status.Text = "Chat ID " + latest.ChatId + " found. Click Save Telegram settings to use it.";
            }
        }
        catch (Exception ex)
        {
            Status.Text = "Could not read Telegram updates: " + TelegramBotClient.Redact(ex.Message, token);
        }
        finally
        {
            telegramBusy = false;
            TelegramFindChatButton.IsEnabled = true;
            TelegramTestButton.IsEnabled = true;
            RestartTelegramCommands();
        }
    }
}
