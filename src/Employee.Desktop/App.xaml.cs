using System.IO;
using System.Windows;
using Employee.Infrastructure;

namespace Employee.Desktop;
public partial class App : Application
{
    private IDisposable? dataLease;
    private DataLocationService locations = null!;
    public bool HasDataOverride { get; private set; }
    public bool IsUpdateValidation { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ScrollAssist.Register();
        try
        {
            var index = Array.IndexOf(e.Args, "--data-dir");
            if (index >= 0 && index + 1 >= e.Args.Length)
                throw new InvalidOperationException("--data-dir requires a folder path.");
            HasDataOverride = index >= 0;
            locations = new(DataLocationService.FindApplicationRoot(AppContext.BaseDirectory));
            var data = locations.Resolve(HasDataOverride ? e.Args[index + 1] : null);
            dataLease = DataLocationService.Acquire(data);
            var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIEmployeeManager");
            if (!HasDataOverride && !File.Exists(locations.ConfigPath) && !File.Exists(Path.Combine(data, "manager.db")) && File.Exists(Path.Combine(legacy, "manager.db")))
            {
                using var oldLease = DataLocationService.Acquire(legacy);
                DataLocationService.CopyDatabase(legacy, data);
            }

            Directory.CreateDirectory(data);
            var healthIndex = Array.IndexOf(e.Args, "--update-health-file");
            var nonceIndex = Array.IndexOf(e.Args, "--update-health-nonce");
            string? healthFile = null, healthNonce = null;
            if (healthIndex >= 0 || nonceIndex >= 0)
            {
                if (healthIndex < 0 || nonceIndex < 0 || healthIndex + 1 >= e.Args.Length || nonceIndex + 1 >= e.Args.Length)
                    throw new InvalidOperationException("Incomplete startup readiness protocol.");
                healthFile = Path.GetFullPath(e.Args[healthIndex + 1]);
                healthNonce = e.Args[nonceIndex + 1];
                var allowed = Path.GetFullPath(Path.Combine(data, "updates")) + Path.DirectorySeparatorChar;
                if (!healthFile.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(healthFile) != "startup.ready" || !System.Text.RegularExpressions.Regex.IsMatch(healthNonce, "^[A-F0-9]{48}$"))
                    throw new InvalidOperationException("Invalid startup readiness scope.");
                IsUpdateValidation = true;
                HasDataOverride = false;
            }

            if (e.Args.Contains("--repair-routing"))
            {
                using var store = new SqliteStore(Path.Combine(data, "manager.db"));
                store.Backup(Path.Combine(data, "backups", "routing-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") + ".db"));
                var repaired = store.WorkItems().Where(w => w.IsGoal).Select(w => w.Id).Where(store.RepairMisclassifiedGoal).ToArray();
                File.WriteAllText(Path.Combine(data, "routing-repair-report.json"), System.Text.Json.JsonSerializer.Serialize(new { RepairedGoals = repaired, Inference = false, HistoryAndBudgetsPreserved = true }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                Shutdown();
                return;
            }

            if (e.Args.Contains("--configure-providers"))
            {
                using var store = new SqliteStore(Path.Combine(data, "manager.db"));
                var settings = store.LoadSettings();
                if (!settings.UseAiCli)
                {
                    File.WriteAllText(Path.Combine(data, "provider-setup-report.json"), "External AI providers are turned off in Settings > AI & Models; no remote provider was called.");
                    Shutdown();
                    return;
                }
                store.Backup(Path.Combine(data, "backups", "providers-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") + ".db"));
                var runner = new ProcessRunner();
                using var ollama = new OllamaProvider();
                var providers = new List<Employee.Core.IAIProvider>
                {
                    new ExternalCliProvider(runner),
                    new ResponsesApiProvider(),
                    ollama
                };
                foreach (var id in settings.Providers.Select(p => p.Id))
                    if (CliProvider.HasExecutionProfile(id) || CliProvider.IsDetectedOnly(id))
                        providers.Add(new CliProvider(id, runner));
                using var client = new CliToolApiClient();
                var discovery = await new CliToolProviderDiscovery(providers, client).DiscoverAsync(settings, CancellationToken.None);
                IReadOnlyList<ProviderSetupResult> report = discovery.Reachable ? CliToolProviderDiscovery.EnableUsable(settings, discovery.Health) : [];
                store.SaveSettings(settings);
                File.WriteAllText(Path.Combine(data, "provider-setup-report.json"), System.Text.Json.JsonSerializer.Serialize(new
                {
                    discovery.Detail,
                    discovery.Total,
                    discovery.Installed,
                    discovery.Usable,
                    Providers = report
                }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                Shutdown();
                return;
            }

            var window = new MainWindow(data, e.Args.Contains("--smoke"));
            if (!HasDataOverride)
                locations.Save(data);
            MainWindow = window;
            if (IsUpdateValidation)
            {
                window.IsEnabled = false;
                var readyFile = healthFile!;
                var readyNonce = healthNonce!;
                window.ContentRendered += (_, _) => File.WriteAllText(readyFile, readyNonce);
                var timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(200)
                };
                timer.Tick += (_, _) =>
                {
                    try
                    {
                        if (File.Exists(readyFile + ".commit") && File.ReadAllText(readyFile + ".commit") == readyNonce)
                        {
                            timer.Stop();
                            IsUpdateValidation = false;
                            window.IsEnabled = true;
                        }
                    }
                    catch (IOException)
                    {
                    }
                };
                timer.Start();
            }

            window.Show();
        }
        catch (Exception ex)
        {
            if (e.Args.Contains("--configure-providers") || e.Args.Contains("--repair-routing") || e.Args.Contains("--update-health-file"))
                Console.Error.WriteLine(ex.Message);
            else
                MessageBox.Show(ex.ToString(), "AI Employee Manager could not start");
            Shutdown(1);
        }
    }

    public void ChangeDataFolder(MainWindow current, string source, string destination)
    {
        if (HasDataOverride)
            throw new InvalidOperationException("Running with --data-dir. Open Run.bat to change the default folder.");
        IDisposable? targetLease = DataLocationService.Acquire(destination);
        MainWindow? next = null;
        try
        {
            DataLocationService.CopyDatabase(source, destination);
            next = new MainWindow(destination, false);
            next.Show();
            locations.Save(destination);
            MainWindow = next;
            current.Close();
            dataLease?.Dispose();
            dataLease = targetLease;
            targetLease = null;
        }
        catch
        {
            MainWindow = current;
            next?.Close();
            throw;
        }
        finally
        {
            targetLease?.Dispose();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        dataLease?.Dispose();
        base.OnExit(e);
    }
}
