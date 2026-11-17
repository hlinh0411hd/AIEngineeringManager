using Employee.Core;

namespace Employee.Infrastructure;
public sealed class ProjectIndexer(IEmployeeStore store)
{
    public sealed record ScanResult(int Added, int Existing, int Unreadable, bool LimitReached);
    // Stop at project roots so solutions/monorepos are registered once, not per subproject.
    public ScanResult ScanAndRegister(string root, CancellationToken token = default)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Choose a physical folder, not a link.");
        var known = store.Projects().Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p.Root))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(root);
        int added = 0, existing = 0, unreadable = 0, visited = 0;
        while (pending.TryPop(out var folder))
        {
            token.ThrowIfCancellationRequested();
            if (++visited > 10000)
                return new(added, existing, unreadable, true);
            try
            {
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                    continue;
                if (known.Contains(folder))
                {
                    existing++;
                    continue;
                }

                var entries = new DirectoryInfo(folder).GetFileSystemInfos();
                bool project = entries.Any(e => e.Name.Equals(".git", StringComparison.OrdinalIgnoreCase)) || entries.OfType<FileInfo>().Any(f => ProjectMarkers.Contains(f.Name) || ProjectExtensions.Contains(f.Extension)) || (entries.OfType<DirectoryInfo>().Any(d => d.Name == "Assets") && entries.OfType<DirectoryInfo>().Any(d => d.Name == "ProjectSettings"));
                if (project)
                {
                    Register(folder, "", "");
                    known.Add(folder);
                    added++;
                    continue;
                }

                foreach (var child in entries.OfType<DirectoryInfo>().OrderByDescending(d => d.Name, StringComparer.OrdinalIgnoreCase))
                    if (!Excluded.Contains(child.Name) && !child.Name.StartsWith('.') && (child.Attributes & FileAttributes.ReparsePoint) == 0)
                        pending.Push(child.FullName);
            }
            catch (UnauthorizedAccessException)
            {
                unreadable++;
            }
            catch (IOException)
            {
                unreadable++;
            }
        }

        return new(added, existing, unreadable, false);
    }

    private static readonly HashSet<string> ProjectMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "package.json",
        "pyproject.toml",
        "requirements.txt",
        "Cargo.toml",
        "go.mod",
        "pom.xml",
        "build.gradle",
        "build.gradle.kts",
        "settings.gradle",
        "settings.gradle.kts",
        "CMakeLists.txt",
        "composer.json",
        "Gemfile",
        "pubspec.yaml"
    };
    private static readonly HashSet<string> ProjectExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".sln",
        ".slnx",
        ".csproj",
        ".fsproj",
        ".vbproj",
        ".vcxproj"
    };
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        "artifacts",
        "Data",
        "dist",
        "coverage",
        ".git",
        ".vs",
        ".idea",
        "node_modules",
        "bin",
        "obj",
        "Library",
        "Temp",
        "Logs",
        "Build",
        "Builds",
        ".venv",
        "venv",
        "__pycache__",
        ".aws",
        ".ssh"
    };
    public ProjectInfo Register(string root, string name, string aliases)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Choose the physical project folder, not a link.");
        if (store.Projects().Any(p => string.Equals(p.Root, root, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("This folder is already registered.");
        var technologies = new List<string>();
        if (Directory.Exists(Path.Combine(root, "Assets")) && Directory.Exists(Path.Combine(root, "ProjectSettings")))
            technologies.Add("Unity");
        if (Directory.EnumerateFiles(root, "*.csproj").Any() || Directory.EnumerateFiles(root, "*.sln*").Any())
            technologies.Add(".NET");
        if (File.Exists(Path.Combine(root, "package.json")))
            technologies.Add("Node.js");
        if (File.Exists(Path.Combine(root, "pyproject.toml")) || File.Exists(Path.Combine(root, "requirements.txt")))
            technologies.Add("Python");
        if (Directory.Exists(Path.Combine(root, ".git")) || File.Exists(Path.Combine(root, ".git")))
            technologies.Add("Git");
        var rules = Path.Combine(root, "AGENTS.md");
        var p = new ProjectInfo(Guid.NewGuid().ToString("N"), string.IsNullOrWhiteSpace(name) ? Path.GetFileName(root) : name.Trim(), root, aliases, string.Join(", ", technologies), SafeRead(rules, 12000));
        p = DiscoverKeywords(p);
        store.SaveProject(p);
        return p;
    }

    // Local search data only: never execute project files or interpret their text as instructions.
    public ProjectInfo RefreshKeywords(ProjectInfo project, CancellationToken token = default)
    {
        var updated = DiscoverKeywords(project, token);
        token.ThrowIfCancellationRequested();
        store.SaveProject(updated);
        return updated;
    }

    private static ProjectInfo DiscoverKeywords(ProjectInfo project, CancellationToken token = default)
    {
        if (!Directory.Exists(project.Root))
            throw new DirectoryNotFoundException(project.Root);
        if ((File.GetAttributes(project.Root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Choose a physical project folder, not a link.");
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        void Add(string text, int weight)
        {
            foreach (var word in Words(text).Distinct())
                counts[word] = counts.GetValueOrDefault(word) + weight;
        }

        Add(project.Name + " " + project.Technology, 10);
        var pending = new Stack<string>();
        pending.Push(project.Root);
        int visited = 0;
        while (pending.TryPop(out var folder) && visited < 10000)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                    continue;
                foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
                {
                    token.ThrowIfCancellationRequested();
                    if (++visited > 10000)
                        break;
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 || entry.Name.StartsWith('.') || Excluded.Contains(entry.Name))
                        continue;
                    if (entry is DirectoryInfo)
                    {
                        Add(entry.Name, 2);
                        pending.Push(entry.FullName);
                        continue;
                    }

                    var extension = Path.GetExtension(entry.Name);
                    if (!SearchExtensions.Contains(extension) && !ProjectMarkers.Contains(entry.Name))
                        continue;
                    Add(Path.GetFileNameWithoutExtension(entry.Name), 3);
                    // Only bounded root documentation/manifests; no arbitrary source, secrets or binary content.
                    if (folder == project.Root && (entry.Name.Equals("README.md", StringComparison.OrdinalIgnoreCase) || ProjectMarkers.Contains(entry.Name)))
                        try
                        {
                            Add(SafeRead(entry.FullName, 16000), 1);
                        }
                        catch (IOException)
                        {
                        }
                        catch (UnauthorizedAccessException)
                        {
                        }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return project with
        {
            Keywords = string.Join(" ", counts.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Take(512).Select(p => p.Key)),
            KeywordsUpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private static readonly HashSet<string> SearchExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs",
        ".xaml",
        ".ts",
        ".tsx",
        ".js",
        ".jsx",
        ".py",
        ".go",
        ".rs",
        ".java",
        ".md",
        ".csproj",
        ".sln",
        ".slnx",
        ".vue",
        ".razor",
        ".cpp",
        ".h"
    };
    private static readonly HashSet<string> StopWords = new("the and for with from this that project src tests test readme main index class public private using return string task fix update add file files app com org microsoft system version name true false null trong cua cho cac va sua them tinh nang".Split(' '), StringComparer.Ordinal);
    private static HashSet<string> Words(string text)
    {
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(\p{Ll})(\p{Lu})", "$1 $2");
        text = new string (text.Normalize(System.Text.NormalizationForm.FormD).Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray()).ToLowerInvariant().Replace('đ', 'd');
        return System.Text.RegularExpressions.Regex.Matches(text, @"[\p{L}\p{N}]{3,40}").Select(m => m.Value).Where(w => !StopWords.Contains(w) && !w.All(char.IsDigit)).ToHashSet(StringComparer.Ordinal);
    }

    public int Index(ProjectInfo project, CancellationToken token = default)
    {
        var files = new List<IndexedFile>();
        var folders = new Stack<string>();
        folders.Push(project.Root);
        while (folders.TryPop(out var folder))
        {
            token.ThrowIfCancellationRequested();
            foreach (var d in new DirectoryInfo(folder).EnumerateFileSystemInfos())
            {
                token.ThrowIfCancellationRequested();
                if ((d.Attributes & FileAttributes.ReparsePoint) != 0)
                    continue;
                if (d is DirectoryInfo)
                {
                    if (!Excluded.Contains(d.Name) && !d.Name.StartsWith('.'))
                        folders.Push(d.FullName);
                }
                else if (!d.Name.StartsWith('.') && !d.Name.EndsWith(".pem", StringComparison.OrdinalIgnoreCase) && d is FileInfo f)
                {
                    files.Add(new(project.Id, Path.GetRelativePath(project.Root, f.FullName), f.Length, f.LastWriteTimeUtc.Ticks));
                    if (files.Count >= 50000)
                        throw new InvalidOperationException("Index limit: 50,000 files. Choose a smaller project folder.");
                }
            }
        }

        RefreshKeywords(project, token);
        store.ReplaceIndex(project.Id, files);
        return files.Count;
    }

    public ProjectInfo? Resolve(string text, ProjectInfo? active = null)
    {
        if (active != null)
            return active;
        // Require complete names/paths: e.g. "app" must not match "mapping".
        // Multiple matches are deliberately left for the user to resolve.
        var matches = store.Projects().Where(p => new[] { p.Name, p.Root, Path.GetFileName(p.Root.TrimEnd('\\', '/')) }.Concat(p.Aliases.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Any(n => !string.IsNullOrWhiteSpace(n) && System.Text.RegularExpressions.Regex.IsMatch(text, @"(?<![\p{L}\p{N}_])" + System.Text.RegularExpressions.Regex.Escape(n.Trim()) + @"(?![\p{L}\p{N}_])", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant))).ToArray();
        if (matches.Length > 0)
            return matches.Length == 1 ? matches[0] : null;
        var words = Words(text);
        var candidates = store.Projects().Select(p => (Project: p, Hits: Words(p.Keywords ?? "").Intersect(words).ToHashSet())).ToArray();
        // Common stack words alone cannot choose a project. Require two terms unique across projects.
        var ranked = candidates.Select(c => (c.Project, Score: c.Hits.Count(w => candidates.Count(other => other.Hits.Contains(w)) == 1))).OrderByDescending(c => c.Score).ToArray();
        return ranked.Length > 0 && ranked[0].Score >= 2 && (ranked.Length == 1 || ranked[0].Score >= ranked[1].Score + 2) ? ranked[0].Project : null;
    }

    public static string SafeRead(string path, int limit)
    {
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            return "";
        using var reader = new StreamReader(path);
        var chars = new char[limit];
        var read = reader.ReadBlock(chars, 0, limit);
        return new string (chars, 0, read);
    }
}
