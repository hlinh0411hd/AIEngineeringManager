using Employee.Core;
using System.Text.Json;

namespace Employee.Infrastructure;
public sealed class CliProvider(string id, IProcessRunner runner) : IAIProvider, IModelCatalogStatus
{
    public string Id { get; } = id;

    private string? executable;
    private string[] prefix = [];
    private Task<ProcessResult> Run(IReadOnlyList<string> args, string directory, string? input, int limit, Action<string>? output, CancellationToken token, IReadOnlyDictionary<string, string>? env = null) => runner.RunAsync(executable!, [..prefix, ..args], directory, input, limit, output, token, env);
    private bool ready;
    private string helpText = "";
    public string ModelDiscoveryDetail { get; private set; } = "Model catalog not inspected yet.";

    public async Task<ProviderHealth> DetectAsync(ProviderSettings settings, CancellationToken token)
    {
        ready = false;
        var name = string.IsNullOrWhiteSpace(settings.Executable) ? (Id switch { "commandcode" => "cmdc", "qoder" => "qodercli", "antigravity" => "agy", _ => Id }) : settings.Executable;
        var launch = ProcessRunner.ResolveCli(name, Id);
        if (launch == null && Id == "commandcode" && string.IsNullOrWhiteSpace(settings.Executable))
            launch = ProcessRunner.ResolveCli("command-code", Id);
        executable = launch?.Executable;
        prefix = launch?.Prefix ?? [];
        if (executable == null)
            return new(Id, false, false, false, "Executable not located. Checked process/user/system PATH, npm launchers and Codex Desktop installation. Configure an executable path if installed elsewhere.");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            var r = await Run(["--help"], Path.GetTempPath(), null, 40000, null, timeout.Token);
            var help = r.Output;
            helpText = help;
            if (Id == "codex" && r.ExitCode == 0)
                help += (await Run(["exec", "--help"], Path.GetTempPath(), null, 40000, null, timeout.Token)).Output;
            if (Id == "commandcode")
                return new(Id, true, false, false, "CommandCode catalog integration. Execution is disabled until compatible tool permissions can be verified.");
            if (Id == "freebuff")
            {
                var interactive = r.ExitCode == 0 && help.Contains("--cwd", StringComparison.OrdinalIgnoreCase) && help.Contains("--continue", StringComparison.OrdinalIgnoreCase) && help.Contains("login", StringComparison.OrdinalIgnoreCase);
                return new(Id, true, false, false, "Found: " + (prefix.FirstOrDefault() ?? executable) + ". " + (interactive ? "Freebuff CLI is an interactive terminal UI: its help advertises no headless prompt, print/JSON output or model-selection flag, so noninteractive execution is blocked and no AI call was made. Use Freebuff in a terminal; models can be catalogued from its local settings." : "Freebuff help did not advertise the expected interactive options; no supported noninteractive execution mode was verified. Execution blocked."), "Unknown");
            }

            var required = Id switch
            {
                "codex" => new[]
                {
                    "--sandbox",
                    "--model",
                    "--json",
                    "--skip-git-repo-check",
                    "--ignore-user-config",
                    "--ephemeral",
                    "--ask-for-approval"
                },
                "claude" => ["--print", "--tools", "--model", "--strict-mcp-config", "--mcp-config", "--disallowedTools"],
                "copilot" => ["--prompt", "--model", "--deny-tool", "--no-ask-user", "--available-tools", "--disable-builtin-mcps", "--no-auto-update"],
                "gemini" => ["--prompt", "--model", "--admin-policy"],
                "opencode" => ["run", "models"],
                "qoder" => ["--print", "--model", "--permission-mode", "--tools", "--cwd", "--list-models"],
                "antigravity" => ["--print", "--model", "--output-format", "--mode", "--add-dir", "--sandbox"],
                _ => Array.Empty<string>()};
            ready = r.ExitCode == 0 && required.Length > 0 && required.All(flag => help.Contains(flag, StringComparison.OrdinalIgnoreCase)) && !help.Contains("CLI was not found", StringComparison.OrdinalIgnoreCase);
            if (Id == "gemini")
            {
                var systemPolicy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "gemini-cli", "policies");
                if (Directory.Exists(systemPolicy) && Directory.EnumerateFiles(systemPolicy, "*.toml").Any())
                    ready = false;
            }

