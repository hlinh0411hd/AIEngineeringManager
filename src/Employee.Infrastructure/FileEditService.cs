using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Employee.Core;

namespace Employee.Infrastructure;
public sealed record FileSnapshot(string Path, string Sha256, string Content);
public sealed record ProposedEdit(string Path, string ExpectedSha256, string Content);
public sealed record EditProposal(string Summary, ProposedEdit[] Edits);
// Tool-free providers propose text. Only this deterministic, reviewed transaction can write it.
public sealed class FileEditService
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs",
        ".xaml",
        ".json",
        ".md",
        ".txt",
        ".js",
        ".ts",
        ".tsx",
        ".jsx",
        ".py",
        ".css",
        ".html",
        ".shader",
        ".toml",
        ".yaml",
        ".yml",
        ".xml",
        ".csproj"
    };
    public static string ResolveSafe(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':'))
            throw new InvalidOperationException("Edit path must be relative.");
        var segments = relative.Replace('\\', '/').Split('/');
        if (segments.Any(s => string.IsNullOrWhiteSpace(s) || s is "." or ".." || s.StartsWith('.') || s.EndsWith(' ') || s.EndsWith('.') || s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidOperationException("Unsafe edit path.");
        if (segments.Any(s => System.Text.RegularExpressions.Regex.IsMatch(s.Split('.')[0], @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
            throw new InvalidOperationException("Reserved Windows filename.");
        var forbidden = new[]
        {
            "secret",
            "credential",
            "password",
            "token",
            "private",
            "node_modules",
            "library",
            "bin",
            "obj",
            "build",
            "dist"
        };
        if (segments.Any(s => forbidden.Any(x => s.Contains(x, StringComparison.OrdinalIgnoreCase))) || !TextExtensions.Contains(Path.GetExtension(relative)))
            throw new InvalidOperationException("This file type/path is excluded from managed edits.");
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Edit escapes project root.");
        var current = fullRoot;
        foreach (var segment in segments.Prepend(""))
        {
            if (segment.Length > 0)
                current = Path.Combine(current, segment);
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Managed edits cannot follow links.");
        }

        return full;
    }

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    public IReadOnlyList<FileSnapshot> Capture(ProjectInfo project, IReadOnlyList<IndexedFile> files, int budget = 20000)
    {
        var list = new List<FileSnapshot>();
        foreach (var file in files.Take(100))
        {
            if (list.Count >= 8 || budget <= 0)
                break;
            try
            {
                var path = ResolveSafe(project.Root, file.Path);
                if (!File.Exists(path) || new FileInfo(path).Length > 32000)
                    continue;
                var bytes = File.ReadAllBytes(path);
                var text = new UTF8Encoding(false, true).GetString(bytes);
                if (text.Length > budget || text.Contains('\0'))
                    continue;
                list.Add(new(file.Path, Hash(bytes), text));
                budget -= text.Length;
            }
            catch (InvalidOperationException)
            {
            }
            catch (DecoderFallbackException)
            {
            }
        }

        return list;
    }

    public static EditProposal? Parse(string text)
    {
        text = text.Trim();
        if (text.StartsWith("```"))
        {
            var start = text.IndexOf('\n');
            var end = text.LastIndexOf("```");
            if (start >= 0 && end > start)
                text = text[(start + 1)..end].Trim();
        }

        try
        {
            var proposal = JsonSerializer.Deserialize<EditProposal>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return proposal is { Edits.Length: > 0 and <= 20 } && proposal.Edits.All(e => e.Path != null && e.ExpectedSha256 != null && e.Content != null) ? proposal : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Apply(ProjectInfo project, EditProposal proposal)
    {
        if (proposal.Edits.Length is < 1 or > 20 || proposal.Edits.Sum(e => e.Content.Length) > 200000)
            throw new InvalidOperationException("Proposal exceeds edit budget.");
        var prepared = new List<(string Path, byte[]? Original, byte[] Content)>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var edit in proposal.Edits)
        {
            var path = ResolveSafe(project.Root, edit.Path);
            if (!paths.Add(path))
                throw new InvalidOperationException("Duplicate edit path.");
            byte[]? original = File.Exists(path) ? File.ReadAllBytes(path) : null;
            var expected = original == null ? "NEW" : Hash(original);
            if (!expected.Equals(edit.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("File changed since handoff: " + edit.Path + ". Regenerate the proposal.");
            prepared.Add((path, original, new UTF8Encoding(false).GetBytes(edit.Content)));
        }

        var applied = new List<(string Path, byte[]? Original)>();
        try
        {
            foreach (var item in prepared)
            {
                // Re-check immediately before each write. A concurrent external editor must not be overwritten.
                ResolveSafe(project.Root, Path.GetRelativePath(project.Root, item.Path));
                var current = File.Exists(item.Path) ? File.ReadAllBytes(item.Path) : null;
                if ((current == null) != (item.Original == null) || (current != null && !current.SequenceEqual(item.Original!)))
                    throw new InvalidOperationException("Concurrent file edit detected.");
                Directory.CreateDirectory(Path.GetDirectoryName(item.Path)!);
                var temporary = item.Path + ".employee-" + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllBytes(temporary, item.Content);
                    File.Move(temporary, item.Path, true);
                    applied.Add((item.Path, item.Original));
                }
                finally
                {
                    if (File.Exists(temporary))
                        File.Delete(temporary);
                }
            }
        }
        catch
        {
            foreach (var item in applied.AsEnumerable().Reverse())
            {
                if (item.Original == null)
                    File.Delete(item.Path);
                else
                    File.WriteAllBytes(item.Path, item.Original);
            }

            throw;
        }
    }
}
