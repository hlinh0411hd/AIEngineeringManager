using System.IO;
using System.Windows;
using Employee.Core;
using Employee.Infrastructure;

namespace Employee.Desktop;
public partial class MainWindow
{
    private bool projectTaskContext;
    private async void RerunTask(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        TaskIdleOnly(SelectedWork());
        if (closing || shuttingDown)
            throw new InvalidOperationException("The application is closing.");
        if (((App)Application.Current).IsUpdateValidation)
            throw new InvalidOperationException("Wait for update validation to finish.");
        var goal = unified.Rerun(SelectedWork());
        if (!selectedTaskStates.Contains(goal.State))
            SetTaskStateFilter([..selectedTaskStates, goal.State]);
        RefreshTasks(goal.Id);
        SetTaskView(TaskView.Details);
        await Begin(false);
    });
    private void OpenProjectTask(string projectId)
    {
        var project = store.Projects().FirstOrDefault(p => p.Id == projectId);
        if (project == null)
        {
            Status.Text = "Project is no longer available.";
            return;
        }

        projectTaskContext = true;
        TaskProject.SelectedItem = TaskProject.Items.OfType<ProjectInfo>().FirstOrDefault(p => p.Id == projectId);
        toolTaskForm?.Refresh(projectId);
        planDraft?.Refresh(projectId);
        AutoAssignmentChanged(this, new());
        NavigateShell("tasks");
        ShowTaskCreate(this, new());
        TaskRequest.Focus();
    }

    private void ProjectTaskClicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.DataGridRow { Item: ProjectInfo project })
        {
            e.Handled = true;
            OpenProjectTask(project.Id);
        }
    }

    private void ProjectTaskKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter && sender is System.Windows.Controls.DataGridRow { Item: ProjectInfo project })
        {
            e.Handled = true;
            OpenProjectTask(project.Id);
        }
    }

    private void AutoAssignmentChanged(object sender, RoutedEventArgs e)
    {
        if (TaskKind == null || TaskAccess == null || TaskWorkflowPanel == null || TaskPriorityField == null)
            return;
        var manual = AutoAssignTask.IsChecked != true;
        TaskKind.IsEnabled = manual;
        TaskAccess.IsEnabled = false;
        TaskProject.IsEnabled = manual || projectTaskContext;
        var visibility = manual ? Visibility.Visible : Visibility.Collapsed;
        TaskManualFields.Visibility = manual || projectTaskContext ? Visibility.Visible : Visibility.Collapsed;
        TaskKindField.Visibility = TaskPriorityField.Visibility = visibility;
        TaskAccessField.Visibility = Visibility.Collapsed;
        TaskRoutePanel.Visibility = TaskRouteHint.Visibility = TaskWorkflowPanel.Visibility = visibility;
    }

    private ProjectInfo ResolveTaskProject()
    {
        if (TaskProject.SelectedItem is ProjectInfo { Id: "" } external)
            return external;
        if (projectTaskContext || AutoAssignTask.IsChecked != true)
            return TaskProject.SelectedItem as ProjectInfo ?? throw new InvalidOperationException("Choose a project before creating the task.");
        var resolved = indexer.Resolve(TaskRequest.Text);
        if (resolved != null)
            return resolved;
        var projects = store.Projects();
        if (projects.Count == 0)
            throw new InvalidOperationException("Register a project or switch off automatic assignment for an external text task.");
        var choice = new System.Windows.Controls.ComboBox
        {
            ItemsSource = projects,
            DisplayMemberPath = "Name",
            MinWidth = 340,
            Margin = new Thickness(0, 12, 0, 12)
        };
        var panel = new System.Windows.Controls.StackPanel
        {
            Margin = new Thickness(20)
        };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "Could not identify a unique project from this request. Please choose a project.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(choice);
        var confirm = new System.Windows.Controls.Button
        {
            Content = "Use selected project",
            IsDefault = true,
            IsEnabled = false
        };
        panel.Children.Add(confirm);
        var dialog = new Window
        {
            Owner = this,
            Title = "Choose task project",
            Content = panel,
            SizeToContent = SizeToContent.Height,
            Width = 460,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        choice.SelectionChanged += (_, _) => confirm.IsEnabled = choice.SelectedItem is ProjectInfo;
        confirm.Click += (_, _) => dialog.DialogResult = true;
        if (dialog.ShowDialog() != true || choice.SelectedItem is not ProjectInfo selected)
            throw new InvalidOperationException("Task was not created. Choose a project to continue.");
        return selected;
    }

    private enum TaskView
    {
        Create,
        List,
        Details
    }

    private void ShowTaskCreate(object sender, RoutedEventArgs e) => SetTaskView(TaskView.Create);
    private void ShowTaskList(object sender, RoutedEventArgs e) => SetTaskView(TaskView.List);
    private void ShowTaskDetails(object sender, RoutedEventArgs e)
    {
        if (TaskGrid.SelectedItem is WorkItem)
            SetTaskView(TaskView.Details);
        else
            SetTaskView(TaskView.List);
    }

    private async void ResumeSelectedTask(object sender, RoutedEventArgs e)
    {
        if (TaskGrid.SelectedItem is not WorkItem { State: WorkState.Paused })
            return;
        SetTaskView(TaskView.Details);
        await Guard(() => Begin(false));
    }

    private void SetTaskView(TaskView view)
    {
        if (TaskLayout == null || TaskDetailsView == null)
            return;
        if (view == TaskView.Create)
        {
            // The Tool task tab starts on the same project as the AI task tab.
            toolTaskForm?.Refresh((TaskProject.SelectedItem as ProjectInfo)?.Id);
            TaskWorkflowChoice.ItemsSource = store.Workflows().Prepend(new ToolWorkflow { Id = "", Name = "Automatic AI task / match workflow name" }).ToArray();
            TaskWorkflowChoice.SelectedIndex = 0;
        }

        TaskCreateView.Visibility = view == TaskView.Create ? Visibility.Visible : Visibility.Collapsed;
        TaskListView.Visibility = view == TaskView.List ? Visibility.Visible : Visibility.Collapsed;
        TaskDetailsView.Visibility = view == TaskView.Details ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void ViewTaskDiagram(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        var id = unified.GoalFor(SelectedWork()).Id;
        new TaskDiagramWindow(() =>
        {
            var all = store.WorkItems();
            var goal = all.Single(w => w.Id == id);
            return new(goal, unified.SessionFor(goal), goal.SessionId == null ? [] : all.Where(w => w.SessionId == goal.SessionId && w.Id != goal.Id).ToArray(), unified.Plans(goal).ToArray(), goal.SessionId == null ? [] : store.Decisions(goal.SessionId).ToArray());
        })
        {
            Owner = this
        }.Show();
        return Task.CompletedTask;
    });
    private DateTimeOffset lastInput = DateTimeOffset.UtcNow;
    private DateTimeOffset lastAwayCheck = DateTimeOffset.MinValue;
    private bool taskPanelLoading;
    private string? displayedTimeGoal;
    private sealed record PlanningOption(PlanningPolicy Policy, string Label);
    private readonly HashSet<WorkState> selectedTaskStates = [..Enum.GetValues<WorkState>()];
    private bool updatingTaskStateFilter;
    private void InitializeTaskStateFilter()
    {
        InitializeTaskListOptions();
        foreach (var state in Enum.GetValues<WorkState>())
        {
            var check = new System.Windows.Controls.CheckBox
            {
                Content = state.ToString(),
                Tag = state,
                IsChecked = true,
                Margin = new Thickness(0, 0, 14, 8)
            };
            check.Checked += TaskStateChecked;
            check.Unchecked += TaskStateChecked;
            TaskStateFilter.Children.Add(check);
        }

        RefreshTaskStateLabels();
    }

    private void TaskStateChecked(object sender, RoutedEventArgs e)
    {
        if (updatingTaskStateFilter)
            return;
        var check = (System.Windows.Controls.CheckBox)sender;
        var state = (WorkState)check.Tag;
        if (check.IsChecked == true)
            selectedTaskStates.Add(state);
        else
            selectedTaskStates.Remove(state);
        RefreshTaskStateLabels();
        taskPage = 0;
        RefreshTasks();
    }

    private void SetTaskStateFilter(params WorkState[] states)
    {
        updatingTaskStateFilter = true;
        try
        {
            taskPage = 0;
            selectedTaskStates.Clear();
            selectedTaskStates.UnionWith(states);
            foreach (var check in TaskStateFilter.Children.OfType<System.Windows.Controls.CheckBox>())
                check.IsChecked = selectedTaskStates.Contains((WorkState)check.Tag);
        }
        finally
        {
            updatingTaskStateFilter = false;
        }

        RefreshTaskStateLabels();
        RefreshTasks();
    }

    private void RefreshTaskStateLabels()
    {
        TaskStateLabels.Children.Clear();
        foreach (var state in Enum.GetValues<WorkState>().Where(selectedTaskStates.Contains))
        {
            var label = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal
            };
            label.Children.Add(new System.Windows.Controls.TextBlock { Text = state.ToString(), VerticalAlignment = VerticalAlignment.Center });
            var remove = new System.Windows.Controls.Button
            {
                Content = "\u00d7",
                Tag = state,
                ToolTip = $"Remove {state} filter",
                Margin = new Thickness(6, 0, 0, 0),
                Padding = new Thickness(4, 0, 4, 0),
                MinHeight = 22,
                MinWidth = 22
            };
            System.Windows.Automation.AutomationProperties.SetName(remove, $"Remove {state} filter");
            remove.Click += RemoveTaskStateLabel;
            label.Children.Add(remove);
            TaskStateLabels.Children.Add(new System.Windows.Controls.Border { Child = label, Background = System.Windows.Media.Brushes.Transparent, BorderBrush = System.Windows.Media.Brushes.SlateGray, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 4, 4, 4), Margin = new Thickness(0, 0, 6, 8) });
        }
    }

    private void RemoveTaskStateLabel(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: WorkState state })
            SetTaskStateFilter(selectedTaskStates.Where(s => s != state).ToArray());
    }

    private void TaskFilterOpened(object? sender, EventArgs e)
    {
        TaskStateFilter.Children.OfType<System.Windows.Controls.CheckBox>().FirstOrDefault()?.Focus();
    }

    private void TaskFilterKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Escape)
            return;
        TaskFilterPopup.IsOpen = false;
        TaskFilterButton.Focus();
        e.Handled = true;
    }

    private void SelectAllTaskStates(object sender, RoutedEventArgs e) => SetTaskStateFilter(Enum.GetValues<WorkState>());
    private void ClearTaskStates(object sender, RoutedEventArgs e) => SetTaskStateFilter();
    private async void SubmitTask(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (updateReviewPending || controlCenter?.IsBusy == true || ((App)Application.Current).IsUpdateValidation || shuttingDown || detecting || evaluationCancel != null || testSettings.IsBusy)
            throw new InvalidOperationException("Wait for the current settings/test operation.");
        var goal = CreateTaskRecord();
        if (goal.ScheduledAt == null && unified.CanStart(goal, settings))
            await Begin(false);
        else
        {
            unified.Enqueue(goal, goal.PreferredRoute);
            queueSuspended = false;
            RefreshTasks(goal.Id);
            Status.Text = "Queued until an agent and independent project are available.";
        }
    });
    private ToolTaskFormView? toolTaskForm;
    private PlanDraftView? planDraft;

    // The Tool task tab: the task runs the chosen tool directly with the form's settings.
    private Task SubmitToolTask(ToolTaskForm form, ToolTaskFormView.SubmitMode mode) => Guard(async () =>
    {
        if (updateReviewPending || controlCenter?.IsBusy == true || ((App)Application.Current).IsUpdateValidation || shuttingDown || detecting || evaluationCancel != null || testSettings.IsBusy)
            throw new InvalidOperationException("Wait for the current settings/test operation.");
        var goal = unified.CreateToolTask(form, settings);
        toolTaskForm?.Clear();
        ShowToast(mode == ToolTaskFormView.SubmitMode.Save ? "Tool task saved" : "Tool task received", Shorten(form.Request));
        if (!selectedTaskStates.Contains(goal.State))
            SetTaskStateFilter([..selectedTaskStates, goal.State]);
        RefreshTasks(goal.Id);
        if (mode == ToolTaskFormView.SubmitMode.Save)
        {
            Status.Text = "Tool task saved. Click Run / resume to start.";
            return;
        }
        if (mode == ToolTaskFormView.SubmitMode.Run && unified.CanStart(goal, settings))
        {
            await Begin(false);
            return;
        }
        unified.Enqueue(goal, null);
        queueSuspended = false;
        RefreshTasks(goal.Id);
        Status.Text = "Tool task queued. It runs when an agent and the project are free.";
    });
    private bool drainingQueue;
    private bool queueSuspended;
    private DateTimeOffset lastQueueCheck;
    private async void QueueNewTask(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        var goal = CreateTaskRecord();
        unified.Enqueue(goal, goal.PreferredRoute);
        queueSuspended = false;
        RefreshTasks(goal.Id);
        Status.Text = "Added to queue. It will run when the current operation finishes.";
        return Task.CompletedTask;
    });
    private async void QueueSelectedTask(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        var goal = SelectedWork();
        unified.Enqueue(goal, SelectedTaskRoute(RunProviderChoice, RouteChoice));
        queueSuspended = false;
        RefreshTasks(goal.Id);
        Status.Text = "Added to queue. It will run when the current operation finishes.";
        return Task.CompletedTask;
    });
    private async void TryRunNextTask()
    {
        // Runs from a UI timer: nothing may escape this async void handler.
        try
        {
            await DispatchQueue();
        }
        catch (Exception ex)
        {
            queueSuspended = true;
            Status.Text = "Queue paused: " + ex.Message;
        }
    }

    private async Task DispatchQueue()
    {
        if (updateReviewPending || controlCenter?.IsBusy == true || ((App)Application.Current).IsUpdateValidation || closing || shuttingDown || queueSuspended || drainingQueue || detecting || evaluationCancel != null || testSettings.IsBusy)
            return;
        if ((DateTimeOffset.UtcNow - lastQueueCheck).TotalSeconds < 1)
            return;
        lastQueueCheck = DateTimeOffset.UtcNow;
        if (unified.NextQueued(settings) == null)
        {
            if (settings.AdaptiveParallelism && ResourceMonitor.Snapshot()is { } memory && memory.AvailableMemoryMb < settings.MinimumFreeMemoryMb && store.WorkItems().Any(work => work.IsGoal && work.State == WorkState.Queued && work.QueuedAt != null && (work.ScheduledAt == null || work.ScheduledAt <= DateTimeOffset.UtcNow)))
                Status.Text = $"Queue waiting for RAM: {memory.AvailableMemoryMb:N0} MB free, {settings.MinimumFreeMemoryMb:N0} MB required. Free memory or adjust Free RAM reserve in Settings.";
            return;
        }

        drainingQueue = true;
        try
        {
            if (health.Count == 0)
                await Detect();
            if (closing || queueSuspended)
                return;
            // Failures do not automatically requeue.
            unified.DispatchQueued(settings, RunQueuedGoal, () => controlCenter?.IsBusy != true);
        }
        finally
        {
            drainingQueue = false;
        }
    }

    private async Task RunQueuedGoal(WorkItem goal)
    {
        try
        {
            if (goal.ToolWorkflowId != null)
                await RunToolTask(goal);
            else
                await unified.StartAsync(store.WorkItems().Single(w => w.Id == goal.Id), settings, health, goal.PreferredRoute);
            if (!closing && !shuttingDown)
            {
                RefreshTasks();
                RefreshPerformance();
            }
        }
        catch (Exception ex)
        {
            if (!closing)
                Status.Text = "Queued task paused: " + ex.Message;
        }
    }

    private void LoadAutomation()
    {
        var a = settings.Automation;
        AutoPlanning.ItemsSource = new[]
        {
            new PlanningOption(PlanningPolicy.Auto, "Automatically when needed"),
            new PlanningOption(PlanningPolicy.Always, "Always plan"),
            new PlanningOption(PlanningPolicy.Never, "Run directly")
        };
        AutoPlanning.SelectedItem = AutoPlanning.Items.Cast<PlanningOption>().First(x => x.Policy == a.Planning);
        AutoDuration.Text = a.DurationMinutes.ToString();
        AutoCalls.Text = a.MaxCalls.ToString();
        AutoTasks.Text = a.MaxTasks.ToString();
        AutoDepth.Text = a.MaxDepth.ToString();
        AutoDelegation.IsChecked = a.AllowDelegation;
        AutoContinue.IsChecked = a.ContinueSteps;
        AutoAway.IsChecked = a.Away;
        AutoTests.IsChecked = a.BatchTests;
        AutoIdleMinutes.Text = a.AutoAwayMinutes.ToString();
        AutoCoordinator.SelectedItem = AutoCoordinator.Items.Cast<RouteOption>().FirstOrDefault(x => x.Route?.ProviderId == a.Coordinator?.ProviderId && x.Route?.ModelId == a.Coordinator?.ModelId) ?? AutoCoordinator.Items.Cast<RouteOption>().FirstOrDefault();
    }

    private async void SaveAutomation(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        IdleOnly();
        var a = new TaskAutomationSettings
        {
            UseToolSkills = settings.Automation.UseToolSkills,
            AutoApplyGeneratedImages = settings.Automation.AutoApplyGeneratedImages,
            PlannerChoosesTools = settings.Automation.PlannerChoosesTools,
            VerifyAcceptance = settings.Automation.VerifyAcceptance,
            SignOffMilestones = settings.Automation.SignOffMilestones,
            MaxVerifyRounds = settings.Automation.MaxVerifyRounds,
            MaxParallelAgents = settings.Automation.MaxParallelAgents,
            Planning = (AutoPlanning.SelectedItem as PlanningOption)?.Policy ?? PlanningPolicy.Auto,
            Coordinator = (AutoCoordinator.SelectedItem as RouteOption)?.Route,
            DurationMinutes = Number(AutoDuration.Text, 0, 43200),
            MaxCalls = Number(AutoCalls.Text, 0, 100000),
            MaxTasks = Number(AutoTasks.Text, 0, 10000),
            MaxDepth = Number(AutoDepth.Text, 0, 5),
            AutoAwayMinutes = Number(AutoIdleMinutes.Text, 0, 120),
            AllowDelegation = AutoDelegation.IsChecked == true,
            ContinueSteps = AutoContinue.IsChecked == true,
            Away = AutoAway.IsChecked == true,
            BatchTests = AutoTests.IsChecked == true
        };
        settings.Automation = a;
        store.SaveSettings(settings);
        Status.Text = "Defaults saved. Tasks will use orchestration when starting new work.";
        return Task.CompletedTask;
    });
    // Approve/Reject only when a decision is waiting; Apply edits only when a step has proposed file changes.
    private void UpdateTaskActions(WorkItem goal, int pendingDecisions)
    {
        var visible = pendingDecisions > 0 ? Visibility.Visible : Visibility.Collapsed;
        ApproveSelectedDecisionButton.Visibility = visible;
        RejectDecisionButton.Visibility = visible;
        // The decision list and its details only take space when something waits for you.
        PendingGrid.Visibility = visible;
        PendingDecisionDetails.Visibility = visible;
        var proposals = unified.Steps(goal).Append(goal).Any(w => store.Logs(w.Id) is var logs && logs.LastOrDefault(l => l.Event == "EditProposal") is { } proposal && !logs.Any(l => l.Event == "EditsApplied" && l.Sequence > proposal.Sequence));
        ApplyEditsButton.Visibility = proposals ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowTaskAiChoice(object sender, RoutedEventArgs e)
    {
        TaskAiActions.Visibility = Visibility.Visible;
        TaskAiActions.IsExpanded = true;
    }

    // The menu item mirrors the (hidden) Away checkbox, whose handlers save the choice.
    private void ToggleTaskAway(object sender, RoutedEventArgs e) => TaskAway.IsChecked = TaskAwayMenu.IsChecked;

    private void OpenTaskMore(object sender, RoutedEventArgs e)
    {
        TaskAwayMenu.IsChecked = TaskAway.IsChecked == true;
        if (TaskMoreButton.ContextMenu is { } menu)
        {
            menu.PlacementTarget = TaskMoreButton;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private void RefreshTaskPanels(WorkItem goal)
    {
        taskPanelLoading = true;
        var previousStep = (StepsGrid.SelectedItem as WorkItem)?.Id;
        var steps = OrderedSteps(unified.Steps(goal));
        StepsGrid.ItemsSource = StepsView(goal, steps);
        StepsGrid.SelectedItem = steps.FirstOrDefault(w => w.Id == previousStep) ?? steps.FirstOrDefault(w => w.State is not (WorkState.Completed or WorkState.Skipped)) ?? steps.LastOrDefault();
        var previousDecision = (PendingGrid.SelectedItem as PendingDecision)?.Id;
        var pending = unified.Decisions(goal);
        PendingGrid.ItemsSource = pending;
        PendingGrid.SelectedItem = pending.FirstOrDefault(d => d.Id == previousDecision) ?? pending.FirstOrDefault();
        UpdateTaskActions(goal, pending.Count);
        var next = NextStepGuide.For(goal, steps, pending, store);
        NextStepText.Text = next ?? "";
        NextStepBanner.Visibility = next == null ? Visibility.Collapsed : Visibility.Visible;
        var plan = unified.Plans(goal).LastOrDefault();
        TaskPlanGrid.ItemsSource = plan?.Plan.Tasks.Select(t => new { t.Key, t.Request, t.Kind, t.Access, t.SkillId, Executor = t.Tool == null ? "AI" : "Tool: " + t.Tool.ToolId + " / " + t.Tool.Operation, After = string.Join(", ", t.DependsOn) }).ToArray();
        var session = unified.SessionFor(goal);
        if (displayedTimeGoal != goal.Id)
        {
            RenewMinutes.Text = (session?.DurationMinutes ?? settings.Automation.DurationMinutes).ToString();
            TaskCallLimit.Text = (session?.MaxCalls ?? settings.Automation.MaxCalls).ToString();
            displayedTimeGoal = goal.Id;
        }

        TaskTimeStatus.Text = session == null ? "Not started" : session.DurationMinutes == 0 ? "Unlimited time" : session.Deadline == null ? "Timer has not started" : SessionTiming.Remaining(session) <= TimeSpan.Zero ? "Time expired · progress saved" : "Until " + session.Deadline.Value.ToLocalTime().ToString("dd/MM HH:mm");
        TaskCallStatus.Text = session == null ? "" : $"AI calls used: {session.CallBudgetDisplay}";
        SaveTaskCallLimitButton.IsEnabled = session != null && goal.State != WorkState.Completed;
        TaskAway.IsChecked = session?.Away ?? goal.AwayOverride ?? settings.Automation.Away;
        taskPanelLoading = false;
    }

    private async void SaveTaskCallLimit(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        var goal = SelectedWork();
        TaskIdleOnly(goal);
        unified.SaveCallLimit(goal, Number(TaskCallLimit.Text, 0, 100));
        RefreshTasks(goal.Id);
        Status.Text = "AI call limit saved. Run / resume to continue.";
        return Task.CompletedTask;
    });
    private async void RenewTaskTime(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var goal = SelectedWork();
        TaskIdleOnly(goal);
        unified.RenewTime(goal, Number(RenewMinutes.Text, 0, 43200));
        RefreshTasks(goal.Id);
        await Begin(false);
    });
    private async void ChangeDataFolder(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        IdleOnly();
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose an empty folder for data",
            InitialDirectory = dataPath
        };
        if (dialog.ShowDialog(this) == true)
            ((App)Application.Current).ChangeDataFolder(this, dataPath, dialog.FolderName);
        return Task.CompletedTask;
    });
    private WorkItem? SelectedTestTarget() => StepsGrid.SelectedItem as WorkItem ?? (TaskGrid.SelectedItem is WorkItem goal ? unified.Steps(goal).LastOrDefault() ?? goal : null);
    private async void ChangeSelectedStepAI(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var goal = SelectedWork();
        TaskIdleOnly(goal);
        var step = StepsGrid.SelectedItem as WorkItem ?? throw new InvalidOperationException("Select an AI step first.");
        if (step.Tool != null || step.State is not (WorkState.Queued or WorkState.Paused or WorkState.Failed or WorkState.WaitingForProvider or WorkState.AwaitingApproval))
            throw new InvalidOperationException("Select a stopped AI step that still needs to run.");
        if (health.Count == 0)
            await Detect();
        var panel = new System.Windows.Controls.StackPanel
        {
            Margin = new Thickness(16)
        };
        var provider = new System.Windows.Controls.ComboBox
        {
            DisplayMemberPath = "Label"
        };
        var model = new System.Windows.Controls.ComboBox
        {
            DisplayMemberPath = "Label"
        };
        var save = new System.Windows.Controls.Button
        {
            Content = "Save AI for this step",
            IsDefault = true
        };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = step.Request, TextWrapping = TextWrapping.Wrap, MaxHeight = 100 });
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "Provider" });
        panel.Children.Add(provider);
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "Model" });
        panel.Children.Add(model);
        panel.Children.Add(save);
        var dialog = new Window
        {
            Owner = this,
            Title = "Change AI for selected step",
            Content = panel,
            Width = 480,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        RefreshTaskRoutePair(provider, model, step.PreferredRoute);
        provider.SelectionChanged += (_, _) => PopulateTaskModels(provider, model);
        Route? target = null;
        save.Click += (_, _) =>
        {
            try
            {
                target = SelectedTaskRoute(provider, model) ?? throw new InvalidOperationException("Select a provider for this step.");
                unified.SwitchStoppedStep(goal, step.Id, target, settings, health);
                dialog.DialogResult = true;
            }
            catch (InvalidOperationException ex)
            {
                MessageBox.Show(dialog, ex.Message, "Change AI for selected step");
            }
        };
        if (dialog.ShowDialog() == true && target != null)
        {
            RefreshTasks(goal.Id);
            Status.Text = $"Saved {target.ProviderId} / {Orchestrator.DisplayModel(target.ModelId)} for the selected step. Run / resume to continue.";
        }
    });
    private async void SkipSelectedStep(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        var goal = SelectedWork();
        var step = StepsGrid.SelectedItem as WorkItem ?? throw new InvalidOperationException("Select a step to skip.");
        unified.SkipStep(goal, step.Id);
        RefreshTasks(goal.Id);
        Status.Text = "Step skipped. Run / resume to continue, or Send & continue updating with a new comment.";
        return Task.CompletedTask;
    });
    private void ViewStep(object sender, RoutedEventArgs e)
    {
        var work = StepsGrid.SelectedItem as WorkItem;
        if (work == null && PendingGrid.SelectedItem is PendingDecision decision)
            work = store.WorkItems().FirstOrDefault(w => w.Id == decision.WorkId);
        if (work == null)
        {
            Status.Text = "Select a step to view its result.";
            return;
        }

        var project = work.IsExternal ? new ProjectInfo("", "External task", Path.GetTempPath()) : store.Projects().Single(p => p.Id == work.ProjectId);
        var report = TaskReviewReport.Create(work, project, store.Logs(work.Id), store.LatestHandoff(work.Id));
        new TaskReviewWindow(report)
        {
            Owner = this
        }.ShowDialog();
    }

    private void PendingDecisionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (PendingDecisionDetails == null || ApproveSelectedDecisionButton == null)
            return;
        if (PendingGrid.SelectedItem is not PendingDecision decision)
        {
            PendingDecisionDetails.Text = "No decision selected. If the task is paused, see its stop reason in Progress & results.";
            ApproveSelectedDecisionButton.IsEnabled = false;
            return;
        }

        var work = store.WorkItems().FirstOrDefault(w => w.Id == decision.WorkId);
        var(action, explanation) = decision.Kind switch
        {
            "Approval" or "ProviderWait" => ("Retry selected step", "This queues another AI attempt. You can choose a different model in the task toolbar before retrying."),
            "Plan" => ("Approve plan", "Accept the proposed steps shown in the Plan tab and continue."),
            "Review" => ("Accept step result", "Open View step result and inspect the output before accepting it."),
            "ToolApproval" => ("Allow tool action", "Inspect the exact request in Tool requests / artifacts before authorizing it."),
            "ToolReview" => ("Accept tool result", "Inspect the tool output and artifacts before accepting the result."),
            "Delegation" => ("Approve AI handoff", "Allow the proposed AI worker to take over the step."),
            "TestReview" => ("Acknowledge test result", "Inspect the test result. Acknowledging it does not change the test outcome."),
            "MilestoneSignOff" => ("Approve milestone", "Run the project and check this milestone. Approve, then Run/resume, to start the next milestone. To change it instead, write a comment on the task: the milestone is reworked and tested again. Reject reopens it without a comment."),
            _ => ("Review required", "Inspect the reason below and resolve the reported issue before resuming.")};
        var reason = decision.Message;
        if (decision.Kind is "Approval" or "ProviderWait")
        {
            var execution = store.Logs(decision.WorkId).LastOrDefault(log => log.Event == "Execution")?.Message;
            if (execution?.Contains("Selected model is at capacity", StringComparison.OrdinalIgnoreCase) == true)
                reason = "The selected model was at capacity. Retry later or select another available model.";
            else if (reason.Contains("item.started", StringComparison.Ordinal))
                reason = "The stored CLI output ends at the start of a command. No command result was recorded. Check changed files in View step result before retrying.";
        }

        PendingDecisionDetails.Text = $"Step: {work?.Request ?? decision.WorkId}\nReason: {reason}\nAction: {explanation}";
        ApproveSelectedDecisionButton.Content = action;
        ApproveSelectedDecisionButton.IsEnabled = decision.Kind is "Approval" or "ProviderWait" or "Plan" or "Review" or "ToolApproval" or "ToolReview" or "Delegation" or "TestReview" or "MilestoneSignOff";
    }

    private async void RejectPending(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        var goal = SelectedWork();
        TaskIdleOnly(goal);
        var decision = PendingGrid.SelectedItem as PendingDecision ?? throw new InvalidOperationException("Select an item to reject in Steps & approvals.");
        unified.Resolve(goal, decision.Id, false, settings, health);
        RefreshTasks(goal.Id);
        Status.Text = "Rejection recorded. Permissions will not be expanded and the proposal will not run automatically.";
        return Task.CompletedTask;
    });
    private void TaskAwayChanged(object sender, RoutedEventArgs e)
    {
        if (taskPanelLoading || TaskGrid.SelectedItem is not WorkItem goal)
            return;
        if (goal.SessionId != null)
            unified.EngineFor(goal).SetAway(goal.SessionId, TaskAway.IsChecked == true);
        else
        {
            goal.AwayOverride = TaskAway.IsChecked == true;
            store.SaveWork(goal);
        }
    }

    private void CheckAutoAway()
    {
        if (closing || (DateTimeOffset.UtcNow - lastAwayCheck).TotalSeconds < 5)
            return;
        lastAwayCheck = DateTimeOffset.UtcNow;
        foreach (var id in unified.CurrentGoalIds)
        {
            var goal = store.WorkItems().FirstOrDefault(w => w.Id == id);
            if (goal == null)
                continue;
            var session = unified.SessionFor(goal);
            if (session is { Away: false, AutoAwayMinutes: > 0 } && (DateTimeOffset.UtcNow - lastInput).TotalMinutes >= session.AutoAwayMinutes)
                unified.EngineFor(goal).SetAway(session.Id, true);
        }
    }

    private void VerifyUnifiedSmoke()
    {
        AutoAssignTask.IsChecked = true;
        if (TaskKind.IsEnabled || TaskAccess.IsEnabled)
            throw new InvalidOperationException("Automatic assignment must disable manual choices.");
        AutoAssignTask.IsChecked = false;
        if (!TaskKind.IsEnabled || TaskAccess.IsEnabled)
            throw new InvalidOperationException("Manual settings must enable task type and keep project write access automatic.");
        TaskKind.SelectedItem = WorkKind.Review;
        TaskAccess.SelectedItem = AccessLevel.ReadProject;
        if (Tabs.Items.Count < 6 || Tabs.Items.Cast<System.Windows.Controls.TabItem>().Any(t => t.Header?.ToString()?.Contains("V2") == true))
            throw new InvalidOperationException("Standalone V2 tab must not be shown.");
        ShowTaskCreate(this, new());
        TaskRequest.Text = "Fixture: multiple steps in Tasks";
        var goal = CreateTaskRecord();
        if (goal.AutoAssign || goal.Kind != WorkKind.Review || goal.Access != AccessLevel.WriteProject)
            throw new InvalidOperationException("Manual choices were not saved with the task.");
        TaskKind.SelectedItem = WorkKind.Coding;
        if (TaskCreateView.Visibility != Visibility.Visible || TaskListView.Visibility != Visibility.Collapsed || SelectedWork().Id != goal.Id)
            throw new InvalidOperationException("Task creation must show only the composer and preserve the selected task.");
        ShowTaskDetails(this, new());
        if (TaskCreateView.Visibility != Visibility.Collapsed || TaskDetailsView.Visibility != Visibility.Visible || TaskListView.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Task details must occupy the task workspace without the list.");
        ShowTaskList(this, new());
        if (TaskListView.Visibility != Visibility.Visible || TaskDetailsView.Visibility != Visibility.Collapsed || TaskCreateView.Visibility != Visibility.Collapsed || SelectedWork().Id != goal.Id)
            throw new InvalidOperationException("Back must restore only the list and preserve selection.");
        ShowTaskCreate(this, new());
        if (TaskDetailsView.Visibility != Visibility.Collapsed || SelectedWork().Id != goal.Id)
            throw new InvalidOperationException("Switching task views lost the selected task.");
        var session = new WorkSession
        {
            RootWorkId = goal.Id,
            Name = "Unified UI fixture",
            Goal = goal.Request,
            ProjectIds = [goal.ProjectId],
            AccessCeiling = goal.Access,
            AutoContinueSteps = true
        };
        store.SaveSession(session);
        goal.SessionId = session.Id;
        store.SaveWork(goal);
        var planner = new PlanningService(store, store, orchestrator);
        var plan = planner.Import(session, new("Plan displayed within the task", [new("a", goal.ProjectId, "First fixture step", WorkKind.Coding, goal.Access, []), new("b", goal.ProjectId, "Next fixture step", WorkKind.Coding, goal.Access, ["a"])]));
        var decision = new PendingDecision
        {
            SessionId = session.Id,
            WorkId = goal.Id,
            Kind = "Plan",
            PlanId = plan.Id,
            Message = "Fixture plan approval in Tasks"
        };
        store.SaveDecision(decision);
        unified.Resolve(goal, decision.Id, true, settings, health);
        session.State = SessionState.Paused;
        store.SaveSession(session);
        unified.Sync(goal.Id);
        RefreshTasks(goal.Id);
        if (StepsGrid.Items.Count != 2 || TaskPlanGrid.Items.Count != 2 || TaskGrid.Items.Count != 2)
            throw new InvalidOperationException("Unified task panels did not load plan/steps/root rows.");
        AutoDuration.Text = "0";
        SaveAutomation(this, new());
        if (store.LoadSettings().Automation.DurationMinutes != 0)
            throw new InvalidOperationException("Unlimited duration was not saved.");
        unified.RenewTime(goal, 0);
        RefreshTasks(goal.Id);
        if (TaskTimeStatus.Text != "Unlimited time" || unified.SessionFor(goal)?.Deadline != null || DataLocation.Text != System.IO.Path.Combine(dataPath, "manager.db"))
            throw new InvalidOperationException("Time/data controls did not reflect persisted settings.");
        TaskPanels.SelectedIndex = 0;
        VerifyAttachmentSmoke(goal);
        VerifyTaskStateFilterSmoke(goal);
        VerifyTaskQueueSmoke();
    }

    private void VerifyTaskQueueSmoke()
    {
        queueSuspended = true;
        try
        {
            SetTaskStateFilter(Enum.GetValues<WorkState>());
            TaskRequest.Text = "Queue creation UI fixture";
            QueueNewTaskButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            queueSuspended = true;
            var queued = SelectedWork();
            if (queued.State != WorkState.Queued || queued.QueuedAt == null || QueueSelectedTaskButton.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Create-and-queue button did not queue or hide the duplicate action.");
            unified.SaveForLater(queued);
            RefreshTasks(queued.Id);
            if (QueueSelectedTaskButton.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Saved task did not show the queue action.");
            QueueSelectedTaskButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            queueSuspended = true;
            if (SelectedWork().State != WorkState.Queued || QueueSelectedTaskButton.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Selected task queue action failed.");
            unified.SaveForLater(SelectedWork());
            RefreshTasks(queued.Id);
            var started = SelectedWork();
            started.Attempts = 1;
            store.SaveWork(started);
            RefreshTasks(started.Id);
            if (QueueSelectedTaskButton.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Started task showed the queue action.");
            SetTaskStateFilter(WorkState.Queued);
            if (TaskGrid.Items.Count != 0 || QueueSelectedTaskButton.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Empty selection retained the queue action.");
            SetTaskStateFilter(Enum.GetValues<WorkState>());
        }
        finally
        {
            queueSuspended = true;
        }
    }

    private void VerifyTaskStateFilterSmoke(WorkItem goal)
    {
        var all = store.WorkItems().Where(w => !w.IsCoordinator && (w.IsGoal || w.ParentTaskId == null)).ToArray();
        InitializeTaskListOptions();
        foreach (var state in Enum.GetValues<WorkState>())
        {
            SetTaskStateFilter(state);
            var expected = all.Count(w => w.State == state);
            if (TaskGrid.Items.Count != expected || TaskGrid.Items.Cast<WorkItem>().Any(w => w.State != state))
                throw new InvalidOperationException("State filter returned incorrect requests.");
            RefreshTasks();
            if (TaskGrid.Items.Count != expected || !selectedTaskStates.SetEquals([state]))
                throw new InvalidOperationException("State filter was lost on refresh.");
            if (expected == 0 && (selectedGoalId != null || TaskDetail.Text.Length != 0 || StepsGrid.Items.Count != 0 || PendingGrid.Items.Count != 0 || TaskPlanGrid.Items.Count != 0 || commentHistory.Children.Count != 0))
                throw new InvalidOperationException("Empty state filter retained task details.");
        }

        SetTaskStateFilter(WorkState.Saved, WorkState.Paused);
        if (TaskGrid.Items.Count != all.Count(w => w.State is WorkState.Saved or WorkState.Paused))
            throw new InvalidOperationException("Multiple state filter failed.");
        var savedCheck = TaskStateFilter.Children.OfType<System.Windows.Controls.CheckBox>().Single(c => (WorkState)c.Tag == WorkState.Saved);
        savedCheck.IsChecked = false;
        if (TaskGrid.Items.Count != all.Count(w => w.State == WorkState.Paused))
            throw new InvalidOperationException("Checkbox toggle did not update requests.");
        SetTaskStateFilter();
        if (TaskGrid.Items.Count != 0 || selectedGoalId != null || TaskDetail.Text.Length != 0)
            throw new InvalidOperationException("Unchecked states must show no requests.");
        SetTaskStateFilter(Enum.GetValues<WorkState>());
        RefreshTasks(goal.Id);
        if (TaskGrid.Items.Count != all.Length || (TaskGrid.SelectedItem as WorkItem)?.Id != goal.Id)
            throw new InvalidOperationException("All states did not restore requests and selection.");
    }
}
