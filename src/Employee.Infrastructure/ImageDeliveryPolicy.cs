using Employee.Core;

namespace Employee.Infrastructure;
/// <summary>Internal image production steps share the existing project write grant.</summary>
internal static class ImageDeliveryPolicy
{
    internal static bool Generates(WorkItem work) => WorkCapabilities.GeneratesImage(work);
    internal static bool Continues(WorkItem work, WorkSession session, IReadOnlyList<WorkItem> tasks, TaskAutomationSettings settings)
    {
        if (!settings.AutoApplyGeneratedImages || session.AccessCeiling != AccessLevel.WriteProject || work.IsCoordinator || work.IsGoal || work.SessionId != session.Id || !session.ProjectIds.Contains(work.ProjectId))
            return false;
        var projectTasks = tasks.Where(w => w.SessionId == session.Id && w.ProjectId == work.ProjectId && !w.IsGoal && !w.IsCoordinator).ToArray();
        var imageIds = projectTasks.Where(Generates).Select(w => w.Id).ToHashSet();
        if (imageIds.Count == 0)
            return false;
        // Include preparation and downstream import/integration, only along declared dependencies.
        var seen = new HashSet<string>();
        var pending = new Stack<string>();
        pending.Push(work.Id);
        while (pending.TryPop(out var id))
        {
            if (!seen.Add(id))
                continue;
            if (imageIds.Contains(id))
                return true;
            foreach (var dependency in projectTasks.FirstOrDefault(w => w.Id == id)?.DependsOn ?? [])
                pending.Push(dependency);
        }

        seen.Clear();
        pending.Push(work.Id);
        while (pending.TryPop(out var id))
        {
            if (!seen.Add(id))
                continue;
            if (imageIds.Contains(id))
                return true;
            foreach (var consumer in projectTasks.Where(w => w.DependsOn.Contains(id)))
                pending.Push(consumer.Id);
        }

        return false;
    }
}
