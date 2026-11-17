namespace Employee.Core;
public sealed record ProjectMemory(string Id, string ProjectId, string Text, DateTimeOffset At, string? WorkId = null);
public interface IWorkInsightsStore
{
    IReadOnlyList<ProjectMemory> Memories(string projectId);
    void SaveMemory(ProjectMemory memory);
    void DeleteMemory(string projectId, string id);
    bool ReserveUsage(string workId, int dailyLimit);
    int CallsOnDay(string day);
}
