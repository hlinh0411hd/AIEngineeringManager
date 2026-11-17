using System.Text;
using Employee.Core;

namespace Employee.Infrastructure;
public static class DailyReportService
{
    public static string Create(IEmployeeStore store, DateTime day)
    {
        var date = day.Date;
        var all = store.WorkItems();
        var work = all.Where(w => !w.IsCoordinator && (w.IsGoal || w.ParentTaskId == null)).ToArray();
        var active = all.Where(w => store.Logs(w.Id).Any(l => l.At.LocalDateTime.Date == date)).Select(w => w.Id).ToHashSet();
        var text = new StringBuilder($"# Report {date:yyyy-MM-dd} (local computer time)\n\n");
        text.AppendLine("Calls reserved by the manager today: " + store.CallsOnDay(date.ToString("yyyy-MM-dd")) + ". This is not the provider's token count, cost or quota.\n");
        foreach (var project in store.Projects())
        {
            var tasks = work.Where(w => w.ProjectId == project.Id).Where(w => w.CreatedAt.LocalDateTime.Date == date || active.Contains(w.Id) || (w.SessionId != null && all.Any(step => step.SessionId == w.SessionId && active.Contains(step.Id)))).ToArray();
            if (tasks.Length == 0)
                continue;
            text.AppendLine("## " + project.Name);
            foreach (var task in tasks)
            {
                text.AppendLine($"- [{task.State}] {task.Request.Replace('\n', ' ')}");
                text.AppendLine($"  AI: {task.ProviderId ?? "not chosen"} / {task.ModelDisplay}; total attempts {task.Attempts}; verification {task.Verification}.");
            }

            text.AppendLine();
        }

        text.AppendLine("States are current; results are not confirmed correct and no tests are run. Compiled by code, no AI call.");
        return text.ToString();
    }
}
