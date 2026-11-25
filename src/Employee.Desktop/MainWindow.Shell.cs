using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Employee.Core;
using Employee.Infrastructure;

namespace Employee.Desktop;
public partial class MainWindow
{
    private ControlCenterView? controlCenter;
    private DashboardView? dashboard;
    private const bool shellAutonomous = false;
    private bool shellLoading, updateReviewPending;
    private DateTimeOffset lastShellRefresh;
    private TimeSpan previousProcessor;
    private DateTimeOffset previousProcessorAt;
    private string gpuStatus = "VRAM: unknown (read metadata with the Local AI button)";
    private sealed record AgentRow(string GoalId, string Role, string Provider, string Model, string State, string Step);
    private sealed record ApprovalRow(string GoalId, string Project, string Request, string State, string Reason, string Decisions);
    private void InitializeShell(bool smoke)
    {
        shellLoading = true;
        ApplyReferenceChrome();
        controlCenter = new(store, unified, DataLocationService.FindApplicationRoot(AppContext.BaseDirectory), dataPath, () => settings, BeforeControlMutation, FreezeUpdateReview, () => Close());
        var skillFolder = Path.Combine(DataLocationService.FindApplicationRoot(AppContext.BaseDirectory), ".agents", "skills");
        if (!Directory.Exists(skillFolder))
            skillFolder = Path.Combine(AppContext.BaseDirectory, "skills");
        var sharedSkills = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agents", "skills");
        var skills = new ToolSkillRegistry(store, skillFolder, sharedSkills) { AutomationDataRoot = dataPath };
        orchestrator.Skills = skills;
        orchestrator.ToolTasks = new ToolTaskExecutor(store, store, controlCenter.ExecutionService, skills, dataPath, (work, paths) => unified.ReserveExternal(paths, settings, work.ParentTaskId ?? throw new InvalidOperationException("Tool task has no parent goal.")));
        toolTaskForm = new ToolTaskFormView(() => store.Projects(), () => store.Tools());
        toolTaskForm.Submitted += SubmitToolTask;
        ToolTaskHost.Content = toolTaskForm;
        // "Plan draft": a third way to create work — a full plan to review, then sent as an AI task.
        planDraft = new PlanDraftView(() => store.Projects())
        {
            Draft = async (text, project, type) =>
            {
                if (health.Count == 0)
                    health = await orchestrator.DetectAsync(settings);
                return await new PlanDrafter(store, providers).DraftAsync(text, project, type, settings, health, CancellationToken.None);
            }
        };
        planDraft.UseAsTask += (project, text) =>
        {
            if (project != null)
                TaskProject.SelectedItem = TaskProject.Items.OfType<ProjectInfo>().FirstOrDefault(p => p.Id == project.Id) ?? TaskProject.SelectedItem;
            TaskRequest.Text = text;
            TaskCreateView.SelectedItem = CreateAiTab;
            TaskRequest.Focus();
            TaskRequest.CaretIndex = TaskRequest.Text.Length;
            ShowToast("Plan copied to the AI task", "Check it, then Create.");
        };
        TaskCreateView.Items.Add(new TabItem { Header = "Plan draft", Content = new ScrollViewer { Content = planDraft, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        controlCenter.AttachSkillRegistry(skills);
        ToolsHost.Content = controlCenter;
        ToolsSettingsHost.Content = controlCenter.SettingsContent;
        controlCenter.SettingsRequested += OpenSettingsSection;
        InitializeSettingsSections();
        controlCenter.Changed += () =>
        {
            lastShellRefresh = DateTimeOffset.MinValue;
        };
        dashboard = new();
        OfficeView.Attach(unified, () => store.WorkItems(), OpenGoal, OpenOfficeSwitch, OfficeWorkDetails, store);
        InitializeOfficeSettings();
        DashboardHost.Content = dashboard;
        dashboard.Navigate += NavigateShell;
        dashboard.OpenGoal += OpenGoal;
        dashboard.CreateProjectTask += OpenProjectTask;
        dashboard.PauseRequested += () => HeaderPause(this, new());
        dashboard.StopRequested += () => HeaderStop(this, new());
        dashboard.SwitchRequested += () =>
        {
            var id = unified.CurrentGoalIds.FirstOrDefault();
            if (id != null)
                OpenGoal(id);
        };
        RefreshLocalModelChoices();
        shellLoading = false;
        NavigateShell("office");
        RefreshShell(true);
        Tabs.SelectionChanged += (_, e) =>
        {
            if (ReferenceEquals(e.Source, Tabs))
                lastShellRefresh = DateTimeOffset.MinValue;
        };
    }

    private void BeforeControlMutation()
    {
        if (closing || shuttingDown || unified.IsBusy || detecting || evaluationCancel != null || testSettings.IsBusy)
            throw new InvalidOperationException("Stop or wait for agents and configuration jobs before changing tools or updating.");
    }

    private IDisposable FreezeUpdateReview()
    {
        BeforeControlMutation();
        updateReviewPending = true;
        return new UiLease(() => updateReviewPending = false);
    }

    private sealed class UiLease(Action release) : IDisposable
    {
        public void Dispose() => release();
    }

    private void ShellNavigate(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string page })
            NavigateShell(page);
    }

    private void NavigateShell(string page)
    {
        if (page.StartsWith("settings:", StringComparison.Ordinal))
        {
            OpenSettingsSection(page[9..]);
            return;
        }

        if (page == "local")
        {
            OpenSettingsSection("Local");
            return;
        }

        Tabs.SelectedItem = page switch
        {
            "dashboard" or "agents" => DashboardTab,
            "projects" => ProjectsTab,
            "tasks" => TasksTab,
            "office" => OfficeTab,
            "providers" => ProvidersTab,
            "tools" or "workflows" or "updates" => ToolsTab,
            "activity" => ActivityTab,
            "approvals" => ApprovalsTab,
            "settings" => SettingsTab,
            _ => DashboardTab
        };
        if (page == "tasks")
            ShowTaskList(this, new());
        if (page is "tools" or "workflows" or "updates")
            controlCenter?.Navigate(page);
        foreach (var button in ShellNavigation.Children.OfType<Button>())
            button.Background = button.Tag?.ToString() == page ? new LinearGradientBrush(Color.FromRgb(84, 58, 132), Color.FromRgb(58, 43, 97), 0) : Brushes.Transparent;
        lastShellRefresh = DateTimeOffset.MinValue;
    }

    private void ShellNewTask(object sender, RoutedEventArgs e)
    {
        // Always show the project selector so the user can choose the project for a new task.
        projectTaskContext = true;
        if (TaskProject.SelectedItem is not ProjectInfo { Id.Length: > 0 })
            TaskProject.SelectedItem = TaskProject.Items.OfType<ProjectInfo>().FirstOrDefault(p => p.Id.Length > 0) ?? TaskProject.SelectedItem;
        AutoAssignmentChanged(this, new());
        NavigateShell("tasks");
        ShowTaskCreate(sender, e);
        TaskRequest.Focus();
    }

    private void HeaderPause(object sender, RoutedEventArgs e)
    {
        unified.PauseAllAfterCurrent();
        controlCenter?.PauseAfterCurrent();
        Status.Text = "No new steps will start after the running ones finish.";
    }

    private async void HeaderStop(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        queueSuspended = true;
        evaluationCancel?.Cancel();
        await unified.StopAllAsync();
        if (controlCenter != null)
            await controlCenter.StopAsync();
        Status.Text = "Stopped; checkpoints and history kept.";
    });
    private void OpenGoal(string id)
    {
        var goal = store.WorkItems().Single(w => w.Id == id);
        if (!selectedTaskStates.Contains(goal.State))
            SetTaskStateFilter([..selectedTaskStates, goal.State]);
        RefreshTasks(goal.Id);
        NavigateShell("tasks");
        ShowTaskDetails(this, new());
    }

