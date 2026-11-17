using System.Text.Json;
using Employee.Core;

namespace Employee.Infrastructure;
public sealed partial class SqliteStore
{
    public IReadOnlyList<ProjectMemory> Memories(string id) => Read("SELECT data FROM project_memory WHERE project_id=$id ORDER BY rowid DESC LIMIT 100", Decode<ProjectMemory>, ("$id", id));
    public void SaveMemory(ProjectMemory memory)
    {
        if (!Projects().Any(p => p.Id == memory.ProjectId) || string.IsNullOrWhiteSpace(memory.Text) || memory.Text.Length > 8000)
            throw new InvalidOperationException("Choose a project and enter at most 8,000 characters.");
        Execute("INSERT INTO project_memory VALUES($id,$project,$data) ON CONFLICT(id) DO UPDATE SET data=$data WHERE project_memory.project_id=$project", ("$id", memory.Id), ("$project", memory.ProjectId), ("$data", JsonSerializer.Serialize(memory)));
    }

    public void DeleteMemory(string projectId, string id) => Execute("DELETE FROM project_memory WHERE project_id=$project AND id=$id", ("$project", projectId), ("$id", id));
    public int CallsOnDay(string day) => Read("SELECT count(*) FROM usage_reservations WHERE day=$day", r => r.GetInt32(0), ("$day", day)).Single();
    public bool ReserveUsage(string workId, int limit)
    {
        if (limit is < 0 or > 10000)
            throw new InvalidOperationException("Daily call limit must be 0–10000; 0 is unlimited.");
        lock (gate)
        {
            var day = DateTimeOffset.Now.ToString("yyyy-MM-dd");
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO usage_reservations SELECT $id,$work,$day,$at WHERE $limit=0 OR (SELECT count(*) FROM usage_reservations WHERE day=$day)<$limit";
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            command.Parameters.AddWithValue("$work", workId);
            command.Parameters.AddWithValue("$day", day);
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$limit", limit);
            return command.ExecuteNonQuery() > 0;
        }
    }
}
