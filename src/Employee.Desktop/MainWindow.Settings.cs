using System.Windows;
using System.Windows.Controls;
using Employee.Core;

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
        RefreshCliToggles();
    }

    private static string CliName(string id) => id switch
    {
        "codex" => "Codex (OpenAI)", "claude" => "Claude Code", "copilot" => "Copilot CLI", "gemini" => "Gemini CLI",
        "opencode" => "OpenCode", "freebuff" => "Freebuff", "commandcode" => "CommandCode", "qoder" => "Qoder", "antigravity" => "Antigravity CLI (agy)", _ => id
    };

    // One checkbox per CLI: ticked = tasks may send it requests. Ollama is set up under Local AI.
    private void RefreshCliToggles()
    {
        if (CliToggles == null || settings == null)
            return;
        CliToggles.Children.Clear();
        foreach (var provider in settings.Providers.Where(p => ManagerSettings.IsCli(p.Id)))
        {
            var found = health.FirstOrDefault(h => h.Id == provider.Id);
            var state = found == null ? "not checked yet" : !found.Installed || found.StandaloneCliMissing ? "not installed" : found.CanExecute ? "installed, ready" : "installed, cannot run headless";
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
            var box = new CheckBox { Content = CliName(provider.Id), IsChecked = provider.Enabled, MinWidth = 180, VerticalAlignment = VerticalAlignment.Center, Tag = provider.Id };
            box.Checked += CliToggleChanged;
            box.Unchecked += CliToggleChanged;
            row.Children.Add(box);
            row.Children.Add(new TextBlock { Text = state, Foreground = UiArt.Brush(found?.CanExecute == true ? "#8FD19E" : "#9D94B5"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
            CliToggles.Children.Add(row);
        }
    }

    // Saved at once. Turning a CLI on also gives it full permissions, like Detect does.
    private void CliToggleChanged(object sender, RoutedEventArgs e) => _ = Guard(() =>
    {
        if (sender is not CheckBox { Tag: string id } box || store == null)
            return Task.CompletedTask;
        var on = box.IsChecked == true;
        settings = store.LoadSettings();
        var provider = settings.Providers.Single(p => p.Id == id);
        provider.Enabled = on;
        provider.TurnedOff = !on;
        if (on)
            provider.AllowProjectWrite = id != "ollama";
        store.SaveSettings(settings);
        LoadProvider();
        RefreshRoutes();
        Status.Text = on ? $"{CliName(id)} is on: tasks may use it." + (health.Any(h => h.Id == id && h.CanExecute) ? "" : " Click Detect so the app checks it first.") : $"{CliName(id)} is off: no requests will be sent to it.";
        return Task.CompletedTask;
    });

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
            Status.Text = use ? "AI CLIs allowed. Detect capabilities before using them." : "AI CLIs off: no CLI will be started for tasks, detection, model listing, plan drafts or refinement. Local AI is unchanged.";
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
