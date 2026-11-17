using System.Text.Json;
using Employee.Core;

namespace Employee.Infrastructure;
public sealed class SessionEngine(IEmployeeStore store, IAutonomyStore autonomy, Orchestrator orchestrator, SmartTestingService testing)
{
    private int active;
    private volatile bool pauseRequested;
    private volatile bool stopRequested;
    private CancellationTokenSource? runCancellation;
    private (string WorkId, Route Route)? requestedSwitch;
    public string? CurrentWorkId { get; private set; }
    public bool IsActive => Volatile.Read(ref active) != 0;

    public event Action? Changed;
    private WorkSession Session(string id) => autonomy.Sessions().Single(s => s.Id == id);
    private List<WorkItem> Workers(string id) => store.WorkItems().Where(w => w.SessionId == id && !w.IsCoordinator && !w.IsGoal).ToList();
    private void Save(WorkSession session)
    {
        autonomy.SaveSession(session);
        Changed?.Invoke();
    }

    public void PauseAfterCurrent() => pauseRequested = true;
    public void Stop()
    {
        stopRequested = true;
        runCancellation?.Cancel();
    }

    public void SwitchAI(string sessionId, string workId, Route target, ManagerSettings settings, IReadOnlyList<ProviderHealth> health, bool selectedStep = false)
    {
        var s = Session(sessionId);
        var work = store.WorkItems().Single(w => w.Id == workId && w.SessionId == sessionId && !w.IsCoordinator);
        if (selectedStep && (IsActive || work.State is not (WorkState.Queued or WorkState.Paused or WorkState.Failed or WorkState.WaitingForProvider or WorkState.AwaitingApproval)))
            throw new InvalidOperationException("Select a stopped AI step that still needs to run.");
        if (work.Tool != null)
            throw new InvalidOperationException("This step uses a tool connector. Change its tool settings or create a new plan; Switch AI applies to AI steps.");
        if (settings.AttemptsUsedUp(work) || s.RemainingCalls <= (IsActive ? 1 : 0))
            throw new InvalidOperationException("No execution budget remains for this task.");
        if (ModelRouter.For(store).Select(work, settings, health, store.Evaluations(), explicitChoice: target) == null)
            throw new InvalidOperationException("Target AI is unavailable or not permitted.");
        if (IsActive)
        {
            if (CurrentWorkId != workId || requestedSwitch != null)
                throw new InvalidOperationException("Only running tasks can switch AI; wait for any previous switch to finish.");
            requestedSwitch = (workId, target);
            orchestrator.Stop(workId);
            return;
        }

        if (selectedStep)
        {
            work.StepRouteOverride = true;
            foreach (var decision in autonomy.Decisions(sessionId).Where(d => d.WorkId == workId && d.State == DecisionState.Pending && (d.Kind is "Approval" or "ProviderWait")))
            {
                decision.State = DecisionState.Approved;
                autonomy.SaveDecision(decision);
            }
        }

        work.PreferredRoute = target;
        work.RouteIsAdvisory = false;
        work.RoutingApproved = true;
        work.State = WorkState.Queued;
        work.Switches++;
        s.SwitchesUsed++;
        store.SaveWork(work);
        store.Log(work.Id, "UserSwitch", JsonSerializer.Serialize(target));
        s.State = SessionState.Paused;
        s.Notes = "New AI selected. Resume the session to continue from the checkpoint.";
        Save(s);
    }

    public void SetAway(string id, bool away)
    {
        var session = Session(id);
        session.Away = away;
        Save(session);
    }

    public void AdoptQueued(string id, string workId)
    {
        if (IsActive)
            throw new InvalidOperationException("Pause the session before adding a task.");
        var s = Session(id);
        var w = store.WorkItems().Single(w => w.Id == workId);
        PlanningService.ValidateSession(s);
        if (w.SessionId != null || w.State != WorkState.Queued || !s.ProjectIds.Contains(w.ProjectId) || w.Access > s.AccessCeiling || s.StepLimitReached(Workers(id)))
            throw new InvalidOperationException("Task must be queued, outside a session and within the selected scope and access.");
        w.SessionId = id;
        w.DelegationAllowed = s.AllowDelegation;
        store.SaveWork(w);
        Changed?.Invoke();
    }

    public void SetDependencies(string id, string workId, IReadOnlyList<string> dependencies)
    {
        if (IsActive)
            throw new InvalidOperationException("Pause the session before editing dependencies.");
        var tasks = Workers(id);
        var work = tasks.Single(w => w.Id == workId);
        if (work.State != WorkState.Queued)
            throw new InvalidOperationException("Only unstarted tasks can have their dependencies edited.");
        work.DependsOn = [..dependencies.Distinct()];
        TaskDependencies.Validate(tasks);
        store.SaveWork(work);
    }

    // Queues a failed tool step to run again with a new request. A completed tool result is reused,
    // never replayed. When the step's input preparation gave nothing usable, that step runs again too.
    private void RetryToolStep(WorkSession s, WorkItem failed, ManagerSettings settings, string reason)
    {
        var runs = store as IToolWorkflowStore;
        var run = failed.ToolRunId == null ? null : runs?.ToolRuns().FirstOrDefault(r => r.Id == failed.ToolRunId);
        if (run is { State: ToolRunState.Completed })
            return;
        var preparation = failed.Tool!.InputFrom == null ? null : Workers(s.Id).FirstOrDefault(w => w.Id == failed.Tool.InputFrom && w.Tool == null && w.ProjectId == failed.ProjectId);
        // Reading the same preparation answer again gives the same failure: prepare again when the
        // answer was not JSON or said it was blocked (seen live: "no Mystic PNG exists yet and I may not
        // make media", from a step whose only job was to describe the new image).
        var blocked = failed.Notes.Contains("Input preparation blocked", StringComparison.Ordinal);
        var rerunPreparation = preparation is { State: WorkState.Completed } && (blocked || failed.Notes.Contains("Input preparation did not return JSON", StringComparison.Ordinal))
            && !(settings.MaxAttempts > 0 && preparation.Attempts >= settings.MaxAttempts) && s.RemainingCalls > 0;
        if (rerunPreparation)
        {
            preparation!.State = WorkState.Queued;
            store.SaveWork(preparation);
            var said = blocked ? failed.Notes[failed.Notes.IndexOf("Input preparation blocked", StringComparison.Ordinal)..] : "";
            store.Log(preparation.Id, StepStatus.Continuing, (blocked ? "Your previous answer was: " + said[..Math.Min(400, said.Length)] + "\n" : "Your previous answer had no usable JSON. Answer with ONLY one JSON object, {\"toolInput\":{...}} or {\"toolInputs\":[{...},{...}]} with one entry per image or motion, and no prose around it.\n") +
                "You are not asked to make the media yourself: the manager calls the tool with the request you write. A result that does not exist yet is exactly what the tool will make, so it is not a blocker. Return {\"toolInput\":{...}} describing the new request (for a new image: action create, prompt, size). Return blocked only when an input the tool cannot do without is missing and cannot be chosen, such as a source file that must already exist.");
        }
        var previous = failed.Notes;
        // The retry runs under the skill guidance as it is now (it may have been updated since planning).
        if (failed.SkillId != null && orchestrator.Skills?.Hash(failed.SkillId) is { } current && current != failed.SkillHash)
        {
            failed.SkillHash = current;
            store.Log(failed.Id, "SkillRebound", "Retry uses the current version of skill " + failed.SkillId + ".");
        }
        failed.ToolRunId = null;
        failed.ToolBindingHash = null;
        failed.State = WorkState.Queued;
        failed.Notes = "Retrying with a new tool request (" + reason + ").";
        store.SaveWork(failed);
        foreach (var decision in autonomy.Decisions(s.Id).Where(d => d.WorkId == failed.Id && d.State == DecisionState.Pending && d.Kind is "ToolError" or "ToolApproval" or "ToolReview"))
        {
            decision.State = DecisionState.Rejected;
            autonomy.SaveDecision(decision);
        }
        store.Log(failed.Id, "RetryToolStep", $"Retrying on {reason}{(rerunPreparation ? ", preparing the input again" : "")}. Previous failure: {previous[..Math.Min(400, previous.Length)]}");
    }

    private PendingDecision Defer(string sessionId, string workId, string kind, string message, DelegationRequest? delegation = null)
    {
        var existing = autonomy.Decisions(sessionId).FirstOrDefault(d => d.WorkId == workId && d.Kind == kind && d.State == DecisionState.Pending);
        if (existing != null)
            return existing;
        var decision = new PendingDecision
        {
            SessionId = sessionId,
            WorkId = workId,
            Kind = kind,
            Message = message,
            Delegation = delegation
        };
        autonomy.SaveDecision(decision);
        Changed?.Invoke();
        return decision;
    }