    private void OpenOfficeSwitch(string id)
    {
        OpenGoal(id);
        TaskAiActions.IsExpanded = true;
        RunProviderChoice.Focus();
    }

    private string OfficeWorkDetails(WorkItem work)
    {
        var logs = store.Logs(work.Id);
        var execution = logs.LastOrDefault(l => l.Event == "Execution");
        var usage = execution == null || work.ProviderId == null ? null : ReportedUsage.Parse(work.ProviderId, execution.Message);
        var usageText = usage == null ? "No data" : $"Input: {usage.Input?.ToString() ?? "—"}, Output: {usage.Output?.ToString() ?? "—"}, Cached input: {usage.CachedInput?.ToString() ?? "—"}";
        var project = store.Projects().FirstOrDefault(p => p.Id == work.ProjectId) ?? new ProjectInfo("", "External", "");
        var report = TaskReviewReport.Create(work, project, logs, store.LatestHandoff(work.Id));
        var recentLogs = string.Join("\n\n", logs.TakeLast(20).Select(l => $"{l.At.ToLocalTime():g} [{l.Event}] {l.Message}"));
        return $"Usage (latest run, when reported): {usageText}\n\nResult:\n{report.Result}\n\nLogs (latest 20):\n{(recentLogs.Length == 0 ? "No data" : recentLogs)}";
    }

