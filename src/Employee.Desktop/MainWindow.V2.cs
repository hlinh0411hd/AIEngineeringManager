using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using Employee.Core;
using Employee.Infrastructure;

namespace Employee.Desktop;
public partial class MainWindow
{
    private DateTimeOffset lastResourceRefresh;
    private void LoadV2Extras()
    {
        DailyCallLimit.Text = settings.MaxDailyCalls.ToString();
        AdaptiveSlots.IsChecked = settings.AdaptiveParallelism;
        MemoryReserve.Text = settings.MinimumFreeMemoryMb.ToString();
        ScheduleDate.SelectedDate = DateTime.Today;
        ScheduleTime.Text = DateTime.Now.AddMinutes(10).ToString("HH:mm");
        ReportDate.SelectedDate = DateTime.Today;
        RefreshResources();
        RefreshMemory();
    }

    private void RefreshResources()
    {
        if ((DateTimeOffset.UtcNow - lastResourceRefresh).TotalSeconds < 5)
            return;
        lastResourceRefresh = DateTimeOffset.UtcNow;
        ResourceStatus.Text = (ResourceMonitor.Snapshot()?.ToString() ?? "RAM telemetry unavailable") + $" · effective agent limit {ResourceMonitor.EffectiveSlots(settings)} · calls today {store.CallsOnDay(DateTime.Now.ToString("yyyy-MM-dd"))}/{(settings.MaxDailyCalls == 0 ? "unlimited" : settings.MaxDailyCalls.ToString())}. Calls are reservations, not provider token/quota measurements.";
    }

