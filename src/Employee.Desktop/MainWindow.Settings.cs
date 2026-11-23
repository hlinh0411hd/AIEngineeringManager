using System.Windows;
using System.Windows.Controls;
using Employee.Core;
using Employee.Infrastructure;

namespace Employee.Desktop;
public partial class MainWindow
{
    private bool syncingSettingsProjects;
    private bool syncingOfficeMode;
    private bool loadingCliMode;
    private void InitializeOfficeSettings()
    {
        syncingOfficeMode = true;
        OfficeFullChoice.IsChecked = settings.VirtualOfficeEnabled;
        OfficeOffChoice.IsChecked = !settings.VirtualOfficeEnabled;
        syncingOfficeMode = false;
        OfficeView.SetEnabled(settings.VirtualOfficeEnabled);
        OfficeView.EnableRequested += () => SetOfficeEnabled(true);
    }

    private void OfficeModeChanged(object sender, RoutedEventArgs e)
    {
        if (!syncingOfficeMode && OfficeFullChoice != null && OfficeOffChoice != null && store != null)
            SetOfficeEnabled(OfficeFullChoice.IsChecked == true);
    }

    private void SetOfficeEnabled(bool enabled)
    {
        var fresh = store.LoadSettings();
        fresh.VirtualOfficeEnabled = enabled;
        store.SaveSettings(fresh);
        settings.VirtualOfficeEnabled = enabled;
        syncingOfficeMode = true;
        OfficeFullChoice.IsChecked = enabled;
        OfficeOffChoice.IsChecked = !enabled;
        syncingOfficeMode = false;
        OfficeView.SetEnabled(enabled);
    }
    // Runs when the Settings view is first shown, when the settings store already exists.
    private void LoadCliMode() => loadingCliMode = true;
    // The Settings tab realizes its content lazily, so this runs when the AI section is first shown.
    private void ShowCliMode()
    {
        loadingCliMode = true;
        try
        {
            UseCliChoice.IsChecked = settings.UseAiCli;
            NoCliChoice.IsChecked = !settings.UseAiCli;
        }
        finally
        {
            loadingCliMode = false;
        }
        if (CliToolApiUrl != null)
        {
            CliToolApiUrl.Text = settings.CliToolApiUrl;
            CliToolApiToken.Clear();
            ClearCliToolApiToken.IsChecked = false;
            RefreshCliToolApiStatus();
        }
        RefreshAgentsGrid();
    }

