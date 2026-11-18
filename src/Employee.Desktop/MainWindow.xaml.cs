using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Employee.Core;
using Employee.Infrastructure;

namespace Employee.Desktop;
public partial class MainWindow : Window
{
    private readonly SqliteStore store;
    private readonly ProjectIndexer indexer;
    private readonly Orchestrator orchestrator;
    private readonly OllamaProvider ollama = new();
    private readonly IReadOnlyList<IAIProvider> providers;
    private ManagerSettings settings;
    private IReadOnlyList<ProviderHealth> health = [];
    private bool detecting;
    private bool loading;
    private bool closing;
    private bool shuttingDown;
    private readonly string dataPath;
    private ModelRatingsService ratings = null!;
    private readonly TaskExecutionPool unified;
    private readonly TestSettingsView testSettings;
    private volatile string? selectedGoalId;
    private CancellationTokenSource? evaluationCancel;
    private readonly DispatcherTimer outputTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(250)
    };
    private readonly object outputGate = new();
    private readonly System.Text.StringBuilder pendingOutput = new();
    private sealed record RouteOption(Route? Route, string Label);
    public MainWindow(string dataPath, bool smoke)
    {
        InitializeComponent();
        // Keep table footers inside the visible desktop, including at high DPI.
        var workArea = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, workArea.Width);
        MinHeight = Math.Min(MinHeight, workArea.Height);
        Width = Math.Min(Width, workArea.Width);
        Height = Math.Min(Height, workArea.Height);
        InitializeAttachments();
        this.dataPath = dataPath;
        ratings = new(dataPath);
        store = new(Path.Combine(dataPath, "manager.db"));
        store.RecoverInterruptedWork();
        store.ReclassifyTaskProblems();
        store.RunMaintenance();
        settings = store.LoadSettings();
        indexer = new(store);
        var runner = new ProcessRunner();
        providers = [..new[]
        {
            "codex",
            "claude",
            "copilot",
            "gemini",
            "opencode",
            "freebuff",
            "commandcode",
            "qoder",
            "antigravity"
        }.Select(id => (IAIProvider)new CliProvider(id, runner)), ollama];
        orchestrator = new(store, providers, runner);
        unified = new(store, store, orchestrator, runner);
        unified.WrapLegacySessions();
        unified.RecoverSavedTasks();
        testSettings = new(store, unified.Testing, () => closing || shuttingDown || updateReviewPending || controlCenter?.IsBusy == true || ((App)Application.Current).IsUpdateValidation || unified.IsBusy || detecting || evaluationCancel != null, () => SelectedTestTarget(), () => RefreshTasks());
        TestSettingsHost.Content = testSettings;
        testSettings.SuiteRequested += (project, request) =>
        {
            OpenProjectTask(project.Id);
            AutoAssignTask.IsChecked = false;
            TaskKind.SelectedItem = WorkKind.Review;
            TaskAccess.SelectedItem = AccessLevel.ReadProject;
            TaskRequest.Text = request;
        };
        unified.Changed += () => Dispatcher.InvokeAsync(() =>
        {
            if (!closing)
                RefreshTasks();
        });
        orchestrator.GoalOutput += (id, chunk) =>
        {
            if (id != selectedGoalId)
                return;
            lock (outputGate)
            {
                if (pendingOutput.Length < settings.MaxOutputChars)
                    pendingOutput.Append(chunk);
            }
        };
        outputTimer.Tick += (_, _) => FlushOutput();
        outputTimer.Start();
        TaskKind.ItemsSource = Enum.GetValues<WorkKind>().Where(k => k != WorkKind.Planning).ToArray();
        TaskKind.SelectedItem = WorkKind.Coding;
        TaskAccess.ItemsSource = Enum.GetValues<AccessLevel>();
        TaskAccess.SelectedItem = AccessLevel.ReadProject;
        InitializeTaskStateFilter();
        AutoAssignmentChanged(this, new());
        ModelKind.ItemsSource = Enum.GetValues<WorkKind>();
        ModelKind.SelectedItem = WorkKind.Coding;
        RoutingModeChoice.ItemsSource = Enum.GetValues<RoutingMode>();
        RoutingModeChoice.SelectedItem = settings.Mode;
        UnlimitedAttempts.IsChecked = settings.UnlimitedAttempts;
        MaxAttempts.Text = settings.UnlimitedAttempts ? "20" : settings.MaxAttempts.ToString();
        MaxAttempts.IsEnabled = !settings.UnlimitedAttempts;
        TimeoutSeconds.Text = settings.TimeoutSeconds.ToString();
        ProviderChoice.ItemsSource = settings.Providers.Select(p => p.Id).ToArray();
        ProviderChoice.SelectedIndex = 0;
        DataLocation.Text = Path.Combine(dataPath, "manager.db");
        ChangeDataButton.IsEnabled = Application.Current is App app && !app.HasDataOverride;
        RefreshProjects();
        RefreshTasks();
        RefreshRoutes();
        LoadAutomation();
        LoadPerformanceSettings();
        LoadV2Extras();
        Tabs.SelectedItem = OfficeTab;
        InitializeNotifications(smoke);
        InitializeShell(smoke);
        PreviewMouseDown += (_, _) => lastInput = DateTimeOffset.UtcNow;
        PreviewKeyDown += (_, _) => lastInput = DateTimeOffset.UtcNow;
        Closing += OnClosing;
        InitializeTaskRetention(smoke);
        if (smoke)
            ContentRendered += SmokeRendered;
    }

    private void DashboardNavigate(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out var index))
        {
            Tabs.SelectedIndex = index;
            if (index == 1)
                ShowTaskCreate(sender, e);
        }
    }

    private void FlushOutput()
    {
        CheckAutoAway();
        RefreshResources();
        RefreshShell();
        TryRunNextTask();
        string text;
        lock (outputGate)
        {
            text = pendingOutput.ToString();
            pendingOutput.Clear();
        }

        if (text.Length == 0)
            return;
        TaskDetail.AppendText(text);
        if (TaskDetail.Text.Length > settings.MaxOutputChars)
            TaskDetail.Text = TaskDetail.Text[^settings.MaxOutputChars..];
        TaskDetail.ScrollToEnd();
    }

    private async Task VerifyAiCliSettingSmoke()
    {
        // The Settings tab content is realized only when selected.
        NavigateShell("settings");
        SettingsSections.SelectedItem = SettingsAISection;
        await Dispatcher.InvokeAsync(() =>
        {
        }, DispatcherPriority.ApplicationIdle);
        if (UseCliChoice.IsChecked != settings.UseAiCli || NoCliChoice.IsChecked == settings.UseAiCli)
            throw new InvalidOperationException("UI acceptance: AI CLI choice does not show the saved setting.");
        // Radio check changes are raised on the dispatcher, like a real click.
        await Dispatcher.InvokeAsync(() => NoCliChoice.IsChecked = true, DispatcherPriority.Normal);
        await Dispatcher.InvokeAsync(() =>
        {
        }, DispatcherPriority.ApplicationIdle);
        if (settings.UseAiCli || store.LoadSettings().UseAiCli || UseCliChoice.IsChecked != false)
            throw new InvalidOperationException("UI acceptance: 'Do not use AI CLIs' was not saved.");
        await Dispatcher.InvokeAsync(() => UseCliChoice.IsChecked = true, DispatcherPriority.Normal);
        await Dispatcher.InvokeAsync(() =>
        {
        }, DispatcherPriority.ApplicationIdle);
        if (!settings.UseAiCli || !store.LoadSettings().UseAiCli || NoCliChoice.IsChecked != false)
            throw new InvalidOperationException("UI acceptance: 'Use AI CLIs' was not saved.");
        UpdateLayout();
        var visual = (FrameworkElement)Content;
        var bitmap = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
            dc.DrawRectangle(new VisualBrush(visual), null, new Rect(0, 0, visual.ActualWidth, visual.ActualHeight));
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(dataPath, "ui-settings-cli-toggle.png"));
        encoder.Save(file);
        NavigateShell("office");
        await Dispatcher.InvokeAsync(() =>
        {
        }, DispatcherPriority.ApplicationIdle);
    }

    private async void SmokeRendered(object? sender, EventArgs e)
    {
        ContentRendered -= SmokeRendered;
        try
        {
            // Isolated, no-inference acceptance path through the actual UI handlers.
            VerifyModelSelectionSmoke();
            if (store.Projects().Count == 0)
            {
                var sample = Path.Combine(dataPath, "sample-project");
                Directory.CreateDirectory(sample);
                File.WriteAllText(Path.Combine(sample, "WeeklyQuest.cs"), "// UI acceptance fixture");
                await VerifyAiCliSettingSmoke();
                AutoAssignTask.IsChecked = false;
                ProjectPath.Text = sample;
                ProjectName.Text = "Sample Garden";
                ProjectAliases.Text = "Garden";
                AddProject(this, new());
                indexer.Index(store.Projects().Single());
                TaskProject.SelectedItem = store.Projects().Single();
                TaskRequest.Text = "Review the weekly quest implementation";
                CreateTask(this, new());
                ProviderChoice.SelectedItem = "codex";
                ProviderEnabled.IsChecked = true;
                SaveProvider(this, new());
                NewModel.Text = "";
                ModelKind.SelectedItem = WorkKind.Coding;
                AddModel(this, new());
                RoutingModeChoice.SelectedItem = RoutingMode.SemiAutomatic;
                SaveSettings(this, new());
                health = [new("codex", true, true, true, "UI fixture: proposal only; no CLI executed")];
                HealthGrid.ItemsSource = health;
                await Begin(false);
                if (store.WorkItems().Single().State != WorkState.AwaitingApproval)
                    throw new InvalidOperationException("UI acceptance: missing routing proposal.");
                RefreshHistory(this, new());
            }

            VerifyUnifiedSmoke();
            VerifyNotificationSmoke();
            for (var i = 0; i < Tabs.Items.Count; i++)
            {
                Tabs.SelectedIndex = i;
                await Dispatcher.InvokeAsync(() =>
                {
                }, DispatcherPriority.ApplicationIdle);
                UpdateLayout();
                var visual = (FrameworkElement)Content;
                var bitmap = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                var drawing = new DrawingVisual();
                using (var dc = drawing.RenderOpen())
                    dc.DrawRectangle(new VisualBrush(visual), null, new Rect(0, 0, visual.ActualWidth, visual.ActualHeight));
                bitmap.Render(drawing);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(dataPath, $"ui-tab-{i}.png"));
                encoder.Save(file);
            }

            var reviewWork = store.WorkItems().First();
            var now = DateTimeOffset.UtcNow;
            var reviewLogs = new[]
            {
                new TaskLog(1, reviewWork.Id, now, "Execution", "fixture metadata\n{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"Sample UI verification result: layout and colors adjusted. This is test data; no AI was called.\"}}")
            };
            var review = new TaskReviewWindow(TaskReviewReport.Create(reviewWork, store.Projects().First(), reviewLogs, null))
            {
                Owner = this
            };
            review.Show();
            await Dispatcher.InvokeAsync(() =>
            {
            }, DispatcherPriority.ApplicationIdle);
            review.UpdateLayout();
            if (((Grid)review.Content).Children.OfType<TabControl>().Single().Items.Count != 4)
                throw new InvalidOperationException("Review tabs missing.");
            var reviewVisual = (FrameworkElement)review.Content;
            var reviewBitmap = new RenderTargetBitmap((int)reviewVisual.ActualWidth, (int)reviewVisual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            var reviewDrawing = new DrawingVisual();
            using (var dc = reviewDrawing.RenderOpen())
                dc.DrawRectangle(new VisualBrush(reviewVisual), null, new Rect(0, 0, reviewVisual.ActualWidth, reviewVisual.ActualHeight));
            reviewBitmap.Render(reviewDrawing);
            var reviewEncoder = new PngBitmapEncoder();
            reviewEncoder.Frames.Add(BitmapFrame.Create(reviewBitmap));
            using (var file = File.Create(Path.Combine(dataPath, "ui-review.png")))
                reviewEncoder.Save(file);
            review.Close();
            File.WriteAllText(Path.Combine(dataPath, "ui-smoke.txt"), $"WPF rendered {ActualWidth}x{ActualHeight}; SQLite opened; {Tabs.Items.Count} tabs. Project registration, indexing, task creation, provider/model permission, settings and SemiAutomatic proposal handlers passed. Image-only clipboard Paste command passed in task and comment inputs; text paste and attachment persistence passed. No AI invoked.");
            Close();
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(dataPath, "ui-smoke.txt"), "FAILED: " + ex);
            Application.Current.Shutdown(1);
        }
    }

    private void RefreshProjects()
    {
        var id = (ProjectGrid.SelectedItem as ProjectInfo)?.Id;
        var projects = store.Projects();
        ProjectGrid.ItemsSource = projects;
        ProjectGrid.SelectedItem = projects.FirstOrDefault(p => p.Id == id) ?? projects.FirstOrDefault();
        var taskProjectId = (TaskProject.SelectedItem as ProjectInfo)?.Id;
        var taskOptions = projects.Prepend(new ProjectInfo("", "Outside projects (text only)", "")).ToArray();
        TaskProject.ItemsSource = taskOptions;
        TaskProject.SelectedItem = taskOptions.FirstOrDefault(p => p.Id == taskProjectId) ?? taskOptions.FirstOrDefault();
        SyncSettingsProjects();
    }

    private void RefreshTasks(string? id = null)
    {
        var requestedId = id;
        id ??= (TaskGrid.SelectedItem as WorkItem)?.Id;
        var tasks = store.WorkItems().Where(w => !w.IsCoordinator && (w.IsGoal || w.ParentTaskId == null)).ToArray();
        ObserveNotifications(tasks);
        RefreshTaskProjectFilter(tasks);
        var projectId = (TaskProjectFilter.SelectedItem as TaskProjectOption)?.Id;
        var matching = tasks.Where(w => selectedTaskStates.Contains(w.State) && (projectId == null || w.ProjectId == projectId)).ToArray();
        var visible = TaskListPage(matching, requestedId);
        selectedDeletionTasks.IntersectWith(visible.Select(w => w.Id));
        var view = new System.Windows.Data.ListCollectionView(visible);
        view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(WorkItem.ProjectId), new TaskProjectGroupConverter(((TaskProjectNameConverter)Resources["TaskProjectName"]).Names)));
        TaskGrid.ItemsSource = view;
        UpdateTaskSortIndicators();
        UpdateTaskDeletionSelection();
        TaskGrid.SelectedItem = visible.FirstOrDefault(w => w.Id == id) ?? visible.FirstOrDefault();
        TaskFilterCount.Text = matching.Length == 0 ? "No matching requests" : $"{matching.Length} / {tasks.Length} requests";
        if (TaskGrid.SelectedItem == null)
        {
            selectedGoalId = null;
            displayedTimeGoal = null;
            TaskDetail.Clear();
            StepsGrid.ItemsSource = null;
            PendingGrid.ItemsSource = null;
            TaskPlanGrid.ItemsSource = null;
            TaskTimeStatus.Text = "";
            commentGoalId = null;
            commentHistory.Children.Clear();
        }
    }

    private void RefreshRoutes()
    {
        RefreshTaskRouteChoices();
        var list = new List<RouteOption>
        {
            new(null, "Automatic selection / proposal")
        };
        list.AddRange(settings.Providers.Where(p => p.Enabled).SelectMany(p => p.Models.Where(m => !m.Blocked).Select(m => new RouteOption(new(p.Id, m.Id, "User selected"), p.Id + " / " + Orchestrator.DisplayModel(m.Id)))));
        var oldCoordinator = (AutoCoordinator.SelectedItem as RouteOption)?.Route;
        AutoCoordinator.ItemsSource = list;
        AutoCoordinator.SelectedItem = list.FirstOrDefault(x => x.Route?.ProviderId == oldCoordinator?.ProviderId && x.Route?.ModelId == oldCoordinator?.ModelId) ?? list[0];
    }

    private WorkItem SelectedWork() => TaskGrid.SelectedItem as WorkItem ?? throw new InvalidOperationException("Select a task first.");
    private void TaskIdleOnly(WorkItem work)
    {
        if (updateReviewPending || closing || shuttingDown)
            throw new InvalidOperationException("Wait for update review or application shutdown to finish.");
        var goal = unified.GoalFor(work);
        if (unified.IsRunning(goal.Id) || activeToolGoalId == goal.Id || orchestrator.IsRunning(goal.Id) || goal.State == WorkState.Running || unified.Steps(goal).Any(w => orchestrator.IsRunning(w.Id) || w.State == WorkState.Running))
            throw new InvalidOperationException("Stop and await this task before changing it.");
    }

    private void IdleOnly()
    {
        if (updateReviewPending || controlCenter?.IsBusy == true || shuttingDown || orchestrator.HasRunning || detecting || evaluationCancel != null || unified.IsBusy || testSettings.IsBusy)
            throw new InvalidOperationException("Wait for the current operation or click Stop & save first.");
    }

    private async Task Guard(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Status.Text = ex.Message;
            MessageBox.Show(this, ex.Message, "AI Employee Manager", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void ProjectSelected(object sender, SelectionChangedEventArgs e)
    {
        RefreshMemory();
        SyncSettingsProjects();
        if (ProjectGrid.SelectedItem is ProjectInfo p)
        {
            SearchResults.ItemsSource = null;
            Status.Text = p.Name + " · " + p.Technology;
        }
    }

    private void BrowseProject(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose a project folder"
        };
        if (dialog.ShowDialog(this) == true)
            ProjectPath.Text = dialog.FolderName;
    }

    private async void ScanProjects(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        IdleOnly();
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose a folder containing projects"
        };
        if (dialog.ShowDialog(this) != true)
            return;
        detecting = true;
        try
        {
            Status.Text = "Scanning folders and registering projects...";
            var result = await Task.Run(() => indexer.ScanAndRegister(dialog.FolderName));
            RefreshProjects();
            Status.Text = $"Added {result.Added} projects; skipped {result.Existing} existing projects; {result.Unreadable} unreadable folders." + (result.LimitReached ? " Stopped at the 10,000-folder limit; choose a smaller folder." : "");
        }
        finally
        {
            detecting = false;
        }
    });
    private async void AddProject(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        IdleOnly();
        var p = indexer.Register(ProjectPath.Text, ProjectName.Text, ProjectAliases.Text);
        RefreshProjects();
        ProjectGrid.SelectedItem = p;
        Status.Text = "Registered " + p.Name + ". Use Update file index when needed.";
        await Task.CompletedTask;
    });
    private async void DeleteProject(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        IdleOnly();
        var project = ProjectGrid.SelectedItem as ProjectInfo ?? throw new InvalidOperationException("Select a project to delete.");
        if (MessageBox.Show(this, $"Remove project \"{project.Name}\" from the app?\n\n{project.Root}\n\nRelated tasks, history, indexes and test configurations will be deleted. The source folder and files will be kept.", "Delete project", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return Task.CompletedTask;
        IdleOnly();
        store.DeleteProject(project.Id);
        SearchResults.ItemsSource = null;
        selectedGoalId = null;
        TaskDetail.Clear();
        StepsGrid.ItemsSource = null;
        PendingGrid.ItemsSource = null;
        RefreshProjects();
        RefreshTasks();
        testSettings.Refresh();
        Status.Text = "Removed project " + project.Name + " from the app.";
        return Task.CompletedTask;
    });
    private async void IndexProject(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        IdleOnly();
        var p = ProjectGrid.SelectedItem as ProjectInfo ?? throw new InvalidOperationException("Select a project.");
        detecting = true;
        try
        {
            Status.Text = "Indexing selected project only…";
            var count = await Task.Run(() => indexer.Index(p));
            RefreshProjects();
            Status.Text = $"Indexed {count:N0} paths in {p.Name}; discovery keywords refreshed.";
        }
        finally
        {
            detecting = false;
        }
    });
    private async void ScanProjectKeywords(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        IdleOnly();
        var p = ProjectGrid.SelectedItem as ProjectInfo ?? throw new InvalidOperationException("Select a project.");
        detecting = true;
        try
        {
            Status.Text = "Scanning local project keywords...";
            var updated = await Task.Run(() => indexer.RefreshKeywords(p));
            RefreshProjects();
            Status.Text = $"Saved {updated.Keywords.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length} discovery keywords for {p.Name}.";
        }
        finally
        {
            detecting = false;
        }
    });
    private async void SearchIndex(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        var p = ProjectGrid.SelectedItem as ProjectInfo ?? throw new InvalidOperationException("Select a project.");
        SearchResults.ItemsSource = store.SearchFiles(p.Id, SearchQuery.Text).Select(f => f.Path).ToArray();
        return Task.CompletedTask;
    });
    private WorkItem CreateTaskRecord()
    {
        if (string.IsNullOrWhiteSpace(TaskRequest.Text) || TaskRequest.Text.Length > 12000)
            throw new InvalidOperationException("Enter a request between 1 and 12,000 characters.");
        var p = ResolveTaskProject();
        var external = p.Id == "";
        var work = new WorkItem
        {
            State = WorkState.Saved,
            IsGoal = true,
            ProjectId = p.Id,
            Request = TaskRequest.Text.Trim(),
            AutoAssign = external ? false : AutoAssignTask.IsChecked == true,
            TestAfterBuild = TestAfterBuildEnabled.IsChecked == true,
            Kind = external ? WorkKind.Content : (WorkKind)TaskKind.SelectedItem,
            Access = external ? AccessLevel.TextOnly : AccessLevel.WriteProject,
            Priority = AutoAssignTask.IsChecked == true ? 50 : Number(TaskPriority.Text, 0, 100)
        };
        work.GrantedAccess = work.Access;
        work.AutomaticIntent = work.AutoAssign;
        var requestedWorkflow = AutoAssignTask.IsChecked == true ? null : TaskWorkflowChoice.SelectedItem as ToolWorkflow;
        if (string.IsNullOrEmpty(requestedWorkflow?.Id))
        {
            var matches = store.Workflows().Where(f => f.ProjectId == p.Id && f.Name.Length >= 4 && work.Request.Contains(f.Name, StringComparison.OrdinalIgnoreCase) && f.State != ToolRunState.Completed).ToArray();
            requestedWorkflow = matches.Length == 1 ? matches[0] : null;
        }

        if (requestedWorkflow is { Id.Length: > 0 })
        {
            if (requestedWorkflow.ProjectId != p.Id)
                throw new InvalidOperationException("Choose the same project as the tool workflow.");
            work.ToolWorkflowId = requestedWorkflow.Id;
            work.AutoAssign = false;
        }

        work.PlanningOverride = shellAutonomous ? PlanningPolicy.Always : null;
        work.ScheduledAt = ReadSchedule();
        work.PreferredRoute = AutoAssignTask.IsChecked == true ? null : SelectedTaskRoute(CreateProviderChoice, CreateModelChoice);
        work.Attachments = [..taskAttachments.Files];
        TaskAttachments.Validate(work.Attachments);
        store.SaveWork(work);
        if (!external && !work.AutoAssign && work.ToolWorkflowId == null && TaskIntent.Normalize(work.Request).Length >= 3)
            store.LearnIntent(new("", work.ProjectId, work.Request, work.Kind, work.Access, IntentExampleSource.ManualCreation, work.Id, DateTimeOffset.UtcNow));
        taskAttachments.Clear();
        store.Log(work.Id, "Created", "Task created by user. Access: " + work.Access);
        TaskRequest.Clear();
        if (!selectedTaskStates.Contains(work.State))
            SetTaskStateFilter([..selectedTaskStates, work.State]);
        RefreshTasks(work.Id);
        Status.Text = "Request saved. Click Run / resume to start.";
        ShowToast("Task received", Shorten(work.Request));
        return work;
    }

    private static string Shorten(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length > 80 ? line[..77] + "…" : line;
    }

    // A small notice at the bottom right of the window that fades out by itself.
    private Border? toast;
    private void ShowToast(string title, string detail)
    {
        if (Content is not Grid root)
            return;
        if (toast != null)
            root.Children.Remove(toast);
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = "✓  " + title, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, FontSize = 14 });
        if (!string.IsNullOrWhiteSpace(detail))
            text.Children.Add(new TextBlock { Text = detail, Foreground = new SolidColorBrush(Color.FromRgb(0xD8, 0xD0, 0xF0)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), MaxWidth = 320 });
        var box = new Border
        {
            Child = text,
            Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x4F)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 10, 16, 10),
            Margin = new Thickness(0, 0, 28, 28),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.4 }
        };
        Grid.SetColumnSpan(box, Math.Max(1, root.ColumnDefinitions.Count));
        Grid.SetRowSpan(box, Math.Max(1, root.RowDefinitions.Count));
        Panel.SetZIndex(box, 1000);
        root.Children.Add(box);
        toast = box;
        var fade = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(0, TimeSpan.Zero));
        fade.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(1, TimeSpan.FromMilliseconds(180)));
        fade.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(1, TimeSpan.FromMilliseconds(2800)));
        fade.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(0, TimeSpan.FromMilliseconds(3400)));
        fade.Completed += (_, _) => { if (toast == box) { root.Children.Remove(box); toast = null; } };
        box.BeginAnimation(OpacityProperty, fade);
    }

    private async void CreateTask(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        CreateTaskRecord();
        return Task.CompletedTask;
    });
    private void WorkSelected(object sender, SelectionChangedEventArgs e)
    {
        QueueSelectedTaskButton.Visibility = TaskGrid.SelectedItem is WorkItem candidate && UnifiedTaskService.CanEnqueue(candidate) ? Visibility.Visible : Visibility.Collapsed;
        if (TaskGrid.SelectedItem is not WorkItem w)
            return;
        if (selectedGoalId != w.Id)
        {
            lock (outputGate)
                pendingOutput.Clear();
        }

        selectedGoalId = w.Id;
        ShowTaskRoute(w.PreferredRoute);
        RefreshTaskProjectAccess(w);
        var report = ReviewReport(w);
        TaskDetail.Text = $"{w.State} · {w.Kind} · access={w.Access} · switches={w.Switches}\n{w.Request}\n\n{report.Summary}\n\nAI RESPONSE\n{report.Result}";
        RefreshTaskPanels(w);
        RefreshComments(w);
    }

    private TaskReviewReport ReviewReport(WorkItem work) => unified.Report(work);
    private async void ViewResult(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        var window = new TaskReviewWindow(ReviewReport(SelectedWork()))
        {
            Owner = this
        };
        window.ShowDialog();
        return Task.CompletedTask;
    });
    private async Task Detect()
    {
        IdleOnly();
        if (!settings.UseAiCli)
            throw new InvalidOperationException("AI CLIs are turned off in Settings > AI & Models. Turn them on first; no CLI was called. Local AI can still be enabled under Local AI.");
        detecting = true;
        Status.Text = "Inspecting installed CLI help and local model list; no AI generation…";
        try
        {
            health = await orchestrator.DetectAsync(settings);
            HealthGrid.ItemsSource = health;
            foreach (var p in providers)
            {
                if (!health.Any(h => h.Id == p.Id && h.Installed))
                    continue;
                var config = settings.Providers.Single(c => c.Id == p.Id);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    var models = await p.ListModelsAsync(timeout.Token);
                    ModelCatalogMerge.Apply(config, models);
                    config.ModelDiscoveryStatus = (p as IModelCatalogStatus)?.ModelDiscoveryDetail ?? $"{models.Count} installed local models from runtime metadata.";
                }
                catch (Exception error)
                {
                    config.ModelDiscoveryStatus = "Model listing failed: " + error.Message;
                    config.ModelCatalogCheckedAt = DateTimeOffset.UtcNow;
                }

                health = health.Select(h => h.Id == p.Id ? h with { Detail = h.Detail + " Models: " + config.ModelDiscoveryStatus } : h).ToArray();
            }

            // Every CLI found on this PC (and not switched off by the user) is turned on with full permissions (Ollama only answers, it
            // never edits project files), then checked again now that it is enabled.
            var added = new List<string>();
            foreach (var config in settings.Providers)
            {
                if (config.TurnedOff || !health.Any(h => h.Id == config.Id && h.Installed && !h.StandaloneCliMissing))
                    continue;
                var write = config.Id != "ollama";
                if (config.Enabled && config.AllowProjectWrite == write)
                    continue;
                config.Enabled = true;
                config.AllowProjectWrite = write;
                added.Add(config.Id);
            }
            if (added.Count > 0)
            {
                store.SaveSettings(settings);
                health = await orchestrator.DetectAsync(settings);
            }
            HealthGrid.ItemsSource = health;
            var assigned = ModelCatalogMerge.AssignAutomaticRoles(settings, health, store);
            store.SaveSettings(settings);
            LoadProvider();
            RefreshRoutes();
            RefreshCliToggles();
            var enabled = settings.Providers.Where(p => p.Enabled).Select(p => p.Id).ToArray();
            Status.Text = enabled.Length == 0
                ? "Detection complete. No AI CLI was found. Install one (for example Claude Code or Codex), then click Detect again."
                : $"Detection complete. Ready to use: {string.Join(", ", enabled)}" + (added.Count > 0 ? $" (just added: {string.Join(", ", added)})" : "") + $". Assigned {assigned} primary roles.";
        }
        finally
        {
            detecting = false;
        }
    }

    private async void DetectProviders(object sender, RoutedEventArgs e) => await Guard(Detect);
    private void ProviderSelected(object sender, SelectionChangedEventArgs e) => LoadProvider();
    private void LoadProvider()
    {
        if (ProviderChoice.SelectedItem is not string id || settings == null)
            return;
        loading = true;
        var p = settings.Providers.Single(p => p.Id == id);
        ProviderExe.Text = p.Executable;
        ProviderPriority.Text = p.Priority.ToString();
        ProviderEnabled.IsChecked = p.Enabled;
        ProviderWrite.IsChecked = p.AllowProjectWrite;
        ProviderWrite.IsEnabled = id != "ollama";
        ModelGrid.ItemsSource = p.Models.ToArray();
        ModelDiscoveryNote.Text = p.ModelDiscoveryStatus + (p.ModelCatalogCheckedAt is { } at ? " | " + at.ToLocalTime().ToString("g") : "");
        loading = false;
    }

    private async void SaveProvider(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        IdleOnly();
        if (loading)
            return Task.CompletedTask;
        var p = settings.Providers.Single(p => p.Id == (string)ProviderChoice.SelectedItem);
        p.Executable = ProviderExe.Text.Trim();
        p.Priority = Number(ProviderPriority.Text, 0, 100);
        p.Enabled = ProviderEnabled.IsChecked == true;
        p.TurnedOff = !p.Enabled;
        p.AllowProjectWrite = p.Id != "ollama" && ProviderWrite.IsChecked == true;
        store.SaveSettings(settings);
        health = [];
        HealthGrid.ItemsSource = health;
        RefreshRoutes();
        RefreshCliToggles();
        Status.Text = "Provider permission saved. Detect capabilities again before running.";
        return Task.CompletedTask;
    });
    private async void AddModel(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        IdleOnly();
        var p = settings.Providers.Single(p => p.Id == (string)ProviderChoice.SelectedItem);
        var id = NewModel.Text.Trim();
        if (p.Id == "ollama" && id.Length == 0)
            throw new InvalidOperationException("Choose an exact installed local model ID.");
        var kind = (WorkKind)ModelKind.SelectedItem;
        var old = p.Models.FirstOrDefault(m => m.Id == id);
        if (old != null)
            p.Models.Remove(old);
        var model = old ?? new ModelInfo(id, []);
        var primary = model.PrimaryKinds.Where(k => k != kind).ToList();
        if (ModelPrimaryRole.IsChecked == true)
            primary.Add(kind);
        p.Models.Add(model with { Kinds = [..model.Kinds.Append(kind).Distinct()], PrimaryKinds = [..primary], ManualRoleKinds = [..model.ManualRoleKinds.Append(kind).Distinct()] });
        store.SaveSettings(settings);
        LoadProvider();
        RefreshRoutes();
        Status.Text = "Model allowed for " + kind + ". Local models still require qualification.";
        return Task.CompletedTask;
    });
    private async void RemoveModel(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        IdleOnly();
        var p = settings.Providers.Single(p => p.Id == (string)ProviderChoice.SelectedItem);
        if (ModelGrid.SelectedItem is ModelInfo m)
            p.Models.Remove(m);
        store.SaveSettings(settings);
        LoadProvider();
        RefreshRoutes();
        return Task.CompletedTask;
    });
    private void ModelSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ModelGrid.SelectedItem is not ModelInfo model)
            return;
        NewModel.Text = model.Id;
        ModelQuality.Text = model.EffectiveQuality.ToString();
        ModelCost.Text = model.EffectiveCostRank.ToString();
        ModelBlocked.IsChecked = model.Blocked;
    }

    private async void SaveModelPreferences(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        IdleOnly();
        var model = ModelGrid.SelectedItem as ModelInfo ?? throw new InvalidOperationException("Select a model first.");
        var quality = Number(ModelQuality.Text, 0, 100);
        var cost = Number(ModelCost.Text, 0, 100);
        var p = settings.Providers.Single(p => p.Id == (string)ProviderChoice.SelectedItem);
        var rated = model.RatedByUser || quality != model.EffectiveQuality || cost != model.EffectiveCostRank;
        p.Models[p.Models.IndexOf(model)] = model with
        {
            Quality = quality,
            CostRank = cost,
            RatedByUser = rated,
            Blocked = ModelBlocked.IsChecked == true
        };
        store.SaveSettings(settings);
        LoadProvider();
        RefreshRoutes();
        Status.Text = rated ? "Saved your rating; it replaces the built-in rating when choosing models." : "Saved; the model still uses the built-in rating.";
        return Task.CompletedTask;
    });
    private async void EvaluateModel(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        IdleOnly();
        if ((string)ProviderChoice.SelectedItem != "ollama" || ModelGrid.SelectedItem is not ModelInfo m)
            throw new InvalidOperationException("Select an installed Ollama model first.");
        evaluationCancel = new(TimeSpan.FromMinutes(5));
        Status.Text = "Running six local-only qualification prompts; no cloud model or downloads…";
        try
        {
            var results = await new LocalEvaluator(ollama, store).EvaluateAsync(m.Id, evaluationCancel.Token);
            ModelCatalogMerge.AssignAutomaticRoles(settings, health, store);
            store.SaveSettings(settings);
            LoadProvider();
            RefreshRoutes();
            Status.Text = string.Join(" · ", results.Select(r => $"{r.Kind}: {r.Passed}/{r.Total} ({r.Milliseconds} ms)"));
        }
        finally
        {
            evaluationCancel.Dispose();
            evaluationCancel = null;
        }
    });
    private async void SaveSettings(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        IdleOnly();
        var attempts = UnlimitedAttempts.IsChecked == true ? 0 : Number(MaxAttempts.Text, 1, ManagerSettings.MaxAttemptsLimit);
        var timeout = Number(TimeoutSeconds.Text, 5, 28800);
        settings.Mode = (RoutingMode)RoutingModeChoice.SelectedItem;
        settings.MaxAttempts = attempts;
        settings.TimeoutSeconds = timeout;
        store.SaveSettings(settings);
        Status.Text = "Orchestration settings saved.";
        return Task.CompletedTask;
    });
    private void UnlimitedAttemptsChanged(object sender, RoutedEventArgs e) => MaxAttempts.IsEnabled = UnlimitedAttempts.IsChecked != true;
    private static int Number(string text, int min, int max) => int.TryParse(text, out var n) && n >= min && n <= max ? n : throw new InvalidOperationException($"Enter a number between {min} and {max}.");
    private async Task Begin(bool approved, bool switching = false)
    {
        var goal = SelectedWork();
        var target = SelectedTaskRoute(RunProviderChoice, RouteChoice);
        if (approved && (goal.State is WorkState.WrongAccess or WorkState.WrongProject))
            throw new InvalidOperationException(goal.ProblemReason + "\nCorrect project/access in task details, then Run/resume. Approve does not fix configuration.");
        if (updateReviewPending || controlCenter?.IsBusy == true || ((App)Application.Current).IsUpdateValidation || detecting || evaluationCancel != null || testSettings.IsBusy)
            throw new InvalidOperationException("Wait for the current operation to finish.");
        if (goal.ToolWorkflowId != null)
        {
            if (switching)
                throw new InvalidOperationException("Tool workflow steps use configured connectors; Switch AI applies to AI tasks.");
            if (approved)
            {
                NavigateShell("tools");
                Status.Text = "Review exact tool input/output in Tools, then resume this task.";
                return;
            }

            if (goal.ScheduledAt > DateTimeOffset.UtcNow)
            {
                goal.ScheduledAt = null;
                store.SaveWork(goal);
            }

            queueSuspended = false;
            if (!unified.CanStart(goal, settings))
            {
                unified.QueueForRun(goal);
                RefreshTasks(goal.Id);
                Status.Text = "Queued until an agent and independent project are available.";
                return;
            }

            await RunToolTask(goal);
            return;
        }

        if (health.Count == 0)
            await Detect();
        if (switching)
        {
            if (target == null)
                throw new InvalidOperationException("Select a target provider / model to switch AI.");
            if (SwitchCoordinatorChoice.IsChecked == true)
            {
                await unified.StopAsync(goal);
                var fresh = store.WorkItems().Single(w => w.Id == goal.Id);
                unified.ChangeCoordinator(fresh, target, settings, health);
                SwitchCoordinatorChoice.IsChecked = false;
                RefreshTasks(goal.Id);
                Status.Text = "Coordinator changed; accepted steps, scope and budgets preserved. Run / resume to continue.";
                return;
            }

            if (goal.SessionId == null)
            {
                goal.PreferredRoute = target;
                store.SaveWork(goal);
                await RunGoal(goal, target, true);
                return;
            }

            var duringPlanning = unified.IsRunning(goal.Id) && unified.EngineFor(goal).CurrentWorkId == null;
            unified.Switch(goal, target, settings, health);
            if (duringPlanning)
            {
                await unified.WaitForAsync(goal.Id);
                await RunGoal(store.WorkItems().Single(w => w.Id == goal.Id), target, true);
            }
            else
                RefreshTasks(goal.Id);
            return;
        }

        if (unified.IsRunning(goal.Id))
            throw new InvalidOperationException("This task is already running. Stop & save before resuming it.");
        if (!approved && target == null && goal.PreferredRoute != null)
            unified.ClearRoutingOverride(goal);
        if (approved)
        {
            var pending = unified.Decisions(goal);
            var chosen = PendingGrid.SelectedItem as PendingDecision;
            if (chosen == null && pending.Count > 0)
                throw new InvalidOperationException("Open Steps & approvals and select the decision you want to act on.");
            if (chosen != null && !ApproveSelectedDecisionButton.IsEnabled)
                throw new InvalidOperationException("Read the selected decision in Steps & approvals for the required action.");
            if (chosen != null)
                unified.Resolve(goal, chosen.Id, true, settings, health, target);
            else if (goal.State != WorkState.AwaitingApproval)
                throw new InvalidOperationException("No items are awaiting approval.");
        }

        if (goal.ScheduledAt > DateTimeOffset.UtcNow)
        {
            goal.ScheduledAt = null;
            store.SaveWork(goal);
            store.Log(goal.Id, "ScheduleOverride", "User explicitly chose Run / resume before the scheduled time.");
        }

        await RunGoal(store.WorkItems().Single(w => w.Id == goal.Id), target, approved);
    }

    private async Task RunGoal(WorkItem goal, Route? route, bool approved)
    {
        queueSuspended = false;
        if (selectedGoalId == goal.Id)
            TaskDetail.Clear();
        Status.Text = "Orchestrating the request in Tasks...";
        var run = unified.StartAsync(goal, settings, health, route, approved);
        TaskGrid.Items.Refresh();
        await run;
        if (closing || shuttingDown)
            return;
        FlushOutput();
        RefreshTasks(selectedGoalId == goal.Id ? goal.Id : null);
        var saved = store.WorkItems().Single(w => w.Id == goal.Id);
        if (selectedGoalId == goal.Id)
            Status.Text = saved.State + " · " + saved.Notes;
        RefreshPerformance();
        if (selectedGoalId == goal.Id && unified.Decisions(saved).Count > 0)
            TaskPanels.SelectedIndex = 1;
    }

    private async void StartWork(object sender, RoutedEventArgs e) => await Guard(() => Begin(false));
    private async void ApproveWork(object sender, RoutedEventArgs e) => await Guard(() => Begin(true));
    private async void SwitchWork(object sender, RoutedEventArgs e) => await Guard(() => Begin(true, true));
    private async void CompleteWork(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        var work = store.WorkItems().Single(w => w.Id == SelectedWork().Id);
        if (!CompleteReviewedTask(work, askFirst: true))
            return Task.CompletedTask;
        RefreshTasks(work.Id);
        Status.Text = store.WorkItems().Single(w => w.Id == work.Id).Notes;
        return Task.CompletedTask;
    });

    // Completes a reviewed task. When steps are unfinished or decisions wait, the user may still judge the
    // result good: those are closed after one confirmation (askFirst) and the task is complete.
    private bool CompleteReviewedTask(WorkItem work, bool askFirst)
    {
        if (orchestrator.IsRunning(work.Id) || work.State == WorkState.Running)
            throw new InvalidOperationException("Stop the task before completing it.");
        if (TaskOutcomes.IsClosed(work.State))
            throw new InvalidOperationException("The task is already finished (" + TaskOutcomes.Label(work.State) + ").");
        if (work.ToolWorkflowId != null)
        {
            if (work.State != WorkState.NeedsReview)
                throw new InvalidOperationException("Finish and review the tool workflow result first.");
            controlCenter!.ConfirmWorkflowTask(work.ToolWorkflowId);
            work.State = WorkState.Completed;
            work.Notes = "User marked the result reviewed and complete.";
            store.SaveWork(work);
            store.Log(work.Id, "UserReview", work.Notes);
        }
        else if (work.State != WorkState.NeedsReview || !unified.CanComplete(work))
        {
            var (open, waiting) = unified.OpenWork(work);
            if (askFirst && MessageBox.Show(this,
                    (open + waiting > 0 ? $"{open} step(s) are unfinished and {waiting} decision(s) are waiting.\nThey will be closed and the task marked complete.\n\n" : "The task has not finished its run.\n\n") + "Complete it anyway because the result is good enough?",
                    "Complete anyway", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                return false;
            unified.CompleteAnyway(work);
        }
        else
        {
            work.State = WorkState.Completed;
            work.Notes = "User marked the result reviewed and complete.";
            store.SaveWork(work);
            store.Log(work.Id, "UserReview", work.Notes);
        }
        store.RecordApproval([work.Id, ..unified.Steps(work).Where(w => w.State == WorkState.Completed).Select(w => w.Id)]);
        if (work.ToolWorkflowId == null && TaskIntent.Normalize(work.Request).Length >= 3 && !unified.Steps(work).Any(w => w.State == WorkState.Skipped))
            store.LearnIntent(new("", work.ProjectId, work.Request, work.Kind, work.Access, IntentExampleSource.ReviewedCompletion, work.Id, DateTimeOffset.UtcNow));
        PushCompletedTask(work);
        return true;
    }

    private async void ApplyEdits(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        IdleOnly();
        var goal = SelectedWork();
        var work = StepsGrid.SelectedItem as WorkItem ?? unified.Steps(goal).FirstOrDefault(w => store.Logs(w.Id).Any(l => l.Event == "EditProposal")) ?? goal;
        var proposalLog = store.Logs(work.Id).LastOrDefault(l => l.Event == "EditProposal") ?? throw new InvalidOperationException("No unapplied file proposals. Codex may have edited files directly; review the result.");
        var proposal = FileEditService.Parse(proposalLog.Message) ?? throw new InvalidOperationException("Invalid proposal.");
        HistoryText.Text = JsonSerializer.Serialize(proposal, new JsonSerializerOptions { WriteIndented = true });
        if (MessageBox.Show(this, "Apply these proposed changes? Review the complete contents in History before confirming.\n\n" + string.Join('\n', proposal.Edits.Select(x => x.Path)), "Apply reviewed edits", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            Tabs.SelectedIndex = 4;
            return;
        }

        orchestrator.ApplyReviewedEdits(work);
        await testSettings.QueueAppliedAsync(work);
        if (goal.SessionId != null)
            unified.Sync(goal.Id);
        RefreshTasks(goal.Id);
        Status.Text = work.Notes;
    });
    private async void StopWork(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (evaluationCancel != null)
        {
            evaluationCancel.Cancel();
            return;
        }

        var w = SelectedWork();
        if (w.State == WorkState.Queued && w.QueuedAt != null)
        {
            unified.SaveForLater(w);
            RefreshTasks(w.Id);
            Status.Text = "Removed from queue and saved for later.";
            return;
        }

        queueSuspended = true;
        if (w.ToolWorkflowId != null && controlCenter != null)
            await controlCenter.StopAsync();
        else
            await unified.StopAsync(w);
        RefreshTasks(w.Id);
        Status.Text = "Stopped, saved progress and waited for the AI process to exit.";
    });
    private void RefreshHistory(object sender, RoutedEventArgs e)
    {
        if (TaskGrid.SelectedItem is not WorkItem w)
        {
            Status.Text = "Select a task on the Tasks tab first.";
            return;
        }

        HistoryText.Text = unified.Report(w).RawLog;
    }

    private void ShowEvaluations(object sender, RoutedEventArgs e) => HistoryText.Text = JsonSerializer.Serialize(store.Evaluations(), new JsonSerializerOptions { WriteIndented = true });
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (shuttingDown && !closing)
        {
            e.Cancel = true;
            return;
        }

        if (!closing && (controlCenter?.IsBusy == true || orchestrator.HasRunning || evaluationCancel != null || detecting || unified.IsBusy || testSettings.IsBusy))
        {
            e.Cancel = true;
            queueSuspended = true;
            if (detecting)
            {
                Status.Text = "Wait for indexing / detection before closing.";
                return;
            }

            shuttingDown = true;
            evaluationCancel?.Cancel();
            await unified.StopAllAsync();
            if (controlCenter != null)
                await controlCenter.StopAsync(!controlCenter.ClosingForUpdate);
            await testSettings.StopAsync();
            if (evaluationCancel != null)
            {
                shuttingDown = false;
                Status.Text = "Stopping local evaluation. Close again after it stops.";
                return;
            }

            closing = true;
            _ = Dispatcher.BeginInvoke(new Action(Close));
            return;
        }

        closing = true;
        StopTelegramCommands();
        taskRetentionTimer.Stop();
        outputTimer.Stop();
        controlCenter?.Dispose();
        ollama.Dispose();
        store.Dispose();
    }
}