            if (Id == "opencode" && ready)
            {
                var sub = await Run(["run", "--help"], Path.GetTempPath(), null, 20000, null, timeout.Token);
                ready = sub.ExitCode == 0 && sub.Output.Contains("--model") && sub.Output.Contains("--format");
            }

            var imageGenerationDetail = "Image generation metadata not inspected; tool/account availability unknown.";
            if (Id == "codex" && ready && helpText.Contains("features", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var features = await Run(["features", "list"], Path.GetTempPath(), null, 20000, null, timeout.Token);
                    var feature = System.Text.RegularExpressions.Regex.Match(features.Output, @"(?m)^\s*image_generation[ \t]+\S+[ \t]+(?<enabled>true|false)[ \t]*\r?$");
                    if (features.ExitCode == 0 && feature.Success)
                        imageGenerationDetail = "CLI image_generation flag=" + feature.Groups["enabled"].Value + "; actual tool/account access unverified until generation.";
                }
                catch (Exception error)when (!token.IsCancellationRequested && (error is OperationCanceledException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception))
                {
                    imageGenerationDetail = "Image generation feature metadata unavailable; core CLI detection remains valid.";
                }
            }

            var authentication = "Unknown";
            if (Id == "claude" && ready && help.Contains("auth"))
            {
                try
                {
                    var authHelp = await Run(["auth", "--help"], Path.GetTempPath(), null, 10000, null, timeout.Token);
                    if (authHelp.ExitCode == 0 && authHelp.Output.Contains("status"))
                    {
                        var statusHelp = await Run(["auth", "status", "--help"], Path.GetTempPath(), null, 10000, null, timeout.Token);
                        if (statusHelp.ExitCode == 0 && statusHelp.Output.Contains("--json"))
                        {
                            var status = await Run(["auth", "status", "--json"], Path.GetTempPath(), null, 10000, null, timeout.Token);
                            using var auth = JsonDocument.Parse(status.Output);
                            if (auth.RootElement.ValueKind == JsonValueKind.Object && auth.RootElement.TryGetProperty("loggedIn", out var logged) && logged.ValueKind is JsonValueKind.True or JsonValueKind.False)
                            {
                                var method = auth.RootElement.TryGetProperty("authMethod", out var mode) && mode.ValueKind == JsonValueKind.String ? mode.GetString() : "";
                                authentication = !logged.GetBoolean() ? "NotAuthenticated" : method is "claude.ai" or "oauth_token" ? "Authenticated" : "Configured (billing/login type unverified)";
                            }
                        }
                    }
                }
                catch (Exception error)when (!token.IsCancellationRequested && (error is OperationCanceledException or IOException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception))
                { /* Optional authentication metadata must not invalidate verified CLI capabilities. */
                }
            }

