using Employee.Core;

namespace Employee.Infrastructure;
// Keeps the data folder from growing without bound, and caches model history that routing reads
// on every decision.
public sealed partial class SqliteStore
{
    public const int BackupsKept = 10;
    public const int HandoffsKeptPerTask = 5;
    public const int ExecutionLogDays = 30;
    public const int ExecutionLogChars = 4000;
    private int modelDataVersion;
    private (int Version, IReadOnlyList<ModelPerformance> Rows)? performanceCache;
    private (int Version, IReadOnlyList<ModelOutcome> Rows)? outcomesCache;
    private void InvalidateModelData() => Interlocked.Increment(ref modelDataVersion);
    private IReadOnlyList<T> Cached<T>(ref (int Version, IReadOnlyList<T> Rows)? cache, Func<IReadOnlyList<T>> load)
    {
        var version = Volatile.Read(ref modelDataVersion);
        if (cache is { } hit && hit.Version == version)
            return hit.Rows;
        var rows = load();
        cache = (version, rows);
        return rows;
    }

    // Prunes old copies and verbose history; task results, plans and recent history are kept.
    public string RunMaintenance()
    {
        var handoffs = Count("SELECT count(*) FROM handoffs");
        Execute($"DELETE FROM handoffs WHERE seq NOT IN (SELECT seq FROM (SELECT seq,row_number() OVER(PARTITION BY work_id ORDER BY seq DESC) n FROM handoffs) WHERE n<={HandoffsKeptPerTask})");
        var cutoff = DateTimeOffset.UtcNow.AddDays(-ExecutionLogDays).ToString("O");
        var trimmed = Count($"SELECT count(*) FROM logs WHERE event='Execution' AND at<'{cutoff}' AND length(message)>{ExecutionLogChars}");
        Execute($"UPDATE logs SET message=substr(message,1,{ExecutionLogChars})||char(10)||'[older output trimmed by maintenance]' WHERE event='Execution' AND at<$cutoff AND length(message)>{ExecutionLogChars}", ("$cutoff", cutoff));
        return $"Maintenance: {handoffs - Count("SELECT count(*) FROM handoffs")} old checkpoints and {trimmed} old execution logs trimmed.";
    }

    private long Count(string sql) => Read(sql, r => r.GetInt64(0)).Single();
    // Backups are full database copies; only the newest few in the backups folder are kept. Other
    // destinations (update staging, data moves) are never pruned.
    private static void PruneBackups(string folder)
    {
        if (!Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)).Equals("backups", StringComparison.OrdinalIgnoreCase))
            return;
        foreach (var old in new DirectoryInfo(folder).EnumerateFiles("*.db").OrderByDescending(f => f.LastWriteTimeUtc).Skip(BackupsKept))
            try
            {
                old.Delete();
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
    }
}