    private void OpenTaskTools(object sender, RoutedEventArgs e) => NavigateShell("tools");
    private string? activeToolGoalId;
    private async Task RunToolTask(WorkItem goal)
    {
        var center = controlCenter ?? throw new InvalidOperationException("Tool center is not ready.");
        if (center.IsBusy || activeToolGoalId != null)
            throw new InvalidOperationException("Wait for the current tool operation.");
        var workflow = store.Workflows().Single(w => w.Id == goal.ToolWorkflowId);
        if (workflow.ProjectId != goal.ProjectId)
            throw new InvalidOperationException("Task/workflow project mismatch.");
        activeToolGoalId = goal.Id;
        goal.State = WorkState.Running;
        goal.QueuedAt = null;
        store.SaveWork(goal);
        store.Log(goal.Id, "ToolWorkflow", "Started/resumed workflow " + workflow.Id);
        try
        {
            await center.RunWorkflowTask(workflow.Id);
            workflow = store.Workflows().Single(w => w.Id == workflow.Id);
            goal = store.WorkItems().Single(w => w.Id == goal.Id);
            goal.State = workflow.State switch
            {
                ToolRunState.Completed => WorkState.Completed,
                ToolRunState.WaitingApproval => WorkState.AwaitingApproval,
                ToolRunState.NeedsReview => WorkState.NeedsReview,
                ToolRunState.Failed => WorkState.Failed,
                _ => WorkState.Paused
            };
            goal.Notes = workflow.Notes + " | Review tool requests/artifacts in Tools; resume from this task.";
            store.SaveWork(goal);
            store.Log(goal.Id, "ToolWorkflow", goal.Notes);
            foreach (var step in workflow.Steps.Where(s => s.RunId != null))
            {
                var run = store.ToolRuns().FirstOrDefault(r => r.Id == step.RunId);
                if (run != null)
                    store.Log(goal.Id, "ToolResult", step.Key + " / " + run.State + "\n" + run.Error + "\n" + run.Output);
            }
        }
        catch (Exception ex)
        {
            goal = store.WorkItems().Single(w => w.Id == goal.Id);
            goal.State = WorkState.Failed;
            goal.Notes = ex.Message;
            store.SaveWork(goal);
            throw;
        }
        finally
        {
            activeToolGoalId = null;
            if (!closing)
                RefreshTasks(goal.Id);
        }
    }

    private void OpenSelectedAgent(object sender, RoutedEventArgs e)
    {
        if (AgentsGrid.SelectedItem is AgentRow row)
            OpenGoal(row.GoalId);
    }