            var copilotDesktop = Id == "copilot" && File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "GitHub Copilot", "github.exe"));
            if (Id == "copilot" && help.Contains("CLI was not found", StringComparison.OrdinalIgnoreCase))
                return new ProviderHealth(Id, true, false, false, "Copilot launcher found, but it reports the standalone CLI is not installed/located. " + (copilotDesktop ? "GitHub Copilot Desktop is installed; Desktop and standalone CLI are separate installations. " : "") + "Configure the real CLI executable; the manager does not launch the GUI or install software automatically.")
                {
                    StandaloneCliMissing = true
                };
            return new(Id, true, ready, ready, "Found: " + (prefix.FirstOrDefault() ?? executable) + ". " + (ready ? "Headless flags verified. Authentication: " + authentication + "; usage/quota Unknown. " + (Id == "codex" ? "Sandboxed project access supported. " + imageGenerationDetail : Id is "claude" or "qoder" or "antigravity" ? "Works in the project folder: reads files; on WriteProject steps edits files directly and runs the commands it needs." : "Tool-free proposals; project writes require reviewed, hash-checked application by manager.") : "CLI missing required safety/headless flags, or shim reports unavailable. Execution blocked."), authentication);
        }
        catch (Exception ex)when (ex is not OperationCanceledException || !token.IsCancellationRequested)
        {
            return new(Id, true, false, false, ex.Message);
        }
    }

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken token)
    {
        var catalog = new List<ModelInfo>();
        if (Id == "codex")
        {
            catalog.AddRange(CliModelCatalog.CodexCache(out var note));
            ModelDiscoveryDetail = note;
        }
        else if (Id is "claude" or "copilot")
        {
            catalog.AddRange(CliModelCatalog.HelpModels(Id, helpText));
            ModelDiscoveryDetail = catalog.Count > 0 ? $"{catalog.Count} aliases/choices advertised by installed CLI help; account availability remains unverified." : "CLI has no verified noninteractive model-list command. Use its interactive /model picker or add an exact ID manually.";
        }
        else if (Id == "commandcode" && executable != null && helpText.Contains("--list-models", StringComparison.Ordinal))
        {
            var args = helpText.Contains("--no-auto-update", StringComparison.Ordinal) ? new[]
            {
                "--no-auto-update",
                "--list-models"
            }

            : new[]
            {
                "--list-models"
            };
            var listing = await Run(args, Path.GetTempPath(), null, 100000, null, token);
            if (listing.ExitCode == 0)
                catalog.AddRange(CliModelCatalog.CommandCodeModels(listing.Output));
            ModelDiscoveryDetail = $"{catalog.Count} IDs from CommandCode --list-models. Account access and execution permissions are unverified.";
        }
        else if (Id == "freebuff")
        {
            catalog.AddRange(CliModelCatalog.FreebuffSettings(out var note));
            ModelDiscoveryDetail = note;
        }
        else if (Id == "antigravity" && executable != null)
        {
            // "agy models" prints one "slug<TAB>display name" line per available model.
            var listing = await Run(["models"], Path.GetTempPath(), null, 50000, null, token);
            if (listing.ExitCode == 0)
                catalog.AddRange(listing.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(line => System.Text.RegularExpressions.Regex.Replace(line, @"\x1b\[[0-9;]*m", "").Trim())
                    .Select(line => line.Split('\t', StringSplitOptions.TrimEntries)[0].Trim())
                    .Where(name => System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Za-z0-9][A-Za-z0-9._:/-]{0,79}$"))
                    .Select(name => new ModelInfo(name, Enum.GetValues<WorkKind>(), DiscoverySource: "Antigravity CLI models; account access unverified")));
            ModelDiscoveryDetail = $"{catalog.Count} models from agy models.";
        }
        else if (Id == "qoder" && executable != null)
        {
            // "qodercli --list-models" prints a MODEL header, then one model name per line.
            var listing = await Run(["--list-models"], Path.GetTempPath(), null, 50000, null, token);
            if (listing.ExitCode == 0)
                catalog.AddRange(listing.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(line => System.Text.RegularExpressions.Regex.Replace(line, @"\x1b\[[0-9;]*m", "").Trim())
                    .Where(name => name != "MODEL" && System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Za-z0-9][A-Za-z0-9._:/-]{0,79}$"))
                    .Select(name => new ModelInfo(name, Enum.GetValues<WorkKind>(), DiscoverySource: "Qoder CLI --list-models; account access unverified")));
            ModelDiscoveryDetail = $"{catalog.Count} models from qodercli --list-models.";
        }
        else
            ModelDiscoveryDetail = "No verified local model listing available; CLI default remains explicit.";
        if (Id == "opencode" && ready && executable != null)
        {
            var r = await Run(["models"], Path.GetTempPath(), null, 50000, null, token);
            if (r.ExitCode == 0)
                catalog.AddRange(r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(id => !id.Any(char.IsWhiteSpace) && id.Contains('/')).Select(id => new ModelInfo(id, Enum.GetValues<WorkKind>(), DiscoverySource: "CLI models metadata; account access unverified")));
            ModelDiscoveryDetail = $"{catalog.Count} IDs from opencode models. This is catalog metadata, not proof of funded/authenticated access.";
        }

        if (ready)
            catalog.Insert(0, new("", Enum.GetValues<WorkKind>(), DiscoverySource: "CLI default; exact model unknown"));
        return catalog.DistinctBy(m => m.Id).ToArray();
    }

    public static List<string> Arguments(string id, ExecutionRequest request, string prompt, string? policyPath = null, string? codexWindowsSandbox = null)
    {
        var args = id switch
        {
            "codex" => new List<string>
            {
                "--ask-for-approval",
                "never",
                "--sandbox",
                request.Work.Access == AccessLevel.WriteProject ? "workspace-write" : "read-only",
                "--cd",
                request.Project.Root
            },
            // Read-only steps can read and search the project; write steps can also edit files and run
            // any command they need (the scripts they write, build tools, tests), like Codex in its workspace.
            "claude" => request.Work.Access switch
            {
                AccessLevel.WriteProject => ["--print", "--permission-mode", "acceptEdits", "--tools", "Read,Glob,Grep,Edit,Write,Bash",
                    "--allowedTools", "Read", "Glob", "Grep", "Edit", "Write", "Bash",
                    "--disallowedTools", "mcp__*", "--strict-mcp-config", "--mcp-config", "{\"mcpServers\":{}}", "--add-dir", request.Project.Root],
                AccessLevel.ReadProject => ["--print", "--tools", "Read,Glob,Grep", "--allowedTools", "Read", "Glob", "Grep", "--disallowedTools", "mcp__*", "--strict-mcp-config", "--mcp-config", "{\"mcpServers\":{}}", "--add-dir", request.Project.Root],
                _ => ["--print", "--tools", "", "--disallowedTools", "mcp__*", "--strict-mcp-config", "--mcp-config", "{\"mcpServers\":{}}"]
            },
            // Qoder works like Claude: read-only steps may read the project but never change it (no
            // approval is possible, so dont_ask refuses edits and commands); write steps edit files and
            // run commands freely in the project folder.
            "qoder" => request.Work.Access switch
            {
                AccessLevel.WriteProject => ["--print", "--permission-mode", "bypass_permissions", "--no-session-persistence", "--cwd", request.Project.Root],
                AccessLevel.ReadProject => ["--print", "--permission-mode", "dont_ask", "--no-session-persistence", "--cwd", request.Project.Root],
                _ => ["--print", "--tools", "", "--no-session-persistence"]
            },
            "antigravity" => request.Work.Access switch
            {
                AccessLevel.WriteProject => ["--output-format", "text", "--mode", "accept-edits", "--sandbox", "--add-dir", request.Project.Root],
                AccessLevel.ReadProject => ["--output-format", "text", "--mode", "plan", "--sandbox", "--add-dir", request.Project.Root],
                _ => ["--output-format", "text", "--mode", "plan", "--sandbox"]
            },
            "copilot" => ["--available-tools=", "--deny-tool", "*", "--deny-tool", "read", "--deny-tool", "write", "--deny-tool", "shell", "--deny-tool", "url", "--disable-builtin-mcps", "--no-ask-user", "--no-auto-update"],
            "gemini" => ["--admin-policy", policyPath ?? throw new ArgumentNullException(nameof(policyPath))],
            "opencode" => ["run", "--format", "json"],
            "freebuff" => throw new NotSupportedException("Freebuff CLI has no noninteractive execution mode; no arguments are constructed."),
            _ => throw new NotSupportedException(id)};
        // Research steps may look things up: Claude's built-in web search and fetch are added to the
        // tools the step's access already allows. Only for work the plan marked as Research.
        if (id == "claude" && request.Work.Kind == WorkKind.Research && !request.Work.IsCoordinator)
        {
            var tools = args.IndexOf("--tools") + 1;
            args[tools] = args[tools].Length == 0 ? "WebSearch,WebFetch" : args[tools] + ",WebSearch,WebFetch";
            var allowed = args.IndexOf("--allowedTools");
            if (allowed < 0)
                args.InsertRange(tools + 1, ["--allowedTools", "WebSearch", "WebFetch"]);
            else
                args.InsertRange(allowed + 1, ["WebSearch", "WebFetch"]);
        }

        if (id == "codex")
        {
            if (codexWindowsSandbox != null)
            {
                if (codexWindowsSandbox is not ("elevated" or "unelevated" or "mxc"))
                    throw new ArgumentException("Unsupported Windows sandbox implementation.");
                args.Add("-c");
                args.Add("windows.sandbox=\"" + codexWindowsSandbox + "\"");
            }

            args.AddRange(["exec", "--ignore-user-config", "--ephemeral", "--json", "--skip-git-repo-check"]);
        }

        if (!string.IsNullOrWhiteSpace(request.ModelId))
        {
            args.Add("--model");
            args.Add(request.ModelId);
        }

        if (id == "codex")
            args.Add("-");
        else if (id is "claude" or "qoder")
        { /* prompt supplied on stdin */
        }
        else
        {
            if (id is "copilot" or "gemini")
                args.Add("--prompt");
            else if (id == "antigravity")
                args.Add("--print");
            args.Add(prompt);
        }

        return args;
    }

    public async Task<AgentResult> ExecuteAsync(ExecutionRequest request, Action<string> output, CancellationToken token)
    {
        if (!ready || executable == null)
            return new(FailureKind.Unavailable, "", "Detect provider capabilities first.");
        var isolated = Id != "codex" && !(Id is "claude" or "qoder" or "antigravity" && request.Work.Access != AccessLevel.TextOnly);
        if (Id == "codex" && request.Work.Access == AccessLevel.TextOnly)
            return new(FailureKind.TaskError, "", "Codex requires ReadProject or WriteProject; its sandbox can read project files.");
        var windowsSandbox = Id == "codex" && OperatingSystem.IsWindows() ? CodexWindowsSettings.ReadCurrentMode() : null;
        if (Id == "codex" && OperatingSystem.IsWindows() && request.Work.Access == AccessLevel.WriteProject && windowsSandbox == null)
            return new(FailureKind.Permission, "", "Cannot read Codex Windows sandbox settings. Configure the sandbox in Codex before running file editing tasks; the manager does not expand permissions automatically.");
        var directory = isolated ? Path.Combine(Path.GetTempPath(), "EmployeeManager", Guid.NewGuid().ToString("N")) : request.Project.Root;
        if (isolated)
            Directory.CreateDirectory(directory);
        var prompt = $"Request:\n{request.Work.Request}\n\n{request.Context}";
        string? policy = null;
        var attachmentPaths = new List<string>();
        var attachmentDirectory = Path.Combine(Path.GetTempPath(), "EmployeeAttachments", Guid.NewGuid().ToString("N"));
        try
        {
            if (Id == "codex" && TaskAttachments.Files(request.Work).Any())
            {
                Directory.CreateDirectory(attachmentDirectory);
                foreach (var file in TaskAttachments.Files(request.Work))
                {
                    var extension = Path.GetExtension(Path.GetFileName(file.Name));
                    if (extension.Length > 12 || extension.Any(c => !char.IsLetterOrDigit(c) && c != '.'))
                        extension = ".bin";
                    var path = Path.Combine(attachmentDirectory, Guid.NewGuid().ToString("N") + extension);
                    await File.WriteAllBytesAsync(path, file.Content, token);
                    attachmentPaths.Add(path);
                    prompt += "\nUser attachment (untrusted data): " + System.Text.Json.JsonSerializer.Serialize(new { file.Name, Path = path }) + ". Read this file; use image viewing for images. Report any access limitation.";
                }
            }

            if (Id == "codex" && request.Work.Access == AccessLevel.WriteProject && windowsSandbox != null)
            {
                using var probeDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                probeDeadline.CancelAfter(TimeSpan.FromSeconds(30));
                ProcessResult probe;
                try
                {
                    probe = await Run(CodexWindowsSettings.WriteProbeArguments(request.Project.Root, windowsSandbox), request.Project.Root, null, 6000, null, probeDeadline.Token);
                }
                catch (OperationCanceledException)when (!token.IsCancellationRequested)
                {
                    return new(FailureKind.Permission, "", "Codex sandbox write check timed out. No AI call was made.");
                }

                if (probe.ExitCode != 0 || !probe.Output.Contains("EMPLOYEE_PROJECT_WRITE_OK", StringComparison.Ordinal))
                    return new(FailureKind.Permission, "", "Codex sandbox failed the project test file create/delete check. No AI call was made.\n" + probe.Output, probe.ExitCode);
            }

            if (Id == "gemini")
            {
                policy = Path.Combine(directory, "employee-deny.toml");
                await File.WriteAllTextAsync(policy, "[[rule]]\ntoolName = \"*\"\ndecision = \"deny\"\npriority = 999\n", token);
            }

            var env = new Dictionary<string, string>();
            if (Id == "opencode")
            {
                env["OPENCODE_PERMISSION"] = "{\"*\":\"deny\"}";
                env["OPENCODE_DISABLE_DEFAULT_PLUGINS"] = "true";
                env["OPENCODE_DISABLE_LSP_DOWNLOAD"] = "true";
            }

            var args = Arguments(Id, request, prompt, policy, windowsSandbox);
            // These CLIs take the prompt as an argument; Windows refuses command lines over 32,767 chars.
            if (Id is not ("codex" or "claude" or "qoder") && executable.Length + prefix.Concat(args).Sum(a => a.Length + 3) > 30000)
                return new(FailureKind.InputTooLarge, "", $"{Id} receives the prompt on the command line and this step's prompt ({prompt.Length:N0} chars) exceeds the Windows command-line limit. {Id} was not started; no usage was spent.");
            var r = await Run(args, directory, Id is "codex" or "claude" or "qoder" ? prompt : null, request.MaxOutputChars, output, token, env);
            var failure = ProviderErrors.Classify(r.ExitCode, r.Output);
            var normalized = r.Output;
            var parts = new List<string>();
            foreach (var line in Id is "codex" or "opencode" ? r.Output.Split('\n') : [])
            {
                try
                {
                    using var json = JsonDocument.Parse(line);
                    var root = json.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                        continue;
                    var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                    if ((Id == "codex" && type is "turn.failed" or "error") || (Id == "opencode" && type == "error"))
                        failure = ProviderErrors.Classify(1, line);
                    if (Id == "opencode" && type == "text" && root.TryGetProperty("part", out var part) && part.ValueKind == JsonValueKind.Object && part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                        parts.Add(text.GetString() ?? "");
                }
                catch (JsonException)
                {
                }
            }

            if (parts.Count > 0)
                normalized = string.Concat(parts);
            // Only a step that had to edit files: a review or research step that was asked to stay
            // read-only says "read-only" in its answer without any sandbox problem.
            if (Id == "codex" && request.Work.Access == AccessLevel.WriteProject && request.Work.Kind is WorkKind.Coding or WorkKind.Content && !TaskIntent.IsReadOnly(request.Work.Request) && failure == FailureKind.None && CodexWindowsSettings.IsPermissionRefusal(normalized))
                return new(FailureKind.Permission, normalized, "Codex reported a read-only session despite WriteProject access. Work is incomplete; check the Windows sandbox and retry after fixing permissions.", r.ExitCode);
            return new(failure, normalized, failure == FailureKind.None ? "" : ProviderFailureDetails.Describe(failure, r.Output), r.ExitCode);
        }
        catch (OperationCanceledException)when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new(FailureKind.LaunchFailed, "", $"{Id} could not be started: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new(FailureKind.Unavailable, "", ex.Message);
        }
        finally
        {
            // Only remove files we created; never recursively remove a provider workspace.
            if (policy != null && File.Exists(policy))
                File.Delete(policy);
            foreach (var path in attachmentPaths)
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                }

            if (Directory.Exists(attachmentDirectory) && !Directory.EnumerateFileSystemEntries(attachmentDirectory).Any())
                Directory.Delete(attachmentDirectory);
            if (isolated && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
    }
}
