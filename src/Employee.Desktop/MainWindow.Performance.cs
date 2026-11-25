using System.IO;
using System.Net.Http;
using System.Windows;
using Employee.Core;
using Employee.Infrastructure;

namespace Employee.Desktop;
public partial class MainWindow
{
    private void LoadPerformanceSettings()
    {
        UsageLevelChoice.ItemsSource = Enum.GetValues<UsageLevel>();
        UsageLevelChoice.SelectedItem = settings.UsageLevel;
        LearnFromHistory.IsChecked = settings.UsePerformanceHistory;
        ParallelAgents.Text = settings.Automation.MaxParallelAgents.ToString();
        RefreshPerformance();
        RatingsUrl.Text = settings.RatingsSourceUrl;
        if (AutoRefreshWebRatings != null)
        {
            loading = true;
            AutoRefreshWebRatings.IsChecked = settings.AutoUpdateWebRatings;
            loading = false;
        }
        LoadRatings();
        _ = RefreshRatingsIfStale();
    }

    private void LoadRatings()
    {
        try
        {
            var summary = ratings.Load();
            RatingsStatus.Text = "Active ratings: " + summary;
            UpdateWebRatingsBanner(summary);
        }
        catch (Exception error)when (error is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            ModelRatings.Apply(null);
            RatingsStatus.Text = "Ratings file not used (" + error.Message + "). Built-in ratings are active.";
            UpdateWebRatingsBanner(ModelRatings.ActiveSource);
        }
    }

    private void UpdateWebRatingsBanner(string activeSummary)
    {
        if (WebRatingsBanner == null)
            return;
        var userRatedCount = settings.Providers.SelectMany(p => p.Models).Count(m => m.RatedByUser);
        var agentModelCount = settings.Providers.Where(p => ManagerSettings.IsCli(p.Id)).SelectMany(p => p.Models).Count(m => !string.IsNullOrWhiteSpace(m.Id));
        WebRatingsBanner.Text = $"Active ratings: {activeSummary} · {agentModelCount} catalog models · {userRatedCount} user-rated override(s)";
    }

    // A configured ratings URL is refreshed at most once a day; failures keep the current ratings.
    private async Task RefreshRatingsIfStale()
    {
        if (string.IsNullOrWhiteSpace(settings.RatingsSourceUrl) || ratings.LastUpdated > DateTimeOffset.UtcNow.AddDays(-1))
            return;
        try
        {
            RatingsStatus.Text = "Active ratings: " + await ratings.UpdateFromUrlAsync(settings.RatingsSourceUrl, CancellationToken.None);
            LoadProvider();
            RefreshRoutes();
        }
        catch (Exception error)when (error is InvalidOperationException or IOException or HttpRequestException or TaskCanceledException)
        {
            RatingsStatus.Text += " Automatic update failed: " + error.Message;
        }
    }

    // Background web ratings refresh when AutoUpdateWebRatings is enabled and ratings are stale.
    private async void RefreshWebRatingsIfStale()
    {
        if (!settings.AutoUpdateWebRatings || !settings.UseAiCli)
            return;
        if (ratings.LastUpdated > DateTimeOffset.UtcNow.AddDays(-1))
            return;
        try
        {
            var sourceNames = string.Join(", ", WebModelRatingsUpdater.DefaultSources.Select(s => s.Name));
            Status.Text = $"Background: consulting public benchmark and pricing websites ({sourceNames})…";
            RatingsStatus.Text = $"Background: updating model ratings from web ({sourceNames})…";

            var result = await ratings.UpdateFromWebAsync(settings, health, store, token: CancellationToken.None);
            settings = store.LoadSettings();

            var consulted = result.ConsultedSources.Count > 0 ? string.Join(", ", result.ConsultedSources) : "none";
            var warningsText = result.Warnings.Count > 0 ? " Warnings: " + string.Join(" | ", result.Warnings) : "";
            RatingsStatus.Text = $"Active ratings: {result.ActiveSummary} · {result.MatchedAgentModels}/{result.TotalAgentModels} agent models matched · Sources: {consulted}.{warningsText}";

            RefreshAgentsGrid();
            LoadProvider();
            RefreshRoutes();
            RefreshPerformance();

            UpdateWebRatingsBanner(result.ActiveSummary);
            Status.Text = "Background web ratings refresh complete.";
        }
        catch (Exception error)when (error is InvalidOperationException or IOException or HttpRequestException or TaskCanceledException)
        {
            // Keep current ratings; only update status text
            RatingsStatus.Text += $" Background web refresh failed: {error.Message}";
            LoadRatings();
        }
    }

    private void AutoRefreshWebRatingsChanged(object sender, RoutedEventArgs e)
    {
        if (loading)
            return;
        settings = store.LoadSettings();
        settings.AutoUpdateWebRatings = AutoRefreshWebRatings.IsChecked == true;
        store.SaveSettings(settings);
        if (settings.AutoUpdateWebRatings)
            _ = Task.Run(RefreshWebRatingsIfStale);
    }

