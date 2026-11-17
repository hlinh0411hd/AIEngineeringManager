using System.Text.Json;
using Employee.Core;

namespace Employee.Infrastructure;
public sealed partial class SqliteStore : IAdvancedModelStore
{
    public IReadOnlyList<UsageSummary> UsageSince(DateTimeOffset since) => Read("""
        SELECT r.provider,count(*),count(u.run_id),sum(u.input),sum(u.output),sum(u.cached)
        FROM model_runs r LEFT JOIN reported_usage u ON r.id=u.run_id WHERE r.at >= $since GROUP BY r.provider
        """, r => new UsageSummary(r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.IsDBNull(3) ? null : r.GetInt64(3), r.IsDBNull(4) ? null : r.GetInt64(4), r.IsDBNull(5) ? null : r.GetInt64(5)), ("$since", since.ToString("O")));
    public IReadOnlyList<ModelReliability> Reliability() => Read("""
        SELECT model,kind,count(*),sum(CASE WHEN failure IN (4,7) THEN 1 ELSE 0 END),max(at) FROM
        (SELECT model,kind,failure,at,row_number() OVER(PARTITION BY model,kind ORDER BY at DESC) n FROM model_runs WHERE provider='ollama' AND failure IN (0,4,7))
        WHERE n<=5 GROUP BY model,kind
        """, r => new ModelReliability(r.GetString(0), (WorkKind)r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), DateTimeOffset.Parse(r.GetString(4))));
    public void MarkAssignmentInvalid(string workId) => Execute("UPDATE model_runs SET failure=7 WHERE work_id=$id AND coordinator=1 AND provider='ollama'", ("$id", workId));
    public void SaveAdvancedEvaluation(AdvancedEvaluation value) => Execute("INSERT INTO advanced_evaluations VALUES($id,$data)", ("$id", value.Id), ("$data", JsonSerializer.Serialize(value)));
    public IReadOnlyList<AdvancedEvaluation> AdvancedEvaluations() => Read("SELECT data FROM advanced_evaluations ORDER BY rowid DESC LIMIT 100", Decode<AdvancedEvaluation>);
}
