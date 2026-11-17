using System.Text.Json;
using Employee.Core;

namespace Employee.Infrastructure;

// Virtual Office action queue: the per-character lanes (current action first) survive a restart.
public sealed partial class SqliteStore : IOfficeActionStore
{
    public void SaveOfficeActions(IReadOnlyList<OfficeActionRecord> records)
    {
        lock (gate)
        {
            using var tx = connection.BeginTransaction();
            using (var clear = connection.CreateCommand())
            {
                clear.Transaction = tx;
                clear.CommandText = "DELETE FROM office_actions";
                clear.ExecuteNonQuery();
            }

            foreach (var record in records)
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = "INSERT INTO office_actions VALUES($id,$seat,$position,$data)";
                insert.Parameters.AddWithValue("$id", record.Id);
                insert.Parameters.AddWithValue("$seat", record.Seat);
                insert.Parameters.AddWithValue("$position", record.Position);
                insert.Parameters.AddWithValue("$data", JsonSerializer.Serialize(record));
                insert.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    public IReadOnlyList<OfficeActionRecord> OfficeActions() => Read("SELECT data FROM office_actions ORDER BY seat, position", Decode<OfficeActionRecord>);
}
