using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Employee.Core;
using Employee.Infrastructure;

namespace Employee.Desktop;
// One row of the AI agents table: what the CLI Tool API reported plus the saved choice.
public sealed class ProviderAgentRow
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool Enabled { get; init; }
    public required string Status { get; init; }
    public required string Models { get; init; }
    public required string Usage { get; init; }
    public required string Runs { get; init; }
    public required string Auth { get; init; }
}

public partial class MainWindow
{
    private static bool NoAdapterFor(string id) => ManagerSettings.IsCli(id) && id is not ("custom-cli" or "responses-api") && !CliProvider.HasExecutionProfile(id) && !CliProvider.IsDetectedOnly(id);

    private static string BuildAgentSummary(ProviderSettings provider)
    {
        var userRated = provider.Models.Count(m => m.RatedByUser);
        var deprecated = provider.Models.Where(m => !string.IsNullOrWhiteSpace(ModelRatings.DeprecationFor(m.Id))).Select(m => m.Id + " (" + ModelRatings.DeprecationFor(m.Id) + ")").ToArray();
        var depText = deprecated.Length > 0 ? " · Deprecated: " + string.Join(", ", deprecated) : "";
        return $"Models available: {provider.Models.Count} (only models this account can run). {provider.ModelDiscoveryStatus}\nUser-rated overrides: {userRated}{depText}\nPriority {provider.Priority} · project writes {(provider.AllowProjectWrite ? "allowed" : "not allowed")}.";
    }

    private static string BuildModelSummary(ProviderSettings provider)
    {
        var userRated = provider.Models.Count(m => m.RatedByUser);
        var deprecated = provider.Models.Where(m => !string.IsNullOrWhiteSpace(ModelRatings.DeprecationFor(m.Id))).ToArray();
        var depText = deprecated.Length > 0 ? $" · {deprecated.Length} deprecated" : "";
        return $"Models: {provider.Models.Count} total · {userRated} user-rated override(s){depText}";
    }

    private void RefreshAgentsGrid()
    {
        if (ProviderAgentsGrid == null || settings == null)
            return;
        var selected = (ProviderAgentsGrid.SelectedItem as ProviderAgentRow)?.Id;
        var runs = store?.ProviderRunsToday() ?? new Dictionary<string, int>();
        var rows = settings.Providers.Where(p => ManagerSettings.IsCli(p.Id)).Select(p =>
        {
            var found = health.FirstOrDefault(h => h.Id.Equals(p.Id, StringComparison.OrdinalIgnoreCase));
            var status = found == null ? "Not checked yet"
                : NoAdapterFor(p.Id) ? "Detected · no adapter"
                : p.Id == "responses-api" ? !found.Installed ? "API not configured" : found.CanExecute ? "API reachable" : "API unavailable"
                : !found.Installed || found.StandaloneCliMissing ? "Not installed"
                : found.CanExecute ? "Ready"
                : "Installed · cannot run";
            var models = p.Models.Count == 0 ? "None" : p.Models.Count == 1 ? "1 model" : p.Models.Count + " models";
            return new ProviderAgentRow
            {
                Id = p.Id,
                Name = string.IsNullOrWhiteSpace(p.DisplayName) ? CliName(p.Id) : p.DisplayName,
                Enabled = p.Enabled,
                Status = status,
                Models = models,
                Usage = found == null || string.IsNullOrWhiteSpace(found.Usage) || found.Usage == "Unknown" ? "No quota data" : found.Usage,
                Runs = runs.TryGetValue(p.Id, out var count) ? count.ToString() : "0",
                Auth = found?.Authentication ?? "Unknown"
            };
        }).ToArray();
        ProviderAgentsGrid.ItemsSource = rows;
        if (selected != null)
            ProviderAgentsGrid.SelectedItem = rows.FirstOrDefault(r => r.Id == selected);
    }

    private void AgentEnabledClicked(object sender, RoutedEventArgs e) => _ = Guard(() =>
    {
        if (sender is not CheckBox { DataContext: ProviderAgentRow row } box || store == null)
            return Task.CompletedTask;
        var on = box.IsChecked == true;
        settings = store.LoadSettings();
        var provider = settings.Providers.Single(p => p.Id == row.Id);
        provider.Enabled = on;
        provider.TurnedOff = !on;
        if (on)
            provider.AllowProjectWrite = row.Id is not ("ollama" or "custom-cli" or "responses-api" or "clixad");
        store.SaveSettings(settings);
        RefreshAgentsGrid();
        LoadProvider();
        RefreshRoutes();
        Status.Text = on
            ? $"{row.Name} is on: tasks may use it." + (health.Any(h => h.Id == row.Id && h.CanExecute) ? "" : " Click Detect to check it first.")
            : $"{row.Name} is off: no requests will be sent to it.";
        return Task.CompletedTask;
    });

