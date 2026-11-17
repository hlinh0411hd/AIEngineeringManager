using System.Text.Json;
using System.Text.RegularExpressions;
using Employee.Core;

namespace Employee.Infrastructure;

// The command that checks a project still builds (and passes its tests) after code steps: the
// user's own test command when one is configured, otherwise one detected from the project.
public sealed record AutoCheckCommand(string Name, string Executable, string[] Arguments, int TimeoutSeconds);
public sealed record AutoCheckResult(bool Ran, bool Passed, string Command, string Output);

public static class AutoCheck
{
    public static AutoCheckCommand? Detect(ProjectInfo project, IEnumerable<ProjectTestProfile> profiles)
    {
        var own = profiles.FirstOrDefault(p => p.ProjectId == project.Id && p.Enabled);
        if (own != null)
            return new(own.Name, own.Executable, own.Arguments, own.TimeoutSeconds);
        var root = project.Root;
        if (!Directory.Exists(root))
            return null;
        bool Exists(string relative) => File.Exists(Path.Combine(root, relative)) || Directory.Exists(Path.Combine(root, relative));
        // The project's own declared self-check (written by the steps that built it) comes before guessing.
        if (Declared(root) is { } declared)
            return declared;
        // Unity: compile the scripts through the editor-generated projects (they build into Temp\).
        if (Exists("Assets") && Exists("ProjectSettings"))
        {
            var csproj = Exists("Assembly-CSharp-Editor.csproj") ? "Assembly-CSharp-Editor.csproj" : Exists("Assembly-CSharp.csproj") ? "Assembly-CSharp.csproj" : null;
            return csproj == null ? null : new("Unity script compile (" + csproj + ")", "dotnet", ["build", csproj, "-nologo", "-v:q", "-clp:ErrorsOnly"], 600);
        }
        // .NET: build the solution; run its tests when it has test projects.
        var solutions = Directory.GetFiles(root, "*.sln");
        if (solutions.Length == 1)
        {
            var hasTests = Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories).Take(200).Any(p => Path.GetFileNameWithoutExtension(p).Contains("Test", StringComparison.OrdinalIgnoreCase));
            return new(".NET " + (hasTests ? "build and tests" : "build"), "dotnet", [hasTests ? "test" : "build", Path.GetFileName(solutions[0]), "-nologo", "-v:q"], 900);
        }
        // Node: its own test runner when the project has tests.
        if (Exists("package.json") && (Exists("test") || Exists("tests")))
        {
            try
            {
                using var package = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "package.json")));
                if (package.RootElement.TryGetProperty("scripts", out var scripts) && scripts.TryGetProperty("test", out var test) && test.GetString() is { } script && script.Contains("node --test", StringComparison.Ordinal))
                    return new("Node tests", "node", ["--test"], 600);
            }
            catch (JsonException)
            {
            }
            return null;
        }
        // Python: pytest when there are tests, otherwise a syntax compile of the sources.
        if (Exists("pyproject.toml") || Exists("requirements.txt") || Directory.GetFiles(root, "*.py").Length > 0)
            return Exists("tests") || Exists("pytest.ini")
                ? new("Python tests", "python", ["-m", "pytest", "-q"], 900)
                : new("Python syntax check", "python", ["-m", "compileall", "-q", "-x", @"(\.venv|venv|node_modules)", "."], 300);
        return null;
    }

    public const string DeclaredFile = ".aiemployee/check.json";
    // Runtimes a declared self-check may start. Never a shell: the command is one program with arguments.
    private static readonly string[] Runners = ["node", "python", "py", "dotnet", "godot", "cargo", "go", "java", "deno", "bun"];
    // Workers do not always use the documented names: "command"/"cmd" and "args" are read too, and a
    // whole command line in "command" ("node tools/check.mjs") is split on spaces.
    private sealed record DeclaredCheck(string? Name, string? Executable, string[]? Arguments, int? TimeoutSeconds, string? Command = null, string? Cmd = null, string[]? Args = null);

    // .aiemployee/check.json = {"name","executable","arguments":[...],"timeoutSeconds"}: the headless
    // build/test command of a project the manager cannot recognise by its files (a new game, a custom
    // layout). It is data written by AI steps, so it is checked like a test profile: a known runtime
    // by name, arguments that stay inside the project, no release/publish commands.
    public static AutoCheckCommand? Declared(string root)
    {
        try
        {
            var path = Path.Combine(root, ".aiemployee", "check.json");
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > 20000 || (file.Attributes & FileAttributes.ReparsePoint) != 0)
                return null;
            var check = JsonSerializer.Deserialize<DeclaredCheck>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true, AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip });
            var executable = (check?.Executable ?? check?.Command ?? check?.Cmd ?? "").Trim();
            var arguments = check?.Arguments ?? check?.Args ?? [];
            if (arguments.Length == 0 && executable.Contains(' '))
            {
                var parts = executable.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                executable = parts[0];
                arguments = parts[1..];
            }
            if (executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                executable = executable[..^4];
            if (!Runners.Contains(executable, StringComparer.OrdinalIgnoreCase) || arguments.Length is 0 or > 24)
                return null;
            foreach (var argument in arguments)
            {
                if (argument == null || argument.Length > 300 || argument.Contains('\0') || Path.IsPathRooted(argument) || argument.Replace('\\', '/').Split('/').Contains(".."))
                    return null;
                if (argument.Equals("Release", StringComparison.OrdinalIgnoreCase) || argument.EndsWith("=Release", StringComparison.OrdinalIgnoreCase) || argument.Equals("publish", StringComparison.OrdinalIgnoreCase) || argument.Equals("pack", StringComparison.OrdinalIgnoreCase) || argument.Equals("install", StringComparison.OrdinalIgnoreCase))
                    return null;
            }
            // Inline code would let the file run anything; the check must be a script kept in the project.
            var isDotnet = executable.Equals("dotnet", StringComparison.OrdinalIgnoreCase);
            if (arguments.Any(a => a is "-e" or "--eval" or "-p" or "--print" or "eval" || a == "-c" && !isDotnet))
                return null;
            var name = string.IsNullOrWhiteSpace(check!.Name) ? "Project self-check" : check.Name.Trim();
            return new((name.Length > 60 ? name[..60] : name) + $" ({executable.ToLowerInvariant()} {string.Join(' ', arguments)})", executable.ToLowerInvariant(), arguments, Math.Clamp(check.TimeoutSeconds ?? 300, 30, 1800));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    // The lines that say what failed (errors, failing tests), for the fix step and to tell whether a
    // new failure is the same as the last one.
    public static string Digest(string output)
    {
        var lines = output.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);
        var important = lines.Where(l => Regex.IsMatch(l, @"(?i)\berror\b|\bfail(ed|ure)?\b|exception|assert|traceback|not ok|✖")).Distinct().Take(40).ToArray();
        var text = string.Join("\n", important.Length > 0 ? important : lines.TakeLast(30));
        return text.Length > 4000 ? text[..4000] : text;
    }
}
