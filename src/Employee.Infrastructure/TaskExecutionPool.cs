using System.Text.Json;
using Employee.Core;

namespace Employee.Infrastructure;
// One bounded pipeline per goal. Internal dependent steps remain sequential within that pipeline.
public sealed class TaskExecutionPool
{
    private readonly object gate = new();
    private readonly IEmployeeStore store;
    private readonly IAutonomyStore autonomy;
    private readonly Orchestrator orchestrator;
    private readonly IProcessRunner runner;
    private readonly UnifiedTaskService reader;
    private readonly Dictionary<string, Entry> active = [];
    private readonly Dictionary<string, string[]> external = [];
    private readonly HashSet<string> ownedExternal = [];
    private sealed record Entry(UnifiedTaskService Service, string[] Roots, TaskCompletionSource Finished);
    public event Action? Changed;
    public TaskExecutionPool(IEmployeeStore store, IAutonomyStore autonomy, Orchestrator orchestrator, IProcessRunner runner)
    {
        this.store = store;
        this.autonomy = autonomy;
        this.orchestrator = orchestrator;
        this.runner = runner;
        reader = new(store, autonomy, orchestrator, runner);
        reader.Changed += () => Changed?.Invoke();
    }

    public bool IsBusy
    {
        get
        {
            lock (gate)
                return active.Count > 0;
        }
    }

    public IReadOnlyList<string> CurrentGoalIds
    {
        get
        {
            lock (gate)
                return active.Keys.ToArray();
        }
    }

    public SmartTestingService Testing => reader.Testing;

    public bool IsRunning(string goalId)
    {
        lock (gate)
            return active.ContainsKey(goalId);
    }

    private UnifiedTaskService Service(WorkItem goal)
    {
        lock (gate)
            return active.TryGetValue(GoalFor(goal).Id, out var entry) ? entry.Service : reader;
    }

