namespace Employee.Core;
public enum SessionState
{
    Draft,
    Planning,
    Running,
    Paused,
    WaitingForUser,
    Completed,
    Stopped,
    BudgetReached
}

public enum DecisionState
{
    Pending,
    Approved,
    Rejected
}

public enum VerificationState
{
    Untested,
    Implemented,
    PartiallyTested,
    Verified
}

public enum ChangeImpact
{
    Minor,
    Small,
    Major,
    Critical
}

public enum TestJobState
{
    Pending,
    Running,
    Passed,
    Failed,
    Interrupted
}

public enum PlanningPolicy
{
    Auto,
    Always,
    Never
}

public sealed class TaskAutomationSettings
{
    public bool UseToolSkills { get; set; } = true;
    public bool AutoApplyGeneratedImages { get; set; } = true;
    public int MaxParallelAgents { get; set; } = 2;
    public PlanningPolicy Planning { get; set; } = PlanningPolicy.Auto;
    public Route? Coordinator { get; set; }
    // 0 = no limit, the default since limits version 1 (time, AI calls, open steps, AI switches).
    public int DurationMinutes { get; set; }
    public int MaxCalls { get; set; }
    public int MaxTasks { get; set; }
    public int MaxDepth { get; set; } = 2;
    public int MaxSwitches { get; set; }
    public int LimitsVersion { get; set; }
    public bool AllowDelegation { get; set; } = true;
    public bool ContinueSteps { get; set; } = true;
    public bool Away { get; set; } = true;
    public bool BatchTests { get; set; } = true;
    public int AutoAwayMinutes { get; set; }
    // The planner may pick enabled connected tools (with task automation allowed) on its own when a
    // goal needs what they produce. Off: a tool is used only when the user names it.
    public bool PlannerChoosesTools { get; set; } = true;
    // After each milestone and at the end, an independent tester step runs the project against the
    // plan's acceptance criteria; failures become fix steps until they pass or the rounds run out.
    public bool VerifyAcceptance { get; set; } = true;
    public int MaxVerifyRounds { get; set; } = AcceptanceCheck.DefaultRounds;
    // Large tasks: after a milestone passes its tester, the run waits for the user to approve it
    // before the next milestone starts, so the milestone can still be changed and tested again.
    public bool SignOffMilestones { get; set; } = true;
}

public sealed class WorkSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Goal { get; set; } = "";
    public string? RootWorkId { get; set; }
    public bool AutoContinueSteps { get; set; }
    public int BaseCallsUsed { get; set; }
    public int BaseSwitchesUsed { get; set; }
    public List<string> ProjectIds { get; set; } = [];
    public AccessLevel AccessCeiling { get; set; } = AccessLevel.ReadProject;
    public RoutingMode Mode { get; set; } = RoutingMode.Automatic;
    public Route? Coordinator { get; set; }
    public bool Away { get; set; }
    public int AutoAwayMinutes { get; set; }
    public bool AllowDelegation { get; set; }
    public bool RunTestsAtBatchEnd { get; set; } = true;
    public bool RunTestsAtTaskEnd { get; set; }
    // 0 = no limit (the default): a large task runs until its goal check says it is done.
    public int DurationMinutes { get; set; }
    public int MaxCalls { get; set; }
    public int MaxTasks { get; set; }
    public int MaxDelegationDepth { get; set; } = 2;
    public List<PlanPhase> Phases { get; set; } = [];
    public int PhaseIndex { get; set; }
    public int GoalChecks { get; set; }
    public bool GoalVerified { get; set; }
    public string? LastGoalGaps { get; set; }
    // Automatic build/test check after code steps: when it last ran, the last failure, fix rounds.
    public DateTimeOffset? LastCheckAt { get; set; }
    public string? LastCheckFailure { get; set; }
    public int CheckFixes { get; set; }
    // Definition of done for the whole goal (planner-written, checkable statements) and the state of
    // the acceptance loop: verify the current scope (a phase, then the whole goal), fix what fails,
    // verify again, until it passes or the rounds for that scope run out.
    public List<string> Acceptance { get; set; } = [];
    public bool VerifyAcceptance { get; set; }
    public int MaxVerifyRounds { get; set; } = AcceptanceCheck.DefaultRounds;
    public string? VerifyScope { get; set; }
    public int VerifyRounds { get; set; }
    public int Verifications { get; set; }
    public string? PendingVerifyId { get; set; }
    public string? LastVerifyFailures { get; set; }
    public List<string> AcceptedScopes { get; set; } = [];
    // Milestones the user approved (sign-off) before the next one started.
    public bool SignOffMilestones { get; set; } = true;
    public List<string> SignedOffScopes { get; set; } = [];
    public List<string> KnownIssues { get; set; } = [];
    // Criteria a tester already handed to the person (cannot be exercised on this machine).
    public List<string> HumanChecks { get; set; } = [];
    // Browser screenshots of the last verification round ("desktop 1280x800: .aiemployee/screenshots/...").
    public List<string> LastScreenshots { get; set; } = [];
    // Set when a scope still fails after its last round: the run stops for the user's review.
    public string? AcceptanceStalled { get; set; }
    // The step limit bounds work that is still open. Finished and skipped steps stay in the history
    // but do not count, so a task can keep taking new updates for as long as the user sends them.
    public static int OpenSteps(IEnumerable<WorkItem> steps) => steps.Count(w => w.State is not (WorkState.Completed or WorkState.Skipped));
    public bool StepLimitReached(IEnumerable<WorkItem> steps, int adding = 1) => MaxTasks > 0 && OpenSteps(steps) + adding > MaxTasks;
    public int MaxSwitches { get; set; }
    public int RemainingCalls => MaxCalls == 0 ? int.MaxValue : Math.Max(0, MaxCalls - CallsUsed);
    public string CallBudgetDisplay => $"{CallsUsed}/{(MaxCalls == 0 ? "∞" : MaxCalls.ToString())}";
    public int CallsUsed { get; set; }
    public int SwitchesUsed { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? Deadline { get; set; }
    public SessionState State { get; set; } = SessionState.Draft;
    public string Notes { get; set; } = "Not started.";
}

