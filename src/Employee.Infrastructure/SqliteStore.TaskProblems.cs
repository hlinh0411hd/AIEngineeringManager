using Employee.Core;

namespace Employee.Infrastructure;
public sealed partial class SqliteStore
{
    public void ReclassifyTaskProblems()
    {
        var all = WorkItems();
        var projects = Projects();
        var sessions = Sessions().ToDictionary(s => s.Id);
        foreach (var work in all.Where(w => w.State is WorkState.WaitingForPermission or WorkState.AwaitingApproval or WorkState.WaitingForProvider))
        {
            var session = work.SessionId != null ? sessions.GetValueOrDefault(work.SessionId) : null;
            var projectError = TaskProblems.ProjectProblem(work, projects, session);
            if (projectError != null)
                TaskProblems.Mark(work, WorkState.WrongProject, projectError);
            else if (work.State == WorkState.WaitingForPermission || new[]
            {
                "Implementation request has read-only execution access",
                "This step requests edits but only read access was approved",
                "Classification requested access above",
                "above its approved scope",
                "Codex needs ReadProject access",
                "project write permission is not enabled",
                "Codex reported a read-only session",
                "Cannot read Codex Windows sandbox settings"
            }.Any(t => work.Notes.Contains(t, StringComparison.OrdinalIgnoreCase)))
                TaskProblems.Mark(work, WorkState.WrongAccess, work.Notes);
            else
                continue;
            SaveWork(work);
            Log(work.Id, "StateDiagnosis", work.State + ": " + work.ProblemReason);
        }

        foreach (var work in all.Where(w => w.State == WorkState.WaitingForProvider && string.IsNullOrWhiteSpace(w.BlockReason)))
        {
            var execution = Logs(work.Id).LastOrDefault(l => l.Event == "Execution");
            if (execution == null)
                continue;
            var header = execution.Message.Split('\n')[0];
            if (!header.Contains("; RateLimit;", StringComparison.Ordinal))
                continue;
            work.BlockReason = "Last recorded run: " + ProviderFailureDetails.Describe(FailureKind.RateLimit, execution.Message) + ". Current provider reset time is not verified.";
            SaveWork(work);
        }

        foreach (var goal in all.Where(w => w.IsGoal && w.SessionId != null && w.State is not (WorkState.Completed or WorkState.Skipped or WorkState.Running)))
        {
            var blocked = all.Where(w => w.SessionId == goal.SessionId && !w.IsGoal && !w.IsCoordinator && (w.State is WorkState.WrongAccess or WorkState.WrongProject or WorkState.WaitingForProvider)).OrderBy(w => w.State == WorkState.WrongProject ? 0 : w.State == WorkState.WrongAccess ? 1 : 2).FirstOrDefault();
            if (blocked == null)
                continue;
            TaskProblems.Mark(goal, blocked.State, blocked.ProblemReason);
            SaveWork(goal);
        }
    }
}
