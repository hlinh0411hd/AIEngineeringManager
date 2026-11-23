using Employee.Core;
using System.Text.RegularExpressions;

namespace Employee.Infrastructure;

public static class TaskResultMedia
{
    public const int MaxBytes = 45 * 1024 * 1024;
    // Any file type may be delivered except runnable/script types and secret-like files.
    private static readonly HashSet<string> Blocked = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".msi", ".bat", ".cmd", ".com", ".scr", ".ps1", ".psm1", ".vbs", ".vbe", ".wsf", ".lnk", ".jar", ".reg", ".hta", ".cpl", ".sys",
        ".env", ".pem", ".key", ".pfx", ".p12", ".sh"
    };
    public static bool IsMedia(string name)
    {
        var file = Path.GetFileName(name);
        var extension = Path.GetExtension(file);
        return extension.Length > 1 && !Blocked.Contains(extension) && !file.StartsWith(".env", StringComparison.OrdinalIgnoreCase);
    }

    // Only explicit output links inside the task project are eligible for automatic delivery.
    public static List<WorkAttachment> FromOutput(string output, string root)
    {
        var result = new List<WorkAttachment>();
        if (!Path.IsPathFullyQualified(root)) return result;
        var boundary = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var paths = Regex.Matches(output, @"!?\[[^\]\r\n]*\]\((?<path>[^\r\n)]+)\)|(?m)^File: (?<path>[^\r\n]+)")
            .Select(m => m.Groups["path"].Value.Trim().Trim('<', '>')).Distinct(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths.Take(40))
        {
            try
            {
                var full = Path.GetFullPath(path, boundary);
                if (!full.StartsWith(boundary, StringComparison.OrdinalIgnoreCase) || !IsMedia(full) || !File.Exists(full)) continue;
                if (!seen.Add(full)) continue;
                // Do not follow links out of the approved project.
                var part = new FileInfo(full) as FileSystemInfo;
                var linked = false;
                while (part != null && part.FullName.Length >= boundary.Length)
                {
                    if ((part.Attributes & FileAttributes.ReparsePoint) != 0) { linked = true; break; }
                    part = part is FileInfo f ? f.Directory : (part as DirectoryInfo)?.Parent;
                }
                var size = new FileInfo(full).Length;
                if (linked || size == 0 || total + size > MaxBytes || result.Count == 10) continue;
                var bytes = File.ReadAllBytes(full);
                if (total + bytes.Length > MaxBytes) continue;
                result.Add(new(Path.GetFileName(full), bytes));
                total += bytes.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
        return result;
    }

    public static IReadOnlyList<WorkAttachment> ForNotice(WorkItem work)
    {
        var since = work.Comments.LastOrDefault(c => !c.IsAssistant)?.At ?? DateTimeOffset.MinValue;
        return work.Comments.Where(c => c.IsAssistant && !c.IsSummary && c.At >= since)
            .SelectMany(c => c.Attachments).Where(a => IsMedia(a.Name) && a.Content.Length is > 0 and <= MaxBytes)
            .DistinctBy(a => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(a.Content)))
            .Take(10).ToArray();
    }
}