    public void ResolveDecision(string sessionId, string decisionId, bool approve, ManagerSettings settings, IReadOnlyList<ProviderHealth> health, Route? routeOverride = null)
    {
        if (IsActive)
            throw new InvalidOperationException("Pause the session before approving a decision.");
        var s = Session(sessionId);
        var decision = autonomy.Decisions(sessionId).Single(d => d.Id == decisionId);
        if (decision.State != DecisionState.Pending)
            throw new InvalidOperationException("Decision has already been processed.");
        if (decision.Kind == "MilestoneSignOff")
        {
            if (!(approve && SignOff(sessionId)))
                ReopenMilestone(sessionId, approve ? "nothing was waiting for sign-off." : "rejected by the user. Comment on the task with what to change, then run it.");
            Changed?.Invoke();
            return;
        }
        var work = store.WorkItems().Single(w => w.Id == decision.WorkId);
        if (approve && (work.State is WorkState.WrongAccess or WorkState.WrongProject or WorkState.WaitingForPermission))
            throw new InvalidOperationException("Correct the project/access in task details, then Run/resume. Approving does not repair configuration.");
        if (approve)
        {
            if (decision.Kind is "ToolApproval" or "ToolReview")
            {
                (orchestrator.ToolTasks ?? throw new InvalidOperationException("V5 executor is unavailable.")).Approve(work, decision.Kind == "ToolReview");
            }
            else if (decision.Kind == "ToolError")
            {
                if (work.State != WorkState.Failed || work.Tool == null)
                    throw new InvalidOperationException("Inspect the failed/interrupted tool request first.");
                RetryToolStep(s, work, settings, "approved retry");
            }
            else if (decision.Kind == "Delegation")
                AcceptDelegation(s, work, decision.Delegation ?? throw new InvalidOperationException("Missing AI switch proposal."), settings, health, manualApproval: true);
            else if (decision.Kind == "Review")
            {
                if (work.State != WorkState.NeedsReview)
                    throw new InvalidOperationException("Task is not awaiting review.");
                work.State = WorkState.Completed;
                work.Notes = "User approved the result.";
                store.SaveWork(work);
                store.Log(work.Id, "UserReview", work.Notes);
                store.RecordApproval([work.Id]);
            }
            else if (decision.Kind is "Approval" or "ProviderWait")
            {
                if (settings.AttemptsUsedUp(work))
                    throw new InvalidOperationException($"This step already used {work.WorkAttempts}/{settings.AttemptsDisplay} attempts. Approve does not extend the limit. Increase Attempts per step in Settings or explicitly skip an obsolete stopped step, then retry.");
                if (s.RemainingCalls == 0)
                    throw new InvalidOperationException($"Session call budget reached ({s.CallsUsed}/{s.MaxCalls}); approving a retry does not extend it.");
                var config = JsonSerializer.Deserialize<ManagerSettings>(JsonSerializer.Serialize(settings))!;
                config.Mode = s.Mode;
                var router = ModelRouter.For(store);
                // Older tasks persisted only ProviderId and an empty ModelId. Treat that
                // shape as automatic model selection; otherwise it pins the task to the
                // CLI default even when the provider has discovered exact model IDs.
                var preferred = routeOverride ?? (work.ProviderId == null ? work.PreferredRoute : new Route(work.ProviderId, work.ModelId ?? "", "User approved", AutoModel: string.IsNullOrWhiteSpace(work.ModelId)));
                if (routeOverride != null)
                    work.RouteIsAdvisory = false;
                var route = router.Choose(work, config, health, store.Evaluations(), null, preferred);
                if (route == null)
                    throw new InvalidOperationException(router.ExplainUnavailable(work, config, health, store.Evaluations(), preferred));
                work.RouteIsAdvisory = routeOverride == null && s.Mode != RoutingMode.Manual && (work.RouteIsAdvisory || work.PreferredRoute == null);
                work.PreferredRoute = route;
                work.RoutingApproved = true;
                work.State = WorkState.Queued;
                store.SaveWork(work);
            }
        }

        decision.State = approve ? DecisionState.Approved : DecisionState.Rejected;
        autonomy.SaveDecision(decision);
        Changed?.Invoke();
    }

    private void AcceptDelegation(WorkSession session, WorkItem parent, DelegationRequest request, ManagerSettings settings, IReadOnlyList<ProviderHealth> health, bool manualApproval = false)
    {
        if (!session.AllowDelegation || !parent.DelegationAllowed || parent.State != WorkState.NeedsReview || parent.DelegationDepth >= session.MaxDelegationDepth || session.StepLimitReached(Workers(session.Id)))
            throw new InvalidOperationException("Proposal exceeds delegation or task limits.");
        if (string.IsNullOrWhiteSpace(request.ProviderId) || request.ModelId == null || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 4000 || request.Request?.Length > 12000)
            throw new InvalidOperationException("Delegation proposal has missing data.");
        var route = new Route(request.ProviderId, request.ModelId, request.Reason);
        var key = AgentProtocol.RouteKey(route);
        if (parent.RouteHistory.Contains(key))
            throw new InvalidOperationException("An AI routing cycle was detected in the task branch.");
        if (ModelRouter.For(store).Select(parent, settings, health, store.Evaluations(), explicitChoice: route) == null)
            throw new InvalidOperationException("Target AI is unavailable, lacks permission or has an unsuitable model.");
        if (!manualApproval)
        {
            var history = store.ModelPerformance();
            var target = history.FirstOrDefault(h => h.ProviderId == route.ProviderId && h.ModelId == route.ModelId && h.Capability == parent.Capability);
            var current = history.FirstOrDefault(h => h.ProviderId == parent.ProviderId && h.ModelId == parent.ModelId && h.Capability == parent.Capability);
            if (target is not { Samples: >= 3 } || current is not { Samples: >= 3 } || target.Adjustment - current.Adjustment < 5)
                throw new InvalidOperationException("Delegation needs your approval: insufficient measured benefit to justify an extra AI handoff. An AI's recommendation alone is not evidence.");
        }

        var child = new WorkItem
        {
            ProjectId = parent.ProjectId,
            SessionId = session.Id,
            ParentTaskId = parent.Id,
            Request = string.IsNullOrWhiteSpace(request.Request) ? parent.Request : request.Request,
            Kind = parent.Kind,
            Access = parent.Access,
            PreferredRoute = route,
            RoutingApproved = true,
            DelegationAllowed = true,
            DelegationDepth = parent.DelegationDepth + 1,
            RouteHistory = [..parent.RouteHistory],
            DependsOn = [..parent.DependsOn],
            Priority = parent.Priority
        };
        var checkpoint = store.LatestHandoff(parent.Id);
        if (checkpoint != null)
            store.SaveHandoff(checkpoint with { WorkId = child.Id, Reason = "Delegation approved by the manager: " + request.Reason });
        store.SaveWork(child);
        parent.State = WorkState.Delegated;
        parent.Notes = "Remaining work delegated to " + route.ProviderId + " / " + Orchestrator.DisplayModel(route.ModelId);
        store.SaveWork(parent);
        store.Log(parent.Id, "Delegation", child.Id + "; " + request.Reason);
        session.SwitchesUsed++;
        Save(session);
    }

    private void Reconcile(string sessionId)
    {
        var tasks = Workers(sessionId);
        foreach (var parent in tasks.Where(w => w.State == WorkState.Delegated).OrderByDescending(w => w.DelegationDepth))
        {
            var children = tasks.Where(w => w.ParentTaskId == parent.Id).ToArray();
            if (children.Length > 0 && children.All(w => w.State is WorkState.Completed or WorkState.Skipped))
            {
                parent.State = children.Any(w => w.State == WorkState.Skipped) ? WorkState.Skipped : WorkState.Completed;
                parent.Notes = Session(sessionId).AutoContinueSteps ? "Delegated steps have finished; review the parent task result." : "User approved the delegated tasks.";
                store.SaveWork(parent);
            }
        }

        foreach (var d in autonomy.Decisions(sessionId).Where(d => d.State == DecisionState.Pending && tasks.Any(w => w.Id == d.WorkId && w.State is WorkState.Completed or WorkState.Skipped)))
        {
            d.State = d.Kind == "Delegation" || tasks.Any(w => w.Id == d.WorkId && w.State == WorkState.Skipped) ? DecisionState.Rejected : DecisionState.Approved;
            autonomy.SaveDecision(d);
        }
    }