    private async void StopSelectedAgent(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (AgentsGrid.SelectedItem is AgentRow row)
            await unified.StopAsync(store.WorkItems().Single(w => w.Id == row.GoalId));
    });
    private void OpenSelectedApproval(object sender, RoutedEventArgs e)
    {
        if (ApprovalsGrid.SelectedItem is ApprovalRow row)
            OpenGoal(row.GoalId);
    }

    private void OpenToolApprovals(object sender, RoutedEventArgs e) => NavigateShell("tools");
    private void RefreshShell(bool force = false)
    {
        if (closing || dashboard == null || (!force && (DateTimeOffset.UtcNow - lastShellRefresh).TotalSeconds < 2))
            return;
        lastShellRefresh = DateTimeOffset.UtcNow;
        var ids = unified.CurrentGoalIds.Concat(activeToolGoalId == null ? Array.Empty<string>() : new[] { activeToolGoalId! }).ToArray();
        var running = ids.Length + (controlCenter?.ExecutionBusy == true && activeToolGoalId == null ? 1 : 0);
        HeaderRunState.Text = running > 0 ? $"● Running · {running}" : "● Ready";
        var all = store.WorkItems();
        var goals = all.Where(w => !w.IsCoordinator && (w.IsGoal || w.ParentTaskId == null)).ToArray();
        var registry = store.Projects();
        RefreshNavigationCounts(goals);
        var current = goals.FirstOrDefault(w => ids.Contains(w.Id));
        var session = current == null ? null : unified.SessionFor(current);
        IReadOnlyList<WorkItem> steps = current == null ? Array.Empty<WorkItem>() : unified.Steps(current);
        var coordinator = current == null ? null : all.FirstOrDefault(w => w.SessionId == current.SessionId && w.IsCoordinator && w.State == WorkState.Running);
        var worker = steps.FirstOrDefault(w => w.State == WorkState.Running);
        var elapsed = session?.StartedAt is { } started ? DateTimeOffset.UtcNow - started : (TimeSpan? )null;
        HeaderElapsed.Text = elapsed is { } t ? $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}" : "—";
        var cpu = "CPU manager: no sample yet";
        using (var process = Process.GetCurrentProcess())
        {
            var now = DateTimeOffset.UtcNow;
            var processor = process.TotalProcessorTime;
            if (previousProcessorAt != default)
            {
                var percent = 100 * (processor - previousProcessor).TotalMilliseconds / Math.Max(1, (now - previousProcessorAt).TotalMilliseconds) / Environment.ProcessorCount;
                cpu = $"CPU manager: {Math.Clamp(percent, 0, 100):F1}%";
            }

            previousProcessor = processor;
            previousProcessorAt = now;
        }

        var memory = ResourceMonitor.Snapshot()?.ToString() ?? "RAM: unknown";
        var hardware = cpu + "\n" + memory + "\n" + gpuStatus;
        if (Tabs.SelectedItem == DashboardTab)
        {
            var recent = registry.OrderByDescending(p => goals.Where(w => w.ProjectId == p.Id).Select(w => w.CreatedAt).DefaultIfEmpty(DateTimeOffset.MinValue).Max()).Take(5).Select(p =>
            {
                var list = goals.Where(w => w.ProjectId == p.Id).ToArray();
                var complete = list.Count(w => w.State == WorkState.Completed);
                return new ProjectOverview(p.Name, $"{complete}/{list.Length} tasks approved · {p.Technology}", list.Length == 0 ? 0 : 100.0 * complete / list.Length, p.Id);
            }).ToArray();
            var events = all.OrderByDescending(w => w.CreatedAt).Take(50).SelectMany(w => store.Logs(w.Id).Select(l => new { l.At, l.Event, Detail = w.Request[..Math.Min(100, w.Request.Length)] })).OrderByDescending(x => x.At).Take(7).Select(x => new ActivityOverview(x.At.ToLocalTime().ToString("dd/MM HH:mm"), x.Event, x.Detail)).ToArray();
            var providerRows = settings.Providers.Select(p =>
            {
                var h = health.FirstOrDefault(v => v.Id == p.Id);
                return new ProviderOverview(p.Id, !p.Enabled ? "Off" : !settings.UseAiCli && ManagerSettings.IsCli(p.Id) ? "External providers off" : h == null ? "Not checked" : !h.CanExecute ? "Unavailable" : "Ready to request (usage unverified)", $"Auth: {h?.Authentication ?? "Unknown"} · Usage/quota: {h?.Usage ?? "Unknown"}", p.Id == "ollama" ? p.Models.FirstOrDefault(m => !m.Blocked)?.Id ?? "" : "");
            }).ToArray();
            string Display(WorkItem? work) => work?.ProviderId == null ? "Not assigned" : work.ProviderId + " / " + work.ModelDisplay;
            var coordinatorLabel = coordinator != null ? Display(coordinator) : session?.Coordinator is { } route ? route.ProviderId + " / " + Orchestrator.DisplayModel(route.ModelId) : "C# orchestrator";
            var count = store.CallsOnDay(DateTime.Now.ToString("yyyy-MM-dd"));
            dashboard.Refresh(new(running, registry.Count, settings.Providers.Count(p => p.Enabled && health.Any(h => h.Id == p.Id && h.CanExecute)), store.Tools().Count(p => p.Enabled && p.Status.StartsWith("Backend reachable", StringComparison.Ordinal)), count, settings.MaxDailyCalls == 0 ? null : settings.MaxDailyCalls, hardware, current?.Id, current?.Request ?? "No goal running", registry.FirstOrDefault(p => p.Id == current?.ProjectId)?.Name ?? "Local workspace", coordinatorLabel, Display(worker), coordinator != null ? "Classifying/planning" : worker?.Request ?? "Not started", HeaderElapsed.Text, steps.Count == 0 ? 0 : 100.0 * steps.Count(w => w.State == WorkState.Completed) / steps.Count, recent, events, providerRows, store.UsageSince(DateTimeOffset.Now.Date).Select(u => new ProviderCallOverview(u.ProviderId, u.Runs)).ToArray()));
        }

        if (Tabs.SelectedItem == AgentsTab)
            AgentsGrid.ItemsSource = all.Where(w => w.State == WorkState.Running && w.SessionId != null).Select(w => new AgentRow(all.FirstOrDefault(g => g.IsGoal && g.SessionId == w.SessionId)?.Id ?? w.Id, w.IsCoordinator ? "Coordinator / classifier" : "Worker", w.ProviderId ?? "Unknown", w.ModelDisplay, w.State.ToString(), w.Request[..Math.Min(180, w.Request.Length)])).ToArray();
        if (Tabs.SelectedItem == ApprovalsTab)
        {
            ApprovalsGrid.ItemsSource = goals.Where(w => w.State is WorkState.NeedsReview or WorkState.AwaitingApproval or WorkState.WaitingForPermission or WorkState.WaitingForProvider or WorkState.WrongAccess or WorkState.WrongProject).Select(w => new ApprovalRow(w.Id, registry.FirstOrDefault(p => p.Id == w.ProjectId)?.Name ?? "?", w.Request, w.State.ToString(), w.ProblemReason, string.Join(" · ", unified.Decisions(w).Select(d => d.Kind)))).ToArray();
            ToolApprovalCount.Text = $"Tools: {store.ToolRuns().Count(r => r.State is ToolRunState.WaitingApproval or ToolRunState.NeedsReview or ToolRunState.Interrupted)} items need review.";
        }

        if (Tabs.SelectedItem == LocalTab)
        {
            LocalResults.ItemsSource = store.Evaluations().OrderByDescending(x => x.At).Take(30).ToArray();
            AdvancedLocalResults.ItemsSource = store.AdvancedEvaluations();
            ReportedUsageGrid.ItemsSource = store.UsageSince(DateTimeOffset.Now.Date);
            LocalResources.Text = hardware;
        }
    }

    private void RefreshLocalModelChoices()
    {
        var selected = LocalModelChoice.SelectedItem as string;
        LocalModelChoice.ItemsSource = settings.Providers.First(p => p.Id == "ollama").Models.Select(m => m.Id).Distinct().ToArray();
        LocalModelChoice.SelectedItem = selected ?? LocalModelChoice.Items.Cast<string>().FirstOrDefault();
    }

    private async void EvaluateLocalStandard(object sender, RoutedEventArgs e) => await RunLocalEvaluation(false);
    private async void EvaluateLocalAdvanced(object sender, RoutedEventArgs e) => await RunLocalEvaluation(true);
    private Task RunLocalEvaluation(bool advanced) => Guard(async () =>
    {
        IdleOnly();
        var model = LocalModelChoice.SelectedItem as string ?? throw new InvalidOperationException("Choose an installed Ollama model; refresh the list in Providers.");
        evaluationCancel = new(TimeSpan.FromMinutes(5));
        try
        {
            if (advanced)
                await new AdvancedLocalEvaluator(ollama, store).EvaluateAsync(model, evaluationCancel.Token);
            else
                await new LocalEvaluator(ollama, store).EvaluateAsync(model, evaluationCancel.Token);
            lastShellRefresh = DateTimeOffset.MinValue;
            RefreshShell(true);
        }
        finally
        {
            evaluationCancel.Dispose();
            evaluationCancel = null;
        }
    });
    private async void ReadGpuMetadata(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        IdleOnly();
        var exe = ProcessRunner.FindExecutable("nvidia-smi") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe");
        if (!File.Exists(exe))
        {
            gpuStatus = "VRAM: nvidia-smi not found; unknown";
            RefreshShell(true);
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var runner = new ProcessRunner();
        var help = await runner.RunAsync(exe, ["--help-query-gpu"], dataPath, null, 60000, null, timeout.Token);
        if (!help.Output.Contains("memory.total") || !help.Output.Contains("memory.used"))
            throw new InvalidOperationException("The driver does not report the required VRAM fields.");
        var result = await runner.RunAsync(exe, ["--query-gpu=memory.total,memory.used", "--format=csv,noheader,nounits"], dataPath, null, 4000, null, timeout.Token);
        gpuStatus = result.ExitCode == 0 ? "VRAM [total, used MB]: " + result.Output.Trim() : "VRAM: unknown";
        RefreshShell(true);
    });
    private void MinimizeShell(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeShell(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseShell(object sender, RoutedEventArgs e) => Close();
    private void DragShell(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left)
            return;
        if (e.ClickCount == 2)
            MaximizeShell(sender, new());
        else
            DragMove();
    }
}
