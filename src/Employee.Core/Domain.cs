namespace Employee.Core;
public enum RoutingMode
{
    Automatic,
    SemiAutomatic,
    Manual
}

public enum WorkEffort
{
    Auto,
    Light,
    Standard,
    Heavy
}

public enum WorkKind
{
    Coding,
    Review,
    Research,
    Content,
    Classification,
    Extraction,
    Planning
}

public enum WorkState
{
    Queued,
    AwaitingApproval,
    Running,
    Paused,
    WaitingForProvider,
    NeedsReview,
    Failed,
    Completed,
    WaitingForPermission,
    Delegated,
    Saved,
    Skipped,
    WrongAccess,
    WrongProject,
    // Ways the user closes a task other than Completed (appended: stored values never change).
    Incomplete,
    NotMet,
    Cancelled
}

public static class TaskOutcomes
{
    public static readonly WorkState[] CloseStates = [WorkState.Incomplete, WorkState.NotMet, WorkState.Cancelled];
    // A task in one of these states is finished: it does not run, queue or resume again (a rerun makes a fresh copy).
    public static bool IsClosed(WorkState state) => state is WorkState.Completed or WorkState.Skipped or WorkState.Incomplete or WorkState.NotMet or WorkState.Cancelled;
    public static string Label(WorkState state) => state switch
    {
        WorkState.Incomplete => "Not completed",
        WorkState.NotMet => "Does not meet requirements",
        WorkState.Cancelled => "Cancelled",
        _ => state.ToString()
    };
}

public enum AccessLevel
{
    TextOnly,
    ReadProject,
    WriteProject
}

public enum FailureKind
{
    None,
    RateLimit,
    Authentication,
    Unavailable,
    Timeout,
    Cancelled,
    TaskError,
    Validation,
    Permission,
    ModelUnavailable,
    InputTooLarge,
    LaunchFailed,
    ModelRefused
}

