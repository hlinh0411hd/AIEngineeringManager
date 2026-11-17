using System.Windows;
using System.Windows.Controls;
using Employee.Core;

namespace Employee.Desktop;
public partial class MainWindow
{
    private readonly HashSet<string> selectedDeletionTasks = new(StringComparer.Ordinal);
    private void UpdateTaskDeletionSelection()
    {
        var visible = TaskGrid.Items.OfType<WorkItem>().ToArray();
        var count = visible.Count(w => selectedDeletionTasks.Contains(w.Id));
        SelectedTaskCount.Text = $"{count} selected";
        DeleteSelectedTasksButton.IsEnabled = count > 0;
        CompleteSelectedTasksButton.IsEnabled = visible.Any(w => selectedDeletionTasks.Contains(w.Id) && !TaskOutcomes.IsClosed(w.State) && w.State != WorkState.Running);
        CloseSelectedTasksButton.IsEnabled = visible.Any(w => selectedDeletionTasks.Contains(w.Id) && !TaskOutcomes.IsClosed(w.State) && w.State != WorkState.Running);
        SelectVisibleTasks.IsEnabled = visible.Length > 0;
        SelectVisibleTasks.IsChecked = count == 0 ? false : count == visible.Length ? true : null;
    }

    private void TaskDeleteCheckLoaded(object sender, RoutedEventArgs e) => SyncTaskDeleteCheck((CheckBox)sender);
    private void TaskDeleteCheckContextChanged(object sender, DependencyPropertyChangedEventArgs e) => SyncTaskDeleteCheck((CheckBox)sender);
    private void SyncTaskDeleteCheck(CheckBox check) => check.IsChecked = check.DataContext is WorkItem task && selectedDeletionTasks.Contains(task.Id);
    private void TaskDeleteCheckClicked(object sender, RoutedEventArgs e)
    {
        var check = (CheckBox)sender;
        if (check.DataContext is not WorkItem task)
            return;
        if (check.IsChecked == true)
            selectedDeletionTasks.Add(task.Id);
        else
            selectedDeletionTasks.Remove(task.Id);
        UpdateTaskDeletionSelection();
        e.Handled = true;
    }

    private void SelectVisibleTasksClicked(object sender, RoutedEventArgs e)
    {
        var select = SelectVisibleTasks.IsChecked == true;
        selectedDeletionTasks.Clear();
        if (select)
            selectedDeletionTasks.UnionWith(TaskGrid.Items.OfType<WorkItem>().Select(w => w.Id));
        TaskGrid.Items.Refresh();
        UpdateTaskDeletionSelection();
    }

    private void DeleteSelectedTasks(object sender, RoutedEventArgs e) => ConfirmDeleteTasks(TaskGrid.Items.OfType<WorkItem>().Where(w => selectedDeletionTasks.Contains(w.Id)).ToArray());
    private async void CompleteSelectedTasks(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        var ids = TaskGrid.Items.OfType<WorkItem>().Where(w => selectedDeletionTasks.Contains(w.Id)).Select(w => w.Id).ToHashSet(StringComparer.Ordinal);
        var tasks = store.WorkItems().Where(w => ids.Contains(w.Id)).ToArray();
        var completed = 0;
        var candidates = tasks.Where(w => !TaskOutcomes.IsClosed(w.State) && w.State != WorkState.Running).ToArray();
        var skipped = tasks.Length - candidates.Length;
        var failures = new List<string>();
        // One question for all selected tasks that still have open steps or decisions.
        var unfinished = candidates.Count(w => w.ToolWorkflowId == null && (w.State != WorkState.NeedsReview || !unified.CanComplete(w)));
        if (unfinished > 0 && MessageBox.Show(this, $"{unfinished} of the selected task(s) still have unfinished steps or waiting decisions.\nThey will be closed and those tasks marked complete. Continue?",
                "Complete anyway", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return System.Threading.Tasks.Task.CompletedTask;
        foreach (var task in candidates)
        {
            try
            {
                CompleteReviewedTask(task, askFirst: false);
                selectedDeletionTasks.Remove(task.Id);
                completed++;
            }
            catch (Exception ex)
            {
                failures.Add($"{task.Request}: {ex.Message}");
            }
        }

        RefreshTasks();
        Status.Text = $"Completed {completed} tasks. Skipped {skipped} running or already finished tasks. Failed {failures.Count}.";
        if (failures.Count > 0)
            MessageBox.Show(this, Status.Text + "\n" + string.Join("\n", failures), "Task completion errors", MessageBoxButton.OK, MessageBoxImage.Warning);
        return System.Threading.Tasks.Task.CompletedTask;
    });
    private void ConfirmDeleteTasks(IReadOnlyList<WorkItem> tasks)
    {
        if (tasks.Count == 0)
            return;
        var description = tasks.Count == 1 ? $"Delete task: {tasks[0].Request}?" : $"Delete {tasks.Count} selected tasks?";
        if (MessageBox.Show(this, description + "\nThis also deletes their steps, sessions and logs. This cannot be undone.", "Confirm task deletion", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        var deleted = 0;
        var failures = new List<string>();
        try
        {
            var removed = store.DeleteTasks(tasks.Select(task => task.Id));
            selectedDeletionTasks.ExceptWith(removed);
            deleted = tasks.Count(task => removed.Contains(task.Id));
            if (deleted != tasks.Count)
                failures.Add("Some selected tasks no longer exist; refresh the list.");
        }
        catch (Exception ex)
        {
            failures.Add(ex.Message);
        }

        try
        {
            RefreshTasks();
        }
        catch (Exception ex)
        {
            failures.Add("Could not refresh task list: " + ex.Message);
        }

        Status.Text = $"Deleted {deleted}/{tasks.Count} tasks.";
        if (failures.Count > 0)
        {
            var message = Status.Text + "\n" + string.Join("\n", failures);
            Status.Text = message;
            MessageBox.Show(this, message, "Task deletion errors", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
