using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Employee.Core;

namespace Employee.Desktop;
public partial class MainWindow
{
    private static bool NeedsCliInstallation(ProviderHealth status) => !status.Installed || status.StandaloneCliMissing;
    private static bool NoAdapter(ProviderHealth status) => status.Detail.Contains("No execution adapter", StringComparison.Ordinal) || status.Detail.Contains("intentionally does not execute", StringComparison.Ordinal);
    private void ProviderStatusActionLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not ProviderHealth status)
            return;
        button.Content = NoAdapter(status) ? "Detected · not runnable here" : status.Id == "responses-api" ? status.CanExecute ? "Check API…" : "Configure API…" : NeedsCliInstallation(status) ? "Install CLI…" : !status.CanExecute ? "Configure CLI…" : status.Id == "claude" && status.Authentication == "NotAuthenticated" ? "Sign in / verify…" : "Verify again";
        button.ToolTip = NoAdapter(status) ? "The CLI Tool API detected this agent; this manager has no headless adapter for it." : "Action for the current status; never installs software or runs inference.";
    }

    private async void ProviderStatusAction(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        IdleOnly();
        if (sender is not Button { DataContext: ProviderHealth status })
            return;
        if (NoAdapter(status))
        {
            MessageBox.Show(this, status.Detail, status.Id + " · detected by CLI Tool API", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (status.Id == "responses-api")
        {
            if (!status.CanExecute)
            {
                OpenSettingsSection("AI");
                ProviderChoice.SelectedItem = status.Id;
                LoadProvider();
                ProviderExe.Focus();
                return;
            }

            await VerifyProviderStatus(status.Id);
            return;
        }
        if (NeedsCliInstallation(status) || status.Id == "claude" && status.Authentication == "NotAuthenticated")
        {
            ShowProviderSetup(status);
            return;
        }

        if (!status.CanExecute)
        {
            OpenSettingsSection("AI");
            ProviderChoice.SelectedItem = status.Id;
            LoadProvider();
            ProviderExe.Focus();
            return;
        }

        await VerifyProviderStatus(status.Id);
    });
    private async Task VerifyProviderStatus(string id)
    {
        IdleOnly();
        if (!settings.UseAiCli && ManagerSettings.IsCli(id))
            throw new InvalidOperationException("External AI providers are turned off in Settings > AI & Models; " + id + " was not called.");
        detecting = true;
        Status.Text = $"Verifying {id}; status check only, no AI content is generated…";
        try
        {
            var provider = providers.FirstOrDefault(p => p.Id == id)
                ?? throw new InvalidOperationException(id + " has no execution adapter in this manager. Its agents and models still come from the CLI Tool API; update aihub or choose a supported agent.");
            var config = settings.Providers.Single(p => p.Id == id);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var result = await provider.DetectAsync(config, timeout.Token);
            health = health.Select(h => h.Id == id ? result : h).ToArray();
            HealthGrid.ItemsSource = health;
            // Explicit per-provider verification: once the CLI itself reports a verified login,
            // enable it (same rule as ProviderSetupService). Unverified logins are never enabled.
            var enabled = false;
            if (!config.Enabled && result.CanExecute && result.Authentication == "Authenticated")
            {
                config.Enabled = true;
                config.AllowProjectWrite = id is not ("ollama" or "custom-cli" or "clixad");
                if (config.Models.Count == 0)
                    config.Models.Add(new("", Enum.GetValues<WorkKind>()));
                store.SaveSettings(settings);
                LoadProvider();
                RefreshRoutes();
                enabled = true;
            }

            Status.Text = $"{id}: Found={result.Installed}, Ready={result.CanExecute}, Auth={result.Authentication}{(enabled ? ", Enabled=True (enabled after verifying sign-in)" : config.Enabled ? "" : ", Enabled=False")}. {result.Detail}";
        }
        finally
        {
            detecting = false;
        }
    }

    private void ShowProviderSetup(ProviderHealth status)
    {
        var login = status.Id == "claude" && !NeedsCliInstallation(status);
        var guide = status.Id switch
        {
            "claude" => "https://code.claude.com/docs/en/setup",
            "copilot" => "https://docs.github.com/en/copilot/how-tos/set-up/install-copilot-cli",
            "codex" => "https://developers.openai.com/codex/cli/",
            "gemini" => "https://geminicli.com/docs/get-started/installation/",
            "opencode" => "https://opencode.ai/docs/",
            "freebuff" => "https://freebuff.com/cli",
            "qoder" => "https://qoder.com",
            "antigravity" => "https://antigravity.google/docs/cli/",
            "ollama" => "https://ollama.com/download",
            _ => null
        };
        var dialog = new Window
        {
            Owner = this,
            Title = $"{status.Id} · {(login ? "CLI sign-in" : "CLI install")}",
            Width = 620,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var panel = new StackPanel
        {
            Margin = new Thickness(20)
        };
        var instructions = status.Id == "custom-cli"
            ? "Install the agent CLI yourself, then configure its executable, arguments and optional API key in Settings > AI & Models. Detection checks the configuration only; it does not start the agent."
            : login
                ? "Claude CLI is ready but not signed in. Open a terminal, run the configured CLI and sign in with /login. When done, click Verify again."
                : "Open the official guide to install the CLI. If it is already installed, choose Configure path. When done, click Verify again. Copilot Desktop and Copilot CLI are separate installs.";
        panel.Children.Add(new TextBlock { Text = instructions, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(new TextBox { Text = status.Detail, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 150, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var actions = new WrapPanel
        {
            Margin = new Thickness(0, 12, 0, 0)
        };
        if (guide != null)
        {
            var open = new Button
            {
                Content = "Open official guide"
            };
            open.Click += async (_, _) => await Guard(() =>
            {
                Process.Start(new ProcessStartInfo(guide) { UseShellExecute = true });
                return Task.CompletedTask;
            });
            actions.Children.Add(open);
        }

        var configure = new Button
        {
            Content = "Configure path"
        };
        configure.Click += (_, _) =>
        {
            dialog.Close();
            OpenSettingsSection("AI");
            ProviderChoice.SelectedItem = status.Id;
            LoadProvider();
            ProviderExe.Focus();
        };
        actions.Children.Add(configure);
        var verify = new Button
        {
            Content = "Verify again"
        };
        verify.Click += async (_, _) => await Guard(async () =>
        {
            verify.IsEnabled = false;
            try
            {
                await VerifyProviderStatus(status.Id);
                dialog.Close();
            }
            finally
            {
                verify.IsEnabled = true;
            }
        });
        actions.Children.Add(verify);
        panel.Children.Add(actions);
        dialog.Content = panel;
        dialog.ShowDialog();
    }
}
