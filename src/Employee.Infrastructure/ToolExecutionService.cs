using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Employee.Core;

namespace Employee.Infrastructure;
public sealed class ToolExecutionService : IDisposable
{
    private readonly IToolWorkflowStore store;
    private readonly IEmployeeStore manager;
    private readonly IProcessRunner runner;
    private readonly string appRoot, dataRoot;
    private readonly Func<string[], IDisposable> reserve;
    public Func<byte[], byte[]>? NormalizePixelBackground { get; init; }
    // Pooled connections are dropped before Node's 5-second keep-alive closes them, so a request is
    // never sent on a connection the backend has just closed.
    private readonly HttpClient http = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, PooledConnectionIdleTimeout = TimeSpan.FromSeconds(2) })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };
    private readonly ConcurrentDictionary<string, CancellationTokenSource> active = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new();
    private readonly ToolBackendLauncher backends;
    public bool IsBusy => !active.IsEmpty;

    public event Action? Changed;
    public ToolExecutionService(IToolWorkflowStore store, IEmployeeStore manager, IProcessRunner runner, string appRoot, string dataRoot, Func<string[], IDisposable> reserve)
    {
        this.store = store;
        this.manager = manager;
        this.runner = runner;
        this.appRoot = appRoot;
        this.dataRoot = dataRoot;
        this.reserve = reserve;
        backends = new ToolBackendLauncher { LogFolder = Path.Combine(dataRoot, "tools") };
    }

    public static IReadOnlyList<ToolCapability> Capabilities(string connector) => connector switch
    {
        "pixel" => [new("status", "Codex availability / login metadata"), new("generate", "Pixel Foundry Codex create/edit/animation job", true, true)],
        // generate runs TrailerForge's own AI video generator (its script is written by an AI CLI), so it counts as a model call.
        "video" => [new("inspect", "Inspect saved TrailerForge project"), new("export", "Export saved project using TrailerForge renderer", true),
            new("generate", "Make a new MP4 with TrailerForge's AI video generator: from a topic, a script, or the project's own images and clips", true, true)],
        // inspect/export use ZapBop's engine directly; sfx/compose/render use ZapBop's own API (API.md).
        // compose runs ZapBop's AI composer on an AI CLI, so it counts as a model call and needs approval.
        "music" => [new("status", "Check ZapBop source and Python engine imports"), new("inspect", "Validate and summarize a saved ZapBop project (.json)"), new("export", "Render a saved ZapBop project to WAV with ZapBop's engine", true),
            new("sfx", "Synthesize one sound effect (ZapBop SFX preset such as Coin, Jump, Hit) to WAV/OGG", true), new("compose", "Compose a new multi-layer song from a description with ZapBop's AI composer, save its project and render it to WAV/OGG", true, true), new("render", "Render a saved or composed ZapBop project (loop, once, stems or variants) to WAV/OGG", true)],
        "marketing" => [new("status", "Backend health"), new("projects", "List projects"), new("jobs", "List workflows"), new("project.create", "Create marketing project", true), new("media.import", "Link media file", true), new("job.create", "Create marketing workflow", true), new("job.run", "Run workflow; backend review gates still apply", true, true), new("job.status", "Read job state"), new("content.set", "Save draft content", true), new("review", "Approve/reject backend revision", true), new("schedule", "Save backend schedule", true), new("export", "Export approved manual publishing package", true), new("platforms", "List supported platforms"), new("queue", "List per-platform posts (targets) and their state"), new("content.create", "Create content with the backend's AI (posts from a prompt, or clips from a video)", true, true), new("task.status", "Read a content-creation task"), new("target.content", "Edit one platform post's fields", true), new("target.schedule", "Schedule one platform post", true), new("target.publish", "Publish one platform post to its connected account after explicit confirmation", true, false, true), new("youtube.publish", "Same as target.publish (older name)", true, false, true)],
        _ => []
    };
    public ToolRun Create(string toolId, string operation, string input)
    {
        if (input.Length > 12_000_000)
            throw new InvalidOperationException("Tool input exceeds 12 MB.");
        _ = JsonNode.Parse(input) as JsonObject ?? throw new InvalidOperationException("Tool input must be a JSON object.");
        var profile = store.Tools().Single(t => t.Id == toolId);
        var capability = Capabilities(profile.Connector).Single(c => c.Id == operation);
        var run = new ToolRun
        {
            ToolId = toolId,
            Operation = operation,
            Input = input,
            State = capability.Writes || capability.UsesAI || capability.Publishes ? ToolRunState.WaitingApproval : ToolRunState.Draft
        };
        if (input.Length > 200000)
        {
            var folder = Path.Combine(dataRoot, "artifacts", "tool-inputs");
            Directory.CreateDirectory(folder);
            run.PayloadFile = Path.Combine(folder, run.Id + ".json");
            File.WriteAllText(run.PayloadFile, input);
            run.PayloadSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(run.PayloadFile)));
            var preview = JsonNode.Parse(input)!.AsObject();
            foreach (var key in preview.Select(k => k.Key).ToArray())
                if (preview[key] is JsonValue text && text.TryGetValue<string>(out var value) && value.Length > 10000)
                    preview[key] = "[Large value stored locally; SHA256-bound to this request]";
            run.Input = preview.ToJsonString();
            if (run.Input.Length > 200000)
                run.Input = JsonSerializer.Serialize(new { Note = "Large JSON stored in the approved local payload file", run.PayloadFile, run.PayloadSha256 });
        }

        store.SaveToolRun(run);
        Changed?.Invoke();
        return run;
    }

    private static string Hash(ToolRun run, ConnectedTool tool) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { run.Operation, run.Input, run.PayloadSha256, tool.Id, tool.Connector, tool.SourcePath, tool.WorkspaceRoot, tool.Executable, tool.Version, tool.Endpoint, tool.ProtectedToken, tool.AllowWrites, tool.AllowModelCalls, tool.AllowPublishing, tool.AllowTaskAutomation, tool.AutoStartBackend, tool.BackendExecutable, tool.BackendArguments, tool.BackendWorkingDirectory, tool.BackendDataRoot, tool.BackendStartupTimeoutSeconds }))));
    public void Approve(string id)
    {
        var run = store.ToolRuns().Single(r => r.Id == id);
        if (run.State != ToolRunState.WaitingApproval)
            throw new InvalidOperationException("Select a request awaiting approval.");
        var tool = store.Tools().Single(t => t.Id == run.ToolId);
        run.ApprovedHash = Hash(run, tool);
        run.State = ToolRunState.Draft;
        store.SaveToolRun(run);
        Changed?.Invoke();
    }

    public void Review(string id)
    {
        var run = store.ToolRuns().Single(r => r.Id == id);
        if (run.State != ToolRunState.NeedsReview)
            throw new InvalidOperationException("Result is not awaiting review.");
        run.State = ToolRunState.Completed;
        store.SaveToolRun(run);
        Changed?.Invoke();
    }

    public async Task ExecuteAsync(string id, CancellationToken token = default, bool scopeReserved = false, Action? onModelCallReserved = null)
    {
        var run = store.ToolRuns().Single(r => r.Id == id);
        var tool = store.Tools().Single(t => t.Id == run.ToolId);
        var resumePixel = tool.Connector == "pixel" && run.Operation == "generate" && run.State == ToolRunState.Interrupted && IsRemoteJobId(run.RemoteId);
        if (run.State != ToolRunState.Draft && !resumePixel)
            throw new InvalidOperationException("Approve the request first. Existing writes are not repeated; interrupted image jobs resume by their remote ID.");
        var capability = Capabilities(tool.Connector).Single(c => c.Id == run.Operation);
        if (!tool.Enabled)
            throw new InvalidOperationException("Enable this connector explicitly in Tools.");
        if (capability.Writes && !tool.AllowWrites || capability.UsesAI && !tool.AllowModelCalls || capability.Publishes && !tool.AllowPublishing)
            throw new InvalidOperationException("Required tool action permissions are not enabled.");
        // Resume only GETs the existing job. Changing a timeout must not send it back through generation approval.
        if (!resumePixel && (capability.Writes || capability.UsesAI || capability.Publishes) && run.ApprovedHash != Hash(run, tool))
        {
            run.State = ToolRunState.WaitingApproval;
            store.SaveToolRun(run);
            Changed?.Invoke();
            return;
        }

        var gate = gates.GetOrAdd(tool.Id, _ => new(1, 1));
        if (!await gate.WaitAsync(0, token))
            throw new InvalidOperationException("This tool already has an active request.");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        IDisposable? lease = null;
        IDisposable? backendLease = null;
        var registered = false;
        try
        {
            if (!scopeReserved)
                lease = reserve(new[] { tool.SourcePath, tool.WorkspaceRoot }.Where(Directory.Exists).ToArray());
            if (!active.TryAdd(id, cancel))
                throw new InvalidOperationException("Request is already running.");
            registered = true;
            run.State = ToolRunState.Running;
            store.SaveToolRun(run);
            Changed?.Invoke();
            var bound = manager.WorkItems().SingleOrDefault(w => w.ToolRunId == run.Id && !w.IsGoal);
            backendLease = await backends.EnsureAsync(tool, async ready =>
            {
                await Request(tool, HttpMethod.Get, tool.Connector == "pixel" ? "/api/codex/status" : "/health", null, ready);
            }, message =>
            {
                run.ConnectionLog += $"{DateTimeOffset.UtcNow:O} {message}\n";
                if (bound != null)
                    manager.Log(bound.Id, "ToolConnection", message);
                store.SaveToolRun(run);
                Changed?.Invoke();
            }, cancel.Token);
            cancel.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(tool.TimeoutSeconds, 5, 7200)));
            if (capability.UsesAI && !resumePixel)
            {
                if (bound?.SessionId is { } sessionId)
                {
                    var session = manager.Sessions().Single(s => s.Id == sessionId);
                    var used = Math.Max(session.CallsUsed, session.BaseCallsUsed + manager.WorkItems().Where(w => w.SessionId == sessionId && !w.IsGoal).Sum(w => w.Attempts));
                    if (session.MaxCalls > 0 && used >= session.MaxCalls)
                        throw new InvalidOperationException("Session call limit reached; tool model was not called.");
                }

                if (!manager.ReserveUsage(run.Id, manager.LoadSettings().MaxDailyCalls))
                    throw new InvalidOperationException("Global daily manager call limit reached; tool model was not called.");
                run.ModelCallReserved = true;
                store.SaveToolRun(run);
                if (bound != null)
                {
                    bound.Attempts = Math.Max(1, bound.Attempts);
                    manager.SaveWork(bound);
                }

                onModelCallReserved?.Invoke();
            }

            run.State = ToolRunState.Running;
            store.SaveToolRun(run);
            Changed?.Invoke();
            var text = run.Input;
            if (run.PayloadFile != null)
            {
                var expected = Path.GetFullPath(Path.Combine(dataRoot, "artifacts", "tool-inputs", run.Id + ".json"));
                if (Path.GetFullPath(run.PayloadFile) != expected)
                    throw new InvalidOperationException("Unexpected payload location.");
                var bytes = await File.ReadAllBytesAsync(expected, cancel.Token);
                if (Convert.ToHexString(SHA256.HashData(bytes)) != run.PayloadSha256)
                    throw new InvalidOperationException("Approved payload changed; create and review a new request.");
                text = Encoding.UTF8.GetString(bytes);
            }

            var input = JsonNode.Parse(text)!.AsObject();
            JsonNode output = resumePixel ? await Request(tool, HttpMethod.Get, "/api/codex/jobs/" + run.RemoteId, null, cancel.Token) : tool.Connector == "video" ? await Video(tool, run, input, cancel.Token) : tool.Connector == "music" ? await Music(tool, run, input, cancel.Token) : await Api(tool, run, input, cancel.Token);
            if (tool.Connector == "pixel" && run.Operation == "generate")
                output = await AwaitPixel(tool, run, output, input, cancel.Token);
            if (tool.Connector == "marketing" && run.Operation == "content.create")
                output = await AwaitMarketingTask(tool, run, output, cancel.Token);
            run.Output = output.ToJsonString();
            if (run.Output.Length > 200000)
            {
                var folder = Path.Combine(dataRoot, "artifacts", "tool-runs", run.Id);
                Directory.CreateDirectory(folder);
                run.OutputFile = Path.Combine(folder, "output.json");
                await File.WriteAllTextAsync(run.OutputFile, run.Output, cancel.Token);
                run.OutputSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(run.OutputFile, cancel.Token)));
                run.Output = JsonSerializer.Serialize(new { run.OutputFile, run.OutputSha256, Note = "Full JSON output stored locally" });
            }

            run.Error = "";
            run.State = capability.Writes || capability.UsesAI || capability.Publishes ? ToolRunState.NeedsReview : ToolRunState.Completed;
        }
        catch (OperationCanceledException)
        {
            if (tool.Connector == "pixel" && run.RemoteId != null)
            {
                run.State = ToolRunState.Interrupted;
                run.Error = "Saved image job " + run.RemoteId + ". The manager stopped waiting but did not cancel the backend job. Run / resume collects this job's result without regenerating or spending more generations.";
            }
            else
            {
                run.State = ToolRunState.Cancelled;
                run.Error = "Stopped. Inspect any remote/local side effects before creating another request.";
            }
        }
        catch (HttpRequestException ex)when (tool.Connector == "pixel" && run.RemoteId != null)
        {
            run.State = ToolRunState.Interrupted;
            run.Error = "Lost connection while waiting for image job " + run.RemoteId + "; job kept for resume, not regenerated. " + ex.Message + backends.ExitNote(tool);
        }
        catch (ToolHttpException ex)when (tool.Connector == "pixel" && run.RemoteId != null && ex.IsTransient)
        {
            // 5xx/429 while polling says nothing about the job itself; keep it resumable instead of failing it.
            run.State = ToolRunState.Interrupted;
            run.Error = "Backend was temporarily unavailable while waiting for image job " + run.RemoteId + "; job kept for resume, not regenerated. " + ex.Message;
        }
        catch (Exception ex)
        {
            run.State = ToolRunState.Failed;
            run.Error = ex.Message;
        }
        finally
        {
            if (registered)
                active.TryRemove(id, out _);
            lease?.Dispose();
            backendLease?.Dispose();
            gate.Release();
            store.SaveToolRun(run);
            Changed?.Invoke();
        }
    }

    private async Task<JsonNode> Video(ConnectedTool tool, ToolRun run, JsonObject input, CancellationToken token)
    {
        var python = ProcessRunner.FindExecutable(tool.Executable);
        if (python == null)
            throw new InvalidOperationException("Configure an installed native Python executable; no dependencies are installed automatically.");
        var bridge = Path.Combine(appRoot, "connectors", "trailerforge_bridge.py");
        if (!File.Exists(bridge))
            throw new InvalidOperationException("TrailerForge bridge is missing from the application root.");
        if (run.Operation == "generate")
        {
            // The task's own project is the root its media may come from; the video goes to this run's folder.
            var bound = manager.WorkItems().FirstOrDefault(w => w.ToolRunId == run.Id && !w.IsGoal);
            var projectRoot = bound == null ? null : manager.Projects().FirstOrDefault(p => p.Id == bound.ProjectId)?.Root;
            var root = projectRoot != null && Directory.Exists(projectRoot) ? projectRoot : tool.WorkspaceRoot;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                throw new InvalidOperationException("The task's project folder (or the tool's workspace) is needed as the root for video media.");
            var runFolder = Path.Combine(dataRoot, "artifacts", "tool-runs", run.Id);
            Directory.CreateDirectory(runFolder);
            var made = await RunBridge(python, [bridge, "--source", tool.SourcePath, "--root", root, "--operation", "generate", "--output-dir", runFolder], input.ToJsonString(), "TrailerForge", result => new InvalidOperationException("TrailerForge: " + (result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "exit code " + result.ExitCode)), token);
            await CheckVideo(made, runFolder, token);
            return made;
        }
        if (string.IsNullOrWhiteSpace(tool.WorkspaceRoot) || !Directory.Exists(tool.WorkspaceRoot))
            throw new InvalidOperationException("Choose the allowed video-project workspace root.");
        var output = await RunBridge(python, [bridge, "--source", tool.SourcePath, "--root", tool.WorkspaceRoot, "--operation", run.Operation], input.ToJsonString(), "TrailerForge", result => new InvalidOperationException(result.Output), token);
        if (run.Operation != "export")
            return output;
        // The renderer writes inside the saved project folder; confirm a real video file is there before review.
        var artifact = Text(output["artifactPath"]) ?? throw new InvalidOperationException("TrailerForge export returned no artifactPath.");
        var full = Path.GetFullPath(artifact);
        if (!full.StartsWith(Path.GetFullPath(tool.WorkspaceRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            throw new InvalidOperationException("TrailerForge export did not produce a file inside the approved workspace.");
        await using (var video = File.OpenRead(full))
        {
            var header = new byte[12];
            if (video.Length < 12 || await video.ReadAtLeastAsync(header, 12, false, token) < 12)
                throw new InvalidOperationException("TrailerForge export is empty or truncated.");
            if (full.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) && !header.AsSpan(4, 4).SequenceEqual("ftyp"u8))
                throw new InvalidOperationException("TrailerForge export is not a valid MP4 file.");
            video.Position = 0;
            output["artifactSha256"] = Convert.ToHexString(await SHA256.HashDataAsync(video, token));
            output["artifactBytes"] = video.Length;
        }
        output["artifactPath"] = full;
        return output;
    }

    // Bridges write their result to a manager-owned file so renderer/engine logs on stdout/stderr can neither
    // hide nor truncate it. Output without that file (older bridges) falls back to the last JSON stdout line.
    private async Task<JsonObject> RunBridge(string python, List<string> args, string input, string bridgeName, Func<ProcessResult, Exception> failure, CancellationToken token)
    {
        var folder = Path.Combine(dataRoot, "artifacts", "bridge-results");
        Directory.CreateDirectory(folder);
        var resultFile = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".json");
        args.AddRange(["--result-file", resultFile]);
        try
        {
            var result = await runner.RunAsync(python, args, appRoot, input, 200000, null, token);
            if (result.ExitCode != 0)
                throw failure(result);
            if (!File.Exists(resultFile))
                return ParseBridgeResult(result.Output, bridgeName);
            if (new FileInfo(resultFile).Length > 16_000_000)
                throw new InvalidOperationException(bridgeName + " bridge result exceeds 16 MB.");
            try
            {
                return JsonNode.Parse(await File.ReadAllBytesAsync(resultFile, token)) as JsonObject ?? throw new InvalidOperationException(bridgeName + " bridge result is not a JSON object.");
            }
            catch (JsonException)
            {
                throw new InvalidOperationException(bridgeName + " bridge result file is not valid JSON.");
            }
        }
        finally
        {
            try { File.Delete(resultFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    // Backend job IDs are opaque; accept any simple path-safe identifier (GUIDs included), never path fragments.
    public static bool IsRemoteJobId(string? id) => id != null && System.Text.RegularExpressions.Regex.IsMatch(id, @"^[A-Za-z0-9_-]{1,128}$");

    private static JsonObject ParseBridgeResult(string output, string bridgeName)
    {
        // Bridges may write library diagnostics around their single-line JSON result. Walk backwards
        // so a trailing log line cannot hide a valid result, but never accept a JSON scalar/array.
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Reverse())
        {
            try
            {
                if (JsonNode.Parse(line) is JsonObject value)
                    return value;
            }
            catch (JsonException)
            {
            }
        }

        throw new InvalidOperationException(bridgeName + " bridge returned no JSON object result.");
    }

    // Python for ZapBop: the configured interpreter, else the project's own .venv (never installs anything).
    public static string? ZapBopPython(ConnectedTool tool)
    {
        if (!string.IsNullOrWhiteSpace(tool.Executable))
            return ProcessRunner.FindExecutable(tool.Executable);
        if (string.IsNullOrWhiteSpace(tool.SourcePath))
            return null;
        var venv = Path.Combine(tool.SourcePath, ".venv", "Scripts", "python.exe");
        return File.Exists(venv) ? venv : null;
    }

    public static bool IsZapBopSource(string path) => !string.IsNullOrWhiteSpace(path) && new[] { "main.py", "pro_engine.py", "audio_engine.py" }.All(f => File.Exists(Path.Combine(path, f)));

    private async Task<JsonNode> RunZapBop(ConnectedTool tool, string operation, string? root, string? outputDir, JsonObject? input, CancellationToken token, string? artifactsRoot = null)
    {
        if (!IsZapBopSource(tool.SourcePath))
            throw new InvalidOperationException("Choose the ZapBop source folder (it contains main.py, pro_engine.py and audio_engine.py).");
        var python = ZapBopPython(tool) ?? throw new InvalidOperationException("Python for ZapBop was not found. Set Python/executable to ZapBop's .venv\\Scripts\\python.exe; no dependencies are installed automatically.");
        var bridge = Path.Combine(appRoot, "connectors", "zapbop_bridge.py");
        if (!File.Exists(bridge))
            throw new InvalidOperationException("ZapBop bridge is missing from the application root.");
        // -I: isolated mode, so nothing in the working folder or PYTHON* variables is loaded.
        var args = new List<string> { "-I", bridge, "--source", tool.SourcePath, "--operation", operation };
        if (root != null)
            args.AddRange(["--root", root]);
        if (outputDir != null)
            args.AddRange(["--output-dir", outputDir]);
        if (artifactsRoot != null && Directory.Exists(artifactsRoot))
            args.AddRange(["--artifacts-root", artifactsRoot]);
        return await RunBridge(python, args, input?.ToJsonString() ?? "{}", "ZapBop", result =>
        {
            var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return new InvalidOperationException("ZapBop: " + (lines.LastOrDefault() ?? "bridge failed with exit code " + result.ExitCode));
        }, token);
    }

    private async Task<JsonNode> Music(ConnectedTool tool, ToolRun run, JsonObject input, CancellationToken token)
    {
        if (run.Operation == "status")
            return await RunZapBop(tool, "status", null, null, null, token);
        if (string.IsNullOrWhiteSpace(tool.WorkspaceRoot) || !Directory.Exists(tool.WorkspaceRoot))
            throw new InvalidOperationException("Choose the allowed ZapBop project workspace root.");
        if (run.Operation == "inspect")
            return await RunZapBop(tool, "inspect", tool.WorkspaceRoot, null, input, token);
        if (run.Operation is "sfx" or "compose" or "render")
        {
            var runFolder = Path.Combine(dataRoot, "artifacts", "tool-runs", run.Id);
            Directory.CreateDirectory(runFolder);
            // render may read the project a compose step saved in its own run folder.
            var made = await RunZapBop(tool, run.Operation, tool.WorkspaceRoot, runFolder, input, token, Path.Combine(dataRoot, "artifacts", "tool-runs"));
            await CheckAudio(made, runFolder, token);
            return made;
        }
        if (run.Operation != "export")
            throw new InvalidOperationException("Unsupported ZapBop operation.");
        // Each export goes to its own manager folder: user files are never overwritten.
        var folder = Path.Combine(dataRoot, "artifacts", "tool-runs", run.Id);
        Directory.CreateDirectory(folder);
        var output = await RunZapBop(tool, "export", tool.WorkspaceRoot, folder, input, token);
        var artifact = Text(output["artifactPath"]) ?? throw new InvalidOperationException("ZapBop export returned no WAV path.");
        var full = Path.GetFullPath(artifact);
        if (!full.StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !full.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            throw new InvalidOperationException("ZapBop export did not produce a WAV in the run folder.");
        await using (var wav = File.OpenRead(full))
        {
            var header = new byte[12];
            if (wav.Length < 44 || await wav.ReadAtLeastAsync(header, 12, false, token) < 12 || !header.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !header.AsSpan(8, 4).SequenceEqual("WAVE"u8))
                throw new InvalidOperationException("ZapBop export is not a valid WAV file.");
            wav.Position = 0;
            output["artifactSha256"] = Convert.ToHexString(await SHA256.HashDataAsync(wav, token));
        }
        output["artifactPath"] = full;
        return output;
    }

    // The generated video must be a real MP4 inside this run's folder.
    internal static async Task CheckVideo(JsonNode output, string runFolder, CancellationToken token)
    {
        var artifact = Text(output["artifactPath"]) ?? throw new InvalidOperationException("TrailerForge returned no video file.");
        var full = Path.GetFullPath(artifact);
        if (!full.StartsWith(Path.GetFullPath(runFolder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !full.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            throw new InvalidOperationException("TrailerForge did not write an MP4 in the run folder: " + artifact);
        await using var video = File.OpenRead(full);
        var header = new byte[12];
        if (video.Length < 1024 || await video.ReadAtLeastAsync(header, 12, false, token) < 12 || !header.AsSpan(4, 4).SequenceEqual("ftyp"u8))
            throw new InvalidOperationException("TrailerForge output is not a valid MP4 file.");
        video.Position = 0;
        output["artifactPath"] = full;
        output["artifactSha256"] = Convert.ToHexString(await SHA256.HashDataAsync(video, token));
        output["artifactBytes"] = video.Length;
    }

    // Every file ZapBop reports must be a real WAV/OGG inside this run's folder; each gets its SHA-256.
    internal static async Task CheckAudio(JsonNode output, string runFolder, CancellationToken token)
    {
        var folder = Path.GetFullPath(runFolder) + Path.DirectorySeparatorChar;
        var paths = (output["artifactPaths"] as JsonArray)?.Select(Text).OfType<string>().ToList() ?? [];
        var first = Text(output["artifactPath"]) ?? throw new InvalidOperationException("ZapBop returned no audio file.");
        if (!paths.Contains(first))
            paths.Insert(0, first);
        var hashes = new JsonArray();
        foreach (var path in paths)
        {
            var full = Path.GetFullPath(path);
            var extension = Path.GetExtension(full).ToLowerInvariant();
            if (!full.StartsWith(folder, StringComparison.OrdinalIgnoreCase) || extension is not (".wav" or ".ogg") || !File.Exists(full))
                throw new InvalidOperationException("ZapBop did not write a WAV/OGG file in the run folder: " + path);
            await using var audio = File.OpenRead(full);
            var header = new byte[12];
            var read = await audio.ReadAtLeastAsync(header, 12, false, token);
            var valid = extension == ".wav" ? audio.Length >= 44 && read == 12 && header.AsSpan(0, 4).SequenceEqual("RIFF"u8) && header.AsSpan(8, 4).SequenceEqual("WAVE"u8) : read >= 4 && header.AsSpan(0, 4).SequenceEqual("OggS"u8);
            if (!valid)
                throw new InvalidOperationException("ZapBop output is not a valid " + extension.TrimStart('.').ToUpperInvariant() + " file: " + Path.GetFileName(full));
            audio.Position = 0;
            hashes.Add(Convert.ToHexString(await SHA256.HashDataAsync(audio, token)));
        }
        output["artifactPath"] = Path.GetFullPath(first);
        output["artifactSha256"] = hashes[0]!.GetValue<string>();
        if (paths.Count > 1)
        {
            output["artifactPaths"] = new JsonArray(paths.Select(p => (JsonNode)Path.GetFullPath(p)).ToArray());
            output["artifactSha256s"] = hashes;
        }
    }

    private static string JobId(JsonObject input) => System.Text.RegularExpressions.Regex.IsMatch(input["jobId"]?.GetValue<string>() ?? "", @"^[A-Za-z0-9_-]{1,128}$") ? input["jobId"]!.GetValue<string>() : throw new InvalidOperationException("jobId must be a simple backend identifier.");
    private Task<JsonNode> Api(ConnectedTool tool, ToolRun run, JsonObject input, CancellationToken token)
    {
        if (tool.Connector == "pixel")
            return Request(tool, run.Operation == "status" ? HttpMethod.Get : HttpMethod.Post, run.Operation == "status" ? "/api/codex/status" : "/api/codex/jobs", run.Operation == "status" ? null : input, token);
        // Which identifier the route needs: a job, one platform post (target) or a content task.
        static string Id(JsonObject input, string name) => System.Text.RegularExpressions.Regex.IsMatch(input[name]?.GetValue<string>() ?? "", @"^[A-Za-z0-9_-]{1,128}$") ? input[name]!.GetValue<string>() : throw new InvalidOperationException(name + " must be a simple backend identifier.");
        var id = run.Operation is "status" or "projects" or "jobs" or "project.create" or "media.import" or "job.create" or "platforms" or "queue" or "content.create" ? ""
            : run.Operation is "target.content" or "target.schedule" or "target.publish" or "youtube.publish" ? Id(input, "targetId")
            : run.Operation == "task.status" ? Id(input, "taskId") : JobId(input);
        var route = run.Operation switch
        {
            "status" => (HttpMethod.Get, "/health"),
            "projects" => (HttpMethod.Get, "/projects"),
            "jobs" => (HttpMethod.Get, "/jobs"),
            "project.create" => (HttpMethod.Post, "/projects"),
            "media.import" => (HttpMethod.Post, "/media"),
            "job.create" => (HttpMethod.Post, "/jobs"),
            "job.status" => (HttpMethod.Get, "/jobs/" + id),
            "job.run" => (HttpMethod.Post, "/jobs/" + id + "/run"),
            "content.set" => (HttpMethod.Put, "/jobs/" + id + "/content"),
            "review" => (HttpMethod.Post, "/jobs/" + id + "/review"),
            "schedule" => (HttpMethod.Put, "/jobs/" + id + "/schedule"),
            "export" => (HttpMethod.Post, "/jobs/" + id + "/export"),
            "platforms" => (HttpMethod.Get, "/platforms"),
            "queue" => (HttpMethod.Get, "/queue"),
            "content.create" => (HttpMethod.Post, "/content"),
            "task.status" => (HttpMethod.Get, "/tasks/" + id),
            "target.content" => (HttpMethod.Put, "/targets/" + id + "/content"),
            "target.schedule" => (HttpMethod.Put, "/targets/" + id + "/schedule"),
            "target.publish" or "youtube.publish" => (HttpMethod.Post, "/targets/" + id + "/publish"),
            _ => throw new InvalidOperationException("Unsupported connector operation.")};
        JsonObject body;
        if (id.Length == 0)
            body = input;
        else if (input["body"] is JsonObject requestedBody)
            body = requestedBody;
        else if (input["body"] == null)
            body = new JsonObject();
        else
            throw new InvalidOperationException("body must be a JSON object for this marketing operation.");
        return Request(tool, route.Item1, route.Item2, route.Item1 == HttpMethod.Get ? null : body, token);
    }

    private async Task<JsonNode> Request(ConnectedTool tool, HttpMethod method, string path, JsonObject? body, CancellationToken token)
    {
        if (!Uri.TryCreate(tool.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "http" || !new[]
        {
            "127.0.0.1",
            "localhost",
            "[::1]",
            "::1"
        }.Contains(endpoint.Host, StringComparer.OrdinalIgnoreCase) || endpoint.UserInfo.Length != 0)
            throw new InvalidOperationException("Only a local loopback HTTP endpoint is supported.");
        using var request = new HttpRequestMessage(method, new Uri(endpoint, path));
        if (tool.Connector == "marketing")
        {
            var secret = SecretProtection.Unprotect(tool.ProtectedToken);
            if (secret.Length == 0)
                throw new InvalidOperationException("Enter the backend session token; no existing account credentials are harvested.");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        }

        if (body != null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var block = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(block, token)) > 0)
        {
            if (buffer.Length + read > 16_000_000)
                throw new InvalidOperationException("Tool response exceeds 16 MB.");
            buffer.Write(block, 0, read);
        }

        var text = Encoding.UTF8.GetString(buffer.ToArray());
        if (!response.IsSuccessStatusCode)
            throw new ToolHttpException((int)response.StatusCode, $"Tool HTTP {(int)response.StatusCode}: " + text[..Math.Min(2000, text.Length)]);
        // 204/empty success bodies are valid acknowledgements; HTML or other non-JSON success bodies are not.
        if (string.IsNullOrWhiteSpace(text))
            return new JsonObject();
        try
        {
            return JsonNode.Parse(text) ?? new JsonObject();
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"Tool HTTP {(int)response.StatusCode} returned a non-JSON response ({response.Content.Headers.ContentType?.MediaType ?? "no content type"}): " + text[..Math.Min(300, text.Length)]);
        }
    }

    private async Task<JsonNode> AwaitPixel(ConnectedTool tool, ToolRun run, JsonNode output, JsonObject input, CancellationToken token)
    {
        // A resumed GET may omit the id; the saved RemoteId is the job being collected then.
        var id = Text(output["id"]) ?? run.RemoteId ?? throw new InvalidOperationException("Pixel tool returned no job ID.");
        if (!IsRemoteJobId(id))
            throw new InvalidOperationException("Invalid remote job ID.");
        run.RemoteId = id;
        store.SaveToolRun(run);
        // One failed status poll is not a lost job: seen live, a poll failed ("An error occurred while
        // sending the request") while the job went on and completed; the step stopped anyway.
        var failedPolls = 0;
        while (Text(output["status"]) is "queued" or "pending" or "running")
        {
            await Task.Delay(failedPolls == 0 ? 1000 : Math.Min(15000, 2000 * failedPolls), token);
            try
            {
                output = await Request(tool, HttpMethod.Get, "/api/codex/jobs/" + id, null, token);
                failedPolls = 0;
            }
            catch (HttpRequestException) when (++failedPolls < 6)
            {
            }
            catch (ToolHttpException error) when (error.IsTransient && ++failedPolls < 6)
            {
            }
        }

        if (Text(output["status"]) != "completed")
        {
            var error = output["error"] is { } detail ? Text(detail) ?? detail.ToJsonString() : null;
            throw new InvalidOperationException(error ?? "Pixel job did not complete (status: " + (Text(output["status"]) ?? "missing") + ").");
        }
        var requestSettings = new JsonObject();
        foreach (var key in new[]
        {
            "action",
            "direction",
            "frames",
            "fps",
            "outputWidth",
            "outputHeight",
            "artMode",
            "preserveStyle",
            "lockPalette",
            "preserveBackground"
        }

        )
            if (input[key] != null)
                requestSettings[key] = input[key]!.DeepClone();
        output["requestSettings"] = requestSettings;
        var image = Text(output["result"]?["image"]);
        if (image?.StartsWith("data:image/png;base64,", StringComparison.Ordinal) != true)
            throw new InvalidOperationException("Pixel job completed without a PNG image result (result.image must be a data:image/png;base64 URL).");
        {
            byte[] bytes;
            try { bytes = Convert.FromBase64String(image[22..]); }
            catch (FormatException) { throw new InvalidOperationException("Pixel result image is not valid base64 PNG data."); }
            if (bytes.Length < 24 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                throw new InvalidOperationException("Pixel result is not a valid PNG header.");
            output["imageWidth"] = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
            output["imageHeight"] = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));
            if (bytes.Length > 12_000_000)
                throw new InvalidOperationException("PNG exceeds the artifact limit.");
            var folder = Path.Combine(dataRoot, "artifacts", "tool-runs", run.Id);
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, "image.png");
            using var save = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            if (NormalizePixelBackground != null && !(input["preserveBackground"] is JsonValue keep && keep.TryGetValue<bool>(out var preserve) && preserve) &&
                Text(input["action"]) is "animation" or "direction" or "skin")
            {
                var cleaned = NormalizePixelBackground(bytes);
                if (!ReferenceEquals(cleaned, bytes) && cleaned.Length <= 12_000_000)
                {
                    var original = Path.Combine(folder, "image-original.png");
                    await File.WriteAllBytesAsync(original, bytes, save.Token);
                    output["originalArtifactPath"] = original;
                    output["backgroundProcessing"] = "Removed near-white background connected to the image border.";
                    bytes = cleaned;
                }
            }
            await File.WriteAllBytesAsync(file, bytes, save.Token);
            output["result"]!["image"] = null;
            // Other candidates the tool made (PixelLab returns several): saved beside the main image.
            if (output["result"]?["candidates"] is JsonArray candidates)
            {
                var saved = new JsonArray();
                var index = 0;
                foreach (var candidate in candidates.Skip(1).Take(15))
                {
                    var text = candidate?.ToString() ?? "";
                    var comma = text.IndexOf(',');
                    if (!text.StartsWith("data:image/png;base64,", StringComparison.Ordinal) || comma < 0)
                        continue;
                    byte[] extra;
                    try { extra = Convert.FromBase64String(text[(comma + 1)..]); } catch (FormatException) { continue; }
                    if (extra.Length < 24 || extra.Length > 4_000_000 || !extra.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                        continue;
                    var candidateFile = Path.Combine(folder, $"candidate-{++index + 1}.png");
                    await File.WriteAllBytesAsync(candidateFile, extra, save.Token);
                    saved.Add(candidateFile);
                }
                output["result"]!["candidates"] = null;
                if (saved.Count > 0)
                    output["candidatePaths"] = saved;
            }
            output["artifactPath"] = file;
            output["artifactSha256"] = Convert.ToHexString(SHA256.HashData(bytes));
        }

        return output;
    }

    // content.create answers with a queued AI task; its result (the jobs and posts it made) only
    // exists once the task has finished, so wait for it like an image job.
    private async Task<JsonNode> AwaitMarketingTask(ConnectedTool tool, ToolRun run, JsonNode output, CancellationToken token)
    {
        if (output["task"] is not JsonObject task || Text(task["id"]) is not { } id || !IsRemoteJobId(id))
            return output; // upload mode answers with the job directly
        run.RemoteId = id;
        store.SaveToolRun(run);
        JsonNode current = task;
        while (Text(current["status"]) is "QUEUED" or "RUNNING")
        {
            await Task.Delay(2000, token);
            current = await Request(tool, HttpMethod.Get, "/tasks/" + id, null, token);
        }
        var status = Text(current["status"]);
        if (status is "CANCELED" or "ACTION_REQUIRED" or "FAILED")
            throw new InvalidOperationException("Marketing content task " + status + ": " + (Text(current["error"]) ?? "no detail given") + " (task " + id + ")");
        return new JsonObject { ["task"] = current.DeepClone() };
    }

    private async Task CancelPixel(ConnectedTool tool, string id)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await Request(tool, HttpMethod.Post, "/api/codex/jobs/" + id + "/cancel", new(), timeout.Token);
        }
        catch (Exception)
        { /* Keep interrupted status; never infer a remote rollback. */
        }
    }

    public async Task CheckAsync(ConnectedTool tool, CancellationToken token = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            if (tool.Connector == "video")
                tool.Status = File.Exists(Path.Combine(tool.SourcePath, "editor_core.py")) && ProcessRunner.FindExecutable(tool.Executable) != null ? "Source/Python located; export dependencies not exercised" : "Configure TrailerForge source and Python executable";
            else if (tool.Connector == "music")
            {
                // Imports ZapBop's engine in its own Python; no project is read, no audio device opened.
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                var value = await RunZapBop(tool, "status", null, null, null, timeout.Token);
                tool.Status = string.IsNullOrWhiteSpace(tool.WorkspaceRoot) || !Directory.Exists(tool.WorkspaceRoot) ? "Engine imports OK; choose the allowed project workspace" : "Engine imports OK; WAV render not exercised";
                tool.Version = "ZapBop source; numpy " + (Text(value["numpy"]) ?? "?");
            }
            else
            {
                var value = await Request(tool, HttpMethod.Get, tool.Connector == "pixel" ? "/api/codex/status" : "/health", null, timeout.Token);
                var loggedIn = value["loggedIn"] is JsonValue flag && flag.TryGetValue<bool>(out var yes) && yes;
                tool.Status = tool.Connector == "pixel" && !loggedIn ? "Backend reachable; Codex login is not confirmed" : "Backend reachable";
                tool.Version = Text(value["version"]) ?? value["version"]?.ToJsonString() ?? "Unknown";
            }
        }
        catch (HttpRequestException) when (tool.AutoStartBackend && tool.Connector is "pixel" or "marketing")
        {
            // Not an error: the manager starts this backend itself when a task uses the tool.
            tool.Status = "Not running now; starts automatically when a task uses it";
        }
        catch (Exception ex)
        {
            tool.Status = "Unavailable: " + ex.Message;
        }

        tool.CheckedAt = DateTimeOffset.UtcNow;
        store.SaveTool(tool);
        Changed?.Invoke();
    }

    public JsonNode ReadOutput(ToolRun run)
    {
        if (run.OutputFile == null)
            return JsonNode.Parse(run.Output) ?? throw new InvalidOperationException("No JSON output.");
        var expected = Path.GetFullPath(Path.Combine(dataRoot, "artifacts", "tool-runs", run.Id, "output.json"));
        if (!expected.Equals(Path.GetFullPath(run.OutputFile), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unexpected output artifact location.");
        var bytes = File.ReadAllBytes(expected);
        if (Convert.ToHexString(SHA256.HashData(bytes)) != run.OutputSha256)
            throw new InvalidOperationException("Workflow output artifact changed.");
        return JsonNode.Parse(bytes) ?? throw new InvalidOperationException("No JSON artifact.");
    }

    public void StopAll()
    {
        foreach (var item in active.Values)
            item.Cancel();
    }

    public async Task StopAndWaitAsync()
    {
        StopAll();
        while (IsBusy)
            await Task.Delay(50);
    }

    public void Dispose()
    {
        StopAll();
        backends.Dispose();
        http.Dispose();
    }
}

// Non-2xx connector response. Kept an InvalidOperationException so existing callers/messages are unchanged.
public sealed class ToolHttpException(int statusCode, string message) : InvalidOperationException(message)
{
    public int StatusCode { get; } = statusCode;
    public bool IsTransient => StatusCode is 408 or 429 or >= 500;
}
