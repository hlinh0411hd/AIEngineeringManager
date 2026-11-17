namespace Employee.Core;
public enum TaskSortField
{
    Name,
    CreatedAt,
    UpdatedAt,
    State,
    Project
}

public enum TaskSortDirection
{
    Ascending,
    Descending
}

public static class TaskCalendar
{
    public static DateTime CreationDay(DateTimeOffset at) => at.UtcDateTime.Date;
}

public interface ITaskLifecycleStore
{
    IReadOnlyList<WorkItem> QueryTasks(TaskSortField field, TaskSortDirection direction);
    IReadOnlyList<string> DeleteTasks(IEnumerable<string> ids);
    IReadOnlyList<string> DeleteTask(string id) => DeleteTasks([id]);
    IDisposable ProtectTask(string id);
}
