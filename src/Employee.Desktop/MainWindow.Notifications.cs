using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using Employee.Core;

namespace Employee.Desktop;
public partial class MainWindow
{
    public sealed record Notice(string WorkId, string Title, DateTimeOffset At, bool Read = false)
    {
        public override string ToString() => $"{(Read ? "" : "● ")}{At.ToLocalTime():dd/MM HH:mm} · {Title}";
    }

    public sealed class NoticeJournal
    {
        public Dictionary<string, string> Seen { get; set; } = [];
        public List<Notice> Items { get; set; } = [];
        public bool WindowsEnabled { get; set; } = true;
    }

    private NoticeJournal? noticeJournal;
    private WindowsNotification? windowsNotification;
    private Window? noticeWindow;
    private ListBox? noticeList;
    private string NoticePath => Path.Combine(dataPath, "notifications.json");

    private void InitializeNotifications(bool smoke)
    {
        try
        {
            noticeJournal = File.Exists(NoticePath) ? JsonSerializer.Deserialize<NoticeJournal>(File.ReadAllText(NoticePath)) : null;
        }
        catch (Exception ex)when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Status.Text = "Cannot read notification history: " + ex.Message;
        }

        noticeJournal ??= new();
        WindowsNotifications.IsChecked = noticeJournal.WindowsEnabled;
        if (!smoke)
            SourceInitialized += (_, _) => windowsNotification = new(this, OpenNotifications);
        Closed += (_, _) =>
        {
            noticeWindow?.Close();
            windowsNotification?.Dispose();
        };
        ObserveNotifications(store.WorkItems().Where(w => !w.IsCoordinator && (w.IsGoal || w.ParentTaskId == null)).ToArray(), false);
    }

    private void ObserveNotifications(IReadOnlyList<WorkItem> tasks, bool alert = true)
    {
        if (noticeJournal == null)
            return;
        var changed = false;
        var fresh = new List<Notice>();
        foreach (var work in tasks)
        {
            var decisions = unified.Decisions(work).Select(d => d.Id).Order().ToArray();
            var signature = work.State + ":" + string.Join(",", decisions);
            var known = noticeJournal.Seen.TryGetValue(work.Id, out var previous);
            if (known && previous == signature)
                continue;
            noticeJournal.Seen[work.Id] = signature;
            changed = true;
            // Resolving a decision must not reannounce the remaining pending decisions.
            if (known && previous!.Split(':')[0] == work.State.ToString() && decisions.All(id => previous[(previous.IndexOf(':') + 1)..].Split(',').Contains(id)))
                continue;
            var title = work.State == WorkState.WrongAccess ? "Wrong access - correct task permissions" : work.State == WorkState.WrongProject ? "Wrong project - correct project/folder" : work.State == WorkState.WaitingForProvider ? "Waiting for AI - see usage/auth/availability reason" : decisions.Length > 0 ? "Approval needed" : work.State switch
            {
                WorkState.AwaitingApproval => "Approval needed to continue",
                WorkState.WaitingForPermission => "Permission needed",
                WorkState.NeedsReview => "Result ready for review",
                WorkState.Failed => "Task failed",
                WorkState.WaitingForProvider => "Waiting for an available AI",
                WorkState.Paused => "Task paused",
                WorkState.Completed => "Task completed",
                _ => null
            };
            // On first launch, surface only outstanding attention, not old completed work.
            if (title == null || (!known && work.State == WorkState.Completed))
                continue;
            var request = work.Request.Replace('\r', ' ').Replace('\n', ' ');
            fresh.Add(new(work.Id, title + " · " + request[..Math.Min(100, request.Length)], DateTimeOffset.UtcNow));
        }

        noticeJournal.Items.InsertRange(0, fresh);
        if (noticeJournal.Items.Count > 200)
            noticeJournal.Items.RemoveRange(200, noticeJournal.Items.Count - 200);
        foreach (var stale in noticeJournal.Seen.Keys.Except(tasks.Select(w => w.Id)).ToArray())
        {
            noticeJournal.Seen.Remove(stale);
            changed = true;
        }

        if (changed)
            SaveNotifications();
        UpdateNotificationBadge();
        if (alert && noticeJournal.WindowsEnabled && fresh.Count > 0)
            foreach (var notice in fresh)
                windowsNotification?.Show("AI Employee Manager", notice.Title, () => OpenNotice(notice));
    }

    private void SaveNotifications()
    {
        try
        {
            File.WriteAllText(NoticePath + ".tmp", JsonSerializer.Serialize(noticeJournal));
            File.Move(NoticePath + ".tmp", NoticePath, true);
        }
        catch (Exception ex)when (ex is IOException or UnauthorizedAccessException)
        {
            Status.Text = "Cannot save notifications: " + ex.Message;
        }
    }

    private void UpdateNotificationBadge()
    {
        var count = noticeJournal?.Items.Count(n => !n.Read) ?? 0;
        NotificationButton.Content = UiArt.Notification(count);
        NotificationButton.ToolTip = $"Notifications ({count})";
        NotificationButton.Foreground = new SolidColorBrush(count > 0 ? Color.FromRgb(216, 190, 255) : Color.FromRgb(238, 234, 251));
        TaskbarItemInfo ??= new TaskbarItemInfo();
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
            context.DrawEllipse(Brushes.Red, new Pen(Brushes.White, 1), new Point(8, 8), 6, 6);
        TaskbarItemInfo.Overlay = count > 0 ? new DrawingImage(drawing) : null;
        TaskbarItemInfo.Description = count > 0 ? $"{count} unread notifications" : "AI Employee Manager";
        if (noticeList != null)
            noticeList.ItemsSource = noticeJournal?.Items.ToArray();
    }

    private void ShowNotifications(object sender, RoutedEventArgs e) => OpenNotifications();
    private void BringNotificationToFront()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        // Raise the window on an explicit notification click without keeping it always on top.
        var wasTopmost = Topmost;
        try
        {
            Topmost = true;
            Activate();
        }
        finally
        {
            Topmost = wasTopmost;
        }

        SetForegroundWindow(new WindowInteropHelper(this).EnsureHandle());
        Focus();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);
    private void OpenNotifications()
    {
        BringNotificationToFront();
        if (noticeWindow != null)
        {
            noticeWindow.Activate();
            return;
        }

        var panel = new DockPanel
        {
            Margin = new Thickness(12)
        };
        var read = new Button
        {
            Content = "Mark all as read",
            HorizontalAlignment = HorizontalAlignment.Left
        };
        DockPanel.SetDock(read, Dock.Top);
        panel.Children.Add(read);
        noticeList = new ListBox
        {
            ItemsSource = noticeJournal!.Items.ToArray()
        };
        panel.Children.Add(noticeList);
        read.Click += (_, _) => MarkNotificationsRead();
        noticeList.MouseDoubleClick += (_, _) =>
        {
            if (noticeList.SelectedItem is not Notice notice)
                return;
            OpenNotice(notice);
            noticeWindow?.Close();
        };
        noticeWindow = new Window
        {
            Owner = this,
            Title = "Notifications · double-click to view task",
            Width = 760,
            Height = 420,
            Content = panel,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        noticeWindow.Closed += (_, _) =>
        {
            noticeWindow = null;
            noticeList = null;
        };
        noticeWindow.Show();
    }

    private void MarkNotificationsRead()
    {
        noticeJournal!.Items = noticeJournal.Items.Select(n => n with { Read = true }).ToList();
        SaveNotifications();
        UpdateNotificationBadge();
    }

    private void OpenNotice(Notice notice)
    {
        noticeWindow?.Close();
        BringNotificationToFront();
        noticeJournal!.Items = noticeJournal.Items.Select(n => n.WorkId == notice.WorkId && n.At == notice.At ? n with { Read = true } : n).ToList();
        SaveNotifications();
        UpdateNotificationBadge();
        NavigateShell("tasks");
        ShowTaskList(this, new());
        var work = store.WorkItems().FirstOrDefault(w => w.Id == notice.WorkId);
        if (work == null)
        {
            Status.Text = "The task linked to this notification has been deleted.";
            return;
        }

        if (!selectedTaskStates.Contains(work.State))
            SetTaskStateFilter([..selectedTaskStates, work.State]);
        RefreshTasks(work.Id);
        ShowTaskDetails(this, new());
    }

    private void ToggleWindowsNotifications(object sender, RoutedEventArgs e)
    {
        if (noticeJournal == null)
            return;
        noticeJournal.WindowsEnabled = WindowsNotifications.IsChecked == true;
        SaveNotifications();
    }

    private void VerifyNotificationSmoke()
    {
        var work = new WorkItem
        {
            ProjectId = store.Projects().First().Id,
            Request = "Notification fixture",
            IsGoal = true,
            State = WorkState.Running
        };
        store.SaveWork(work);
        RefreshTasks();
        var before = noticeJournal!.Items.Count;
        work.State = WorkState.NeedsReview;
        store.SaveWork(work);
        RefreshTasks();
        if (noticeJournal.Items.Count != before + 1 || TaskbarItemInfo?.Overlay == null)
            throw new InvalidOperationException("Notification or taskbar badge missing.");
        RefreshTasks();
        if (noticeJournal.Items.Count != before + 1)
            throw new InvalidOperationException("Repeated notification for unchanged task.");
        SetTaskStateFilter(WorkState.Running);
        OpenNotice(noticeJournal.Items.First(n => n.WorkId == work.Id));
        if ((TaskGrid.SelectedItem as WorkItem)?.Id != work.Id || noticeJournal.Items.First(n => n.WorkId == work.Id).Read != true)
            throw new InvalidOperationException("Notification did not reveal filtered task or mark it read.");
        MarkNotificationsRead();
        if (TaskbarItemInfo?.Overlay != null)
            throw new InvalidOperationException("Read badge was not cleared.");
        WindowsNotifications.IsChecked = false;
        ToggleWindowsNotifications(this, new());
        var saved = JsonSerializer.Deserialize<NoticeJournal>(File.ReadAllText(NoticePath))!;
        if (saved.WindowsEnabled || saved.Items.Any(n => !n.Read) || !saved.Seen.ContainsKey(work.Id))
            throw new InvalidOperationException("Notification history/settings were not persisted.");
        WindowsNotifications.IsChecked = true;
        ToggleWindowsNotifications(this, new());
        SetTaskStateFilter(Enum.GetValues<WorkState>());
        OpenNotifications();
        noticeWindow!.Close();
        File.WriteAllText(Path.Combine(dataPath, "notification-smoke.txt"), "PASS: state transition, deduplication, taskbar badge, filtered task navigation, mark read, history/settings persistence, notification window. Native Windows balloon requires manual desktop verification; smoke does not display system notifications.");
    }
}