    private void AgentDetailsClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ProviderAgentRow row })
            ShowAgentDetails(row);
    }

    // A row click opens details; the Use checkbox and Details button keep their own behavior.
    private void ProviderAgentRowClicked(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source || FindAncestor<CheckBox>(source) != null || FindAncestor<Button>(source) != null)
            return;
        if (ItemsControl.ContainerFromElement(ProviderAgentsGrid, source) is DataGridRow { Item: ProviderAgentRow row })
            ShowAgentDetails(row);
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node != null)
        {
            if (node is T found)
                return found;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    private void ShowAgentDetails(ProviderAgentRow row)
    {
        var provider = settings.Providers.FirstOrDefault(p => p.Id == row.Id);
        if (provider == null)
            return;
        var found = health.FirstOrDefault(h => h.Id.Equals(row.Id, StringComparison.OrdinalIgnoreCase));
        var dialog = new Window
        {
            Owner = this,
            Title = row.Name + " · " + row.Id,
            Width = 940,
            Height = 660,
            MinWidth = 720,
            MinHeight = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var panel = new DockPanel { Margin = new Thickness(18) };
        var summary = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
            Text = $"{row.Status}. {found?.Detail ?? "Not checked yet; press Detect on the AI tab."}\n"
                + $"Usage: {row.Usage} · runs today: {row.Runs} · authentication: {row.Auth}\n"
                + BuildAgentSummary(provider)
        };
        DockPanel.SetDock(summary, Dock.Top);
        panel.Children.Add(summary);

        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        var actions = new WrapPanel();
        var enabled = new CheckBox { Content = "Use this agent", IsChecked = provider.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
        var write = new CheckBox
        {
            Content = "Allow project writes",
            IsChecked = provider.AllowProjectWrite,
            IsEnabled = row.Id is not ("custom-cli" or "ollama"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0)
        };
        actions.Children.Add(enabled);
        actions.Children.Add(write);
        actions.Children.Add(new TextBlock { Text = "Priority", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        var priority = new TextBox { Text = provider.Priority.ToString(), Width = 55, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
        actions.Children.Add(priority);
        TextBox? executable = null;
        if (!NoAdapterFor(row.Id) && row.Id != "custom-cli")
        {
            actions.Children.Add(new TextBlock { Text = row.Id == "responses-api" ? "Base URL" : "Executable (empty = PATH)", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            executable = new TextBox { Text = row.Id == "responses-api" ? provider.ResponsesApiBaseUrl : provider.Executable, Width = 300, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
            actions.Children.Add(executable);
        }

        var save = new Button { Content = "Save", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 6, 0) };
        save.Click += (_, _) => _ = Guard(() =>
        {
            settings = store.LoadSettings();
            var current = settings.Providers.Single(p => p.Id == row.Id);
            current.Enabled = enabled.IsChecked == true;
            current.TurnedOff = !current.Enabled;
            current.AllowProjectWrite = row.Id is not ("ollama" or "custom-cli" or "responses-api" or "clixad") && write.IsChecked == true;
            current.Priority = Number(priority.Text, 0, 100);
            if (executable != null)
            {
                if (row.Id == "responses-api")
                {
                    current.ResponsesApiBaseUrl = executable.Text.Trim();
                    ResponsesApiProvider.ParseBaseUrl(current.ResponsesApiBaseUrl);
                }
                else
                    current.Executable = executable.Text.Trim();
            }
            store.SaveSettings(settings);
            RefreshAgentsGrid();
            LoadProvider();
            RefreshRoutes();
            Status.Text = row.Name + " settings saved.";
            dialog.Close();
            return Task.CompletedTask;
        });
        actions.Children.Add(save);

        var verify = new Button { Content = "Verify now", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 6, 0) };
        verify.Click += async (_, _) =>
        {
            if (NoAdapterFor(row.Id))
            {
                MessageBox.Show(dialog, found?.Detail ?? "This manager has no execution adapter for this agent.", row.Name, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            dialog.Close();
            await Guard(() => VerifyProviderStatus(row.Id));
            RefreshAgentsGrid();
        };
        actions.Children.Add(verify);

        var updateWebRatings = new Button
        {
            Content = "Update ratings from web",
            Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(0, 0, 6, 0),
            ToolTip = "Consult public benchmark and pricing websites to update quality and usage rank for agent models"
        };
        actions.Children.Add(updateWebRatings);

        var advanced = new Button { Content = "Advanced settings…", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 6, 0) };
        advanced.Click += (_, _) =>
        {
            dialog.Close();
            OpenSettingsSection("AI");
            if (AdvancedAiSettings != null)
                AdvancedAiSettings.IsExpanded = true;
            ProviderChoice.SelectedItem = row.Id;
            LoadProvider();
        };
        actions.Children.Add(advanced);
        var close = new Button { Content = "Close", Padding = new Thickness(12, 4, 12, 4) };
        close.Click += (_, _) => dialog.Close();
        actions.Children.Add(close);
        footer.Children.Add(actions);
        footer.Children.Add(new TextBlock
        {
            Text = "Add, block or rate models, and edit custom CLI / Responses API connection details, under Advanced settings.",
            Foreground = UiArt.Brush("#B8B0CF"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        });
        DockPanel.SetDock(footer, Dock.Bottom);
        panel.Children.Add(footer);

        var modelsSummary = new TextBlock
        {
            Text = BuildModelSummary(provider),
            Foreground = UiArt.Brush("#B8B0CF"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 4),
            FontSize = 12
        };
        panel.Children.Add(modelsSummary);

        var models = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            ItemsSource = provider.Models.ToArray()
        };
        models.LoadingRow += (_, args) =>
        {
            if (args.Row.Item is ModelInfo m)
            {
                var capTooltip = ModelRatings.DescribeCapabilities(row.Id, m);
                var deprecation = ModelRatings.DeprecationFor(m.Id);
                var ratedFlag = m.RatedByUser ? " · User-rated override" : "";
                var depText = deprecation != null ? $" · {deprecation}" : "";
                args.Row.ToolTip = capTooltip + ratedFlag + depText;
            }
        };
        updateWebRatings.Click += async (_, _) =>
        {
            updateWebRatings.IsEnabled = false;
            try
            {
                await Guard(async () =>
                {
                    await RunWebRatingsUpdateAsync();
                    var updatedProvider = settings.Providers.Single(p => p.Id == row.Id);
                    models.ItemsSource = updatedProvider.Models.ToArray();
                    summary.Text = $"{row.Status}. {found?.Detail ?? "Not checked yet; press Detect on the AI tab."}\n"
                        + $"Usage: {row.Usage} · runs today: {row.Runs} · authentication: {row.Auth}\n"
                        + BuildAgentSummary(updatedProvider);
                    modelsSummary.Text = BuildModelSummary(updatedProvider);
                });
            }
            finally
            {
                updateWebRatings.IsEnabled = true;
            }
        };
        models.Columns.Add(new DataGridTextColumn { Header = "Model ID", Binding = new System.Windows.Data.Binding("Id"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        models.Columns.Add(new DataGridTextColumn { Header = "Task types", Binding = new System.Windows.Data.Binding("AllowedTaskTypes"), Width = new DataGridLength(200) });
        models.Columns.Add(new DataGridTextColumn { Header = "Quality", Binding = new System.Windows.Data.Binding("EffectiveQuality"), Width = new DataGridLength(65) });
        models.Columns.Add(new DataGridTextColumn { Header = "Usage", Binding = new System.Windows.Data.Binding("EffectiveCostRank"), Width = new DataGridLength(60) });
        models.Columns.Add(new DataGridTextColumn { Header = "Rating", Binding = new System.Windows.Data.Binding("RatingSource"), Width = new DataGridLength(200) });
        models.Columns.Add(new DataGridCheckBoxColumn { Header = "Blocked", Binding = new System.Windows.Data.Binding("Blocked"), Width = new DataGridLength(65) });
        models.Columns.Add(new DataGridTextColumn { Header = "Source / verification", Binding = new System.Windows.Data.Binding("VerificationDisplay"), Width = new DataGridLength(240) });
        panel.Children.Add(models);

        dialog.Content = panel;
        dialog.ShowDialog();
    }
}