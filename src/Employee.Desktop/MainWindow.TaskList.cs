using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Employee.Core;

namespace Employee.Desktop;
public sealed class TaskProjectNameConverter : IValueConverter
{
    public Dictionary<string, string> Names { get; set; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is string id ? Names.GetValueOrDefault(id, id) : "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class TaskModifiedTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is WorkItem task ? (task.UpdatedAt ?? task.CreatedAt).ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss", culture) : "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

// Groups the task list by project: the group's name is the project's name.
public sealed class TaskProjectGroupConverter(Dictionary<string, string> names) : IValueConverter
{
    public const string Outside = "Outside projects";
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is string id && !string.IsNullOrWhiteSpace(id) ? names.GetValueOrDefault(id, id) : Outside;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class TaskCreationDayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is DateTimeOffset at ? TaskCalendar.CreationDay(at).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " UTC" : "Unknown date";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public partial class MainWindow
{
    private int taskPage;
    private const int TaskPageSize = 50;
    private sealed record TaskProjectOption(string? Id, string Name);
    private bool updatingTaskProjectFilter;
    private void RefreshTaskProjectFilter(WorkItem[] tasks)
    {
        var selected = (TaskProjectFilter.SelectedItem as TaskProjectOption)?.Id;
        var names = store.Projects().ToDictionary(p => p.Id, p => p.Name);
        ((TaskProjectNameConverter)Resources["TaskProjectName"]).Names = names;
        var options = names.Keys.Concat(tasks.Select(w => w.ProjectId)).Distinct(StringComparer.Ordinal).Select(id => new TaskProjectOption(id, string.IsNullOrWhiteSpace(id) ? "Outside projects (text only)" : names.GetValueOrDefault(id, id))).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id, StringComparer.Ordinal).Prepend(new TaskProjectOption(null, "All projects")).ToArray();
        updatingTaskProjectFilter = true;
        try
        {
            TaskProjectFilter.ItemsSource = options;
            TaskProjectFilter.SelectedItem = options.FirstOrDefault(p => p.Id == selected) ?? options[0];
        }
        finally
        {
            updatingTaskProjectFilter = false;
        }
    }

    // Project groups the user collapsed, by group name; kept while the app runs.
    private readonly HashSet<string> collapsedTaskGroups = new(StringComparer.OrdinalIgnoreCase);
    private bool restoringTaskGroup;

    private void TaskGroupLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Expander { DataContext: System.Windows.Data.CollectionViewGroup group } expander)
            return;
        restoringTaskGroup = true;
        expander.IsExpanded = !collapsedTaskGroups.Contains(group.Name?.ToString() ?? "");
        restoringTaskGroup = false;
    }

    private void TaskGroupToggled(object sender, RoutedEventArgs e)
    {
        if (restoringTaskGroup || sender is not Expander { DataContext: System.Windows.Data.CollectionViewGroup group } expander)
            return;
        var name = group.Name?.ToString() ?? "";
        if (expander.IsExpanded)
            collapsedTaskGroups.Remove(name);
        else
            collapsedTaskGroups.Add(name);
        e.Handled = true;
    }

    private void TaskProjectFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingTaskProjectFilter || store == null)
            return;
        taskPage = 0;
        RefreshTasks();
    }

    private TaskSortField taskSortField = TaskSortField.CreatedAt;
    private TaskSortDirection taskSortDirection = TaskSortDirection.Descending;
    private void InitializeTaskListOptions()
    {
        taskSortField = TaskSortField.CreatedAt;
        taskSortDirection = TaskSortDirection.Descending;
        foreach (var column in TaskGrid.Columns)
            column.SortDirection = null;
    }

    private void TaskGridSorting(object sender, DataGridSortingEventArgs e)
    {
        // Sort the complete query before grouping and pagination, not just visible rows.
        e.Handled = true;
        var field = e.Column.SortMemberPath switch
        {
            "Name" => TaskSortField.Name,
            "State" => TaskSortField.State,
            "UpdatedAt" => TaskSortField.UpdatedAt,
            "Project" => TaskSortField.Project,
            _ => (TaskSortField? )null
        };
        if (field == null)
            return;
        taskSortDirection = taskSortField == field && taskSortDirection == TaskSortDirection.Ascending ? TaskSortDirection.Descending : TaskSortDirection.Ascending;
        taskSortField = field.Value;
        taskPage = 0;
        RefreshTasks();
        UpdateTaskSortIndicators();
    }

    private void UpdateTaskSortIndicators()
    {
        foreach (var column in TaskGrid.Columns)
            column.SortDirection = column.SortMemberPath == taskSortField.ToString() ? (taskSortDirection == TaskSortDirection.Ascending ? System.ComponentModel.ListSortDirection.Ascending : System.ComponentModel.ListSortDirection.Descending) : null;
    }

    private void PreviousTaskPage(object sender, RoutedEventArgs e)
    {
        taskPage = Math.Max(0, taskPage - 1);
        RefreshTasks();
    }

    private void NextTaskPage(object sender, RoutedEventArgs e)
    {
        taskPage++;
        RefreshTasks();
    }

    private WorkItem[] TaskListPage(WorkItem[] tasks, string? requestedId)
    {
        var field = taskSortField;
        var direction = taskSortDirection;
        var ids = tasks.Select(w => w.Id).ToHashSet(StringComparer.Ordinal);
        // QueryTasks supplies the stable field ordering and legacy UpdatedAt fallback.
        var sorted = store.QueryTasks(field, direction).Where(w => ids.Contains(w.Id)).ToArray();
        // The list is grouped by project: each project's tasks stay together (projects by name, tasks
        // in the selected order inside), so a page holds whole groups wherever it can.
        var names = ((TaskProjectNameConverter)Resources["TaskProjectName"]).Names;
        string Group(WorkItem w) => string.IsNullOrWhiteSpace(w.ProjectId) ? "￿" : names.GetValueOrDefault(w.ProjectId, w.ProjectId);
        sorted = sorted.OrderBy(Group, StringComparer.OrdinalIgnoreCase).ToArray();
        var pages = Math.Max(1, (sorted.Length + TaskPageSize - 1) / TaskPageSize);
        if (requestedId != null)
        {
            var index = Array.FindIndex(sorted, w => w.Id == requestedId);
            if (index >= 0)
                taskPage = index / TaskPageSize;
        }

        taskPage = Math.Clamp(taskPage, 0, pages - 1);
        TaskPageStatus.Text = $"Page {taskPage + 1} / {pages} · {TaskPageSize} tasks per page";
        PreviousTaskPageButton.IsEnabled = taskPage > 0;
        NextTaskPageButton.IsEnabled = taskPage + 1 < pages;
        return sorted.Skip(taskPage * TaskPageSize).Take(TaskPageSize).ToArray();
    }
}
