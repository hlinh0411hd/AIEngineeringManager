using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Employee.Desktop;
public partial class MainWindow
{
    private readonly DispatcherTimer taskRetentionTimer = new()
    {
        Interval = TimeSpan.FromHours(1)
    };
    private void InitializeTaskRetention(bool smoke)
    {
        TaskRetentionDays.Text = settings.TaskRetentionDays.ToString();
        if (smoke || ((App)Application.Current).IsUpdateValidation)
            return;
        taskRetentionTimer.Tick += (_, _) => CleanupExpiredTasks();
        CleanupExpiredTasks();
        taskRetentionTimer.Start();
    }

    private void CleanupExpiredTasks()
    {
        if (closing || shuttingDown)
            return;
        try
        {
            if (store.CleanupExpiredTasks(DateTimeOffset.UtcNow) > 0)
                RefreshTasks();
        }
        catch (Exception ex)
        {
            Status.Text = "Task retention cleanup failed: " + ex.Message;
            try
            {
                store.Log("task-retention", "TaskRetentionError", ex.ToString());
            }
            catch (Exception logError)
            {
                // Keep a fallback if the database itself is unavailable.
                try
                {
                    File.AppendAllText(Path.Combine(dataPath, "task-retention-errors.log"), $"{DateTimeOffset.UtcNow:O} {ex}\nLogging failed: {logError}\n");
                }
                catch (Exception fileError)
                {
                    System.Diagnostics.Trace.TraceError($"Task retention: {ex}; logging: {logError}; fallback: {fileError}");
                }
            }
        }
    }

    private async void SaveTaskRetention(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        var days = Number(TaskRetentionDays.Text, 1, 3650);
        var saved = store.LoadSettings();
        saved.TaskRetentionDays = days;
        store.SaveSettings(saved);
        settings.TaskRetentionDays = days;
        Status.Text = "Task retention saved. The next cleanup will use the new value.";
        return Task.CompletedTask;
    });
}
