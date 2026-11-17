using System.Windows;
using System.Windows.Controls;
using Employee.Core;

namespace Employee.Desktop;

// Ending a task without "Complete": not completed, does not meet the requirements, or cancelled.
public partial class MainWindow
{
    private void OpenCloseTaskMenu(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private async void CloseTaskAs(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        var outcome = Outcome(sender);
        var goal = store.WorkItems().Single(w => w.Id == SelectedWork().Id);
        if (AskCloseReason(outcome, 1) is not { } reason)
            return Task.CompletedTask;
        unified.Close(goal, outcome, reason);
        RefreshTasks(goal.Id);
        Status.Text = "Task closed: " + TaskOutcomes.Label(outcome) + ".";
        return Task.CompletedTask;
    });

    private async void CloseSelectedTasksAs(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        var outcome = Outcome(sender);
        var ids = TaskGrid.Items.OfType<WorkItem>().Where(w => selectedDeletionTasks.Contains(w.Id)).Select(w => w.Id).ToHashSet(StringComparer.Ordinal);
        var tasks = store.WorkItems().Where(w => ids.Contains(w.Id) && !TaskOutcomes.IsClosed(w.State)).ToArray();
        if (tasks.Length == 0 || AskCloseReason(outcome, tasks.Length) is not { } reason)
            return Task.CompletedTask;
        var failures = new List<string>();
        foreach (var task in tasks)
            try
            {
                unified.Close(task, outcome, reason);
                selectedDeletionTasks.Remove(task.Id);
            }
            catch (InvalidOperationException ex)
            {
                failures.Add($"{task.RequestPreview}: {ex.Message}");
            }
        RefreshTasks();
        Status.Text = $"Closed {tasks.Length - failures.Count} task(s) as {TaskOutcomes.Label(outcome).ToLowerInvariant()}. Failed {failures.Count}.";
        if (failures.Count > 0)
            MessageBox.Show(this, Status.Text + "\n" + string.Join("\n", failures), "Closing tasks", MessageBoxButton.OK, MessageBoxImage.Warning);
        return Task.CompletedTask;
    });

    private static WorkState Outcome(object sender) => sender is MenuItem { Tag: string tag } && Enum.TryParse<WorkState>(tag, out var state) && TaskOutcomes.CloseStates.Contains(state)
        ? state
        : throw new InvalidOperationException("Unknown way to close the task.");

    // Confirms the outcome and asks for an optional reason. Null when the user cancels.
    private string? AskCloseReason(WorkState outcome, int count)
    {
        var panel = new StackPanel { Margin = new Thickness(16) };
        var what = count == 1 ? "this task" : $"{count} tasks";
        panel.Children.Add(new TextBlock
        {
            Text = $"Close {what} as \"{TaskOutcomes.Label(outcome)}\"?" + outcome switch
            {
                WorkState.NotMet => "\nThe AIs that made the result get a miss in their ratings.",
                WorkState.Cancelled => "\nIt leaves the queue and will not run again.",
                _ => ""
            } + "\nUnfinished steps and waiting decisions are closed. Rerun makes a fresh copy if you need it later.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        });
        panel.Children.Add(new TextBlock { Text = "Reason (optional)", Margin = new Thickness(0, 0, 0, 4) });
        var reason = new TextBox { Height = 70, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(reason);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var confirm = new Button { Content = "Close " + (count == 1 ? "task" : "tasks"), IsDefault = true };
        var cancel = new Button { Content = "Keep open", IsCancel = true };
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        panel.Children.Add(buttons);
        var dialog = new Window
        {
            Owner = this,
            Title = "Close as " + TaskOutcomes.Label(outcome),
            Content = panel,
            SizeToContent = SizeToContent.Height,
            Width = 480,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        confirm.Click += (_, _) => dialog.DialogResult = true;
        dialog.Loaded += (_, _) => reason.Focus();
        return dialog.ShowDialog() == true ? reason.Text : null;
    }
}
