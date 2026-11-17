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

    public IReadOnlyList<ModelPerformance> ModelPerformance() => Cached(ref performanceCache, () => Read("""
        SELECT r.provider,r.model,r.kind,count(*),
            sum(CASE WHEN r.failure=0 THEN 1 ELSE 0 END),
            sum(CASE WHEN r.failure IN (4,6,7) THEN 1 ELSE 0 END),
            sum(CASE WHEN f.good=1 THEN 1 ELSE 0 END),sum(CASE WHEN f.good=0 THEN 1 ELSE 0 END),
            avg(CASE WHEN r.failure=0 THEN r.milliseconds ELSE NULL END),coalesce(r.capability,r.kind)
        FROM (SELECT * FROM model_runs WHERE coordinator=0 AND failure IN (0,4,6,7) ORDER BY at DESC LIMIT 5000) r
        LEFT JOIN model_feedback f ON f.run_id=r.id GROUP BY r.provider,r.model,r.kind,coalesce(r.capability,r.kind)
        """, r => new ModelPerformance(r.GetString(0), r.GetString(1), (WorkKind)r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6), r.GetInt32(7), r.IsDBNull(8) ? 0 : r.GetDouble(8), (WorkCapability)r.GetInt32(9))));
    public void RecordApproval(IEnumerable<string> workIds)
    {
        foreach (var workId in workIds.Distinct())
            Execute("""
            INSERT INTO model_feedback(run_id,good,at,source)
            SELECT id,1,$at,'approval' FROM (SELECT id,failure FROM model_runs WHERE work_id=$work AND coordinator=0 ORDER BY at DESC,rowid DESC LIMIT 1) WHERE failure=0
            ON CONFLICT(run_id) DO NOTHING
            """, ("$at", DateTimeOffset.UtcNow.ToString("O")), ("$work", workId));
    }

    public IReadOnlyList<ModelOutcome> ModelOutcomes() => Cached(ref outcomesCache, () => Read("""
        SELECT r.provider,r.model,coalesce(r.capability,r.kind),coalesce(r.difficulty,50),
            CASE WHEN f.run_id IS NULL THEN 0 ELSE f.good END,
            CASE WHEN f.run_id IS NULL THEN 'failure' ELSE coalesce(f.source,'rating') END,r.at
        FROM (SELECT * FROM model_runs WHERE coordinator=0 ORDER BY at DESC LIMIT 5000) r
        LEFT JOIN model_feedback f ON f.run_id=r.id
        WHERE f.run_id IS NOT NULL OR r.failure IN (4,6,7)
        """, r => new ModelOutcome(r.GetString(0), r.GetString(1), (WorkCapability)r.GetInt32(2), r.GetInt32(3), r.GetInt32(4) == 1, r.GetString(5) switch
    {
        "approval" => OutcomeSource.Approval,
        "rework" => OutcomeSource.Rework,
        "failure" => OutcomeSource.Failure,
        _ => OutcomeSource.Rating
    }, DateTimeOffset.Parse(r.GetString(6), System.Globalization.CultureInfo.InvariantCulture))));
    public void RecordRework(IEnumerable<string> workIds)
    {
        foreach (var workId in workIds.Distinct())
            Execute("""
            INSERT INTO model_feedback(run_id,good,at,source)
            SELECT id,0,$at,'rework' FROM (SELECT id,failure FROM model_runs WHERE work_id=$work AND coordinator=0 ORDER BY at DESC,rowid DESC LIMIT 1) WHERE failure=0
            ON CONFLICT(run_id) DO UPDATE SET good=0,at=excluded.at,source='rework'
            """, ("$at", DateTimeOffset.UtcNow.ToString("O")), ("$work", workId));
    }

    public bool RateModelResult(string workId, bool good)
    {
        lock (gate)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
            INSERT INTO model_feedback(run_id,good,at,source)
            SELECT id,$good,$at,'rating' FROM (SELECT id,failure FROM model_runs WHERE work_id=$work AND coordinator=0 ORDER BY at DESC,rowid DESC LIMIT 1) WHERE failure=0
            ON CONFLICT(run_id) DO UPDATE SET good=excluded.good,at=excluded.at,source='rating'
            """;
            command.Parameters.AddWithValue("$good", good ? 1 : 0);
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$work", workId);
            InvalidateModelData();
            return command.ExecuteNonQuery() > 0;
        }
    }
}
