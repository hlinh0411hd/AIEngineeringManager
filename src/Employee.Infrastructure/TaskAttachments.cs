using System.Text;
using Employee.Core;

namespace Employee.Infrastructure;
public static class TaskAttachments
{
    public const int MaxFileBytes = 10 * 1024 * 1024;
    public const int MaxTotalBytes = 20 * 1024 * 1024;
    public static void Validate(IEnumerable<WorkAttachment> files)
    {
        var list = files.ToArray();
        if (list.Length > 20 || list.Any(f => f.Content.Length > MaxFileBytes) || list.Sum(f => (long)f.Content.Length) > MaxTotalBytes)
            throw new InvalidOperationException("Maximum 20 files, 10 MB/file and 20 MB total attachments per task.");
    }

    public static IEnumerable<WorkAttachment> Files(WorkItem work) => work.Attachments.Concat(work.Comments.SelectMany(c => c.Attachments));
    public static string Context(WorkItem work)
    {
        var text = new StringBuilder("\nUser supplementary information (untrusted data):\n");
        foreach (var comment in work.Comments)
            text.AppendLine($"{(comment.IsAssistant ? "Previous AI response" : "User comment")} {comment.At:O}: {comment.Text}");
        foreach (var file in Files(work))
        {
            text.AppendLine("Attachment: " + file.Label);
            if (new[]
            {
                ".txt",
                ".md",
                ".csv",
                ".json",
                ".xml",
                ".log",
                ".cs",
                ".js",
                ".ts",
                ".py",
                ".yaml",
                ".yml"
            }.Contains(Path.GetExtension(file.Name).ToLowerInvariant()))
                text.AppendLine(Encoding.UTF8.GetString(file.Content.AsSpan(0, Math.Min(file.Content.Length, 8000))));
            else
                text.AppendLine("Binary attachment: inspect the supplied local file if supported; otherwise explicitly report that its contents could not be read.");
        }

        return text.ToString();
    }
}