    private async void SaveCliToolApi(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        IdleOnly();
        settings = store.LoadSettings();
        var url = CliToolApiUrl.Text.Trim();
        if (!CliToolApiClient.TryParseBaseUrl(url, out _))
            throw new InvalidOperationException("Enter the aihub address, for example http://127.0.0.1:4777. Plain http is allowed only for this computer; use https otherwise.");
        settings.CliToolApiUrl = url;
        if (CliToolApiToken.Password.Length > 0)
            settings.ProtectedCliToolApiToken = SecretProtection.Protect(CliToolApiToken.Password.Trim());
        else if (ClearCliToolApiToken.IsChecked == true)
            settings.ProtectedCliToolApiToken = "";
        store.SaveSettings(settings);
        CliToolApiToken.Clear();
        ClearCliToolApiToken.IsChecked = false;
        RefreshCliToolApiStatus();
        Status.Text = "Saved the CLI Tool API connection. Click Detect providers to fetch the agent list and models.";
        return Task.CompletedTask;
    });

    private static string CliName(string id) => id switch
    {
        "codex" => "Codex (OpenAI)", "claude" => "Claude Code", "copilot" => "Copilot CLI", "gemini" => "Gemini CLI",
        "opencode" => "OpenCode", "freebuff" => "Freebuff", "commandcode" => "CommandCode", "qoder" => "Qoder", "antigravity" => "Antigravity CLI (agy)", "kilo" => "Kilo Code CLI", "cline" => "Cline CLI", "clixad" => "clixad (ad-funded)", "devin" => "Devin CLI", "custom-cli" => "Custom CLI agent", "responses-api" => "OpenAI-compatible Responses API", _ => id
    };

    // The choice is saved by its own handler (it fires even when the tab is never opened), so a
    // task can never run against a CLI the user turned off. Errors are shown, never thrown.
    private void CliModeChanged(object sender, RoutedEventArgs e) => _ = Guard(() =>
    {
        if (loadingCliMode || store == null || UseCliChoice?.IsChecked is not { } use)
            return Task.CompletedTask;
        IdleOnly();
        settings = store.LoadSettings();
        settings.UseAiCli = use;
        store.SaveSettings(settings);
        // Turning CLIs back on needs fresh detection; loading or unchecking keeps the status.
        if (use && health.Count > 0)
        {
            health = [];
            HealthGrid.ItemsSource = health;
        }

        RefreshRoutes();
        if (!shellLoading)
            Status.Text = use ? "External AI providers allowed. Detect capabilities before using them." : "External AI providers off: no CLI or direct API provider will be called. Local AI is unchanged.";
        return Task.CompletedTask;
    });

    private void InitializeSettingsSections()
    {
        SyncSettingsProjects();
        SettingsSections.SelectionChanged += (_, e) =>
        {
            if (!ReferenceEquals(e.Source, SettingsSections))
                return;
            if (SettingsSections.SelectedItem == SettingsLocalSection)
                RefreshLocalModelChoices();
            if (SettingsSections.SelectedItem == SettingsAISection)
            {
                RefreshPerformance();
                ShowCliMode();
            }
        };
    }

    private void OpenSectionSettings(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string section })
            OpenSettingsSection(section);
    }

    private void OpenSettingsSection(string section)
    {
        if (section == "Testing")
        {
            NavigateShell("testing");
            return;
        }

        NavigateShell("settings");
        SettingsSections.SelectedItem = section switch
        {
            "Projects" => SettingsProjectsSection,
            "Tasks" => SettingsTasksSection,
            "Agents" => SettingsAgentsSection,
            "AI" => SettingsAISection,
            "Local" => SettingsLocalSection,
            "Tools" => SettingsToolsSection,
            "Workflows" => SettingsWorkflowsSection,
            "Notifications" => SettingsNotificationsSection,
            "VirtualOffice" => SettingsOfficeSection,
            "Updates" => SettingsUpdatesSection,
            _ => SettingsGeneralSection
        };
        if (section == "Local")
            RefreshLocalModelChoices();
        if (section == "AI")
            ShowCliMode();
    }

    private void OpenWorkflowWorkspace(object sender, RoutedEventArgs e) => NavigateShell("workflows");
    private void OpenUpdateWorkspace(object sender, RoutedEventArgs e) => NavigateShell("updates");
    private void SyncSettingsProjects()
    {
        if (syncingSettingsProjects || SettingsProjectChoice == null || ProjectGrid == null)
            return;
        syncingSettingsProjects = true;
        try
        {
            SettingsProjectChoice.ItemsSource = ProjectGrid.ItemsSource;
            SettingsProjectChoice.SelectedItem = ProjectGrid.SelectedItem;
        }
        finally
        {
            syncingSettingsProjects = false;
        }
    }

    private void SettingsProjectChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncingSettingsProjects)
            return;
        if (SettingsProjectChoice.SelectedItem is ProjectInfo project)
            ProjectGrid.SelectedItem = ProjectGrid.Items.Cast<ProjectInfo>().FirstOrDefault(p => p.Id == project.Id);
    }

    private async void SaveAgentSectionSettings(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        IdleOnly();
        var slots = Number(ParallelAgents.Text, 1, 4);
        var reserve = Number(MemoryReserve.Text, 512, 32768);
        settings = store.LoadSettings();
        settings.Automation.MaxParallelAgents = slots;
        settings.AdaptiveParallelism = AdaptiveSlots.IsChecked == true;
        settings.MinimumFreeMemoryMb = reserve;
        store.SaveSettings(settings);
        lastResourceRefresh = DateTimeOffset.MinValue;
        RefreshResources();
        Status.Text = "Saved agent count and RAM limit.";
        return Task.CompletedTask;
    });
    private async void SaveAIBudgetSettings(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        IdleOnly();
        var calls = Number(DailyCallLimit.Text, 0, 10000);
        settings = store.LoadSettings();
        settings.MaxDailyCalls = calls;
        store.SaveSettings(settings);
        lastResourceRefresh = DateTimeOffset.MinValue;
        RefreshResources();
        Status.Text = "Saved daily AI call limit.";
        return Task.CompletedTask;
    });
}
