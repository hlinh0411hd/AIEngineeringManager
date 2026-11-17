using System.Text.Json;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Employee.Core;
using Employee.Infrastructure;

namespace Employee.Desktop;
public partial class TestSettingsView : UserControl
{
    private readonly SqliteStore store;
    private readonly SmartTestingService testing;
    private readonly Func<bool> blocked;
    private readonly Func<WorkItem?> currentWork;
    private readonly Action changed;
    private Task? operation;
    private CancellationTokenSource? cancellation;
    public bool IsBusy => operation is { IsCompleted: false };

    public event Action<ProjectInfo, string>? SuiteRequested;
    public TestSettingsView(SqliteStore store, SmartTestingService testing, Func<bool> blocked, Func<WorkItem?> currentWork, Action changed)
    {
        InitializeComponent();
        this.store = store;
        this.testing = testing;
        this.blocked = blocked;
        this.currentWork = currentWork;
        this.changed = changed;
        Loaded += (_, _) => Refresh();
        Refresh();
    }

    public void Refresh()
    {
        var id = (Projects.SelectedItem as ProjectInfo)?.Id;
        Projects.ItemsSource = store.Projects();
        Projects.SelectedItem = Projects.Items.Cast<ProjectInfo>().FirstOrDefault(p => p.Id == id) ?? Projects.Items.Cast<ProjectInfo>().FirstOrDefault();
        RefreshProject();
    }

    private void ProjectChanged(object sender, SelectionChangedEventArgs e)
    {
        if (store != null)
            RefreshProject();
    }

    private void RefreshProject()
    {
        var id = (Projects.SelectedItem as ProjectInfo)?.Id;
        var taskId = (TargetTasks.SelectedItem as WorkItem)?.Id;
        var profileId = (Profiles.SelectedItem as ProjectTestProfile)?.Id;
        TargetTasks.ItemsSource = store.WorkItems().Where(w => w.ProjectId == id && !w.IsCoordinator).ToArray();
        TargetTasks.SelectedItem = TargetTasks.Items.Cast<WorkItem>().FirstOrDefault(w => w.Id == taskId) ?? TargetTasks.Items.Cast<WorkItem>().FirstOrDefault(w => w.Id == currentWork()?.Id);
        Profiles.ItemsSource = store.TestProfiles().Where(p => p.ProjectId == id).ToArray();
        Profiles.SelectedItem = Profiles.Items.Cast<ProjectTestProfile>().FirstOrDefault(p => p.Id == profileId);
        Jobs.ItemsSource = store.TestJobs().Where(j => j.ProjectId == id).OrderByDescending(j => j.At).ToArray();
    }

    private WorkItem Target() => TargetTasks.SelectedItem as WorkItem ?? throw new InvalidOperationException("Choose the task in this project that receives results.");
    private async void RunFull(object sender, RoutedEventArgs e) => await Guard(() => QueueAndRun(false, false));
    private async void RunUpdated(object sender, RoutedEventArgs e) => await Guard(() => QueueAndRun(true, false));
    private async void RunSelected(object sender, RoutedEventArgs e) => await Guard(() => QueueAndRun(false, true));
    private async Task QueueAndRun(bool updated, bool selected)
    {
        Idle();
        var work = Target();
        string[] paths = ["project"];
        var snapshot = "User requested full test";
        if (updated)
        {
            var proposal = store.Logs(work.Id).LastOrDefault(l => l.Event == "EditProposal");
            var edits = proposal == null ? null : FileEditService.Parse(proposal.Message);
            if (edits == null || edits.Edits.Length == 0)
                throw new InvalidOperationException("The task has no edit proposal to identify updated parts. Choose a subtask with changes or test everything.");
            paths = edits.Edits.Select(e => e.Path).ToArray();
            snapshot = proposal!.Message;
        }

        var profile = selected ? Profiles.SelectedItem as ProjectTestProfile ?? throw new InvalidOperationException("Select a test suite.") : null;
        var jobs = testing.Enqueue(work, paths, snapshot, profile?.Id, true, updated);
        if (jobs.Count == 0)
            throw new InvalidOperationException("No test suite is allowed to run yet. Review and save its settings first.");
        await RunJobs(jobs.Where(j => j.State == TestJobState.Pending).ToArray());
        Notice.Text = $"Processed {jobs.Count} test suites. See status and output below.";
    }

