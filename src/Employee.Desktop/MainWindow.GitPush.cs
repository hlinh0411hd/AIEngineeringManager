using Employee.Core;
using Employee.Infrastructure;

namespace Employee.Desktop;

// When the user confirms a task as complete, the project is committed and pushed if it has a Git repository with a remote.
public partial class MainWindow
{
    private readonly GitAutoPush gitAutoPush = new(new ProcessRunner());

    private void PushCompletedTask(WorkItem work)
    {
        if (work.IsExternal || closing || shuttingDown)
            return;
        var project = store.Projects().FirstOrDefault(p => p.Id == work.ProjectId);
        if (project == null)
            return;
        var id = work.Id;
        var request = work.Request;
        _ = Task.Run(async () =>
        {
            GitPushResult result;
            try
            {
                result = await gitAutoPush.RunAsync(project, request);
            }
            catch (Exception ex)
            {
                result = new(GitPushOutcome.Failed, "Git push failed: " + ex.Message);
            }

            if (result.Outcome == GitPushOutcome.Skipped)
                return;
            await Dispatcher.BeginInvoke(() =>
            {
                if (closing || shuttingDown)
                    return;
                store.Log(id, "GitPush", result.Message);
                Status.Text = result.Message;
            });
        });
    }
}