    public SessionEngine EngineFor(WorkItem goal) => Service(goal).Engine;
    private string[] Roots(WorkItem goal)
    {
        var ids = SessionFor(goal)?.ProjectIds ?? [goal.ProjectId];
        return store.Projects().Where(p => ids.Contains(p.Id)).Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p.Root))).ToArray();
    }

    private static bool Overlap(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase) || a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private bool CanStartLocked(WorkItem goal, ManagerSettings settings, string[] roots) => active.Count + external.Count - ownedExternal.Count < ResourceMonitor.EffectiveSlots(settings) && !active.ContainsKey(goal.Id) && roots.Length > 0 && !active.Values.Any(e => e.Roots.Any(a => roots.Any(b => Overlap(a, b)))) && !external.Values.Any(e => e.Any(a => roots.Any(b => Overlap(a, b))));
    public IDisposable ReserveExternal(string[] paths, ManagerSettings settings, string? ownerGoalId = null)
    {
        var roots = paths.Where(Directory.Exists).Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var id = Guid.NewGuid().ToString("N");
        lock (gate)
        {
            if (ownerGoalId != null && !active.ContainsKey(ownerGoalId))
                throw new InvalidOperationException("Tool scope has no active owner task.");
            // A step of a running task already holds that task's slot: low memory never refuses it
            // (that dropped finished image jobs at import), only another project using the same folders.
            if (NoSlotLeft(active.Count, external.Count - ownedExternal.Count, ownerGoalId != null, ResourceMonitor.EffectiveSlots(settings)) || active.Where(e => e.Key != ownerGoalId).Any(e => e.Value.Roots.Any(a => roots.Any(b => Overlap(a, b)))) || external.Values.Any(e => e.Any(a => roots.Any(b => Overlap(a, b)))))
                throw new InvalidOperationException("Tool/workflow conflicts with a running project or no resource slot remains.");
            external.Add(id, roots);
            if (ownerGoalId != null)
                ownedExternal.Add(id);
        }

        return new ExternalLease(() =>
        {
            lock (gate)
            {
                external.Remove(id);
                ownedExternal.Remove(id);
            }
        });
    }

    internal static bool NoSlotLeft(int activeTasks, int unownedLeases, bool ownedByRunningTask, int slots) => !ownedByRunningTask && activeTasks + unownedLeases >= slots;

    private sealed class ExternalLease(Action release) : IDisposable
    {
        private Action? remaining = release;
        public void Dispose() => Interlocked.Exchange(ref remaining, null)?.Invoke();
    }

    public bool CanStart(WorkItem input, ManagerSettings settings)
    {
        var goal = GoalFor(input);
        if (TaskProblems.ProjectProblem(goal, store.Projects(), SessionFor(goal)) != null)
            return true;
        var roots = Roots(goal);
        lock (gate)
            return CanStartLocked(goal, settings, roots);
    }

    public async Task StartAsync(WorkItem input, ManagerSettings settings, IReadOnlyList<ProviderHealth> health, Route? selected = null, bool approved = false, CancellationToken token = default)
    {
        var goal = GoalFor(store.WorkItems().Single(w => w.Id == input.Id));
        if (goal.State == WorkState.Running || IsRunning(goal.Id))
            throw new InvalidOperationException("Task is already running. Stop and await it first.");
        if (TaskOutcomes.IsClosed(goal.State))
            throw new InvalidOperationException("Task is finished. Use an explicit fresh rerun.");
        var projectError = TaskProblems.ProjectProblem(goal, store.Projects(), SessionFor(goal));
        if (projectError != null)
        {
            TaskProblems.Mark(goal, WorkState.WrongProject, projectError);
            store.SaveWork(goal);
            store.Log(goal.Id, "WrongProject", projectError);
            Changed?.Invoke();
            return;
        }

        using var protection = store.ProtectTask(goal.Id);
        var roots = Roots(goal);
        var service = new UnifiedTaskService(store, autonomy, orchestrator, runner);
        var entry = new Entry(service, roots, new(TaskCreationOptions.RunContinuationsAsynchronously));
        lock (gate)
        {
            if (!CanStartLocked(goal, settings, roots))
            {
                token.ThrowIfCancellationRequested();
                QueueForRun(goal, selected, approved);
                return;
            }

            active.Add(goal.Id, entry);
            if (goal.QueuedAt != null)
            {
                goal.QueuedAt = null;
                store.SaveWork(goal);
            }
        }

        service.Changed += OnChanged;
        void OnChanged() => Changed?.Invoke();
        try
        {
            Changed?.Invoke();
            var snapshot = JsonSerializer.Deserialize<ManagerSettings>(JsonSerializer.Serialize(settings))!;
            await service.StartAsync(store.WorkItems().Single(w => w.Id == goal.Id), snapshot, health.ToArray(), selected, approved, token);
        }
        finally
        {
            service.Changed -= OnChanged;
            lock (gate)
                active.Remove(goal.Id);
            entry.Finished.TrySetResult();
            Changed?.Invoke();
        }
    }

    public Task WaitForAsync(string id)
    {
        lock (gate)
            return active.TryGetValue(id, out var entry) ? entry.Finished.Task : Task.CompletedTask;
    }

    public void PauseAllAfterCurrent()
    {
        Entry[] entries;
        lock (gate)
            entries = active.Values.ToArray();
        foreach (var entry in entries)
            entry.Service.Pause();
    }

    public async Task StopAsync(WorkItem goal)
    {
        Entry? entry;
        lock (gate)
            active.TryGetValue(GoalFor(goal).Id, out entry);
        if (entry != null)
        {
            entry.Service.Stop();
            await entry.Finished.Task;
        }
    }

    public async Task StopAllAsync()
    {
        Entry[] entries;
        lock (gate)
            entries = active.Values.ToArray();
        foreach (var entry in entries)
            entry.Service.Stop();
        await Task.WhenAll(entries.Select(e => e.Finished.Task));
    }

    public void RecoverSavedTasks() => reader.RecoverSavedTasks();
    public void WrapLegacySessions() => reader.WrapLegacySessions();
    public WorkItem GoalFor(WorkItem work) => reader.GoalFor(work);
    public WorkSession? SessionFor(WorkItem goal) => reader.SessionFor(goal);
    public IReadOnlyList<WorkItem> Steps(WorkItem goal) => reader.Steps(goal);
    public IReadOnlyList<PendingDecision> Decisions(WorkItem goal) => reader.Decisions(goal);
    public IReadOnlyList<PlanRecord> Plans(WorkItem goal) => reader.Plans(goal);
    public TaskReviewReport Report(WorkItem work) => reader.Report(work);
    public void Sync(string id) => reader.Sync(id);
    public void Enqueue(WorkItem work, Route? route = null) => reader.Enqueue(work, route);
    // Run/resume may wait for capacity even when the task already has a session.
    public void QueueForRun(WorkItem input, Route? route = null, bool approved = false)
    {
        lock (gate)
        {
            var goal = GoalFor(store.WorkItems().Single(w => w.Id == input.Id));
            if (active.ContainsKey(goal.Id) || goal.State == WorkState.Running)
                throw new InvalidOperationException("Task is already running. Stop and await it first.");
            if (TaskOutcomes.IsClosed(goal.State))
                throw new InvalidOperationException("Task is finished. Use an explicit fresh rerun.");
            goal.State = WorkState.Queued;
            goal.QueuedAt ??= DateTimeOffset.UtcNow;
            if (route != null)
            {
                goal.PreferredRoute = route;
                goal.RouteIsAdvisory = false;
            }

            if (approved)
            {
                goal.RoutingApproved = true;
                goal.PreferredRoute = route ?? (goal.ProviderId == null || Steps(goal).Count > 0 ? goal.PreferredRoute : new Route(goal.ProviderId, goal.ModelId ?? "", "User approved", AutoModel: string.IsNullOrWhiteSpace(goal.ModelId)));
            }

            goal.Notes = "Queued until an agent slot and independent project are available; progress preserved.";
            store.SaveWork(goal);
        }

        Changed?.Invoke();
    }

    public WorkItem Rerun(WorkItem input)
    {
        lock (gate)
        {
            var source = GoalFor(store.WorkItems().Single(w => w.Id == input.Id));
            if (active.ContainsKey(source.Id) || source.State == WorkState.Running || SessionFor(source)?.State is SessionState.Running or SessionState.Planning || autonomy.TestJobs().Any(j => j.State == TestJobState.Running && j.WorkIds.Any(id => id == source.Id || Steps(source).Any(w => w.Id == id))))
                throw new InvalidOperationException("Stop and await this task and its tests before rerunning.");
            if (source.State == WorkState.Queued && source.QueuedAt != null)
                throw new InvalidOperationException("Remove this task from the queue before rerunning.");
            if (store is SqliteStore permissionStore)
            {
                permissionStore.ApplyPendingTaskAccess(source.Id);
                source = store.WorkItems().Single(w => w.Id == source.Id);
            }

            var grant = store is SqliteStore scopeStore ? scopeStore.ApprovedTaskAccess(source) : source.GrantedAccess ?? source.Access;
            var automatic = source.AutomaticIntent ?? (source.AutoAssign || store.Logs(source.Id).Any(l => l.Event is "Assignment" or "Rerun"));
            var goal = new WorkItem
            {
                IsGoal = true,
                State = WorkState.Saved,
                ProjectId = source.ProjectId,
                Request = source.Request,
                Attachments = source.Attachments.Select(a => new WorkAttachment(a.Name, a.Content.ToArray())).ToList(),
                Comments = source.Comments.Where(c => !c.IsAssistant).Select(c => new WorkComment(c.Text, c.Attachments.Select(a => new WorkAttachment(a.Name, a.Content.ToArray())).ToList(), c.At)).ToList(),
                AutoAssign = source.AutoAssign,
                AutomaticIntent = automatic,
                GrantedAccess = grant,
                Kind = source.Kind,
                Access = grant,
                Priority = source.Priority,
                PlanningOverride = source.PlanningOverride,
                AwayOverride = source.AwayOverride,
                TestAfterBuild = source.TestAfterBuild,
                PreferredRoute = source.PreferredRoute,
                RouteIsAdvisory = source.RouteIsAdvisory,
                DelegationAllowed = source.DelegationAllowed,
                Reworks = source.Reworks,
                RejectedRoutes = [..source.RejectedRoutes]
            };
            // Rerunning a result that was never approved means it missed: escalate the fresh run. A task
            // closed as not meeting the requirements was already counted as a miss when it was closed.
            if (source.State is WorkState.NeedsReview or WorkState.NotMet)
            {
                var results = UnifiedTaskService.ResultSteps(source, Steps(source));
                if (results.Count > 0)
                {
                    if (source.State == WorkState.NeedsReview)
                        store.RecordRework(results.Select(w => w.Id));
                    goal.Reworks++;
                    goal.RejectedRoutes = [..goal.RejectedRoutes.Union(results.Select(w => w.ProviderId + "|" + (w.ModelId ?? ""))).TakeLast(10)];
                }
            }

            TaskAttachments.Validate(TaskAttachments.InputFiles(goal));
            if (source.ToolWorkflowId != null)
            {
                var original = ((IToolWorkflowStore)store).Workflows().Single(w => w.Id == source.ToolWorkflowId);
                var workflow = new ToolWorkflow
                {
                    Name = original.Name,
                    ProjectId = original.ProjectId,
                    AutoContinueLocal = original.AutoContinueLocal,
                    Steps = original.Steps.Select(s => new ToolWorkflowStep { Key = s.Key, ToolId = s.ToolId, Operation = s.Operation, Input = (System.Text.Json.Nodes.JsonObject)s.Input.DeepClone(), DependsOn = [..s.DependsOn] }).ToList()
                };
                ((IToolWorkflowStore)store).SaveWorkflow(workflow);
                goal.ToolWorkflowId = workflow.Id;
            }

            store.SaveWork(goal);
            store.Log(goal.Id, "Rerun", "Fresh run of task " + source.Id + "; previous execution history preserved.");
            Changed?.Invoke();
            return goal;
        }
    }

    public WorkItem? NextQueued(ManagerSettings settings)
    {
        lock (gate)
            if (active.Count + external.Count - ownedExternal.Count >= ResourceMonitor.EffectiveSlots(settings))
                return null;
        return store.WorkItems().Where(w => w.State == WorkState.Queued && w.QueuedAt != null && w.ParentTaskId == null && !w.IsCoordinator && (w.ScheduledAt == null || w.ScheduledAt <= DateTimeOffset.UtcNow)).OrderByDescending(w => w.Priority).ThenBy(w => w.QueuedAt).ThenBy(w => w.CreatedAt).FirstOrDefault(w => TaskProblems.ProjectProblem(w, store.Projects(), SessionFor(w)) != null || CanStart(w, settings));
    }

    public IReadOnlyList<string> DeleteTask(string id) => DeleteTasks([id]);
    public IReadOnlyList<string> DeleteTasks(IEnumerable<string> ids)
    {
        lock (gate)
        {
            var result = store.DeleteTasks(ids);
            Changed?.Invoke();
            return result;
        }
    }

    public IReadOnlyList<WorkItem> QueryTasks(TaskSortField field, TaskSortDirection direction) => store.QueryTasks(field, direction);
    public void SaveForLater(WorkItem work) => reader.SaveForLater(work);
    // Starts every queued task that may run now. Each start reserves its slot synchronously, so the
    // parallel limit in NextQueued holds; the caller only decides how a task runs and when to stop.
    public int DispatchQueued(ManagerSettings settings, Func<WorkItem, Task> start, Func<bool>? mayContinue = null)
    {
        var started = 0;
        while (started < 64 && (mayContinue?.Invoke() ?? true) && NextQueued(settings) is { } next)
        {
            SaveForLater(next);
            _ = start(next);
            started++;
        }

        return started;
    }
    public void SkipStep(WorkItem work, string stepId)
    {
        lock (gate)
        {
            if (active.ContainsKey(GoalFor(work).Id))
                throw new InvalidOperationException("Stop & save before skipping a step.");
            reader.SkipStep(work, stepId);
        }
    }

    public WorkItem AddComment(WorkItem work, string text, IEnumerable<WorkAttachment> files, bool continueUpdating = false) => Service(work).AddComment(work, text, files, continueUpdating);
    public WorkItem CreateToolTask(ToolTaskForm form, ManagerSettings settings) => reader.CreateToolTask(form, settings);
    public void Resolve(WorkItem work, string id, bool approve, ManagerSettings settings, IReadOnlyList<ProviderHealth> health, Route? route = null) => Service(work).Resolve(work, id, approve, settings, health, route);
    public void Switch(WorkItem work, Route route, ManagerSettings settings, IReadOnlyList<ProviderHealth> health) => Service(work).Switch(work, route, settings, health);
    public void SwitchStoppedStep(WorkItem goal, string stepId, Route route, ManagerSettings settings, IReadOnlyList<ProviderHealth> health)
    {
        lock (gate)
        {
            if (IsRunning(goal.Id) || !Steps(goal).Any(step => step.Id == stepId) || goal.SessionId == null)
                throw new InvalidOperationException("Stop & save the task and select one of its steps.");
            reader.Engine.SwitchAI(goal.SessionId, stepId, route, settings, health, selectedStep: true);
            reader.Sync(goal.Id);
        }
    }

    public void ChangeCoordinator(WorkItem work, Route route, ManagerSettings settings, IReadOnlyList<ProviderHealth> health) => Service(work).ChangeCoordinator(work, route, settings, health);
    public void ClearRoutingOverride(WorkItem work) => Service(work).ClearRoutingOverride(work);
    public void SaveCallLimit(WorkItem work, int maxCalls) => Service(work).SaveCallLimit(work, maxCalls);
    public void RenewTime(WorkItem work, int minutes) => Service(work).RenewTime(work, minutes);
    public bool CanComplete(WorkItem work) => !IsRunning(GoalFor(work).Id) && reader.CanComplete(work);
    public (int Steps, int Decisions) OpenWork(WorkItem work) => reader.OpenWork(work);
    public string Summary(WorkItem work, bool includeLastResult = false) => reader.Summary(work, includeLastResult);
    public WorkItem CompleteAnyway(WorkItem work)
    {
        lock (gate)
        {
            if (active.ContainsKey(GoalFor(work).Id))
                throw new InvalidOperationException("Stop the task before completing it.");
            var done = reader.CompleteAnyway(work);
            Changed?.Invoke();
            return done;
        }
    }
    public void Close(WorkItem work, WorkState outcome, string reason)
    {
        lock (gate)
        {
            if (active.ContainsKey(GoalFor(work).Id))
                throw new InvalidOperationException("Stop the task before closing it.");
            reader.Close(work, outcome, reason);
        }
        Changed?.Invoke();
    }
}
