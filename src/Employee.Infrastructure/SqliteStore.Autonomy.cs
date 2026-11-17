using System.Text.Json;
using Employee.Core;

namespace Employee.Infrastructure;
public sealed partial class SqliteStore
{
    public void SaveSession(WorkSession s) => Execute("INSERT INTO sessions VALUES($id,$data) ON CONFLICT(id) DO UPDATE SET data=$data", ("$id", s.Id), ("$data", JsonSerializer.Serialize(s)));
    public IReadOnlyList<WorkSession> Sessions() => Read("SELECT data FROM sessions", Decode<WorkSession>);
    public void SavePlan(PlanRecord p) => Execute("INSERT INTO plans VALUES($id,$session,$data) ON CONFLICT(id) DO UPDATE SET data=$data", ("$id", p.Id), ("$session", p.SessionId), ("$data", JsonSerializer.Serialize(p)));
    public IReadOnlyList<PlanRecord> Plans(string id) => Read("SELECT data FROM plans WHERE session_id=$id", Decode<PlanRecord>, ("$id", id));
    public void SaveDecision(PendingDecision d) => Execute("INSERT INTO decisions VALUES($id,$session,$data) ON CONFLICT(id) DO UPDATE SET data=$data", ("$id", d.Id), ("$session", d.SessionId), ("$data", JsonSerializer.Serialize(d)));
    public IReadOnlyList<PendingDecision> Decisions(string id) => Read("SELECT data FROM decisions WHERE session_id=$id", Decode<PendingDecision>, ("$id", id));
    public void SaveTestProfile(ProjectTestProfile p) => Execute("INSERT INTO test_profiles VALUES($id,$data) ON CONFLICT(id) DO UPDATE SET data=$data", ("$id", p.Id), ("$data", JsonSerializer.Serialize(p)));
    public IReadOnlyList<ProjectTestProfile> TestProfiles() => Read("SELECT data FROM test_profiles", Decode<ProjectTestProfile>);
    public void SaveTestJob(TestJob p) => Execute("INSERT INTO test_jobs VALUES($id,$data) ON CONFLICT(id) DO UPDATE SET data=$data", ("$id", p.Id), ("$data", JsonSerializer.Serialize(p)));
    public IReadOnlyList<TestJob> TestJobs() => Read("SELECT data FROM test_jobs", Decode<TestJob>);
    public void ApprovePlanAtomically(string planId, IReadOnlyList<WorkItem> tasks)
    {
        lock (gate)
        {
            using var tx = connection.BeginTransaction();
            using var select = connection.CreateCommand();
            select.Transaction = tx;
            select.CommandText = "SELECT data FROM plans WHERE id=$id";
            select.Parameters.AddWithValue("$id", planId);
            var plan = JsonSerializer.Deserialize<PlanRecord>(select.ExecuteScalar() as string ?? throw new InvalidOperationException("No plan found."))!;
            if (plan.State != DecisionState.Pending)
                throw new InvalidOperationException("Plan has already been processed.");
            foreach (var work in tasks)
            {
                if (work.SessionId != plan.SessionId)
                    throw new InvalidOperationException("Task does not belong to this session.");
                using var insert = connection.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = "INSERT INTO work VALUES($id,$data)";
                insert.Parameters.AddWithValue("$id", work.Id);
                insert.Parameters.AddWithValue("$data", WorkJson(work, tx));
                insert.ExecuteNonQuery();
            }

            plan.State = DecisionState.Approved;
            using var update = connection.CreateCommand();
            update.Transaction = tx;
            update.CommandText = "UPDATE plans SET data=$data WHERE id=$id";
            update.Parameters.AddWithValue("$data", JsonSerializer.Serialize(plan));
            update.Parameters.AddWithValue("$id", planId);
            update.ExecuteNonQuery();
            tx.Commit();
        }
    }
}