    private async Task CollectTests(WorkItem work, CancellationToken token)
    {
        if (work.Access != AccessLevel.WriteProject || !Orchestrator.EditsDirectly(work.ProviderId))
            return;
        var checkpoint = store.LatestHandoff(work.Id);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (checkpoint != null && !checkpoint.GitStatus.Contains("fatal:", StringComparison.OrdinalIgnoreCase))
            foreach (var line in checkpoint.GitStatus.Split('\n'))
                if (System.Text.RegularExpressions.Regex.IsMatch(line, @"^[ MADRCU?!]{2} "))
                    paths.Add(line[3..].Trim().Trim('"'));
        var execution = store.Logs(work.Id).LastOrDefault(l => l.Event == "Execution");
        foreach (var line in (execution?.Message ?? "").Split('\n'))
        {
            try
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                if (root.TryGetProperty("type", out var type) && type.GetString() == "item.completed" && root.TryGetProperty("item", out var item) && item.TryGetProperty("type", out var kind) && kind.GetString() == "file_change" && item.TryGetProperty("changes", out var changes) && changes.ValueKind == JsonValueKind.Array)
                    foreach (var change in changes.EnumerateArray())
                        if (change.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String)
                            paths.Add(path.GetString()!);
            }
            catch (JsonException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        var jobs = testing.Enqueue(work, paths.ToArray(), (checkpoint?.GitStatus ?? "") + (checkpoint?.GitDiff ?? ""));
        foreach (var job in jobs.Where(j => j.Impact == ChangeImpact.Critical && j.State == TestJobState.Pending))
            await testing.RunAsync(job, token);
        foreach (var job in jobs.Where(j => j.State is TestJobState.Failed or TestJobState.Interrupted))
            Defer(work.SessionId!, work.Id, "TestReview", "Tests did not pass: " + job.Id + ". Review test results before confirming the task.");
    }

    private async Task BatchTests(WorkSession session, IReadOnlyList<WorkItem> tasks, CancellationToken token)
    {
        if (!session.RunTestsAtBatchEnd)
            return;
        foreach (var job in autonomy.TestJobs().Where(j => j.State == TestJobState.Pending && j.WorkIds.Any(id => tasks.Any(w => w.Id == id && w.State != WorkState.Skipped))))
        {
            await testing.RunAsync(job, token);
            if (job.State is TestJobState.Failed or TestJobState.Interrupted)
                foreach (var workId in job.WorkIds.Where(id => tasks.Any(w => w.Id == id && w.State != WorkState.Skipped)))
                    Defer(session.Id, workId, "TestReview", "Test suite did not pass: " + job.Id + ". Review test results.");
        }
    }

    // True when a fix step was added. Runs only for code that finished since the last check, in a
    // single-project task that continues on its own.
    private async Task<bool> AutoCheckAsync(WorkSession s, IReadOnlyList<WorkItem> tasks, CancellationToken token)
    {
        if (!s.AutoContinueSteps || !s.RunTestsAtBatchEnd || s.RootWorkId == null || s.ProjectIds.Count != 1)
            return false;
        var changed = tasks.Any(w => w.Kind == WorkKind.Coding && w.Access == AccessLevel.WriteProject && w.VerifyScope == null && w.State == WorkState.Completed && (s.LastCheckAt == null || (w.UpdatedAt ?? w.CreatedAt) > s.LastCheckAt));
        var project = store.Projects().FirstOrDefault(p => p.Id == s.ProjectIds[0]);
        if (!changed || project == null)
            return false;
        s.Notes = "Checking that the project builds and its tests pass…";
        Save(s);
        var check = await testing.AutoCheckAsync(project, token);
        var rootId = s.RootWorkId;
        s = Session(s.Id);
        s.LastCheckAt = DateTimeOffset.UtcNow;
        if (!check.Ran)
        {
            store.Log(rootId, "AutoCheckSkipped", check.Output);
            Save(s);
            return false;
        }
        var digest = AutoCheck.Digest(check.Output);
        store.Log(rootId, check.Passed ? "AutoCheckPassed" : "AutoCheckFailed", check.Command + (check.Passed ? ": passed." : ":\n" + digest));
        if (check.Passed)
        {
            s.LastCheckFailure = null;
            Save(s);
            return false;
        }
        if (s.LastCheckFailure != null && StepStatus.SameGap(s.LastCheckFailure, digest))
        {
            s.Notes = $"{check.Command} still fails with the same errors after a fix step; stopping for your review.";
            store.Log(rootId, "AutoCheckStalled", digest);
            Save(s);
            return false;
        }
        if (s.AccessCeiling != AccessLevel.WriteProject)
        {
            s.Notes = $"{check.Command} fails, but this task cannot edit the project to fix it.";
            Save(s);
            return false;
        }
        s.LastCheckFailure = digest;
        s.CheckFixes++;
        Save(s);
        store.SaveWork(new WorkItem
        {
            ProjectId = project.Id,
            SessionId = s.Id,
            ParentTaskId = s.RootWorkId,
            PlanKey = "fix" + s.CheckFixes,
            Kind = WorkKind.Coding,
            Access = AccessLevel.WriteProject,
            CapabilityHint = "Debugging",
            Difficulty = 65,
            Request = $"The project's automatic check \"{check.Command}\" fails after the latest steps. Fix the code so it passes, without removing features or disabling checks or tests. Errors:\n{digest}"
        });
        return true;
    }

    // The acceptance loop for the scope being worked on (the current milestone, then the whole goal).
    // True when a step was added: a tester step that runs the project against the criteria, or a fix
    // step for the criteria it failed. A scope that passes is accepted; one that still fails after
    // its last round stops the run for the user with the failures spelled out.
    // Set by the task service: plans the steps that fix failed criteria when the result involves
    // connected tools (an AI fix step cannot call a tool again; the coordinator can plan one).
    public Func<WorkSession, string, CancellationToken, Task<bool>>? AcceptanceReplan { get; set; }

    internal static int TesterDifficulty(bool judgesArt) => judgesArt ? 80 : 75;
    // The tester's job is judging a result against criteria (review), and with art, reading images.
    // Rated as "Testing" the cheapest model always qualified, however hard the judgement.
    internal static string TesterCapability(bool judgesArt) => judgesArt ? nameof(WorkCapability.ImageUnderstanding) : nameof(WorkCapability.Review);

    // After the coordinator says the goal is done, every milestone not accepted yet is tested in
    // order and then the whole goal: when one scope passes, the next one starts at once.
    private async Task<bool> CheckAcceptance(WorkSession s, IReadOnlyList<WorkItem> tasks, CancellationToken token)
    {
        for (var scopes = 0; scopes <= s.Phases.Count + 1; scopes++)
        {
            var before = AcceptanceCheck.ScopeKey(s);
            if (await CheckAcceptanceOnce(s, tasks, token))
                return true;
            // A passed milestone waits for the user's sign-off before the next one is tested.
            if (!s.GoalVerified || s.AcceptanceStalled != null || AcceptanceCheck.ScopeKey(s) == before || s.AcceptedScopes.Contains(AcceptanceCheck.ScopeKey(s)) || SignOffDue(s) != null)
                return false;
        }
        return false;
    }

    private async Task<bool> CheckAcceptanceOnce(WorkSession s, IReadOnlyList<WorkItem> tasks, CancellationToken token)
    {
        if (!s.VerifyAcceptance || !s.AutoContinueSteps || s.RootWorkId == null || s.ProjectIds.Count != 1 || s.AccessCeiling != AccessLevel.WriteProject || s.AcceptanceStalled != null)
            return false;
        // Only large work: a plan with milestones, or building something new. Ordinary tasks run as before.
        if (s.Phases.Count == 0 && !TaskIntent.IsNewBuild(store.WorkItems().FirstOrDefault(w => w.Id == s.RootWorkId)?.Request ?? s.Goal))
            return false;
        var scope = AcceptanceCheck.ScopeKey(s);
        var criteria = AcceptanceCheck.Criteria(s, scope);
        if (criteria.Count == 0 || s.AcceptedScopes.Contains(scope))
            return false;
        // Art made by this task is judged by looking at it next to the project's own art.
        var art = ArtReview.Made(store, s);
        if (art.Count > 0)
            criteria = [..criteria, ArtReview.Criterion];
        if (s.VerifyScope != scope)
        {
            s.VerifyScope = scope;
            s.VerifyRounds = 0;
            s.LastVerifyFailures = null;
            s.PendingVerifyId = null;
        }

        var rootId = s.RootWorkId;
        var name = AcceptanceCheck.ScopeName(s, scope);
        WorkItem Step(string key, string request, string capability, int difficulty, string? verifyScope = null)
        {
            var step = new WorkItem
            {
                ProjectId = s.ProjectIds[0],
                SessionId = s.Id,
                ParentTaskId = rootId,
                PlanKey = key,
                Kind = WorkKind.Coding,
                Access = AccessLevel.WriteProject,
                CapabilityHint = capability,
                Difficulty = difficulty,
                Priority = 90,
                VerifyScope = verifyScope,
                Milestone = verifyScope != null && verifyScope.StartsWith("phase:", StringComparison.Ordinal) && int.TryParse(verifyScope[6..], out var tested) ? tested : MilestoneView.Current(s),
                Request = request
            };
            store.SaveWork(step);
            return step;
        }
        async Task Verify()
        {
            s.VerifyRounds++;
            s.Verifications++;
            // A web project is opened in a real (headless) browser by the manager first: the tester's
            // sandbox cannot start one, and layouts that only break in a browser must be seen.
            var root = store.Projects().FirstOrDefault(p => p.Id == s.ProjectIds[0])?.Root;
            IReadOnlyList<PageSnapshot.Shot> shots = [];
            if (root != null && Directory.Exists(root))
                try
                {
                    shots = await PageSnapshot.TakeAsync(root, "verify" + s.Verifications, Path.Combine(Path.GetTempPath(), "EmployeeManager", "browser"), token);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    store.Log(rootId, "PageSnapshotFailed", error.Message);
                }
            if (art.Count > 0 && root != null && Directory.Exists(root))
                try
                {
                    var scratch = Path.Combine(Path.GetTempPath(), "EmployeeManager", "browser");
                    var since = store.WorkItems().FirstOrDefault(w => w.Id == rootId)?.CreatedAt ?? DateTimeOffset.UtcNow;
                    var reference = ArtReview.Reference(store, store as IToolWorkflowStore, s, root, since, Path.Combine(scratch, "art-sources-" + s.Verifications));
                    if (await PageSnapshot.ArtSheetAsync(reference, art, Path.Combine(root, ".aiemployee", "screenshots", "art-review-" + s.Verifications + ".png"), scratch, token) is { } sheet)
                        shots = [..shots, new("ART REVIEW: the project's existing art (top) and the art made in this task (below), scaled with hard pixels", sheet)];
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    store.Log(rootId, "PageSnapshotFailed", "Art review image: " + error.Message);
                }
            s.LastScreenshots = [..shots.Select(shot => $"{shot.Name}: {Path.GetRelativePath(root!, shot.Path)}")];
            var request = AcceptanceCheck.VerifyRequest(s, scope, criteria, s.VerifyRounds, s.LastVerifyFailures);
            var pageShots = shots.Where(shot => !shot.Name.StartsWith("ART REVIEW", StringComparison.Ordinal)).ToArray();
            if (pageShots.Length > 0)
                request += "\nSCREENSHOTS taken just now by the manager in a real browser (headless Edge) of the page served over http, before any input: " + string.Join("; ", pageShots.Select(shot => $"{shot.Name}: {Path.GetRelativePath(root!, shot.Path)}")) + ". Open each image and look at it (layout, the play area's size and visibility, text, overlapping or collapsed parts, empty or broken areas). They are real browser evidence: judge visual and \"displays\" criteria from them, a visible defect is a blocking failure, and do not answer unverifiable for what the screenshots show.";
            if (shots.FirstOrDefault(shot => shot.Name.StartsWith("ART REVIEW", StringComparison.Ordinal)) is { } sheetShot)
                request += "\nART REVIEW image made just now by the manager: " + Path.GetRelativePath(root!, sheetShot.Path) + ". It shows the project's existing art (top) and the art made in this task (below), scaled with hard pixels. It is how the art criterion is judged: open it with your image viewer and compare the new art with the existing art as a player would see it (proportions, outline, palette, noise, whether the frames of one animation match each other, whether the motions differ, whether the source image given to the tool was the project's own art). Art that is clearly worse than or off-style from the existing art is a blocking failure; say exactly what is wrong in the evidence.";
            else if (art.Count > 0)
                request += "\nNo ART REVIEW image could be made (no browser). Open the new PNGs and the project's existing art yourself to judge the art criterion.";
            // The tester decides whether a milestone is done and now also judges art by eye: a strong
            // model. Seen live: every tester round ran on the cheapest model at difficulty 60.
            var testerStep = Step("verify" + s.Verifications, request, TesterCapability(art.Count > 0), TesterDifficulty(art.Count > 0), scope);
            // The tester sees what this task's tool steps returned (paths, sizes, ids), to open and check.
            testerStep.DependsOn = [..tasks.Where(w => w.Tool != null && w.State is WorkState.Completed or WorkState.Skipped).OrderBy(w => w.CreatedAt).TakeLast(12).Select(w => w.Id)];
            store.SaveWork(testerStep);
            s.PendingVerifyId = testerStep.Id;
            s.Notes = $"Testing {name} against its acceptance criteria (round {s.VerifyRounds}/{s.MaxVerifyRounds})…";
            store.Log(rootId, "AcceptanceVerify", $"{name}, round {s.VerifyRounds}/{s.MaxVerifyRounds}:\n" + string.Join("\n", criteria.Select((c, i) => $"{i + 1}. {c}")));
            Save(s);
        }
        void Stall(string failures)
        {
            s.AcceptanceStalled = $"{name} still fails after {s.VerifyRounds} verification round(s): {failures}";
            store.Log(rootId, "AcceptanceStalled", s.AcceptanceStalled);
            Save(s);
        }

        if (s.PendingVerifyId is { } pending && tasks.FirstOrDefault(w => w.Id == pending) is { } tester)
        {
            s.PendingVerifyId = null;
            if (tester.State == WorkState.Skipped)
            {
                s.AcceptedScopes.Add(scope);
                s.KnownIssues.Add($"{name}: verification was skipped by the user, not passed.");
                store.Log(rootId, "AcceptanceSkipped", name);
                Save(s);
                return false;
            }
            var log = store.Logs(tester.Id).LastOrDefault(l => l.Event == "Execution");
            var output = log == null ? "" : TaskReviewReport.ReadableOutput(log.Message[(log.Message.IndexOf('\n') + 1)..]);
            var verdict = AcceptanceCheck.Parse(output, criteria.Count);
            if (verdict == null)
            {
                // No readable verdict is not a pass: ask a tester again while rounds remain.
                store.Log(rootId, "AcceptanceUnreadable", $"The tester for {name} returned no {AcceptanceCheck.Marker} line.");
                if (s.VerifyRounds >= s.MaxVerifyRounds || s.RemainingCalls <= 1)
                {
                    Stall("the tester did not return a verdict; check its report in Comments.");
                    return false;
                }
                await Verify();
                return true;
            }

            foreach (var issue in verdict.MinorIssues)
                s.KnownIssues.Add($"{name}: {(issue.Unverifiable ? "NEEDS A PERSON TO CHECK (cannot be tested on this machine): " : "")}{(issue.Id <= criteria.Count ? criteria[issue.Id - 1] : "")} — {issue.Evidence}");
            if (s.KnownIssues.Count > 30)
                s.KnownIssues = [..s.KnownIssues.TakeLast(30)];
            var failed = verdict.BlockingFailures;
            store.Log(rootId, failed.Count == 0 ? "AcceptancePassed" : "AcceptanceFailed",
                $"{name}, round {s.VerifyRounds}: {verdict.Criteria.Count(c => c.Pass)}/{criteria.Count} criteria passed. {verdict.Summary}\n" +
                string.Join("\n", verdict.Criteria.Select(c => $"{(c.Pass ? "PASS" : c.Blocking ? "FAIL" : "minor")} [{c.Id}] {criteria[c.Id - 1]} — {c.Evidence}")));
            if (failed.Count == 0)
            {
                s.AcceptedScopes.Add(scope);
                s.LastVerifyFailures = null;
                // If later work reopens this scope, its verification starts again at round 1.
                s.VerifyScope = null;
                s.VerifyRounds = 0;
                foreach (var handed in verdict.Criteria.Where(c => c.Unverifiable && c.Id <= criteria.Count).Select(c => criteria[c.Id - 1]))
                    if (!s.HumanChecks.Contains(handed))
                        s.HumanChecks.Add(handed);
                s.Notes = $"{name} passed its acceptance criteria.";
                Save(s);
                return false;
            }

            var failures = AcceptanceCheck.Describe(criteria, failed);
            if (s.VerifyRounds >= s.MaxVerifyRounds || s.RemainingCalls <= 2)
            {
                Stall(failures);
                return false;
            }
            s.LastVerifyFailures = failures;
            if (AcceptanceReplan != null && s.Mode == RoutingMode.Automatic && s.RemainingCalls > 3 && tasks.Any(w => w.Tool != null && w.State == WorkState.Completed))
            {
                s.Notes = $"{name}: {failed.Count} acceptance criteria failed; planning the fix (tool steps included).";
                Save(s);
                if (await AcceptanceReplan(s, $"{name}, verification round {s.VerifyRounds}: " + failures + (s.LastScreenshots.Count > 0 ? " Browser screenshots the tester saw: " + string.Join("; ", s.LastScreenshots) : ""), token))
                    return true;
            }
            // A second failed round means the first fix was not enough: route the next fix to a stronger model.
            var fix = Step("accfix" + s.Verifications, AcceptanceCheck.FixRequest(s, scope, criteria, verdict, s.VerifyRounds) + (s.LastScreenshots.Count > 0 ? "\nBrowser screenshots the tester saw (open them): " + string.Join("; ", s.LastScreenshots) + ". The manager takes new ones after your fix." : ""), "Debugging", s.VerifyRounds > 1 ? 80 : 65);
            if (s.VerifyRounds > 1 && tasks.LastOrDefault(w => w.PlanKey?.StartsWith("accfix", StringComparison.Ordinal) == true && w.ProviderId != null) is { } earlier)
            {
                fix.Reworks = earlier.Reworks + 1;
                fix.RejectedRoutes = [..earlier.RejectedRoutes, earlier.ProviderId + "|" + (earlier.ModelId ?? "")];
                store.SaveWork(fix);
            }
            s.Notes = $"{name}: {failed.Count} acceptance criteria failed; fixing, then testing again.";
            Save(s);
            return true;
        }

        if (s.RemainingCalls <= 1)
            return false;
        await Verify();
        return true;
    }

    // A tool the planner chose by itself (the user did not name it) that keeps failing must not stop
    // the whole goal: it is retried once with a fresh request, then skipped so the steps that need
    // its output make a simple stand-in and the gap is reported.
    internal static bool TransientToolFailure(string notes)
    {
        var text = notes.ToLowerInvariant();
        if (new[] { "usage limit", "hit your", "rate limit", "quota", "not signed in", "login", "exceeds the current session grant", "call limit" }.Any(text.Contains))
            return false;
        return new[] { "eperm", "eacces", "ebusy", "econnreset", "socket hang up", "permission error", "lost connection while waiting", "server có thể đã khởi động lại", "không tìm thấy tác vụ", "has stopped (exit code", "temporarily unavailable", "backend exited", "no resource slot remains", "already has an active request",
            // Refused by an earlier manager version that read full project paths as wrong; the same input now works.
            "use a relative project png path" }.Any(text.Contains);
    }

    private bool RecoverPlannerTool(WorkSession s, WorkItem failed, ManagerSettings settings)
    {
        // The connection dropped while the image job kept running in the tool (seen live: the job
        // completed a minute later). Collect it: the run keeps its job id and is not sent again.
        if (failed.Tool != null && failed.State == WorkState.Paused && failed.ToolRunId != null && s.AutoContinueSteps && TransientToolFailure(failed.Notes)
            && store.Logs(failed.Id).Count(l => l.Event == "ToolResumeCollect") < 2)
        {
            store.Log(failed.Id, "ToolResumeCollect", "The connection to the tool dropped while its job was running; collecting the same job without sending it again.");
            failed.State = WorkState.Queued;
            failed.Notes = "Collecting the running tool job after a dropped connection; it is not generated again.";
            store.SaveWork(failed);
            foreach (var decision in autonomy.Decisions(s.Id).Where(d => d.WorkId == failed.Id && d.State == DecisionState.Pending && d.Kind == "ToolError"))
            {
                decision.State = DecisionState.Rejected;
                autonomy.SaveDecision(decision);
            }
            return true;
        }
        if (failed.Tool == null || failed.State != WorkState.Failed || !s.AutoContinueSteps || s.RootWorkId == null)
            return false;
        // The preparation found nothing for the tool to do (seen live: no saved ZapBop project to inspect).
        // Retrying asks the same question; a stand-in would invent what the plan said not to invent.
        if (failed.Notes.Contains(ToolTaskExecutor.NothingToRun, StringComparison.Ordinal))
        {
            var why = failed.Notes[failed.Notes.IndexOf(ToolTaskExecutor.NothingToRun, StringComparison.Ordinal)..];
            failed.State = WorkState.Skipped;
            failed.Notes = "NOTHING TO RUN: " + why[..Math.Min(400, why.Length)] + " The tool was not called. Steps that use this output continue without it and report what is missing; they do not invent a substitute.";
            store.SaveWork(failed);
            foreach (var decision in autonomy.Decisions(s.Id).Where(d => d.WorkId == failed.Id && d.State == DecisionState.Pending))
            {
                decision.State = DecisionState.Rejected;
                autonomy.SaveDecision(decision);
            }
            store.Log(failed.Id, "ToolNothingToRun", failed.Notes);
            store.Log(s.RootWorkId, "ToolNothingToRun", $"{failed.Tool.ToolId} / {failed.Tool.Operation}: {why[..Math.Min(300, why.Length)]}");
            s.KnownIssues.Add($"{failed.Tool.ToolId} / {failed.Tool.Operation} had nothing to run: {why[..Math.Min(200, why.Length)]}");
            Save(s);
            return true;
        }
        // The tool was never called: its preparation answer could not be used (no JSON, or "blocked").
        // Preparing again, told why, costs one AI call and loses nothing, whoever chose the tool.
        if ((failed.Notes.Contains("Input preparation did not return JSON", StringComparison.Ordinal) || failed.Notes.Contains("Input preparation blocked", StringComparison.Ordinal))
            && !store.Logs(failed.Id).Any(l => l.Event == "RetryToolStep" && l.Message.Contains("the preparation answer could not be used", StringComparison.Ordinal)))
        {
            RetryToolStep(s, failed, settings, "automatic retry: the preparation answer could not be used");
            return failed.State == WorkState.Queued;
        }
        // A passing error inside the tool (its own file save refused, the backend stopped or restarted,
        // the connection dropped) says nothing about the request: one fresh attempt, whoever chose the tool.
        // Seen live: "saving result.png failed with an EPERM permission error" and "Lost connection while
        // waiting for image job" stopped a task that a retry would have finished.
        if (TransientToolFailure(failed.Notes) && !store.Logs(failed.Id).Any(l => l.Event == "RetryToolStep" && l.Message.Contains("a passing tool error", StringComparison.Ordinal)))
        {
            RetryToolStep(s, failed, settings, "automatic retry after a passing tool error");
            return failed.State == WorkState.Queued;
        }
        if (!settings.Automation.PlannerChoosesTools)
            return false;
        var root = store.WorkItems().FirstOrDefault(w => w.Id == s.RootWorkId);
        var named = root?.ToolPreset?.ToolId == failed.Tool.ToolId || root?.ToolPresetFirst?.ToolId == failed.Tool.ToolId
            || orchestrator.Skills?.RequestedTools(root?.Request ?? s.Goal).Any(t => t.Id == failed.Tool.ToolId) == true
            || root?.Comments.Any(c => !c.IsAssistant && orchestrator.Skills?.RequestedTools(c.Text).Any(t => t.Id == failed.Tool.ToolId) == true) == true;
        if (named)
            return false;
        var reason = failed.Notes;
        if (!store.Logs(failed.Id).Any(l => l.Event == "RetryToolStep"))
        {
            RetryToolStep(s, failed, settings, "automatic retry of a planner-chosen tool");
            return failed.State == WorkState.Queued;
        }
        failed.State = WorkState.Skipped;
        failed.Notes = "TOOL UNAVAILABLE, STAND-IN NEEDED: this tool step failed twice and was skipped (" + reason[..Math.Min(400, reason.Length)] + "). The steps that use its output must create a simple stand-in asset themselves and say so.";
        store.SaveWork(failed);
        foreach (var decision in autonomy.Decisions(s.Id).Where(d => d.WorkId == failed.Id && d.State == DecisionState.Pending))
        {
            decision.State = DecisionState.Rejected;
            autonomy.SaveDecision(decision);
        }
        store.Log(failed.Id, "ToolFallback", failed.Notes);
        store.Log(s.RootWorkId, "ToolFallback", $"{failed.Tool.ToolId} / {failed.Tool.Operation} failed twice; continuing with a stand-in. {reason[..Math.Min(300, reason.Length)]}");
        s.KnownIssues.Add($"Tool {failed.Tool.ToolId} failed ({reason[..Math.Min(160, reason.Length)]}); a stand-in asset is used for: {failed.RequestPreview[..Math.Min(120, failed.RequestPreview.Length)]}");
        Save(s);
        return true;
    }

    // Large tasks: the first milestone the tester accepted that the user has not signed off yet, while a
    // later milestone remains (the last one is the task's own final review). Null when nothing waits.
    internal static string? SignOffDue(WorkSession s)
    {
        if (!s.SignOffMilestones || s.RootWorkId == null || s.Phases.Count < 2)
            return null;
        for (var i = 0; i < s.Phases.Count - 1; i++)
            if (s.AcceptedScopes.Contains("phase:" + i) && !s.SignedOffScopes.Contains("phase:" + i))
                return "phase:" + i;
        return null;
    }

    // Stops the run for the user's sign-off of a milestone. True when the run must stop.
    private bool RequestSignOff(WorkSession s)
    {
        if (SignOffDue(s) is not { } scope)
            return false;
        var name = AcceptanceCheck.ScopeName(s, scope);
        var next = int.Parse(scope["phase:".Length..]) + 1;
        var issues = s.KnownIssues.Where(issue => issue.StartsWith(name, StringComparison.Ordinal) || issue.Contains("not verified here", StringComparison.Ordinal)).TakeLast(6).ToArray();
        var checks = s.HumanChecks.Count > 0 ? "\nPlease check yourself (could not be tested on this machine): " + string.Join("; ", s.HumanChecks.TakeLast(6)) : "";
        var message = $"{name} passed its tester. Review it (run the project) before milestone {next + 1}/{s.Phases.Count} \"{s.Phases[next].Name}\" starts."
            + (issues.Length > 0 ? "\nKnown issues: " + string.Join("; ", issues) : "") + checks
            + "\nApprove to start the next milestone. To change this milestone, write a comment on the task instead: it is reworked and tested again before the next one starts.";
        var asked = autonomy.Decisions(s.Id).Any(d => d.Kind == "MilestoneSignOff" && d.State == DecisionState.Pending);
        Defer(s.Id, s.RootWorkId!, "MilestoneSignOff", message);
        if (!asked)
            store.Log(s.RootWorkId!, "MilestoneSignOffRequested", message);
        s.State = SessionState.WaitingForUser;
        s.Notes = $"Waiting for your sign-off of {name}. Approve it to start milestone {next + 1}, or comment on the task to change it.";
        Save(s);
        return true;
    }

    // The user approves the milestone waiting for sign-off (the button, or an "ok" comment).
    public bool SignOff(string sessionId)
    {
        var s = Session(sessionId);
        if (SignOffDue(s) is not { } signed)
            return false;
        s.SignedOffScopes.Add(signed);
        s.Notes = AcceptanceCheck.ScopeName(s, signed) + " signed off by the user; the next milestone starts when the task runs.";
        store.Log(s.RootWorkId ?? sessionId, "MilestoneSignedOff", s.Notes);
        Save(s);
        foreach (var decision in autonomy.Decisions(sessionId).Where(d => d.Kind == "MilestoneSignOff" && d.State == DecisionState.Pending))
        {
            decision.State = DecisionState.Approved;
            autonomy.SaveDecision(decision);
        }
        Changed?.Invoke();
        return true;
    }

    // The user wants a signed-off-pending milestone changed (a comment or a rejection): it and every
    // later milestone are no longer accepted, so the change is tested again before the run moves on.
    public bool ReopenMilestone(string sessionId, string why)
    {
        var s = Session(sessionId);
        var pending = autonomy.Decisions(sessionId).Where(d => d.Kind == "MilestoneSignOff" && d.State == DecisionState.Pending).ToArray();
        var scope = SignOffDue(s);
        if (scope == null && pending.Length == 0)
            return false;
        scope ??= s.AcceptedScopes.Where(a => a.StartsWith("phase:", StringComparison.Ordinal)).OrderBy(a => int.Parse(a["phase:".Length..])).LastOrDefault();
        foreach (var decision in pending)
        {
            decision.State = DecisionState.Rejected;
            autonomy.SaveDecision(decision);
        }
        if (scope == null)
            return pending.Length > 0;
        var index = int.Parse(scope["phase:".Length..]);
        s.AcceptedScopes.RemoveAll(a => a == AcceptanceCheck.GoalScope || a.StartsWith("phase:", StringComparison.Ordinal) && int.Parse(a["phase:".Length..]) >= index);
        s.SignedOffScopes.RemoveAll(a => a.StartsWith("phase:", StringComparison.Ordinal) && int.Parse(a["phase:".Length..]) >= index);
        s.GoalVerified = false;
        s.AcceptanceStalled = null;
        s.VerifyScope = null;
        s.VerifyRounds = 0;
        s.PendingVerifyId = null;
        s.PhaseIndex = index;
        s.Notes = $"{AcceptanceCheck.ScopeName(s, scope)} reopened: {why}";
        if (s.RootWorkId != null)
            store.Log(s.RootWorkId, "MilestoneReopened", s.Notes);
        Save(s);
        return true;
    }

    // Finishes a step whose only gap is a check that could not run here (compile, build, tests), in an
    // automatic task. Returns false, changing nothing, for anything else.
    private bool AcceptUnverified(WorkSession s, WorkItem work, string gap, bool held)
    {
        if (held || s.Mode != RoutingMode.Automatic || !s.AutoContinueSteps || work.State != WorkState.NeedsReview || !StepStatus.OnlyVerificationMissing(gap))
            return false;
        var proposal = store.Logs(work.Id).LastOrDefault(l => l.Event == "EditProposal");
        if (proposal != null && !store.Logs(work.Id).Any(l => l.Event == "EditsApplied" && l.Sequence > proposal.Sequence))
            return false;
        work.State = WorkState.Completed;
        work.Notes = "Done; its own check could not run here and is left to the milestone tester: " + gap[..Math.Min(300, gap.Length)];
        store.SaveWork(work);
        foreach (var decision in autonomy.Decisions(s.Id).Where(d => d.WorkId == work.Id && d.State == DecisionState.Pending && d.Kind == "Review"))
        {
            decision.State = DecisionState.Approved;
            autonomy.SaveDecision(decision);
        }
        store.Log(work.Id, "StepUnverified", work.Notes);
        s.KnownIssues.Add($"{work.PlanKey ?? work.Id[..8]} not verified here: {gap[..Math.Min(200, gap.Length)]}");
        Save(s);
        return true;
    }

    // A fix for a failed criterion reports that no AI can meet it: the thing does not exist, or only
    // the user can decide (seen live: "compare monster-fighting incremental games without offline
    // progress", and none could be confirmed). The criterion is handed to the user as a known issue,
    // the milestone counts as accepted with that note, and the run goes on instead of stopping. A
    // fix that failed for lack of skill is not waived: it goes back through the normal rounds.
    private bool WaiveUnreachableCriteria(WorkSession s, IReadOnlyList<WorkItem> tasks)
    {
        if (s.Mode != RoutingMode.Automatic || s.VerifyScope is not { } scope || s.RootWorkId == null)
            return false;
        foreach (var fix in tasks.Where(w => w.Tool == null && w.State == WorkState.NeedsReview && w.PlanKey?.StartsWith("accfix", StringComparison.Ordinal) == true))
        {
            var reason = StepStatus.Latest(store.Logs(fix.Id), StepStatus.GaveUp);
            if (reason == null || StepStatus.WhyUnfinished(reason) != StepStatus.Why.External)
                continue;
            if (autonomy.Decisions(s.Id).Any(d => d.WorkId == fix.Id && d.State == DecisionState.Pending && d.Kind != "Review"))
                continue;
            var name = AcceptanceCheck.ScopeName(s, scope);
            var failed = s.LastVerifyFailures ?? "a criterion";
            fix.State = WorkState.Skipped;
            fix.Notes = "Could not be met without you; handed to you as a known issue: " + reason;
            store.SaveWork(fix);
            foreach (var decision in autonomy.Decisions(s.Id).Where(d => d.WorkId == fix.Id && d.State == DecisionState.Pending && d.Kind == "Review"))
            {
                decision.State = DecisionState.Rejected;
                autonomy.SaveDecision(decision);
            }
            s.AcceptedScopes.Add(scope);
            s.KnownIssues.Add($"{name}: NOT MET, needs your decision — {failed[..Math.Min(300, failed.Length)]} — the fix reported: {reason[..Math.Min(300, reason.Length)]}");
            s.VerifyScope = null;
            s.VerifyRounds = 0;
            s.LastVerifyFailures = null;
            s.Notes = $"{name}: a criterion cannot be met without you; it is listed as a known issue and the work goes on.";
            Save(s);
            store.Log(s.RootWorkId, "AcceptanceWaived", $"{name}: {failed}\nThe fix step reported it cannot be met without the user: {reason}");
            return true;
        }
        return false;
    }

    public Func<WorkSession, string, CancellationToken, Task<bool>>? ToolOutputReplan { get; set; }
    private readonly HashSet<string> repairTried = new(StringComparer.Ordinal);

    private async Task<bool> RepairMissingToolOutput(WorkSession session, IReadOnlyList<WorkItem> tasks, CancellationToken token)
    {
        if (session.Mode != RoutingMode.Automatic || session.RemainingCalls <= 3 || ToolOutputReplan == null) return false;
        var root = store.WorkItems().FirstOrDefault(w => w.Id == session.RootWorkId);
        var userScope = TaskIntent.Normalize((root?.Request ?? session.Goal) + " " + (PlanningService.ExecutionUpdate(store, root)?.Text ?? ""));
        if (new[] { "do not regenerate", "no regeneration", "reuse only", "khong gen lai", "khong tao lai anh", "khong ve lai" }.Any(userScope.Contains)) return false;
        if (!tasks.Any(w => w.Tool != null && w.State == WorkState.Completed)) return false;
        foreach (var work in tasks.Where(w => w.Tool == null && w.State == WorkState.NeedsReview && w.VerifyScope == null))
        {
            var logs = store.Logs(work.Id);
            var reason = StepStatus.Latest(logs, StepStatus.GaveUp);
            var text = reason?.ToLowerInvariant();
            // The report must be about tool-made media. A step that uses a tool's output and cannot go on
            // is about that output however it is worded (seen live: "cần Manager trả hai strip đúng
            // 256×64" names neither "missing" nor "wrong"). Other steps must name media and a defect.
            var usesTool = work.DependsOn.Any(id => tasks.Any(t => t.Id == id && t.Tool != null));
            if (text == null || !usesTool && (!new[] { "frame", "animation", "sprite", "asset", "png", "image", "gui", "icon", "video", "clip", "audio", "music", "sound", "ảnh", "khung hình", "nhạc", "âm thanh" }.Any(text.Contains) ||
                !new[] { "missing", "unavailable", "not available", "incorrect", "wrong", "not supplied", "only one", "single", "still image", "size", "thiếu", "không có", "không đúng", "chỉ có", "kích thước" }.Any(text.Contains)) ||
                new[] { "permission denied", "access denied", "read-only", "quyền", "projectcontentmissing" }.Any(text.Contains)) continue;
            if (autonomy.Decisions(session.Id).Any(d => d.WorkId == work.Id && d.State == DecisionState.Pending && d.Kind != "Review")) continue;
            var origin = logs.LastOrDefault(l => l.Event == "ToolOutputRepairOrigin")?.Message ?? work.Id;
            // At most two repairs that were planned, and one try per step in each run: a repair plan
            // the manager rejected does not use up the step's repairs, it waits for the next Run.
            if (store.Logs(origin).Count(l => l.Event == "ToolOutputRepairDone") >= 2 || !repairTried.Add(work.Id)) continue;
            store.Log(origin, "ToolOutputRepairAttempt", reason!);
            var priorIds = tasks.Select(w => w.Id).ToHashSet(StringComparer.Ordinal);
            if (!await ToolOutputReplan(session, "Repair missing deliverables of step " + work.Id + ": " + work.Request +
                "\nReport: " + reason + "\nKeep usable files/code. Plan corrected tool jobs for missing or invalid assets and the implementation that completes this step. Downstream review steps already exist. Do not depend on the blocked step or repeat finished gameplay/research.", token)) continue;
            var added = Workers(session.Id).Where(w => !priorIds.Contains(w.Id)).ToArray();
            if (added.Length == 0) continue;
            var dependents = tasks.Where(w => w.DependsOn.Contains(work.Id) && w.State == WorkState.Queued).ToArray();
            var terminalIds = added.Where(w => !added.Any(other => other.DependsOn.Contains(w.Id))).Select(w => w.Id).ToArray();
            foreach (var dependent in dependents)
                dependent.DependsOn = dependent.DependsOn.Where(id => id != work.Id).Concat(terminalIds).Distinct().ToList();
            TaskDependencies.Validate(tasks.Concat(added).ToArray());
            foreach (var dependent in dependents) store.SaveWork(dependent);
            foreach (var addedWork in added) store.Log(addedWork.Id, "ToolOutputRepairOrigin", origin);
            work.State = WorkState.Skipped;
            work.Notes = "Superseded by corrective steps: " + string.Join(", ", terminalIds) + ". The incomplete result was not approved.";
            store.SaveWork(work);
            foreach (var decision in autonomy.Decisions(session.Id).Where(d => d.WorkId == work.Id && d.State == DecisionState.Pending && d.Kind == "Review"))
            {
                decision.State = DecisionState.Rejected;
                autonomy.SaveDecision(decision);
            }
            store.Log(work.Id, "ToolOutputRepairPlanned", work.Notes);
            store.Log(origin, "ToolOutputRepairDone", string.Join(", ", terminalIds));
            return true;
        }
        return false;
    }

    private void ResumeManagedToolHandoffs(WorkSession session, ManagerSettings settings)
    {
        foreach (var work in Workers(session.Id))
        {
            if (ManagedToolHandoff.RecoveryKey(work, session, settings, store) is not { } key)
                continue;
            // Never dismiss an unrelated permission, test, delegation or explicit routing decision.
            if (autonomy.Decisions(session.Id).Any(d => d.WorkId == work.Id && d.State == DecisionState.Pending && d.Kind != "Review"))
                continue;
            store.Log(work.Id, ManagedToolHandoff.RecoveryEvent, key);
            store.Log(work.Id, StepStatus.Continuing, "The Manager's named tool has completed and its receipt is available. Recheck current files and complete only this step's own scope. Reuse the tool output; do not regenerate it or repeat completed code changes.");
            work.State = WorkState.Queued;
            work.BlockReason = "";
            work.Notes = "Manager tool output is ready. Queued to recheck and finish this step; its result has not been approved.";
            store.SaveWork(work);
            foreach (var decision in autonomy.Decisions(session.Id).Where(d => d.WorkId == work.Id && d.State == DecisionState.Pending && d.Kind == "Review"))
            {
                decision.State = DecisionState.Rejected;
                autonomy.SaveDecision(decision);
            }
        }
    }

    // The per-task "test after build" option: one test run after the steps and the build check have
    // finished, reported on the task like the other steps. Returns a status line for the session notes.
    public async Task<string?> RunTestsAfterBuild(WorkSession s, CancellationToken token = default)
    {
        if (!s.RunTestsAtTaskEnd || s.RootWorkId == null || s.ProjectIds.Count != 1)
            return null;
        var project = store.Projects().FirstOrDefault(p => p.Id == s.ProjectIds[0]);
        if (project == null)
            return null;
        var result = await testing.RunTestsAfterBuildAsync(project, token);
        if (!result.Ran)
        {
            store.Log(s.RootWorkId, "TaskTestSkipped", result.Command + ": " + result.Output);
            return " After-build tests were skipped: " + result.Output;
        }
        store.Log(s.RootWorkId, result.Passed ? "TaskTestPassed" : "TaskTestFailed", result.Command + (result.Passed ? ": passed." : ":\n" + AutoCheck.Digest(result.Output)));
        return result.Passed ? " The after-build tests passed (" + result.Command + ")." : " The after-build tests FAILED (" + result.Command + "); review the task test log.";
    }

    // Set by the task service: runs a goal check for the session and returns true when it added steps.
    public Func<WorkSession, CancellationToken, Task<bool>>? GoalCheck { get; set; }

    public async Task RunAsync(string sessionId, ManagerSettings settings, IReadOnlyList<ProviderHealth> health, CancellationToken token = default)
    {
        if (Interlocked.CompareExchange(ref active, 1, 0) != 0)
            throw new InvalidOperationException("A session is already running.");
        pauseRequested = false;
        stopRequested = false;
        repairTried.Clear();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        runCancellation = cancellation;
        try
        {
            var s = Session(sessionId);
            PlanningService.ValidateSession(s);
            SessionTiming.Start(s);
            s.State = SessionState.Running;
            s.Notes = "Running approved tasks.";
            Save(s);
            var remaining = SessionTiming.Remaining(s);
            if (remaining <= TimeSpan.Zero)
            {
                s.State = SessionState.BudgetReached;
                s.Notes = "Time expired; progress and checkpoints preserved. Extend time in Tasks to continue.";
                Save(s);
                return;
            }

            if (remaining is { } left)
                cancellation.CancelAfter(left);
            // Retry provider waits once per explicit resume, never on every scheduler iteration.
            // Recheck known configuration faults, but do not spend usage blindly retrying a runtime permission refusal.
            foreach (var work in Workers(sessionId).Where(w => w.State is WorkState.WaitingForProvider or WorkState.WrongAccess or WorkState.WrongProject))
            {
                if (work.State == WorkState.WrongProject && TaskProblems.ProjectProblem(work, store.Projects(), s) != null)
                    continue;
                if (work.State == WorkState.WrongAccess)
                {
                    // Only a missing write permission that has since been granted unblocks the step.
                    // The text checks below only recognise rows written before BlockCode existed.
                    var missingWrite = work.BlockCode == BlockCode.MissingWritePermission || work.BlockCode == BlockCode.None && work.ProblemReason.Contains("project write permission is not enabled", StringComparison.OrdinalIgnoreCase);
                    var obsoleteReadScope = work.BlockCode == BlockCode.None && work.Tool == null && !work.IsExternal && s.RootWorkId != null && s.ProjectIds.Count == 1 && s.AccessCeiling == AccessLevel.WriteProject && work.Access != AccessLevel.WriteProject && !ToolSkillRegistry.IsPreparation(work, store) && work.ProblemReason.Contains("but this step is restricted to ReadProject", StringComparison.OrdinalIgnoreCase);
                    if (!obsoleteReadScope && (!missingWrite || TaskProblems.MissingWritePermission(work, settings, health, work.PreferredRoute)))
                        continue;
                }

                work.State = WorkState.Queued;
                store.SaveWork(work);
                foreach (var decision in autonomy.Decisions(sessionId).Where(d => d.WorkId == work.Id && (d.Kind is "Approval" or "ProviderWait" or "WrongAccess" or "WrongProject") && d.State == DecisionState.Pending))
                {
                    decision.State = DecisionState.Rejected;
                    autonomy.SaveDecision(decision);
                }
            }

            // An explicit Run / resume retries every failed tool step with a freshly prepared request,
            // whatever stopped it (bad input, a missing dependency, the tool refusing or being down).
            foreach (var failed in Workers(sessionId).Where(w => w.State == WorkState.Failed && w.Tool != null))
                RetryToolStep(s, failed, settings, "explicit resume");

            foreach (var work in Workers(sessionId).Where(w => w.Tool != null && (w.State is WorkState.AwaitingApproval or WorkState.NeedsReview)))
            {
                if (orchestrator.ToolTasks?.WasReviewedExternally(work) != true && orchestrator.ToolTasks?.CanContinueImage(work, s) != true)
                    continue;
                work.State = WorkState.Queued;
                store.SaveWork(work);
                foreach (var decision in autonomy.Decisions(sessionId).Where(d => d.WorkId == work.Id && d.State == DecisionState.Pending && (d.Kind is "ToolApproval" or "ToolReview")))
                {
                    decision.State = DecisionState.Approved;
                    autonomy.SaveDecision(decision);
                }
            }

            foreach (var work in Workers(sessionId).Where(w => w.Tool != null && w.State == WorkState.Completed))
            {
                var executor = orchestrator.ToolTasks;
                if (executor?.NeedsImageImport(work, s) == true)
                    await executor.RunAsync(work, s, cancellation.Token);
            }

            ResumeManagedToolHandoffs(s, settings);
            foreach (var work in Workers(sessionId).Where(w => w.Tool == null && w.State == WorkState.NeedsReview))
            {
                if (StepStatus.Latest(store.Logs(work.Id), StepStatus.GaveUp) is { } gap)
                {
                    // A step that stopped earlier only because its check could not run (it waits for review).
                    AcceptUnverified(s, work, gap, autonomy.Decisions(sessionId).Any(d => d.WorkId == work.Id && d.State == DecisionState.Pending && d.Kind is "Delegation" or "TestReview"));
                    continue;
                }
                if (!ImageDeliveryPolicy.Continues(work, s, Workers(sessionId), settings.Automation))
                    continue;
                if (autonomy.Decisions(sessionId).Any(d => d.WorkId == work.Id && d.State == DecisionState.Pending && d.Kind != "Review"))
                    continue;
                var logs = store.Logs(work.Id);
                var proposal = logs.LastOrDefault(l => l.Event == "EditProposal");
                if (proposal != null && !logs.Any(l => l.Event == "EditsApplied" && l.Sequence > proposal.Sequence))
                {
                    orchestrator.ApplyReviewedEdits(work, imageDelivery: true);
                }

                work.State = WorkState.Completed;
                work.Notes = "Continuing the image generate/apply chain from existing results; finished steps are not called again.";
                store.SaveWork(work);
                foreach (var decision in autonomy.Decisions(sessionId).Where(d => d.WorkId == work.Id && d.Kind == "Review" && d.State == DecisionState.Pending))
                {
                    decision.State = DecisionState.Approved;
                    autonomy.SaveDecision(decision);
                }

                store.Log(work.Id, "ImageStepResumed", work.Notes);
            }

            while (true)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                s = Session(sessionId);
                Reconcile(sessionId);
                if (pauseRequested)
                {
                    s.State = SessionState.Paused;
                    s.Notes = "Stopped accepting new tasks; the previous task has finished.";
                    Save(s);
                    return;
                }

                ResumeManagedToolHandoffs(s, settings);
                var tasks = Workers(sessionId);
                TaskDependencies.Validate(tasks);
                if (tasks.Count > 0 && tasks.All(w => w.State is WorkState.Completed or WorkState.Skipped))
                {
                    // Code changed since the last check: build/test the project. A failure becomes a fix
                    // step and the run goes on; the same failure again after a fix stops for review.
                    if (await AutoCheckAsync(s, tasks, cancellation.Token))
                    {
                        Changed?.Invoke();
                        continue;
                    }
                    s = Session(sessionId);
                    // Milestone finished: an independent tester runs the project against the
                    // milestone's acceptance criteria; what fails is fixed and tested again.
                    if (await CheckAcceptance(s, tasks, cancellation.Token))
                    {
                        Changed?.Invoke();
                        continue;
                    }
                    s = Session(sessionId);
                    if (RequestSignOff(s))
                        return;
                    // Large goals: compare the results with the goal; the coordinator adds the steps for
                    // gaps or for the next phase, and the run goes on until the goal is achieved.
                    // Bounded: a goal that keeps producing "one more gap" stops for the user.
                    if (GoalCheck != null && s.RootWorkId != null && s.AutoContinueSteps && !s.GoalVerified && s.AcceptanceStalled == null && (s.Phases.Count > 0 || tasks.Count(w => w.State == WorkState.Completed) >= 4) && s.RemainingCalls > 0 && s.GoalChecks < Math.Max(8, s.Phases.Count * 3))
                    {
                        if (await GoalCheck(s, cancellation.Token))
                        {
                            Changed?.Invoke();
                            continue;
                        }
                        // The coordinator says the goal is done: test the whole result once more
                        // against the goal's own acceptance criteria before believing it.
                        s = Session(sessionId);
                        if (await CheckAcceptance(s, Workers(sessionId), cancellation.Token))
                        {
                            Changed?.Invoke();
                            continue;
                        }
                        s = Session(sessionId);
                        if (RequestSignOff(s))
                            return;
                    }
                    await BatchTests(s, tasks, cancellation.Token);
                    var testNote = await RunTestsAfterBuild(s, cancellation.Token);
                    s = Session(sessionId);
                    s.State = SessionState.Completed;
                    var acceptance = AcceptanceCheck.Summary(s);
                    if (acceptance.Length > 0 && s.RootWorkId != null)
                        store.Log(s.RootWorkId, "AcceptanceReport", acceptance.Trim());
                    s.Notes = (s.AutoContinueSteps ? "Steps have finished. Review and approve the parent task result; tests are tracked separately." : "User marked all tasks complete. Test status is tracked separately.") + acceptance + testNote;
                    Save(s);
                    return;
                }

                var next = tasks.Where(w => w.State is WorkState.Queued or WorkState.Paused).Where(w => TaskDependencies.Ready(w, tasks)).OrderByDescending(w => w.Priority).ThenBy(w => w.CreatedAt).FirstOrDefault();
                if (next == null)
                {
                    if (WaiveUnreachableCriteria(s, tasks))
                    {
                        Changed?.Invoke();
                        continue;
                    }
                    if (await RepairMissingToolOutput(s, tasks, cancellation.Token))
                    {
                        Changed?.Invoke();
                        continue;
                    }
                    await BatchTests(s, tasks, cancellation.Token);
                    s = Session(sessionId);
                    s.State = SessionState.WaitingForUser;
                    s.Notes = tasks.FirstOrDefault(w => w.State is WorkState.WrongAccess or WorkState.WrongProject or WorkState.WaitingForProvider || w.Tool != null && w.State != WorkState.Completed)?.Notes ?? "No tasks are ready. Check pending decisions, review results or dependencies.";
                    Save(s);
                    return;
                }

                if (next.Tool == null && s.RemainingCalls == 0)
                {
                    await BatchTests(s, tasks, cancellation.Token);
                    s.State = SessionState.BudgetReached;
                    s.Notes = "AI execution attempt budget exhausted.";
                    Save(s);
                    return;
                }

                var projectError = TaskProblems.ProjectProblem(next, store.Projects(), s);
                if (projectError != null)
                {
                    TaskProblems.Mark(next, WorkState.WrongProject, projectError);
                    store.SaveWork(next);
                    Defer(sessionId, next.Id, "WrongProject", projectError);
                    continue;
                }

                // Every step of a task with write access in its one project uses that access, tool steps included.
                var executionProject = next.IsExternal ? new ProjectInfo("", "External task", Path.GetTempPath()) : store.Projects().Single(p => p.Id == next.ProjectId);
                if (!next.IsExternal && s.RootWorkId != null && s.ProjectIds.Count == 1 && s.AccessCeiling == AccessLevel.WriteProject && next.Access != AccessLevel.WriteProject && !ToolSkillRegistry.IsPreparation(next, store))
                {
                    next.Access = AccessLevel.WriteProject;
                    if (next.Tool == null && TaskIntent.RequiresImplementation(next.Request, executionProject) && !TaskIntent.IsReadOnly(next.Request))
                        next.Kind = next.Kind == WorkKind.Research ? WorkKind.Research : WorkKind.Coding;
                    store.SaveWork(next);
                    store.Log(next.Id, "ProjectWriteDefault", "Step uses the task's WriteProject access in the registered project.");
                }

                if (next.Tool != null)
                {
                    var attemptsBefore = next.Attempts;
                    CurrentWorkId = next.Id;
                    try
                    {
                        await (orchestrator.ToolTasks ?? throw new InvalidOperationException("V5 executor is unavailable.")).RunAsync(next, s, cancellation.Token);
                    }
                    finally
                    {
                        CurrentWorkId = null;
                        s = Session(sessionId);
                        s.CallsUsed += Math.Max(0, next.Attempts - attemptsBefore);
                        Save(s);
                    }

                    if (next.State == WorkState.Completed && ImageDeliveryPolicy.Continues(next, s, tasks, settings.Automation))
                        foreach (var decision in autonomy.Decisions(sessionId).Where(d => d.WorkId == next.Id && d.State == DecisionState.Pending && (d.Kind is "ToolApproval" or "ToolReview" or "ToolError")))
                        {
                            decision.State = DecisionState.Approved;
                            autonomy.SaveDecision(decision);
                        }

                    if (RecoverPlannerTool(s, next, settings))
                    {
                        Changed?.Invoke();
                        continue;
                    }

                    if (next.State != WorkState.Completed)
                    {
                        var kind = next.State == WorkState.WrongAccess ? "WrongAccess" : next.State == WorkState.AwaitingApproval ? "ToolApproval" : next.State == WorkState.NeedsReview ? "ToolReview" : "ToolError";
                        Defer(sessionId, next.Id, kind, next.Notes);
                        if (!s.Away || next.State == WorkState.Paused)
                        {
                            s.State = SessionState.WaitingForUser;
                            s.Notes = next.Notes;
                            Save(s);
                            Changed?.Invoke();
                            return;
                        }
                    }

                    Changed?.Invoke();
                    continue;
                }

                if (!ToolSkillRegistry.IsPreparation(next, store) && (TaskIntent.RequiresImplementation(next.Request, executionProject) || ImageDeliveryPolicy.Continues(next, s, tasks, settings.Automation)) && !TaskIntent.IsReadOnly(next.Request) && next.Access != AccessLevel.WriteProject)
                {
                    if (s.AccessCeiling != AccessLevel.WriteProject)
                    {
                        next.State = WorkState.WrongAccess;
                        next.Notes = "This step requests edits but only read access was approved. Set task access explicitly before continuing.";
                        next.BlockReason = next.Notes;
                        store.SaveWork(next);
                        Defer(sessionId, next.Id, "WrongAccess", next.Notes);
                        continue;
                    }

                    next.Access = AccessLevel.WriteProject;
                    next.Kind = next.Kind == WorkKind.Research ? WorkKind.Research : WorkKind.Coding;
                    next.RoutingApproved = false;
                    store.SaveWork(next);
                    store.Log(next.Id, "ExecutionScopeRepair", "Implementation worker uses the existing session WriteProject grant; checkpoints and attempts preserved.");
                }

                var config = JsonSerializer.Deserialize<ManagerSettings>(JsonSerializer.Serialize(settings))!;
                config.Mode = s.Mode;
                var attempts = next.Attempts;
                var switches = next.Switches;
                CurrentWorkId = next.Id;
                await orchestrator.RunAsync(next, config, health, next.PreferredRoute, next.RoutingApproved, cancellation.Token, s.RemainingCalls);
                CurrentWorkId = null;
                s = Session(sessionId);
                s.CallsUsed += next.Attempts - attempts;
                s.SwitchesUsed += next.Switches - switches;
                Save(s);
                if (s.RootWorkId != null && store.WorkItems().Single(w => w.Id == s.RootWorkId).PendingAccess != null)
                {
                    s.State = SessionState.WaitingForUser;
                    s.Notes = "Comment permission request saved. Session stopped; Run / resume applies it before a fresh runtime starts.";
                    Save(s);
                    return;
                }

                if (requestedSwitch is { } change && change.WorkId == next.Id)
                {
                    requestedSwitch = null;
                    next.PreferredRoute = change.Route;
                    next.RouteIsAdvisory = false;
                    next.RoutingApproved = true;
                    next.State = WorkState.Queued;
                    next.Switches++;
                    s.SwitchesUsed++;
                    store.SaveWork(next);
                    store.Log(next.Id, "UserSwitch", JsonSerializer.Serialize(change.Route));
                    Save(s);
                    continue;
                }

                if (next.State == WorkState.NeedsReview)
                {
                    var execution = store.Logs(next.Id).Last(l => l.Event == "Execution").Message;
                    var delegation = AgentProtocol.Delegation(execution[(execution.IndexOf('\n') + 1)..]);
                    if (delegation != null && s.AllowDelegation)
                    {
                        if (s.Mode == RoutingMode.Automatic)
                        {
                            try
                            {
                                AcceptDelegation(s, next, delegation, settings, health);
                            }
                            catch (InvalidOperationException ex)
                            {
                                Defer(sessionId, next.Id, "Delegation", ex.Message, delegation);
                            }
                        }
                        else
                            Defer(sessionId, next.Id, "Delegation", delegation.Reason, delegation);
                    }

                    if (next.State == WorkState.NeedsReview)
                    {
                        var proposal = store.Logs(next.Id).LastOrDefault(l => l.Event == "EditProposal");
                        if (proposal != null && next.Access == AccessLevel.WriteProject && ImageDeliveryPolicy.Continues(next, s, tasks, settings.Automation) && !store.Logs(next.Id).Any(l => l.Event == "EditsApplied" && l.Sequence > proposal.Sequence))
                            orchestrator.ApplyReviewedEdits(next, imageDelivery: true);
                        await CollectTests(next, cancellation.Token);
                        var unapplied = proposal != null && !store.Logs(next.Id).Any(l => l.Event == "EditsApplied" && l.Sequence > proposal.Sequence);
                        var blocked = autonomy.Decisions(sessionId).Any(d => d.WorkId == next.Id && d.State == DecisionState.Pending && d.Kind is "Delegation" or "TestReview");
                        // Still unfinished after its allowed attempts: the user decides, it is not passed on as done.
                        // Only the check could not run here: the step is done, the gap is a known issue and the
                        // milestone tester still judges the result.
                        if (StepStatus.Latest(store.Logs(next.Id), StepStatus.GaveUp) is { } unverified && AcceptUnverified(s, next, unverified, unapplied || blocked))
                            next.State = WorkState.Completed;
                        else if (StepStatus.Latest(store.Logs(next.Id), StepStatus.GaveUp) is { } unfinished)
                            Defer(sessionId, next.Id, "Review", "The AI reports unfinished work after its allowed attempts: " + unfinished + " Send it back with a comment to continue, or approve it as it is.");
                        else if (((s.AutoContinueSteps && s.Mode == RoutingMode.Automatic) || ImageDeliveryPolicy.Continues(next, s, tasks, settings.Automation)) && !unapplied && !blocked)
                        {
                            var saved = store.WorkItems().Single(w => w.Id == next.Id);
                            saved.State = WorkState.Completed;
                            saved.Notes = "Internal step finished without separate user review. The final result is in the parent task.";
                            store.SaveWork(saved);
                            store.Log(next.Id, "StepFinished", saved.Notes);
                            next.State = WorkState.Completed;
                        }
                        else
                            Defer(sessionId, next.Id, "Review", "Review results and changed files, run the project if needed, then approve the result.");
                    }
                }
                else if (next.State is WorkState.WrongAccess or WorkState.WrongProject)
                    Defer(sessionId, next.Id, next.State.ToString(), next.ProblemReason);
                else if (next.State == WorkState.WaitingForProvider)
                    Defer(sessionId, next.Id, "ProviderWait", next.Notes);
                else if (next.State is WorkState.AwaitingApproval or WorkState.WaitingForPermission or WorkState.Failed)
                    Defer(sessionId, next.Id, "Approval", next.Notes);
                Changed?.Invoke();
                if (!s.Away && next.State is not (WorkState.Delegated or WorkState.Completed))
                {
                    s = Session(sessionId);
                    s.State = SessionState.WaitingForUser;
                    s.Notes = "The finished task needs your review or decision before continuing. Away allows independent tasks to continue.";
                    Save(s);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            var s = Session(sessionId);
            var expired = !stopRequested && SessionTiming.Remaining(s) <= TimeSpan.FromSeconds(1) /* the deadline timer can fire a few ms early */;
            s.State = expired ? SessionState.BudgetReached : SessionState.Stopped;
            s.Notes = expired ? "Time expired; progress, files and checkpoints preserved. Extend time in Tasks to continue." : "Stopped and saved a checkpoint.";
            Save(s);
        }
        catch (Exception ex)
        {
            var s = Session(sessionId);
            s.State = SessionState.Paused;
            s.Notes = ex.Message;
            Save(s);
        }
        finally
        {
            CurrentWorkId = null;
            requestedSwitch = null;
            runCancellation = null;
            Volatile.Write(ref active, 0);
            Changed?.Invoke();
        }
    }
}