    private void Generate(object sender, RoutedEventArgs e) => RequestSuite(false);
    private void GenerateUpdate(object sender, RoutedEventArgs e) => RequestSuite(true);
    private void RequestSuite(bool update)
    {
        _ = Guard(() =>
        {
            Idle();
            var project = Projects.SelectedItem as ProjectInfo ?? throw new InvalidOperationException("Select a project.");
            var profile = update ? Profiles.SelectedItem as ProjectTestProfile ?? throw new InvalidOperationException("Select the test suite to update.") : null;
            SuiteRequested?.Invoke(project, (update ? "Update" : "Create") + " a test suite for the project. Inspect the existing source and test framework; propose test cases, coverage, test files and a ProjectTestProfile JSON config (Name, Executable, Arguments, TimeoutSeconds, FullValidation, SourcePaths). Do not run tests, install dependencies or edit files in this review step. Return the result for the user to check and edit before applying. The config must be Enabled=false. Current suite: " + (profile == null ? "none" : JsonSerializer.Serialize(profile)));
            return Task.CompletedTask;
        });
    }

    private void ShowSuite(object sender, RoutedEventArgs e)
    {
        if (Profiles.SelectedItem is ProjectTestProfile p)
            SuiteJson.Text = JsonSerializer.Serialize(p, new JsonSerializerOptions { WriteIndented = true });
    }

    private async void ImportSuite(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        Idle();
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Test suite JSON|*.json"
        };
        if (dialog.ShowDialog() == true)
        {
            if (new FileInfo(dialog.FileName).Length > 1000000)
                throw new InvalidOperationException("JSON exceeds 1 MB.");
            SuiteJson.Text = File.ReadAllText(dialog.FileName);
        }

