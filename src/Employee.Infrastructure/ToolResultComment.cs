using System.Text;
using System.Text.Json.Nodes;
using Employee.Core;

namespace Employee.Infrastructure;

// A tool step's result as a readable comment: what was made, the image itself to look at, and the
// files/folders it produced (a video, a sheet) as lines the comment view turns into Open buttons.
public static class ToolResultComment
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".webp"];
    public const string FilePrefix = "File: ", FolderPrefix = "Folder: ";
    private const int MaxImages = 6, MaxImageBytes = 4 * 1024 * 1024;

    public static (string Text, List<WorkAttachment> Attachments)? Build(string toolResult, string stepRequest)
    {
        JsonObject? json;
        try
        {
            json = JsonNode.Parse(toolResult) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
        if (json == null)
            return null;

        var files = new List<string>();
        Collect(json, files, 0);
        // The copy in the project comes first; the tool's own artifact copy only when there is no project copy.
        var project = json["projectAssetPath"]?.ToString();
        if (project != null && File.Exists(project))
            files.RemoveAll(f => f != project && f.Contains(Path.DirectorySeparatorChar + "tool-runs" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        files = files.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(f => string.Equals(f, project, StringComparison.OrdinalIgnoreCase)).ToList();

        var text = new StringBuilder();
        var status = json["status"]?.ToString();
        text.AppendLine("Kết quả công cụ" + (status != null ? ": " + status : "") + ".");
        if (json["sheetWidth"] != null)
        {
            var columns = json["columns"]?.ToString() ?? "1";
            var rows = json["rows"]?.ToString() ?? "1";
            text.AppendLine($"Image {json["sheetWidth"]}×{json["sheetHeight"]}" + (columns != "1" || rows != "1" ? $", {columns}×{rows} frames of {json["frameWidth"]}×{json["frameHeight"]}" : "") + ".");
        }
        if (ToolTaskExecutor.QualitySummary(json["result"]?["quality"]) is { } quality)
            text.AppendLine(quality);
        var attachments = new List<WorkAttachment>();
        foreach (var file in files)
        {
            var extension = Path.GetExtension(file).ToLowerInvariant();
            if (TaskResultMedia.IsMedia(file) && attachments.Count < MaxImages && File.Exists(file) && new FileInfo(file).Length <= (ImageExtensions.Contains(extension) ? MaxImageBytes : TaskResultMedia.MaxBytes)
                && attachments.Sum(a => (long)a.Content.Length) + new FileInfo(file).Length <= TaskResultMedia.MaxBytes)
                attachments.Add(new WorkAttachment(Path.GetFileName(file), File.ReadAllBytes(file)));
        }
        var shown = files.Take(8).ToList();
        if (shown.Count > 0)
        {
            text.AppendLine();
            foreach (var file in shown.Where(File.Exists))
                text.AppendLine(FilePrefix + file);
            foreach (var folder in shown.Where(Directory.Exists).Concat(shown.Where(File.Exists).Select(Path.GetDirectoryName).OfType<string>()).Distinct(StringComparer.OrdinalIgnoreCase).Take(3))
                text.AppendLine(FolderPrefix + folder);
        }
        return (text.ToString().TrimEnd(), attachments);
    }

    // Absolute paths to existing files (images, media, outputs) or folders anywhere in the result.
    private static void Collect(JsonNode? node, List<string> files, int depth)
    {
        if (node == null || depth > 8 || files.Count > 40)
            return;
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                    if (key is not ("image" or "input" or "requestSettings"))
                        Collect(value, files, depth + 1);
                break;
            case JsonArray array:
                foreach (var item in array)
                    Collect(item, files, depth + 1);
                break;
            case JsonValue value when value.TryGetValue<string>(out var text) && text.Length is > 3 and < 400 && !text.StartsWith("data:", StringComparison.Ordinal):
                if (!Path.IsPathFullyQualified(text))
                    return;
                try
                {
                    var extension = Path.GetExtension(text).ToLowerInvariant();
                    if (File.Exists(text) && TaskResultMedia.IsMedia(text))
                        files.Add(Path.GetFullPath(text));
                    else if (Directory.Exists(text))
                        files.Add(Path.GetFullPath(text));
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
                {
                }
                break;
        }
    }
}
