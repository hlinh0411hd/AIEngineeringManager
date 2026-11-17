using System.Diagnostics;
using System.Text;
using Employee.Core;

namespace Employee.Infrastructure;
public sealed record ProcessResult(int ExitCode, string Output);
public sealed record ExecutableLaunch(string Executable, string[] Prefix);
public sealed record CliSearchContext(string[] Folders, string LocalAppData);
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> args, string directory, string? input, int limit, Action<string>? output, CancellationToken token, IReadOnlyDictionary<string, string>? environment = null);
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> args, string directory, string? input, int limit, Action<string>? output, CancellationToken token, IReadOnlyDictionary<string, string>? environment = null)
    {
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        // Never silently route a subscription CLI through inherited paid API credentials.
        foreach (var key in new[]
        {
            "OPENAI_API_KEY",
            "CODEX_API_KEY",
            "ANTHROPIC_API_KEY",
            "GEMINI_API_KEY",
            "GOOGLE_API_KEY",
            "OPENROUTER_API_KEY",
            "AZURE_OPENAI_API_KEY"
        }

        )
            info.Environment.Remove(key);
        info.Environment["NO_COLOR"] = "1";
        if (environment != null)
            foreach (var(k, v)in environment)
                info.Environment[k] = v;
        using var process = new Process
        {
            StartInfo = info
        };
        process.Start();
        using var registration = token.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
        });
        var result = new StringBuilder();
        var gate = new object ();
        var emitted = 0;
        var tailLimit = args.Contains("--json") && args.Contains("--ephemeral") ? Math.Min(limit / 2, 32_000) : 0;
        var stdoutTail = new StringBuilder();
        var stdoutTruncated = false;
        async Task Pump(StreamReader reader)
        {
            var buffer = new char[2048];
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None)) > 0)
            {
                string chunk;
                lock (gate)
                {
                    var take = Math.Min(count, Math.Max(0, limit - emitted));
                    chunk = new string (buffer, 0, take);
                    if (tailLimit > 0 && ReferenceEquals(reader, process.StandardOutput))
                    {
                        stdoutTruncated |= take < count;
                        stdoutTail.Append(buffer, 0, count);
                        if (stdoutTail.Length > tailLimit) stdoutTail.Remove(0, stdoutTail.Length - tailLimit);
                    }
                    emitted += take;
                    result.Append(chunk);
                    if (chunk.Length > 0)
                        output?.Invoke(chunk);
                }
            }
        }

        var stdout = Pump(process.StandardOutput);
        var stderr = Pump(process.StandardError);
        try
        {
            if (input != null)
                await process.StandardInput.WriteAsync(input.AsMemory(), token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            token.ThrowIfCancellationRequested();
            if (stdoutTruncated)
            {
                result.Length = Math.Min(result.Length, Math.Max(0, limit - stdoutTail.Length - 1));
                result.Append('\n').Append(stdoutTail);
            }
            return new(process.ExitCode, result.ToString());
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(true);
            }
            catch (InvalidOperationException)
            {
            }

            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            throw;
        }
    }

    public static string? FindExecutable(string name, IEnumerable<string>? folders = null)
    {
        if (Path.IsPathFullyQualified(name))
            return File.Exists(name) && Path.GetExtension(name).Equals(".exe", StringComparison.OrdinalIgnoreCase) ? name : null;
        foreach (var dir in folders ?? (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var folder = Environment.ExpandEnvironmentVariables(dir.Trim().Trim('"'));
            if (!Path.IsPathFullyQualified(folder))
                continue;
            var path = Path.Combine(folder, name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe");
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    public static ExecutableLaunch? ResolveCli(string name, string providerId, CliSearchContext? search = null)
    {
        search ??= DefaultCliSearch();
        var orderedFolders = providerId == "copilot" ? search.Folders.OrderBy(p => p.Contains("copilot-shim", StringComparison.OrdinalIgnoreCase) ? 1 : 0).ToArray() : search.Folders;
        var native = FindExecutable(name, orderedFolders);
        var desktopShim = providerId == "copilot" && native?.Contains("copilot-shim", StringComparison.OrdinalIgnoreCase) == true;
        if (native != null && !desktopShim)
            return new(native, []);
        // The native agy installer does not need to be on the PATH inherited by Explorer.
        // Never replace an explicit user-configured executable with this default installation.
        if (providerId == "antigravity" && (name.Equals("agy", StringComparison.OrdinalIgnoreCase) || name.Equals("agy.exe", StringComparison.OrdinalIgnoreCase)))
        {
            var installed = Path.Combine(search.LocalAppData, "agy", "bin", "agy.exe");
            if (File.Exists(installed))
                return new(installed, []);
        }
        // An explicit configured path must never silently select a different installation.
        if (providerId == "codex" && (name.Equals("codex", StringComparison.OrdinalIgnoreCase) || name.Equals("codex.exe", StringComparison.OrdinalIgnoreCase)))
        {
            var desktopBin = Path.Combine(search.LocalAppData, "OpenAI", "Codex", "bin");
            try
            {
                if (Directory.Exists(desktopBin))
                {
                    var installed = Directory.EnumerateDirectories(desktopBin).Select(d => new FileInfo(Path.Combine(d, "codex.exe"))).Where(f => f.Exists).OrderByDescending(f => f.LastWriteTimeUtc).ThenBy(f => f.FullName, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                    if (installed != null)
                        return new(installed.FullName, []);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        // Recognize only standard installed npm launchers and invoke the package EXE or Node directly, never cmd.exe.
        var candidates = Path.IsPathFullyQualified(name) ? new[]
        {
            name
        }

        : search.Folders.Where(Path.IsPathFullyQualified).Select(d => Path.Combine(d, name + ".cmd"));
        var package = providerId switch
        {
            "claude" => "@anthropic-ai/claude-code/",
            "gemini" => "@google/gemini-cli/",
            "opencode" => "opencode-ai/",
            "copilot" => "@github/copilot/",
            "freebuff" => "freebuff/",
            "commandcode" => "command-code/",
            _ => ""
        };
        if (package.Length == 0)
            return null;
        foreach (var shim in candidates)
        {
            if (!File.Exists(shim) || !Path.GetExtension(shim).Equals(".cmd", StringComparison.OrdinalIgnoreCase))
                continue;
            var info = new FileInfo(shim);
            if (info.Length > 16000 || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                continue;
            var match = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(shim), "(?<path>node_modules[\\\\/][^\"\\r\\n]+?\\.(?:exe|js|cjs|mjs))");
            if (!match.Success)
                continue;
            var relative = match.Groups["path"].Value.Replace('\\', '/');
            if (!relative.StartsWith("node_modules/" + package, StringComparison.OrdinalIgnoreCase) || relative.Contains(".."))
                continue;
            var folder = Path.GetDirectoryName(Path.GetFullPath(shim))!;
            var script = Path.GetFullPath(Path.Combine(folder, relative));
            if (Path.GetExtension(script).Equals(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(script))
                return new(script, []);
            var localNode = Path.Combine(folder, "node.exe");
            var node = File.Exists(localNode) ? localNode : FindExecutable("node", search.Folders);
            if (node != null && File.Exists(script))
                return new(node, [script]);
        }

        // Desktop/SDK can materialize a complete CLI runtime outside PATH.
        // Only default command discovery may fall back here; configured paths stay authoritative.
        if (providerId == "copilot" && (name.Equals("copilot", StringComparison.OrdinalIgnoreCase) || name.Equals("copilot.exe", StringComparison.OrdinalIgnoreCase)))
        {
            var runtime = FindCopilotSdkRuntime(search.LocalAppData);
            if (runtime != null)
                return new(runtime, []);
        }

        return desktopShim && native != null ? new(native, []) : null;
    }

    private static string? FindCopilotSdkRuntime(string localAppData)
    {
        var root = Path.Combine(localAppData, "github-copilot-sdk", "cli");
        try
        {
            if (!Directory.Exists(root))
                return null;
            // Inspect known bundle roots only, never arbitrary plugin executables or SDK entry points.
            var candidates = Directory.EnumerateDirectories(root, "copilot-sdk-runtime-*", SearchOption.TopDirectoryOnly).Where(folder => (File.GetAttributes(folder) & FileAttributes.ReparsePoint) == 0).Select(folder => new FileInfo(Path.Combine(folder, "copilot.exe"))).Where(file => file.Exists && (file.Attributes & FileAttributes.ReparsePoint) == 0).OrderByDescending(file => file.LastWriteTimeUtc).ThenBy(file => file.FullName, StringComparer.OrdinalIgnoreCase);
            return candidates.FirstOrDefault()?.FullName;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static CliSearchContext DefaultCliSearch()
    {
        // Explorer can retain an older PATH. Read current persisted user/system PATH as well.
        var paths = new List<string>
        {
            Environment.GetEnvironmentVariable("PATH") ?? ""
        };
        foreach (var target in new[]
        {
            EnvironmentVariableTarget.User,
            EnvironmentVariableTarget.Machine
        }

        )
        {
            try
            {
                paths.Add(Environment.GetEnvironmentVariable("PATH", target) ?? "");
            }
            catch (System.Security.SecurityException)
            {
            }
        }

        paths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
        paths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"));
        paths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".bun", "bin"));
        paths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links"));
        paths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "GitHub Copilot", "bin"));
        var folders = paths.SelectMany(p => p.Split(Path.PathSeparator)).Select(p => Environment.ExpandEnvironmentVariables(p.Trim().Trim('"'))).Where(Path.IsPathFullyQualified).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new(folders, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    }
}

public static class ProviderErrors
{
    // Only the provider's failing process can trigger fallback. Successful generated content cannot.
    public static FailureKind Classify(int exitCode, string output)
    {
        if (exitCode == 0)
            return FailureKind.None;
        var s = output.ToLowerInvariant();
        // The model's own safety filter declined this prompt. Another model can usually do the step.
        if (new[]
        {
            "safeguards flagged",
            "can't respond to this message",
            "cannot respond to this message",
            "anthropic.com/legal/aup",
            "content_policy_violation",
            "content policy violation",
            "responsibleaipolicyviolation",
            "content_filter",
            "flagged by our content filter"
        }.Any(s.Contains))
            return FailureKind.ModelRefused;
        // Model IDs often occur between "model" and the diagnosis (including JSON errors).
        // Keep these model-scoped, otherwise one bad catalog entry disables the whole AI.
        if (System.Text.RegularExpressions.Regex.IsMatch(s, @"\bmodel\b[^\r\n]{0,160}?\b(does not exist|not found|not supported|not available|unavailable|overloaded|not accessible|not permitted|no access|do not have access)\b") ||
            s.Contains("no access to model") || s.Contains("do not have access to the model"))
            return FailureKind.ModelUnavailable;
        if (new[]
        {
            "model is at capacity",
            "model_not_found",
            "model_not_supported",
            "model_not_available",
            "unknown model",
            "model is not available",
            "model rate limit",
            "model quota exceeded"
        }.Any(s.Contains))
            return FailureKind.ModelUnavailable;
        if (new[]
        {
            "rate limit",
            "rate_limit",
            "quota exceeded",
            "usage limit",
            "too many requests",
            "insufficient_quota",
            "you have hit your limit",
            "you've hit your limit",
            "credit balance is too low",
            "premium request limit",
            "rate_limit_exceeded",
            "usage_limit_reached",
            "insufficient credits",
            "quota has been exceeded",
            "exhausted your premium request",
            "premium request allowance exhausted"
        }.Any(s.Contains) ||
            // Claude Code: "You've hit your 5-hour limit · resets 8:20pm", "...your weekly limit...".
            System.Text.RegularExpressions.Regex.IsMatch(s, @"\b(?:you'?ve|you have) (?:hit|reached) your [a-z0-9 -]{0,30}limit\b"))
            return FailureKind.RateLimit;
        if (new[]
        {
            "not logged in",
            "authentication failed",
            "unauthorized",
            "please log in",
            "please login",
            "invalid api key"
        }.Any(s.Contains))
            return FailureKind.Authentication;
        if (new[]
        {
            "not found",
            "not recognized",
            "econnrefused",
            "service unavailable",
            "econnreset",
            "etimedout",
            "enotfound",
            "fetch failed",
            "connection reset",
            "connection timed out",
            "connection error",
            "server overloaded",
            "overloaded_error",
            "bad gateway",
            "gateway timeout"
        }.Any(s.Contains))
            return FailureKind.Unavailable;
        return FailureKind.TaskError;
    }
}