public sealed record PlannedTask(string Key, string ProjectId, string Request, WorkKind Kind, AccessLevel Access, string[] DependsOn, Route? PreferredRoute = null, int Priority = 50, string? SkillId = null, ToolTaskSpec? Tool = null, WorkEffort Effort = WorkEffort.Auto, int? Difficulty = null, string? Capability = null,
    [property: System.Text.Json.Serialization.JsonConverter(typeof(TextOrListConverter))] string? Acceptance = null);

// Planners write a step's acceptance as one sentence or as a list; both are read as text.
public sealed class TextOrListConverter : System.Text.Json.Serialization.JsonConverter<string?>
{
    public override string? Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case System.Text.Json.JsonTokenType.Null:
                return null;
            case System.Text.Json.JsonTokenType.String:
                return reader.GetString();
            case System.Text.Json.JsonTokenType.StartArray:
                var items = new List<string>();
                while (reader.Read() && reader.TokenType != System.Text.Json.JsonTokenType.EndArray)
                    if (reader.TokenType == System.Text.Json.JsonTokenType.String && reader.GetString() is { Length: > 0 } item)
                        items.Add(item);
                    else
                        reader.Skip();
                return items.Count == 0 ? null : string.Join("; ", items);
            default:
                reader.Skip();
                return null;
        }
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, string? value, System.Text.Json.JsonSerializerOptions options)
    {
        if (value == null)
            writer.WriteNullValue();
        else
            writer.WriteStringValue(value);
    }
}
// A large goal is split into phases: only the current phase is planned in detail; each next one is
// planned from the results so far. Done/Phase are the coordinator's answer to a goal check.
// Acceptance: checkable statements that say when this milestone is acceptable.
public sealed record PlanPhase(string Name, string Goal, string[]? Acceptance = null);
public sealed record WorkPlan(string Summary, PlannedTask[] Tasks, PlanPhase[]? Phases = null, bool? Done = null, int? Phase = null, string[]? Acceptance = null);
public sealed class PlanRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SessionId { get; set; } = "";
    public string SourceWorkId { get; set; } = "";
    public WorkPlan Plan { get; set; } = new("", []);
    public DecisionState State { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record DelegationRequest(string ProviderId, string ModelId, string Reason, string? Request = null);
public sealed class PendingDecision
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SessionId { get; set; } = "";
    public string WorkId { get; set; } = "";
    public string Kind { get; set; } = "Review";
    public string? PlanId { get; set; }
    public string Message { get; set; } = "";
    public DelegationRequest? Delegation { get; set; }
    public DecisionState State { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ProjectTestProfile
{
    public string[] SourcePaths { get; set; } = [];
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ProjectId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Executable { get; set; } = "";
    public string[] Arguments { get; set; } = [];
    public bool Enabled { get; set; }
    public bool FullValidation { get; set; }
    public int TimeoutSeconds { get; set; } = 300;
}

public sealed class TestJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ProjectId { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string ProfileSignature { get; set; } = "";
    public string Snapshot { get; set; } = "";
    public List<string> Paths { get; set; } = [];
    public List<string> WorkIds { get; set; } = [];
    public ChangeImpact Impact { get; set; }
    public TestJobState State { get; set; }
    public string Output { get; set; } = "";
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

public interface IAutonomyStore
{
    void SaveSession(WorkSession session);
    IReadOnlyList<WorkSession> Sessions();
    void SavePlan(PlanRecord plan);
    IReadOnlyList<PlanRecord> Plans(string sessionId);
    void ApprovePlanAtomically(string planId, IReadOnlyList<WorkItem> tasks);
    void SaveDecision(PendingDecision decision);
    IReadOnlyList<PendingDecision> Decisions(string sessionId);
    void SaveTestProfile(ProjectTestProfile profile);
    IReadOnlyList<ProjectTestProfile> TestProfiles();
    void SaveTestJob(TestJob job);
    IReadOnlyList<TestJob> TestJobs();
}

public static class TaskDependencies
{
    public static void Validate(IReadOnlyList<WorkItem> tasks)
    {
        var map = tasks.ToDictionary(t => t.Id);
        var visited = new HashSet<string>();
        var active = new HashSet<string>();
        void Visit(string id)
        {
            if (!map.TryGetValue(id, out var task))
                throw new InvalidOperationException("A dependency task does not exist in this session.");
            if (active.Contains(id))
                throw new InvalidOperationException("A task dependency cycle was detected.");
            if (!visited.Add(id))
                return;
            active.Add(id);
            foreach (var dependency in task.DependsOn)
                Visit(dependency);
            active.Remove(id);
        }

        foreach (var task in tasks)
            Visit(task.Id);
    }

    public static bool Ready(WorkItem work, IReadOnlyList<WorkItem> tasks) => work.DependsOn.All(id => tasks.Any(t => t.Id == id && t.State is WorkState.Completed or WorkState.Skipped));
}