public sealed record ProjectInfo(string Id, string Name, string Root, string Aliases = "", string Technology = "Unknown", string Rules = "", string Keywords = "", DateTimeOffset? KeywordsUpdatedAt = null);
public sealed record IndexedFile(string ProjectId, string Path, long Size, long ModifiedTicks);
public sealed class WorkItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ProjectId { get; set; } = "";
    public bool IsExternal => string.IsNullOrWhiteSpace(ProjectId);
    public string Request { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public string RequestPreview => System.Text.RegularExpressions.Regex.Replace(Request, @"\s+", " ").Trim();
    public List<WorkAttachment> Attachments { get; set; } = [];
    public List<WorkComment> Comments { get; set; } = [];
    public DateTimeOffset? AccessEditedAt { get; set; }
    public PendingTaskAccess? PendingAccess { get; set; }
    public string AccessResult { get; set; } = "";
    public bool AutoAssign { get; set; }
    public bool? AutomaticIntent { get; set; }
    public AccessLevel? GrantedAccess { get; set; }
    public PlanningPolicy? PlanningOverride { get; set; }
    public string? SkillId { get; set; }
    public string? SkillHash { get; set; }
    public string? PlanKey { get; set; }
    // How this step's own result is checked (planner-written); the worker verifies it before reporting done.
    public string? Acceptance { get; set; }
    // Set on an acceptance verification step: the scope it tests ("phase:0", "goal").
    public string? VerifyScope { get; set; }
    // The milestone (0-based) this step was planned for, in a task with milestones.
    public int? Milestone { get; set; }
    public ToolTaskSpec? Tool { get; set; }
    public string? ToolRunId { get; set; }
    public string? ToolBindingHash { get; set; }
    public string ExecutorDisplay => Tool == null ? "AI" : "Tool · " + Tool.ToolId + " / " + Tool.Operation;
    public string? ToolWorkflowId { get; set; }
    // Tool and settings the user picked in the Tool task form. The task is planned as usual; the
    // tool step for this tool uses exactly these settings.
    public ToolTaskSpec? ToolPreset { get; set; }
    // For a two-step choice ("Create, then animate"): the tool call that makes the image ToolPreset
    // then works from.
    public ToolTaskSpec? ToolPresetFirst { get; set; }
    public WorkKind Kind { get; set; } = WorkKind.Coding;
    public WorkEffort Effort { get; set; }
    // Planner-rated 0-100 difficulty for this step; null means rule-based estimate.
    public int? Difficulty { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public WorkEffort RoutingEffort => WorkComplexity.Estimate(this);

    [System.Text.Json.Serialization.JsonIgnore]
    public int DifficultyScore => WorkComplexity.Score(this);
    // Planner-chosen WorkCapability name for this step; null means detected from the step.
    public string? CapabilityHint { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public WorkCapability Capability => WorkCapabilities.For(this);

    [System.Text.Json.Serialization.JsonIgnore]
    public string CapabilityDisplay => WorkCapabilities.Display(Capability) + (CapabilityHint != null && Tool == null && !IsCoordinator ? " (planner)" : "");

    [System.Text.Json.Serialization.JsonIgnore]
    public string DifficultyDisplay => $"{DifficultyScore} · {RoutingEffort}" + (Difficulty == null ? "" : " (planner)");
    public WorkState State { get; set; } = WorkState.Queued;
    public AccessLevel Access { get; set; } = AccessLevel.TextOnly;
    public string? ProviderId { get; set; }
    public string? ModelId { get; set; }
    public string ModelDisplay => ProviderId == null || ProviderId.StartsWith("tool:", StringComparison.Ordinal) ? "" : string.IsNullOrEmpty(ModelId) ? "CLI default (exact model unknown)" : ModelId;
    public int Priority { get; set; } = 50;
    public int Attempts { get; set; }
    // Invocations that failed only because the provider was out of usage or unavailable. They are
    // recorded as calls but never count toward the per-step attempt limit.
    public int ProviderWaits { get; set; }
    // Times the user sent this work back (change request or rerun on an unapproved result), and
    // the provider|model routes whose results were sent back. Routing escalates on both.
    public int Reworks { get; set; }
    // Times this step was moved to a stronger model because it stalled on something the model could not do.
    public int Escalations { get; set; }
    public List<string> RejectedRoutes { get; set; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public int WorkAttempts => Math.Max(0, Attempts - ProviderWaits);
    public int Switches { get; set; }
    public string Notes { get; set; } = "";
    public string BlockReason { get; set; } = "";
    // Why the work is blocked, for logic; BlockReason is the text shown to the user.
    public BlockCode BlockCode { get; set; }
    public string ProblemReason => State is WorkState.WrongAccess or WorkState.WrongProject or WorkState.WaitingForProvider or WorkState.Failed or WorkState.Paused or WorkState.AwaitingApproval ? string.IsNullOrWhiteSpace(BlockReason) ? Notes : BlockReason : "";
    public DateTimeOffset? QueuedAt { get; set; }
    public DateTimeOffset? ScheduledAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? SessionId { get; set; }
    public string? ParentTaskId { get; set; }
    public bool IsCoordinator { get; set; }
    public bool IsAssignmentClassifier { get; set; }
    public bool IsGoal { get; set; }
    public bool? AwayOverride { get; set; }
    public bool? TestAfterBuild { get; set; }
    public bool DelegationAllowed { get; set; }
    public int DelegationDepth { get; set; }
    public List<string> DependsOn { get; set; } = [];
    public List<string> RouteHistory { get; set; } = [];
    public Route? PreferredRoute { get; set; }
    public bool StepRouteOverride { get; set; }
    public bool RouteIsAdvisory { get; set; }
    public bool RoutingApproved { get; set; }
    public VerificationState Verification { get; set; } = VerificationState.Untested;
    public string DependenciesText => string.Join(", ", DependsOn.Select(x => x[..Math.Min(8, x.Length)]));
}

public sealed record WorkAttachment(string Name, byte[] Content)
{
    // Content hash used by storage to keep attachment bytes outside the task row.
    public string? Key { get; init; }
    public string Label => $"{Name} ({Content.Length:N0} bytes)";
}

public sealed record WorkComment(string Text, List<WorkAttachment> Attachments, DateTimeOffset At)
{
    public bool IsAssistant { get; init; }
    public string? SourceWorkId { get; init; }
    public long? ExecutionSequence { get; init; }
}

public sealed class ProviderSettings
{
    public string Id { get; set; } = "";
    public string Executable { get; set; } = "";
    public bool Enabled { get; set; }
    // The user switched this CLI off (Settings > AI & Models or Provider settings): it gets no
    // requests, and detection must not turn it back on.
    public bool TurnedOff { get; set; }
    public int Priority { get; set; } = 50;
    public bool AllowProjectWrite { get; set; }
    public DateTimeOffset? CooldownUntil { get; set; }
    public string LastError { get; set; } = "";
    public string ModelDiscoveryStatus { get; set; } = "Not inspected";
    public DateTimeOffset? ModelCatalogCheckedAt { get; set; }
    public List<ModelInfo> Models { get; set; } = [];
}

public sealed record ModelInfo(string Id, WorkKind[] Kinds, int Quality = 50, int CostRank = 50, bool Blocked = false, string DiscoverySource = "User catalog", bool AccountAvailabilityVerified = false)
{
    public WorkKind[] PrimaryKinds { get; init; } = [];
    public WorkKind[] ManualRoleKinds { get; init; } = [];
    public WorkKind[] AutomaticPrimaryKinds { get; init; } = [];
    public bool RatedByUser { get; init; }
    public int EffectiveQuality => ModelRatings.Effective(this).Quality;
    public int EffectiveCostRank => ModelRatings.Effective(this).CostRank;
    public string RatingSource => ModelRatings.Effective(this).Source;
    public string VerificationDisplay => AccountAvailabilityVerified ? "Account access verified by a successful run" : DiscoverySource;

    public int RoleRank(WorkKind kind) => PrimaryKinds.Contains(kind) ? 2 : !ManualRoleKinds.Contains(kind) && AutomaticPrimaryKinds.Contains(kind) ? 1 : 0;
    public string AllowedTaskTypes => string.Join(", ", Kinds.Select(kind => RoleRank(kind) switch
    {
        2 => $"{kind} (primary, manual)",
        1 => $"{kind} (primary, auto)",
        _ => $"{kind} (backup)"}));
}

public interface IModelCatalogStatus
{
    string ModelDiscoveryDetail { get; }
}

public sealed class ManagerSettings
{
    public bool VirtualOfficeEnabled { get; set; } = true;
    private int taskRetentionDays = 7;
    public int TaskRetentionDays { get => taskRetentionDays; set => taskRetentionDays = value is >= 1 and <= 3650 ? value : throw new ArgumentOutOfRangeException(nameof(TaskRetentionDays), "Task retention must be between 1 and 3650 days."); }
    public int MaxDailyCalls { get; set; } = 100;
    public bool AdaptiveParallelism { get; set; } = true;
    public int MinimumFreeMemoryMb { get; set; } = 1536;
    public RoutingPreference RoutingPreference { get; set; } = RoutingPreference.Balanced;
    public UsageLevel UsageLevel { get; set; } = UsageLevel.Auto;
    public bool UsePerformanceHistory { get; set; } = true;
    // Providers the router uses first. Others (qoder, antigravity, copilot...) only run a step when
    // none of these can: out of usage, not signed in, disabled or without the needed permission.
    // Local AI (Ollama) is always eligible. Empty = no tiers.
    public List<string> PrimaryProviders { get; set; } = ["codex", "claude"];
    // Per work type model order (see ModelRatings.PriorityFor). Off = lowest expected usage decides.
    public bool UsePriorityLists { get; set; } = true;
    // A review is done by a different provider than the one that wrote what it reviews, when one is usable.
    public bool ReviewByOtherModel { get; set; } = true;
    // Optional JSON ratings source refreshed automatically once a day (see ModelRatingsService).
    public string RatingsSourceUrl { get; set; } = "";
    // Results judged before this time are ignored by learning (Reset learning).
    public DateTimeOffset? LearningResetAt { get; set; }
    public TaskAutomationSettings Automation { get; set; } = new();
    public RoutingMode Mode { get; set; } = RoutingMode.Automatic;
    public int MaxSwitches { get; set; }
    // 0 means unlimited: a step keeps going until it is done, stalls, or the user stops it.
    public int MaxAttempts { get; set; } = 4;
    public const int MaxAttemptsLimit = 100;
    public bool UnlimitedAttempts => MaxAttempts == 0;
    public bool AttemptsUsedUp(WorkItem work) => MaxAttempts > 0 && work.WorkAttempts >= MaxAttempts;
    public string AttemptsDisplay => UnlimitedAttempts ? "unlimited" : MaxAttempts.ToString();
    public int TimeoutSeconds { get; set; } = 900;
    public int MaxOutputChars { get; set; } = 200_000;
    public int MaxContextChars { get; set; } = 40_000;
    public double LocalPassThreshold { get; set; } = 1.0;
    // When off, no AI CLI is called: not for tasks, detection, model listing, planning or refinement.
    // Local AI (Ollama) is not a CLI and stays available.
    public bool UseAiCli { get; set; } = true;
    public static bool IsCli(string? providerId) => providerId != null && providerId != "ollama";

    public static readonly string[] DefaultProviderIds = ["codex", "claude", "copilot", "gemini", "opencode", "freebuff", "commandcode", "qoder", "antigravity", "ollama"];
    public List<ProviderSettings> Providers { get; set; } = [..DefaultProviderIds.Select(id => new ProviderSettings { Id = id })];
}

public sealed record ProviderHealth(string Id, bool Installed, bool CanExecute, bool CanWrite, string Detail, string Authentication = "Unknown", string Usage = "Unknown", DateTimeOffset? CooldownUntil = null)
{
    // A launcher exists but the real CLI behind it is not installed (Copilot Desktop shim).
    public bool StandaloneCliMissing { get; init; }
    // Excluded by the "Use AI CLIs" setting; the CLI was not called.
    public bool ExcludedBySetting { get; init; }
}
// Machine-readable reason a task or step is blocked. Logic reads this, never the reason text.
public enum BlockCode { None, MissingWritePermission, NoUsableProvider, WrongAccess, WrongProject }
public sealed record Route(string ProviderId, string ModelId, string Reason, bool AutoModel = false);
public sealed record ExecutionRequest(ProjectInfo Project, WorkItem Work, string ModelId, string Context, int MaxOutputChars);
public sealed record AgentResult(FailureKind Failure, string Output, string Detail = "", int? ExitCode = null, TokenUsage? Usage = null)
{
    public bool Success => Failure == FailureKind.None;
}

public sealed record TaskLog(long Sequence, string WorkId, DateTimeOffset At, string Event, string Message);
public sealed record Handoff(string WorkId, DateTimeOffset At, string Reason, string OriginalRequest, string ProjectRoot, string ProjectRules, string Progress, string RemainingWork, string GitStatus, string GitDiff, string Tests, string Decisions);
public sealed record Evaluation(string ModelId, WorkKind Kind, int Passed, int Total, long Milliseconds, DateTimeOffset At, int Timeouts = 0, int Errors = 0)
{
    public double Accuracy => Total == 0 ? 0 : (double)Passed / Total;
}

public interface IAIProvider
{
    string Id { get; }

    Task<ProviderHealth> DetectAsync(ProviderSettings settings, CancellationToken token);
    Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken token);
    Task<AgentResult> ExecuteAsync(ExecutionRequest request, Action<string> output, CancellationToken token);
}

// Everything the manager persists, as one dependency. Services take this instead of casting a
// narrower store to optional interfaces at run time.
public interface IEmployeeStore : IManagerStore, IAutonomyStore, IModelPerformanceStore, IWorkInsightsStore, IAdvancedModelStore, IIntentLearningStore, ITaskLifecycleStore
{
}

public interface IManagerStore : IDisposable
{
    void SaveProject(ProjectInfo project);
    IReadOnlyList<ProjectInfo> Projects();
    void SaveWork(WorkItem work);
    IReadOnlyList<WorkItem> WorkItems();
    void Log(string workId, string eventName, string message);
    IReadOnlyList<TaskLog> Logs(string workId);
    void SaveHandoff(Handoff handoff);
    Handoff? LatestHandoff(string workId);
    void ReplaceIndex(string projectId, IReadOnlyList<IndexedFile> files);
    IReadOnlyList<IndexedFile> SearchFiles(string projectId, string query);
    void SaveSettings(ManagerSettings settings);
    ManagerSettings LoadSettings();
    void SaveEvaluation(Evaluation evaluation);
    IReadOnlyList<Evaluation> Evaluations();
    void RecoverInterruptedWork();
}

// Phase 3 contracts. No application control or publication is enabled by Phase 1.
public sealed record ToolManifest(string Id, string Name, string Version, string Executable, string SourcePath, string ConnectorVersion, string[] Capabilities, string[] Permissions, string InputSchema, string OutputSchema);
public interface IToolConnector
{
    ToolManifest Manifest { get; }

    Task<bool> HealthCheckAsync(CancellationToken token);
    Task<string> ExecuteAsync(string capability, string inputJson, CancellationToken token);
}

public sealed record PendingTaskAccess(string WorkId, string ProjectId, string Comment, DateTimeOffset CommentAt, AccessLevel PreviousAccess, string Reason, AccessLevel RequestedAccess = AccessLevel.WriteProject);