        return Task.CompletedTask;
    });
    private async void SaveSuite(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        Idle();
        var project = Projects.SelectedItem as ProjectInfo ?? throw new InvalidOperationException("Select a project.");
        var profile = JsonSerializer.Deserialize<ProjectTestProfile>(SuiteJson.Text) ?? throw new InvalidOperationException("Invalid JSON.");
        profile.Id = Guid.NewGuid().ToString("N");
        profile.ProjectId = project.Id;
        profile.Enabled = false;
        SmartTestingService.ValidateProfile(profile);
        store.SaveTestProfile(profile);
        Refresh();
        Notice.Text = "Saved a new version, not yet allowed to run. Select the suite to review it and enable Allow execution.";
        return Task.CompletedTask;
    });
    private async void ExportSuite(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        Idle();
        JsonSerializer.Deserialize<ProjectTestProfile>(SuiteJson.Text);
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Test suite JSON|*.json",
            DefaultExt = ".json",
            FileName = "test-suite.json"
        };
        if (dialog.ShowDialog() == true)
            File.WriteAllText(dialog.FileName, SuiteJson.Text);
        return Task.CompletedTask;
    });
    private void Idle()
    {
        if (IsBusy || blocked())
            throw new InvalidOperationException("Wait for the current task or test to finish.");
    }

    private async Task Guard(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Notice.Text = ex.Message;
        }
    }

    private async void Save(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        Idle();
        var project = Projects.SelectedItem as ProjectInfo ?? throw new InvalidOperationException("Select a project.");
        var profile = Profiles.SelectedItem as ProjectTestProfile ?? new();
        if (!int.TryParse(Timeout.Text, out var seconds))
            throw new InvalidOperationException("Enter a timeout in seconds.");
        profile.ProjectId = project.Id;
        profile.Name = ProfileName.Text.Trim();
        profile.Executable = Executable.Text.Trim();
        profile.Arguments = JsonSerializer.Deserialize<string[]>(Arguments.Text) ?? [];
        profile.TimeoutSeconds = seconds;
        profile.Enabled = Enabled.IsChecked == true;
        profile.FullValidation = Full.IsChecked == true;
        profile.SourcePaths = SourcePaths.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        SmartTestingService.ValidateProfile(profile);
        store.SaveTestProfile(profile);
        Refresh();
        Notice.Text = "Saved. Tasks will use enabled commands when testing is needed.";
        return Task.CompletedTask;
    });
    private void NewProfile(object sender, RoutedEventArgs e)
    {
        if (IsBusy)
            return;
        Profiles.SelectedItem = null;
        ProfileName.Clear();
        Executable.Clear();
        Arguments.Text = "[]";
        Enabled.IsChecked = false;
        Full.IsChecked = false;
        SourcePaths.Clear();
    }

    private void ProfileSelected(object sender, SelectionChangedEventArgs e)
    {
        if (Profiles.SelectedItem is not ProjectTestProfile p)
            return;
        Projects.SelectedItem = Projects.Items.Cast<ProjectInfo>().FirstOrDefault(x => x.Id == p.ProjectId);
        ProfileName.Text = p.Name;
        Executable.Text = p.Executable;
        Arguments.Text = JsonSerializer.Serialize(p.Arguments);
        Timeout.Text = p.TimeoutSeconds.ToString();
        Enabled.IsChecked = p.Enabled;
        Full.IsChecked = p.FullValidation;
        SourcePaths.Text = string.Join(Environment.NewLine, p.SourcePaths);
    }

    private void JobSelected(object sender, SelectionChangedEventArgs e)
    {
        if (Jobs.SelectedItem is TestJob j)
            Output.Text = j.Output;
    }

    private async void Queue(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        Idle();
        var work = Target();
        testing.Enqueue(work, ["project"], "User requested test", force: true);
        Refresh();
        changed();
        return Task.CompletedTask;
    });
    private async Task RunJobs(IEnumerable<TestJob> jobs)
    {
        cancellation = new();
        try
        {
            operation = RunAll();
            await operation;
        }
        finally
        {
            operation = null;
            cancellation.Dispose();
            cancellation = null;
            Refresh();
            changed();
        }

        async Task RunAll()
        {
            foreach (var job in jobs)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                await testing.RunAsync(job, cancellation.Token);
            }
        }
    }

    private async void Run(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        Idle();
        await RunJobs(store.TestJobs().Where(j => j.ProjectId == (Projects.SelectedItem as ProjectInfo)?.Id && j.State == TestJobState.Pending).ToArray());
    });
    private async void Retry(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        Idle();
        var job = Jobs.SelectedItem as TestJob ?? throw new InvalidOperationException("Select a test job.");
        if (job.State is not (TestJobState.Interrupted or TestJobState.Failed))
            throw new InvalidOperationException("Only failed or interrupted jobs can be retried.");
        job.State = TestJobState.Pending;
        store.SaveTestJob(job);
        Refresh();
        return Task.CompletedTask;
    });
    private async void Stop(object sender, RoutedEventArgs e) => await Guard(StopAsync);
    public async Task StopAsync()
    {
        cancellation?.Cancel();
        if (operation != null)
            try
            {
                await operation;
            }
            catch (OperationCanceledException)
            {
            }
    }

    public async Task QueueAppliedAsync(WorkItem work)
    {
        var proposal = store.Logs(work.Id).LastOrDefault(l => l.Event == "EditProposal");
        var edits = proposal == null ? null : FileEditService.Parse(proposal.Message);
        if (edits == null)
            return;
        var jobs = testing.Enqueue(work, edits.Edits.Select(e => e.Path).ToArray(), proposal!.Message);
        var immediate = jobs.Where(j => j.State == TestJobState.Pending && j.Impact == ChangeImpact.Critical).ToArray();
        if (immediate.Length > 0)
            await RunJobs(immediate);
        Refresh();
    }
}