    private async void SaveUsageAndResources(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        IdleOnly();
        settings.MaxDailyCalls = Number(DailyCallLimit.Text, 0, 10000);
        settings.AdaptiveParallelism = AdaptiveSlots.IsChecked == true;
        settings.MinimumFreeMemoryMb = Number(MemoryReserve.Text, 512, 32768);
        store.SaveSettings(settings);
        lastResourceRefresh = DateTimeOffset.MinValue;
        RefreshResources();
        return Task.CompletedTask;
    });
    private DateTimeOffset? ReadSchedule()
    {
        if (ScheduleEnabled.IsChecked != true)
            return null;
        if (ScheduleDate.SelectedDate is not { } day || !TimeSpan.TryParseExact(ScheduleTime.Text, "hh\\:mm", CultureInfo.InvariantCulture, out var time))
            throw new InvalidOperationException("Choose a date and enter time HH:mm (local computer time).");
        var local = DateTime.SpecifyKind(day.Date + time, DateTimeKind.Unspecified);
        if (TimeZoneInfo.Local.IsInvalidTime(local) || TimeZoneInfo.Local.IsAmbiguousTime(local))
            throw new InvalidOperationException("This time is ambiguous or unavailable because of daylight-saving changes. Choose another time.");
        var schedule = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
        if (schedule <= DateTimeOffset.Now)
            throw new InvalidOperationException("Scheduled time must be in the future.");
        return schedule;
    }

    private async void EnableAvailableProviders(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        IdleOnly();
        if (!settings.UseAiCli)
            throw new InvalidOperationException("External AI providers are turned off in Settings > AI & Models. Turn them on first; no remote provider was called.");
        detecting = true;
        try
        {
            store.Backup(Path.Combine(dataPath, "backups", "providers-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") + ".db"));
            using var client = new CliToolApiClient();
            var result = await new CliToolProviderDiscovery(providerInstances, client).DiscoverAsync(settings, CancellationToken.None);
            cliToolApiStatus = result.Detail;
            RefreshCliToolApiStatus();
            var known = providerInstances.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            RebuildProviders();
            if (result.Reachable && providerInstances.Any(p => !known.Contains(p.Id) && CliProvider.HasExecutionProfile(p.Id)))
            {
                result = await new CliToolProviderDiscovery(providerInstances, client).DiscoverAsync(settings, CancellationToken.None);
                cliToolApiStatus = result.Detail;
                RefreshCliToolApiStatus();
            }
            health = result.Health;
            HealthGrid.ItemsSource = health;
            IReadOnlyList<ProviderSetupResult> report = result.Reachable ? CliToolProviderDiscovery.EnableUsable(settings, health) : [];
            store.SaveSettings(settings);
            File.WriteAllText(Path.Combine(dataPath, "provider-setup-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            settings = store.LoadSettings();
            ModelCatalogMerge.AssignAutomaticRoles(settings, health, store);
            store.SaveSettings(settings);
            RefreshProviderChoices();
            LoadProvider();
            RefreshRoutes();
            RefreshAgentsGrid();
            Status.Text = result.Detail + (report.Count == 0 ? "" : " " + string.Join(" · ", report.Select(r => $"{r.Provider}: {(r.Enabled ? "enabled" : r.Ready ? "not enabled" : "unavailable")}")));
            HistoryText.Text = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        }
        finally
        {
            detecting = false;
        }
    });
    private void RefreshMemory()
    {
        if (store == null || MemoryList == null)
            return;
        var project = ProjectGrid.SelectedItem as ProjectInfo;
        MemoryList.ItemsSource = project == null ? null : store.Memories(project.Id);
        MemoryText.Clear();
    }

    private void MemorySelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (MemoryList.SelectedItem is ProjectMemory memory)
            MemoryText.Text = memory.Text;
    }

    private async void SaveProjectMemory(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        var project = ProjectGrid.SelectedItem as ProjectInfo ?? throw new InvalidOperationException("Choose a project.");
        var selected = MemoryList.SelectedItem as ProjectMemory;
        if (unified.CurrentGoalIds.Select(id => store.WorkItems().Single(w => w.Id == id)).Any(w => unified.SessionFor(w)?.ProjectIds.Contains(project.Id) == true))
            throw new InvalidOperationException("Pause this project's active goal before changing its memory.");
        store.SaveMemory(new(selected?.Id ?? Guid.NewGuid().ToString("N"), project.Id, MemoryText.Text.Trim(), DateTimeOffset.UtcNow, selected?.WorkId));
        RefreshMemory();
        Status.Text = "Saved project memory; it does not grant AI permissions.";
        return Task.CompletedTask;
    });
    private async void DeleteProjectMemory(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        IdleOnly();
        var project = ProjectGrid.SelectedItem as ProjectInfo ?? throw new InvalidOperationException("Choose a project.");
        var memory = MemoryList.SelectedItem as ProjectMemory ?? throw new InvalidOperationException("Choose a memory entry.");
        store.DeleteMemory(project.Id, memory.Id);
        RefreshMemory();
        return Task.CompletedTask;
    });
    private async void RememberReviewedTask(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        var work = SelectedWork();
        if (work.State != WorkState.Completed)
            throw new InvalidOperationException("Confirm the final result as complete before saving it as reviewed project memory.");
        var result = unified.Report(work).Result;
        store.SaveMemory(new(Guid.NewGuid().ToString("N"), work.ProjectId, $"Reviewed task: {work.Request[..Math.Min(2000, work.Request.Length)]}\n" + result[..Math.Min(5000, result.Length)], DateTimeOffset.UtcNow, work.Id));
        RefreshMemory();
        Status.Text = "Saved reviewed result for this project.";
        return Task.CompletedTask;
    });
    private async void ShowDailyReport(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        HistoryText.Text = DailyReportService.Create(store, ReportDate.SelectedDate ?? DateTime.Today);
        return Task.CompletedTask;
    });
    private async void ExportDailyReport(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        var date = ReportDate.SelectedDate ?? DateTime.Today;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"employee-report-{date:yyyy-MM-dd}.md",
            Filter = "Markdown|*.md",
            InitialDirectory = dataPath
        };
        if (dialog.ShowDialog(this) == true)
            File.WriteAllText(dialog.FileName, DailyReportService.Create(store, date));
        return Task.CompletedTask;
    });
}