    private async void UpdateRatingsNow(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var url = RatingsUrl.Text.Trim();
        settings = store.LoadSettings();
        settings.RatingsSourceUrl = url;
        store.SaveSettings(settings);
        if (url.Length == 0)
        {
            LoadRatings();
            Status.Text = "Ratings URL cleared.";
            return;
        }

        var summary = await ratings.UpdateFromUrlAsync(url, CancellationToken.None);
        RatingsStatus.Text = "Active ratings: " + summary;
        UpdateWebRatingsBanner(summary);
        LoadProvider();
        RefreshRoutes();
        Status.Text = "Model ratings updated from " + url;
    });

    private async void UpdateRatingsFromWeb(object sender, RoutedEventArgs e) => await Guard(RunWebRatingsUpdateAsync);

    private async Task RunWebRatingsUpdateAsync()
    {
        IdleOnly();
        settings = store.LoadSettings();
        detecting = true;
        if (UpdateWebRatingsButton != null)
            UpdateWebRatingsButton.IsEnabled = false;
        if (UpdateWebRatingsAdvancedButton != null)
            UpdateWebRatingsAdvancedButton.IsEnabled = false;
        try
        {
            var sourceNames = string.Join(", ", WebModelRatingsUpdater.DefaultSources.Select(s => s.Name));
            Status.Text = $"Consulting public benchmark and pricing websites ({sourceNames})…";
            RatingsStatus.Text = $"Updating model ratings from web ({sourceNames})…";

            var result = await ratings.UpdateFromWebAsync(settings, health, store, token: CancellationToken.None);
            settings = store.LoadSettings();

            var consulted = result.ConsultedSources.Count > 0 ? string.Join(", ", result.ConsultedSources) : "none";
            var warningsText = result.Warnings.Count > 0 ? " Warnings: " + string.Join(" | ", result.Warnings) : "";
            RatingsStatus.Text = $"Active ratings: {result.ActiveSummary} · {result.MatchedAgentModels}/{result.TotalAgentModels} agent models matched · Sources: {consulted}.{warningsText}";

            RefreshAgentsGrid();
            LoadProvider();
            RefreshRoutes();
            RefreshPerformance();

            UpdateWebRatingsBanner(result.ActiveSummary);

            var summary = $"Updated model ratings from {consulted}: {result.MatchedAgentModels}/{result.TotalAgentModels} agent models matched.";
            Status.Text = result.Warnings.Count > 0 ? $"{summary} ({result.Warnings.Count} warning(s); see Model ratings)." : summary;
            ShowToast("Model ratings updated", $"{result.MatchedAgentModels}/{result.TotalAgentModels} agent models matched ({consulted}).");
        }
        catch
        {
            LoadRatings();
            throw;
        }
        finally
        {
            detecting = false;
            if (UpdateWebRatingsButton != null)
                UpdateWebRatingsButton.IsEnabled = true;
            if (UpdateWebRatingsAdvancedButton != null)
                UpdateWebRatingsAdvancedButton.IsEnabled = true;
        }
    }

    private async void OpenRatingsFile(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        var path = ratings.EnsureFile();
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        Status.Text = "Edit the file, save it, then click Reload ratings file.";
        return Task.CompletedTask;
    });
    private async void ReloadRatings(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        ratings.Load();
        LoadRatings();
        LoadProvider();
        RefreshRoutes();
        Status.Text = RatingsStatus.Text;
        return Task.CompletedTask;
    });
    private void RefreshPerformance()
    {
        var router = ModelRouter.For(store);
        PerformanceGrid.ItemsSource = store.ModelPerformance().Select(row => row with { LearnedOffset = router.Offset(row.ProviderId, row.ModelId, row.Capability, settings) }).ToArray();
    }

    private async void ResetLearning(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        IdleOnly();
        if (MessageBox.Show("Ignore every result judged so far? Models go back to their ratings; run history and ratings are kept.", "Reset learning", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            return Task.CompletedTask;
        settings = store.LoadSettings();
        settings.LearningResetAt = DateTimeOffset.UtcNow;
        store.SaveSettings(settings);
        RefreshPerformance();
        RefreshRoutes();
        Status.Text = "Learning reset. New results start from the ratings.";
        return Task.CompletedTask;
    });
    private void RefreshPerformanceClick(object sender, RoutedEventArgs e) => RefreshPerformance();
    private async void SavePerformanceSettings(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        IdleOnly();
        settings = store.LoadSettings();
        settings.UsageLevel = (UsageLevel)UsageLevelChoice.SelectedItem;
        settings.RoutingPreference = RoutingPreference.Balanced;
        settings.UsePerformanceHistory = LearnFromHistory.IsChecked == true;
        store.SaveSettings(settings);
        Status.Text = $"Saved usage level {settings.UsageLevel} and history policy. New routing decisions use it.";
        return Task.CompletedTask;
    });
    private async void RateGood(object sender, RoutedEventArgs e) => await RateSelectedModel(true);
    private async void RatePoor(object sender, RoutedEventArgs e) => await RateSelectedModel(false);
    private Task RateSelectedModel(bool good) => Guard(() =>
    {
        var goal = SelectedWork();
        if (unified.IsRunning(goal.Id))
            throw new InvalidOperationException("Wait for this task or stop it before rating the result.");
        var work = StepsGrid.SelectedItem as WorkItem ?? unified.Steps(goal).LastOrDefault(w => w.Attempts > 0) ?? goal;
        if (work.State is not (WorkState.Completed or WorkState.NeedsReview))
            throw new InvalidOperationException("Select a finished worker result to rate.");
        if (!store.RateModelResult(work.Id, good))
            throw new InvalidOperationException("No measured successful worker run exists for this step. History starts with the updated version.");
        store.Log(work.Id, "ModelFeedback", good ? "User rated result good." : "User rated result needs improvement.");
        RefreshPerformance();
        Status.Text = "Rating saved for this model and task kind. Repeated clicks replace the same rating.";
        return Task.CompletedTask;
    });
}
