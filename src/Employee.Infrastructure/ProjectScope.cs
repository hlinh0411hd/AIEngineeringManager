using Employee.Core;

namespace Employee.Infrastructure;

/// <summary>
/// Estimates actual work scope from the project's indexed files, design docs, and prior milestones
/// rather than relying solely on the user's prompt wording.
/// </summary>
public static class ProjectScope
{
    public sealed record Snapshot(
        int IndexedFiles,
        int ModuleCount,
        string? DesignDocPath,
        long DesignDocBytes,
        int UnfinishedMilestones);

    private static readonly string[] DesignCandidates =
    [
        "docs/DESIGN.md",
        "DESIGN.md",
        "ROADMAP.md",
        "SPEC.md",
        "TODO.md"
    ];

    private static readonly string[] BroadContinuationSignals =
    [
        "tiep", "lam tiep", "phat trien tiep", "hoan thien", "nang cap", "cai thien", "mo rong",
        "bo sung", "tinh nang", "chuc nang", "day du", "du cac phan",
        "continue", "finish", "complete", "upgrade", "improve", "extend", "feature", "full"
    ];

    public static Snapshot Assess(IEmployeeStore store, IAutonomyStore autonomy, string projectId)
    {
        var project = store.Projects().FirstOrDefault(p => p.Id == projectId);
        var files = string.IsNullOrWhiteSpace(projectId) ? [] : store.SearchFiles(projectId, "");
        var modules = files
            .Select(f => f.Path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length > 1)
            .Select(parts => parts[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        string? designPath = null;
        long designBytes = 0;
        if (project != null && Directory.Exists(project.Root))
        {
            foreach (var candidate in DesignCandidates)
            {
                var full = Path.Combine(project.Root, candidate.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(full))
                {
                    designPath = candidate;
                    designBytes = new FileInfo(full).Length;
                    break;
                }
            }
        }

        var unfinishedMilestones = autonomy.Sessions()
            .Where(s => s.ProjectIds.Contains(projectId) && s.Phases.Count > 0 && !s.GoalVerified)
            .Sum(s => Math.Max(0, s.Phases.Count - s.AcceptedScopes.Count(a => a.StartsWith("phase:", StringComparison.Ordinal))));

        return new(files.Count, modules, designPath, designBytes, unfinishedMilestones);
    }

    public static bool ShouldPlan(IEmployeeStore store, IAutonomyStore autonomy, WorkItem goal, string request, PlanningPolicy policy)
    {
        if (policy == PlanningPolicy.Never || goal.Kind is WorkKind.Classification or WorkKind.Extraction)
            return false;
        if (UnifiedTaskService.NeedsPlan(request, goal.Kind, policy))
            return true;

        // Explicitly light/local UI tweaks (buttons, labels, colors, typos) skip planning.
        var sample = new WorkItem
        {
            Request = request,
            Kind = goal.Kind,
            Access = goal.Access,
            Effort = goal.Effort
        };
        if (WorkComplexity.Score(sample) < WorkComplexity.LightBelow)
            return false;

        var project = store.Projects().FirstOrDefault(p => p.Id == goal.ProjectId);
        if (project == null)
            return false;

        var snapshot = Assess(store, autonomy, goal.ProjectId);
        var normalized = " " + TaskIntent.Normalize(request) + " ";
        var isBroadOrContinuation = BroadContinuationSignals.Any(s => normalized.Contains(" " + s + " ", StringComparison.Ordinal));

        // Empty/new project with an implementation request needs scaffolding and a plan.
        if (snapshot.IndexedFiles <= 3 && TaskIntent.RequiresImplementation(request, project))
            return true;

        // Project has a design/roadmap doc or unfinished milestones and the user asks to continue/extend/improve.
        if ((snapshot.DesignDocPath != null || snapshot.UnfinishedMilestones > 0) && isBroadOrContinuation)
            return true;

        // Substantial multi-module project with a broad feature/continuation request.
        if (snapshot.IndexedFiles >= 15 && snapshot.ModuleCount >= 3 && isBroadOrContinuation && TaskIntent.RequiresImplementation(request, project))
            return true;

        return false;
    }

    public static string DescribeForPlanner(IEmployeeStore store, IAutonomyStore autonomy, string projectId)
    {
        var snapshot = Assess(store, autonomy, projectId);
        var parts = new List<string>
        {
            $"Indexed project files: {snapshot.IndexedFiles} across {snapshot.ModuleCount} top-level folder(s)."
        };
        if (snapshot.DesignDocPath != null)
            parts.Add($"Design/spec document found: {snapshot.DesignDocPath} ({snapshot.DesignDocBytes:N0} bytes) — inspect it to see what is already built vs still missing.");
        if (snapshot.UnfinishedMilestones > 0)
            parts.Add($"Prior sessions in this project have {snapshot.UnfinishedMilestones} unverified milestone(s).");
        parts.Add("ADAPTIVE SIZING: Inspect the actual files and design doc before sizing the plan. If the remaining work is small (1-2 files, localized change), return 1-2 concrete steps and omit \"phases\". If the remaining work spans multiple systems, missing assets/audio/UI, or unimplemented sections of a design doc, return \"phases\" (2-20 milestones of 2-6 steps each) and put ONLY phase 1 in \"tasks\".");
        return "\nPROJECT SCOPE SNAPSHOT:\n" + string.Join("\n", parts);
    }
}
