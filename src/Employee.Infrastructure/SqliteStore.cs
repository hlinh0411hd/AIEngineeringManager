using System.Text.Json;
using Employee.Core;
using Microsoft.Data.Sqlite;

namespace Employee.Infrastructure;
public sealed partial class SqliteStore : IEmployeeStore
{
    private readonly SqliteConnection connection;
    private readonly object gate = new();
    public string DatabasePath { get; }

    public SqliteStore(string path)
    {
        DatabasePath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        connection.Open();
        using (var version = connection.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version";
            var schema = Convert.ToInt32(version.ExecuteScalar());
            if (schema > 3)
            {
                connection.Dispose();
                throw new InvalidOperationException("Database schema is newer than this application. Use the matching application version.");
            }

            if (schema is 1 or 2)
            {
                using var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path + ".v" + schema + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + ".bak" }.ToString());
                backup.Open();
                connection.BackupDatabase(backup);
            }
        }

        Execute("PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;");
        Execute("""
            CREATE TABLE IF NOT EXISTS projects(id TEXT PRIMARY KEY, data TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS work(id TEXT PRIMARY KEY, data TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS settings(id INTEGER PRIMARY KEY CHECK(id=1), data TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS logs(seq INTEGER PRIMARY KEY AUTOINCREMENT, work_id TEXT, at TEXT, event TEXT, message TEXT);
            CREATE INDEX IF NOT EXISTS logs_work ON logs(work_id,seq);
            CREATE TABLE IF NOT EXISTS handoffs(seq INTEGER PRIMARY KEY AUTOINCREMENT, work_id TEXT, data TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS evaluations(seq INTEGER PRIMARY KEY AUTOINCREMENT, data TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS files(project_id TEXT, path TEXT, size INTEGER, modified INTEGER, PRIMARY KEY(project_id,path));
            CREATE VIRTUAL TABLE IF NOT EXISTS file_search USING fts5(project_id UNINDEXED, path, tokenize='unicode61');
            """);
        Execute("""
            BEGIN IMMEDIATE;
            CREATE TABLE IF NOT EXISTS sessions(id TEXT PRIMARY KEY,data TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS plans(id TEXT PRIMARY KEY,session_id TEXT NOT NULL,data TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS decisions(id TEXT PRIMARY KEY,session_id TEXT NOT NULL,data TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS test_profiles(id TEXT PRIMARY KEY,data TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS test_jobs(id TEXT PRIMARY KEY,data TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS model_runs(id TEXT PRIMARY KEY,work_id TEXT NOT NULL,provider TEXT NOT NULL,model TEXT NOT NULL,kind INTEGER NOT NULL,failure INTEGER NOT NULL,milliseconds INTEGER NOT NULL,coordinator INTEGER NOT NULL,at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS model_runs_work ON model_runs(work_id,at);
            CREATE INDEX IF NOT EXISTS model_runs_history ON model_runs(coordinator,at DESC);
            CREATE TABLE IF NOT EXISTS model_feedback(run_id TEXT PRIMARY KEY,good INTEGER NOT NULL,at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS project_memory(id TEXT PRIMARY KEY,project_id TEXT NOT NULL,data TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS project_memory_project ON project_memory(project_id);
            CREATE TABLE IF NOT EXISTS usage_reservations(id TEXT PRIMARY KEY,work_id TEXT NOT NULL,day TEXT NOT NULL,at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS usage_reservations_day ON usage_reservations(day);
            CREATE TABLE IF NOT EXISTS connected_tools(id TEXT PRIMARY KEY,data TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS tool_runs(id TEXT PRIMARY KEY,data TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS tool_workflows(id TEXT PRIMARY KEY,data TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS reported_usage(run_id TEXT PRIMARY KEY,input INTEGER,output INTEGER,cached INTEGER);
            CREATE TABLE IF NOT EXISTS advanced_evaluations(id TEXT PRIMARY KEY,data TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS intent_examples(id TEXT PRIMARY KEY,project_id TEXT NOT NULL,data TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS intent_examples_project ON intent_examples(project_id);
            CREATE TABLE IF NOT EXISTS office_actions(id TEXT PRIMARY KEY,seat TEXT NOT NULL,position INTEGER NOT NULL,data TEXT NOT NULL);
            PRAGMA user_version=3;
            COMMIT;
            """);
        // Learned model quality is tracked per work capability (debugging, UI, testing...).
        var runColumns = Read("PRAGMA table_info(model_runs)", r => r.GetString(1));
        if (!runColumns.Contains("capability"))
            Execute("ALTER TABLE model_runs ADD COLUMN capability INTEGER");
        // Learning compares each result with what the rating predicted at that step's difficulty.
        if (!runColumns.Contains("difficulty"))
            Execute("ALTER TABLE model_runs ADD COLUMN difficulty INTEGER");
        var feedbackColumns = Read("PRAGMA table_info(model_feedback)", r => r.GetString(1));
        if (!feedbackColumns.Contains("source"))
            Execute("ALTER TABLE model_feedback ADD COLUMN source TEXT");
        // Share of a verdict on a whole task that this run carries (1 = all of it; older rows = 1).
        if (!feedbackColumns.Contains("weight"))
            Execute("ALTER TABLE model_feedback ADD COLUMN weight REAL");
        MigrateAttachments();
    }

    private void Execute(string sql, params (string, object? )[] values)
    {
        lock (gate)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            foreach (var(k, v)in values)
                cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        if (sql.Contains("model_runs", StringComparison.Ordinal) || sql.Contains("model_feedback", StringComparison.Ordinal))
            InvalidateModelData();
    }

    public void Backup(string path)
    {
        lock (gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            if (File.Exists(path))
                throw new InvalidOperationException("Backup already exists.");
            using (var copy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
            {
                copy.Open();
                connection.BackupDatabase(copy);
            }

            PruneBackups(Path.GetDirectoryName(Path.GetFullPath(path))!);
        }
    }

    private List<T> Read<T>(string sql, Func<SqliteDataReader, T> map, params (string, object? )[] values)
    {
        lock (gate)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            foreach (var(k, v)in values)
                cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
            using var reader = cmd.ExecuteReader();
            var list = new List<T>();
            while (reader.Read())
                list.Add(map(reader));
            return list;
        }
    }

    private static T Decode<T>(SqliteDataReader r) => JsonSerializer.Deserialize<T>(r.GetString(0))!;
    public void SaveProject(ProjectInfo p) => Execute("INSERT INTO projects VALUES($id,$data) ON CONFLICT(id) DO UPDATE SET data=$data", ("$id", p.Id), ("$data", JsonSerializer.Serialize(p)));
    public IReadOnlyList<ProjectInfo> Projects() => Read("SELECT data FROM projects", Decode<ProjectInfo>);
    public void DeleteProject(string id)
    {
        lock (gate)
        {
            var sessions = Sessions().Where(s => s.ProjectIds.Contains(id)).ToArray();
            if (sessions.Any(s => s.ProjectIds.Any(p => p != id)))
                throw new InvalidOperationException("Cannot delete a project in a session shared by multiple projects.");
            if (WorkItems().Any(w => w.ProjectId == id && w.State == WorkState.Running) || sessions.Any(s => s.State is SessionState.Running or SessionState.Planning) || TestJobs().Any(j => j.ProjectId == id && j.State == TestJobState.Running))
                throw new InvalidOperationException("Stop project tasks and tests before deleting the project.");
            using var tx = connection.BeginTransaction();
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                DELETE FROM logs WHERE work_id IN (SELECT id FROM work WHERE json_extract(data,'$.ProjectId')=$id);
                DELETE FROM handoffs WHERE work_id IN (SELECT id FROM work WHERE json_extract(data,'$.ProjectId')=$id);
                DELETE FROM decisions WHERE session_id IN (SELECT s.id FROM sessions s,json_each(s.data,'$.ProjectIds') p WHERE p.value=$id);
                DELETE FROM plans WHERE session_id IN (SELECT s.id FROM sessions s,json_each(s.data,'$.ProjectIds') p WHERE p.value=$id);
                DELETE FROM sessions WHERE id IN (SELECT s.id FROM sessions s,json_each(s.data,'$.ProjectIds') p WHERE p.value=$id);
                DELETE FROM work WHERE json_extract(data,'$.ProjectId')=$id;
                DELETE FROM test_jobs WHERE json_extract(data,'$.ProjectId')=$id;
                DELETE FROM test_profiles WHERE json_extract(data,'$.ProjectId')=$id;
                DELETE FROM file_search WHERE project_id=$id;
                DELETE FROM files WHERE project_id=$id;
                DELETE FROM project_memory WHERE project_id=$id;
                DELETE FROM intent_examples WHERE project_id=$id;
                DELETE FROM projects WHERE id=$id;
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
    }

    public void SaveWork(WorkItem w)
    {
        w.UpdatedAt = DateTimeOffset.UtcNow;
        lock (gate)
            Execute("INSERT INTO work VALUES($id,$data) ON CONFLICT(id) DO UPDATE SET data=$data", ("$id", w.Id), ("$data", WorkJson(w, null)));
    }

    public IReadOnlyList<WorkItem> WorkItems() => Read("SELECT data FROM work", DecodeWork).OrderByDescending(w => w.Priority).ThenBy(w => w.CreatedAt).ToList();
    public void Log(string workId, string eventName, string message) => Execute("INSERT INTO logs(work_id,at,event,message) VALUES($id,$at,$event,$message)", ("$id", workId), ("$at", DateTimeOffset.UtcNow.ToString("O")), ("$event", eventName), ("$message", message));
    public IReadOnlyList<TaskLog> Logs(string id) => Read("SELECT seq,work_id,at,event,message FROM logs WHERE work_id=$id ORDER BY seq", r => new TaskLog(r.GetInt64(0), r.GetString(1), DateTimeOffset.Parse(r.GetString(2)), r.GetString(3), r.GetString(4)), ("$id", id));
    public void SaveHandoff(Handoff h) => Execute("INSERT INTO handoffs(work_id,data) VALUES($id,$data)", ("$id", h.WorkId), ("$data", JsonSerializer.Serialize(h)));
    public Handoff? LatestHandoff(string id) => Read("SELECT data FROM handoffs WHERE work_id=$id ORDER BY seq DESC LIMIT 1", Decode<Handoff>, ("$id", id)).FirstOrDefault();
    public void SaveSettings(ManagerSettings s) => Execute("INSERT INTO settings VALUES(1,$data) ON CONFLICT(id) DO UPDATE SET data=$data", ("$data", JsonSerializer.Serialize(s)));
    public ManagerSettings LoadSettings()
    {
        var settings = Read("SELECT data FROM settings WHERE id=1", Decode<ManagerSettings>).FirstOrDefault() ?? new();
        // Settings saved by older builds lack providers added since; expose them disabled until detected.
        foreach (var id in ManagerSettings.DefaultProviderIds)
            if (settings.Providers.All(p => p.Id != id))
                settings.Providers.Add(new() { Id = id });
        if (settings.Automation.LimitsVersion < 1)
        {
            (settings.Automation.DurationMinutes, settings.Automation.MaxCalls, settings.Automation.MaxTasks, settings.Automation.MaxSwitches, settings.Automation.LimitsVersion) = (0, 0, 0, 0, 1);
            settings.MaxSwitches = 0;
        }
        return settings;
    }

    public void SaveEvaluation(Evaluation e) => Execute("INSERT INTO evaluations(data) VALUES($data)", ("$data", JsonSerializer.Serialize(e)));
    public IReadOnlyList<Evaluation> Evaluations() => Read("SELECT data FROM evaluations", Decode<Evaluation>);
    public void ReplaceIndex(string id, IReadOnlyList<IndexedFile> files)
    {
        lock (gate)
        {
            using var tx = connection.BeginTransaction();
            void Run(string sql, params (string, object? )[] pairs)
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                foreach (var(k, v)in pairs)
                    cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }

            // Unchanged metadata is preserved; removed files and FTS entries are reconciled atomically.
            Run("CREATE TEMP TABLE IF NOT EXISTS seen_paths(path TEXT PRIMARY KEY); DELETE FROM seen_paths;");
            foreach (var f in files)
            {
                Run("INSERT INTO seen_paths VALUES($path)", ("$path", f.Path));
                Run("INSERT INTO files VALUES($id,$path,$size,$modified) ON CONFLICT(project_id,path) DO UPDATE SET size=$size,modified=$modified WHERE size<>$size OR modified<>$modified", ("$id", id), ("$path", f.Path), ("$size", f.Size), ("$modified", f.ModifiedTicks));
            }

            Run("DELETE FROM files WHERE project_id=$id AND path NOT IN (SELECT path FROM seen_paths); DELETE FROM file_search WHERE project_id=$id; INSERT INTO file_search SELECT project_id,path FROM files WHERE project_id=$id", ("$id", id));
            tx.Commit();
        }
    }

    public IReadOnlyList<IndexedFile> SearchFiles(string id, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Read("SELECT project_id,path,size,modified FROM files WHERE project_id=$id LIMIT 100", Map, ("$id", id));
        var terms = System.Text.RegularExpressions.Regex.Matches(query, @"[\p{L}\p{N}_]+").Select(m => '"' + m.Value + '"' + "*").Take(10).ToArray();
        if (terms.Length == 0)
            return[];
        return Read("SELECT f.project_id,f.path,f.size,f.modified FROM file_search s JOIN files f ON f.project_id=s.project_id AND f.path=s.path WHERE file_search MATCH $query AND f.project_id=$id LIMIT 100", Map, ("$id", id), ("$query", string.Join(" AND ", terms)));
        static IndexedFile Map(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3));
    }

    public void RecoverInterruptedWork()
    {
        foreach (var w in WorkItems().Where(w => w.State == WorkState.Running))
        {
            w.State = WorkState.Paused;
            w.Notes = "Interrupted execution recovered; review files before Resume.";
            SaveWork(w);
            Log(w.Id, "Recovery", w.Notes);
        }

        foreach (var session in Sessions().Where(s => s.State is SessionState.Running or SessionState.Planning))
        {
            session.State = SessionState.Paused;
            session.Notes = "Session interrupted; manual resume required.";
            SaveSession(session);
        }

        foreach (var job in TestJobs().Where(j => j.State == TestJobState.Running))
        {
            job.State = TestJobState.Interrupted;
            job.Output += "\nTesting interrupted; it will not restart automatically.";
            SaveTestJob(job);
        }
    }

    public void Dispose()
    {
        lock (gate)
            connection.Dispose();
    }
}
