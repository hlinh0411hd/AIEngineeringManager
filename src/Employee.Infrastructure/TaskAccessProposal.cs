using Employee.Core;

namespace Employee.Infrastructure;
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record TaskAccessRequest(string TaskId, string ProjectId, string Comment, string Access, string Reason);
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record TaskAccessEnvelope(TaskAccessRequest? TaskAccess);
// Output is a request, never a grant. Only persisted user comments may justify it.
public static class TaskAccessProposal
{
    public static bool AllowsRequest(WorkItem goal, ProjectInfo project, AccessLevel access)
    {
        if (access == AccessLevel.WriteProject)
            return HasWriteRequest(goal, project);
        var comment = goal.Comments.LastOrDefault(c => !c.IsAssistant);
        return access == AccessLevel.ReadProject && comment != null && TaskIntent.TryResolve(comment.Text, out _, out var needed, project) && needed >= AccessLevel.ReadProject;
    }

    public static bool HasWriteRequest(WorkItem goal, ProjectInfo project)
    {
        var comment = goal.Comments.LastOrDefault(c => !c.IsAssistant);
        return comment != null && !TaskIntent.IsReadOnly(comment.Text) && TaskIntent.RequiresImplementation(comment.Text, project);
    }

    public static string DisplayResult(IEmployeeStore store, WorkItem goal)
    {
        if (!goal.AccessResult.StartsWith("Permission proposal rejected:", StringComparison.Ordinal) || goal.SessionId == null)
            return goal.AccessResult;
        var planner = store.WorkItems().Where(w => w.SessionId == goal.SessionId && w.IsCoordinator).Where(w => store.Logs(w.Id).Any(l => l.Event == "TaskAccessRejected")).OrderByDescending(w => w.CreatedAt).FirstOrDefault();
        if (planner == null)
            return goal.AccessResult;
        return "Previous planning response: parent task has " + goal.Access + "; planning agent intentionally uses " + planner.Access + ". The AI requested editing access instead of returning a plan. This is a planning-response error, not proof that the project folder denies writes. Task permissions were not changed.";
    }

    public static bool Apply(IEmployeeStore store, WorkItem work, string output)
    {
        var proposal = AgentProtocol.Parse<TaskAccessEnvelope>(output)?.TaskAccess;
        var json = AgentProtocol.Parse<System.Text.Json.Nodes.JsonObject>(output);
        // A valid plan/result can mention the protocol in nested data without being a permission request.
        var envelope = json?.Any(field => field.Key.Equals("taskAccess", StringComparison.OrdinalIgnoreCase)) == true;
        if (proposal == null && !envelope && (json != null || !output.Contains("\"taskAccess\"", StringComparison.Ordinal)))
            return false;
        var goal = work.SessionId != null ? store.WorkItems().SingleOrDefault(w => w.Id == store.Sessions().Single(s => s.Id == work.SessionId).RootWorkId) : work;
        if (work.IsCoordinator)
        {
            var reason = "Planning/classification response error: this agent must return the requested plan or classification, not request editing access. Its " + work.Access + " access is intentional; parent task access is " + (goal?.Access.ToString() ?? "unchanged") + ". No permissions changed and this is not a filesystem write-denial result.";
            TaskProblems.Mark(work, WorkState.Failed, reason);
            if (goal != null)
            {
                goal.AccessResult = reason;
                store.SaveWork(goal);
            }

            store.Log(work.Id, "TaskAccessRejected", reason);
            return true;
        }

        if (proposal == null)
        {
            TaskProblems.Mark(work, WorkState.WrongAccess, "Permission proposal rejected: malformed taskAccess protocol or unsupported fields. No permissions changed.");
            store.Log(work.Id, "TaskAccessRejected", work.Notes);
            return true;
        }

        var project = store.Projects().Single(p => p.Id == work.ProjectId);
        var comment = goal?.Comments.LastOrDefault(c => !c.IsAssistant);
        var parsed = Enum.TryParse<AccessLevel>(proposal.Access, out var requested) && Enum.IsDefined(requested) && proposal.Access == requested.ToString();
        var valid = parsed && !work.IsCoordinator && goal != null && goal.ProjectId == work.ProjectId && proposal.TaskId == work.Id && proposal.ProjectId == work.ProjectId && requested > work.Access && requested > goal.Access && !string.IsNullOrWhiteSpace(proposal.Reason) && proposal.Reason.Length <= 2000 && comment != null && !(goal!.AccessEditedAt >= comment.At) && proposal.Comment == comment.Text && AllowsRequest(goal, project, requested);
        if (!valid)
        {
            var detail = proposal.TaskId != work.Id || proposal.ProjectId != work.ProjectId ? "task/project ID does not match this execution step" : !parsed ? "unsupported access level" : requested <= work.Access ? "requested access is already available to this step" : goal != null && requested <= goal.Access ? "the parent already grants " + goal.Access + ", but this step is restricted to " + work.Access + ". A read-only step must not expand its own scope; editing belongs in an approved implementation step" : comment == null ? "no persisted user comment authorizes the change" : proposal.Comment != comment.Text ? "the proposal does not quote the latest user comment" : goal?.AccessEditedAt >= comment.At ? "a manual access edit supersedes this comment" : string.IsNullOrWhiteSpace(proposal.Reason) || proposal.Reason.Length > 2000 ? "a nonempty reason up to 2000 characters is required" : "the latest user comment does not authorize this access change";
            TaskProblems.Mark(work, WorkState.WrongAccess, "Permission proposal rejected: " + detail + ". Parent access: " + (goal?.Access.ToString() ?? "unknown") + "; step access: " + work.Access + ". No permissions changed.");
            if (goal != null)
            {
                goal.AccessResult = work.Notes;
                store.SaveWork(goal);
                if (goal.Id == work.Id)
                    work.AccessResult = goal.AccessResult;
            }

            store.Log(work.Id, "TaskAccessRejected", work.Notes);
            return true;
        }

        // Persist a request only. The current session and runtime retain their original access.
        var pending = new PendingTaskAccess(work.Id, work.ProjectId, comment!.Text, comment.At, goal!.Access, proposal.Reason, requested);
        if (goal.PendingAccess != pending)
        {
            goal.PendingAccess = pending;
            goal.AccessResult = $"Pending {requested}; applies after the session stops, on Run / resume. Reason: " + proposal.Reason;
            store.SaveWork(goal);
        }

        if (work.Id == goal.Id)
        {
            work.PendingAccess = goal.PendingAccess;
            work.AccessResult = goal.AccessResult;
        }

        TaskProblems.Mark(work, WorkState.WrongAccess, goal.AccessResult);
        return true;
    }
}
