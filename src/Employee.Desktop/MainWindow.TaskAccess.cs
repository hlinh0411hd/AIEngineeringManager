using Employee.Infrastructure;
using System.Windows;
using Employee.Core;

namespace Employee.Desktop;
public partial class MainWindow
{
    private void RefreshTaskProjectAccess(WorkItem work)
    {
        work = unified.GoalFor(work);
        var projects = store.Projects();
        DetailAccessStatus.Text = "Current access: " + work.Access + "\n" + TaskAccessProposal.DisplayResult(store, work);
        var options = projects.Prepend(new ProjectInfo("", "Outside projects (text only)", "")).ToArray();
        DetailProjectChoice.ItemsSource = options;
        DetailProjectChoice.SelectedItem = options.FirstOrDefault(p => p.Id == work.ProjectId);
        DetailProjectPath.Text = work.IsExternal ? "External task: no project files are available." : "Current project: " + (projects.FirstOrDefault(p => p.Id == work.ProjectId)?.Root ?? "Missing project: " + work.ProjectId);
        DetailProjectChoice.SelectionChanged -= DetailProjectChanged;
        DetailProjectChoice.SelectionChanged += DetailProjectChanged;
    }

    private void DetailProjectChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (DetailProjectChoice.SelectedItem is ProjectInfo project)
            DetailProjectPath.Text = project.Root;
    }

    private async void SaveTaskProjectAccess(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        var goal = unified.GoalFor(SelectedWork());
        TaskIdleOnly(goal);
        if (DetailProjectChoice.SelectedItem is not ProjectInfo project)
            throw new InvalidOperationException("Choose a registered project.");
        if (project.Id == "")
            throw new InvalidOperationException("External tasks use fixed TextOnly access. Choose a registered project to enable file access.");
        if (project.Id == goal.ProjectId)
            return Task.CompletedTask;
        store.UpdateTaskAccess(goal.Id, project.Id, AccessLevel.WriteProject);
        RefreshTasks(goal.Id);
        Status.Text = "Project saved. Run will start from the beginning; previous execution history archived.";
        return Task.CompletedTask;
    });
}
