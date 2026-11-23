using Employee.Core;

namespace Employee.Infrastructure;
public sealed partial class SqliteStore
{
    public void SaveModelRun(ModelRun r)
    {
        Execute("INSERT OR IGNORE INTO model_runs(id,work_id,provider,model,kind,failure,milliseconds,coordinator,at,capability,difficulty) VALUES($id,$work,$provider,$model,$kind,$failure,$ms,$coordinator,$at,$capability,$difficulty)", ("$id", r.Id), ("$work", r.WorkId), ("$provider", r.ProviderId), ("$model", r.ModelId), ("$kind", (int)r.Kind), ("$failure", (int)r.Failure), ("$ms", r.Milliseconds), ("$coordinator", r.Coordinator ? 1 : 0), ("$at", r.At.ToString("O")), ("$capability", r.Capability is { } capability ? (int)capability : null), ("$difficulty", r.Difficulty));
        if (r.Usage is { } usage)
            Execute("INSERT OR IGNORE INTO reported_usage VALUES($id,$input,$output,$cached)", ("$id", r.Id), ("$input", usage.Input), ("$output", usage.Output), ("$cached", usage.CachedInput));
    }

    public IReadOnlyDictionary<string, int> ProviderRunsToday()
    {
        var since = new DateTimeOffset(DateTime.Today, TimeZoneInfo.Local.GetUtcOffset(DateTime.Today)).ToUniversalTime();
        return Read("SELECT provider,count(*) FROM model_runs WHERE at>=$since GROUP BY provider", r => (r.GetString(0), r.GetInt32(1)), ("$since", since.ToString("O")))
            .ToDictionary(pair => pair.Item1, pair => pair.Item2, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ModelPerformance> ModelPerformance() => Cached(ref performanceCache, () => Read("""
        SELECT r.provider,r.model,r.kind,count(*),
            sum(CASE WHEN r.failure=0 THEN 1 ELSE 0 END),
            sum(CASE WHEN r.failure IN (4,6,7) THEN 1 ELSE 0 END),
            sum(CASE WHEN f.good=1 THEN 1 ELSE 0 END),sum(CASE WHEN f.good=0 THEN 1 ELSE 0 END),
            avg(CASE WHEN r.failure=0 THEN r.milliseconds ELSE NULL END),coalesce(r.capability,r.kind)
        FROM (SELECT * FROM model_runs WHERE coordinator=0 AND failure IN (0,4,6,7) ORDER BY at DESC LIMIT 5000) r
        LEFT JOIN model_feedback f ON f.run_id=r.id GROUP BY r.provider,r.model,r.kind,coalesce(r.capability,r.kind)
        """, r => new ModelPerformance(r.GetString(0), r.GetString(1), (WorkKind)r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6), r.GetInt32(7), r.IsDBNull(8) ? 0 : r.GetDouble(8), (WorkCapability)r.GetInt32(9))));
    // The latest finished run of each step a verdict covers.
    private List<string> VerdictRuns(IEnumerable<string> workIds) => [..workIds.Distinct().Select(workId => Read("SELECT id FROM (SELECT id,failure FROM model_runs WHERE work_id=$work AND coordinator=0 ORDER BY at DESC,rowid DESC LIMIT 1) WHERE failure=0", r => r.GetString(0), ("$work", workId)).FirstOrDefault()).OfType<string>()];
    // A verdict on a whole task is one verdict: it is divided between the steps that produced it, so a
    // single weak step does not count against (or for) every model that worked on the task. A single
    // step (rerun of one step) carries all of it.
    internal static double Share(int runs) => runs <= 1 ? 1 : Math.Max(0.1, 1.0 / runs);

    public void RecordApproval(IEnumerable<string> workIds)
    {
        var runs = VerdictRuns(workIds);
        foreach (var run in runs)
            Execute("""
            INSERT INTO model_feedback(run_id,good,at,source,weight) VALUES($run,1,$at,'approval',$share)
            ON CONFLICT(run_id) DO NOTHING
            """, ("$at", DateTimeOffset.UtcNow.ToString("O")), ("$run", run), ("$share", Share(runs.Count)));
    }

    public IReadOnlyList<ModelOutcome> ModelOutcomes() => Cached(ref outcomesCache, () => Read("""
        SELECT r.provider,r.model,coalesce(r.capability,r.kind),coalesce(r.difficulty,50),
            CASE WHEN f.run_id IS NULL THEN 0 ELSE f.good END,
            CASE WHEN f.run_id IS NULL THEN 'failure' ELSE coalesce(f.source,'rating') END,r.at,coalesce(f.weight,1.0)
        FROM (SELECT * FROM model_runs WHERE coordinator=0 ORDER BY at DESC LIMIT 5000) r
        LEFT JOIN model_feedback f ON f.run_id=r.id
        WHERE f.run_id IS NOT NULL OR r.failure IN (4,6,7)
        """, r => new ModelOutcome(r.GetString(0), r.GetString(1), (WorkCapability)r.GetInt32(2), r.GetInt32(3), r.GetInt32(4) == 1, r.GetString(5) switch
    {
        "approval" => OutcomeSource.Approval,
        "rework" => OutcomeSource.Rework,
        "failure" => OutcomeSource.Failure,
        _ => OutcomeSource.Rating
    }, DateTimeOffset.Parse(r.GetString(6), System.Globalization.CultureInfo.InvariantCulture), r.GetDouble(7))));
    public void RecordRework(IEnumerable<string> workIds)
    {
        var runs = VerdictRuns(workIds);
        foreach (var run in runs)
            Execute("""
            INSERT INTO model_feedback(run_id,good,at,source,weight) VALUES($run,0,$at,'rework',$share)
            ON CONFLICT(run_id) DO UPDATE SET good=0,at=excluded.at,source='rework',weight=excluded.weight
            """, ("$at", DateTimeOffset.UtcNow.ToString("O")), ("$run", run), ("$share", Share(runs.Count)));
    }

    public bool RateModelResult(string workId, bool good)
    {
        lock (gate)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
            INSERT INTO model_feedback(run_id,good,at,source,weight)
            SELECT id,$good,$at,'rating',1.0 FROM (SELECT id,failure FROM model_runs WHERE work_id=$work AND coordinator=0 ORDER BY at DESC,rowid DESC LIMIT 1) WHERE failure=0
            ON CONFLICT(run_id) DO UPDATE SET good=excluded.good,at=excluded.at,source='rating',weight=1.0
            """;
            command.Parameters.AddWithValue("$good", good ? 1 : 0);
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$work", workId);
            InvalidateModelData();
            return command.ExecuteNonQuery() > 0;
        }
    }
}
