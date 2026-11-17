using Employee.Core;

namespace Employee.Infrastructure;

// Read-only checks: never repair, reset or recreate a project automatically.
internal static class ProjectContinuity
{
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".plastic", ".vs", ".idea", ".dotnet-cli", ".aiemployee",
        "Library", "Temp", "Logs", "UserSettings", "node_modules", ".venv", "venv",
        "bin", "obj", "artifacts", "Data", "Release", "Build", "Builds", "dist", "coverage"
    };

    internal static string[] Capture(string root)
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var folder))
        {
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    continue;
                if (entry is DirectoryInfo directory)
                {
                    if (!Excluded.Contains(directory.Name)) pending.Push(directory.FullName);
                }
                else
                {
                    files.Add(entry.FullName);
                    if (files.Count > 100000)
                        throw new InvalidOperationException("Project continuity inventory exceeds 100,000 source files; choose a narrower project root. No project files were changed by this check.");
                }
            }
        }
        return files.ToArray();
    }

    internal static string? ExistingProjectProblem(ProjectInfo project, IReadOnlyList<IndexedFile> indexed)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(project.Root));
        var known = indexed.Where(f => f.ProjectId == project.Id).Select(f => Path.GetFullPath(Path.Combine(root, f.Path)))
            .Where(path => path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var missing = known.Where(path => !File.Exists(path)).ToArray();
        if (known.Length >= 20 && missing.Length * 5 >= known.Length * 4)
            return $"ProjectContentMissing: {missing.Length}/{known.Length} previously indexed files are missing. Restore or confirm the project changes and refresh its index before resuming. Do not rebuild an empty replacement project.";
        var assets = Path.Combine(root, "Assets");
        if (File.Exists(Path.Combine(root, "ProjectSettings", "ProjectVersion.txt")) &&
            indexed.Any(f => f.Path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || f.Path.StartsWith("Assets\\", StringComparison.OrdinalIgnoreCase)) &&
            (!Directory.Exists(assets) || Capture(assets).Length == 0))
            return "ProjectContentMissing: this existing Unity project's Assets folder is empty or missing. Restore its contents before continuing; no AI was called.";
        return null;
    }

    internal static string? MissingAfterRun(string[] before)
    {
        if (before.Length < 20) return null;
        var missing = before.Where(path => !File.Exists(path)).ToArray();
        if (missing.Length < 20 || missing.Length * 5 < before.Length) return null;
        return $"ProjectContentMissing: {missing.Length}/{before.Length} source files present before this AI call are now missing. Execution stopped; do not retry or recreate the project automatically. Inspect version control/backups. Examples: " + string.Join(", ", missing.Take(5));
    }
}
