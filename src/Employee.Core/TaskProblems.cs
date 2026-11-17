namespace Employee.Core;
public static class TaskProblems
{
    public static void Mark(WorkItem work, WorkState state, string reason)
    {
        work.State = state;
        work.BlockReason = reason;
        work.BlockCode = state switch
        {
            WorkState.WrongProject => BlockCode.WrongProject,
            WorkState.WrongAccess => BlockCode.WrongAccess,
            _ => work.BlockCode
        };
        work.Notes = reason;
        work.QueuedAt = null;
    }

    public static string? ProjectProblem(WorkItem work, IReadOnlyList<ProjectInfo> projects, WorkSession? session = null)
    {
        if (work.IsExternal)
        {
            if (work.Access != AccessLevel.TextOnly)
                return "External tasks do not have project file access. Choose a project for ReadProject or WriteProject work.";
            if (session != null && session.ProjectIds.Count > 0)
                return "External task is not part of a project-scoped session.";
            return null;
        }

        var project = projects.FirstOrDefault(p => p.Id == work.ProjectId);
        if (project == null)
            return "WrongProject: project ID is not registered: " + work.ProjectId + ". Choose the correct project in task details.";
        if (!Directory.Exists(project.Root))
            return "WrongProject: project folder is missing or unavailable: " + project.Root + ". Restore it or choose another project.";
        if (session != null && !session.ProjectIds.Contains(work.ProjectId))
            return "WrongProject: task project " + project.Name + " is outside the session's approved projects. Correct the task/session project before resuming.";
        return null;
    }

    public static bool MissingWritePermission(WorkItem work, ManagerSettings settings, IReadOnlyList<ProviderHealth> health, Route? choice = null)
    {
        if (work.Access != AccessLevel.WriteProject)
            return false;
        var candidates = settings.Providers.Where(p => p.Enabled && (choice == null || p.Id == choice.ProviderId) && health.Any(h => h.Id == p.Id && h.CanExecute) && p.Models.Any(m => !m.Blocked && (choice == null || choice.AutoModel || m.Id == choice.ModelId) && (m.Kinds.Contains(work.Kind) || (settings.Mode != RoutingMode.Manual && work.Kind == WorkKind.Review && m.Kinds.Contains(WorkKind.Coding))))).ToArray();
        return candidates.Length > 0 && candidates.All(p => !p.AllowProjectWrite || health.First(h => h.Id == p.Id).CanWrite == false);
    }
}
