using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Employee.Core;
using Microsoft.Data.Sqlite;

namespace Employee.Infrastructure;
// Attachment bytes live in attachment_blobs, keyed by SHA-256, so a task row stays a small JSON
// document. Writing a task stores its attachments once; reading fills them from an in-memory cache.
public sealed partial class SqliteStore
{
    private static readonly ConditionalWeakTable<byte[], string> AttachmentKeys = new();
    private readonly Dictionary<string, byte[]> blobCache = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions WorkStorage = new()
    {
        Converters =
        {
            new StoredAttachmentConverter()
        }
    };
    internal static string AttachmentKey(byte[] content) => AttachmentKeys.GetValue(content, bytes => Convert.ToHexString(SHA256.HashData(bytes)));
    private static IEnumerable<WorkAttachment> AllAttachments(WorkItem work) => work.Attachments.Concat(work.Comments.SelectMany(c => c.Attachments));
    // Serializes a task for storage and saves its attachment bytes in the same transaction.
    private string WorkJson(WorkItem work, SqliteTransaction? transaction)
    {
        foreach (var attachment in AllAttachments(work).Where(a => a.Content.Length > 0))
        {
            var key = AttachmentKey(attachment.Content);
            if (blobCache.ContainsKey(key))
                continue;
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT OR IGNORE INTO attachment_blobs(key,content) VALUES($key,$content)";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$content", attachment.Content);
            command.ExecuteNonQuery();
            blobCache[key] = attachment.Content;
        }

        return JsonSerializer.Serialize(work, WorkStorage);
    }

    private WorkItem DecodeWork(SqliteDataReader reader) => FillAttachments(JsonSerializer.Deserialize<WorkItem>(reader.GetString(0), WorkStorage)!);
    private WorkItem FillAttachments(WorkItem work)
    {
        work.Attachments = work.Attachments.Select(Fill).ToList();
        work.Comments = work.Comments.Select(c => c with { Attachments = c.Attachments.Select(Fill).ToList() }).ToList();
        return work;
    }

    private WorkAttachment Fill(WorkAttachment attachment)
    {
        if (attachment.Content.Length > 0 || attachment.Key == null)
            return attachment;
        if (!blobCache.TryGetValue(attachment.Key, out var content))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT content FROM attachment_blobs WHERE key=$key";
            command.Parameters.AddWithValue("$key", attachment.Key);
            content = command.ExecuteScalar() as byte[] ?? [];
            if (content.Length > 0)
                blobCache[attachment.Key] = content;
        }

        return attachment with
        {
            Content = content
        };
    }

    // One-time move of attachment bytes out of task rows written by earlier versions, then reclaims
    // the space. Unreferenced blobs (deleted tasks) are removed on every start.
    private void MigrateAttachments()
    {
        Execute("CREATE TABLE IF NOT EXISTS attachment_blobs(key TEXT PRIMARY KEY,content BLOB NOT NULL)");
        // Rewriting a row is idempotent, so every large row is simply stored again in the new form.
        var legacy = Read("SELECT id FROM work WHERE length(data)>20000", r => r.GetString(0));
        var moved = 0;
        foreach (var id in legacy)
        {
            lock (gate)
            {
                using var transaction = connection.BeginTransaction();
                using var select = connection.CreateCommand();
                select.Transaction = transaction;
                select.CommandText = "SELECT data FROM work WHERE id=$id";
                select.Parameters.AddWithValue("$id", id);
                var work = JsonSerializer.Deserialize<WorkItem>((string)select.ExecuteScalar()!, WorkStorage)!;
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE work SET data=$data WHERE id=$id";
                update.Parameters.AddWithValue("$data", WorkJson(work, transaction));
                update.Parameters.AddWithValue("$id", id);
                update.ExecuteNonQuery();
                transaction.Commit();
                moved++;
            }
        }

        PruneAttachmentBlobs();
        if (moved > 0)
            Execute("VACUUM");
    }

    private void PruneAttachmentBlobs()
    {
        var referenced = WorkItems().SelectMany(AllAttachments).Select(a => a.Key ?? AttachmentKey(a.Content)).ToHashSet(StringComparer.Ordinal);
        foreach (var key in Read("SELECT key FROM attachment_blobs", r => r.GetString(0)).Where(key => !referenced.Contains(key)))
        {
            Execute("DELETE FROM attachment_blobs WHERE key=$key", ("$key", key));
            lock (gate)
                blobCache.Remove(key);
        }
    }

    // Writes Name and Key only; reads legacy rows that still carry base64 Content.
    private sealed class StoredAttachmentConverter : JsonConverter<WorkAttachment>
    {
        public override WorkAttachment Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            string name = "", key = "";
            byte[] content = [];
            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException("Attachment object expected.");
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                var property = reader.GetString();
                reader.Read();
                switch (property)
                {
                    case "Name":
                        name = reader.GetString() ?? "";
                        break;
                    case "Key":
                        key = reader.GetString() ?? "";
                        break;
                    case "Content":
                        content = reader.TokenType == JsonTokenType.String ? reader.GetBytesFromBase64() : [];
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }

            return new WorkAttachment(name, content)
            {
                Key = key.Length > 0 ? key : content.Length > 0 ? AttachmentKey(content) : null
            };
        }

        public override void Write(Utf8JsonWriter writer, WorkAttachment value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("Name", value.Name);
            var key = value.Content.Length > 0 ? AttachmentKey(value.Content) : value.Key;
            if (key != null)
                writer.WriteString("Key", key);
            writer.WriteString("Content", "");
            writer.WriteEndObject();
        }
    }
}
