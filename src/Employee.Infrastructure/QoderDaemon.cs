using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Employee.Core;

namespace Employee.Infrastructure;
public sealed record QoderProbe(string Version, bool Authenticated);

// qodercli 1.1.81 removed the headless print worker (sdk_worker_removed) and replaced it with a
// JSON-lines daemon started by the hidden `--sdk` flag: control frames open one Worker session per
// step, a user frame carries the prompt, and the session streams assistant/result frames back.
// The daemon borrows the local qodercli login through a one-shot auth payload file named by
// QODER_SDK_AUTH_PAYLOAD_FILE, which this class creates before starting the daemon and deletes
// afterwards. The payload holds no secret beyond the borrow marker and is never logged.
public static class QoderDaemon
{
    private const string SdkVersion = "employee-manager";
    private static readonly string[] Capabilities = ["multi_session_v2", "daemon_queries_v1"];
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(45);
    private static readonly object InitParams = new
    {
        daemon_protocol_version = 2,
        sdk_version = SdkVersion,
        capabilities = Capabilities,
        auth = new
        {
            type = "qodercli"
        }
    };

    public static async Task<QoderProbe> ProbeAsync(string executable, CancellationToken token)
    {
        await using var daemon = Session.Start(executable);
        var init = await daemon.CommandAsync("daemon.initialize", InitParams, HandshakeTimeout, token);
        var version = init.Result is { } initResult ? Text(initResult, "daemon_version") ?? "unknown" : "unknown";
        var auth = await daemon.CommandAsync("daemon.authenticate", null, TimeSpan.FromSeconds(20), token);
        var authenticated = auth.Result is { } result && result.TryGetProperty("authenticated", out var value) && value.ValueKind == JsonValueKind.True;
        return new(version, authenticated);
    }

    public static async Task<(IReadOnlyList<ModelInfo> Models, string Detail)> ModelsAsync(string executable, CancellationToken token)
    {
        await using var daemon = Session.Start(executable);
        var init = await daemon.CommandAsync("daemon.initialize", InitParams, HandshakeTimeout, token);
        CommandResult listing;
        try
        {
            listing = await daemon.CommandAsync("daemon.get_models", new
            {
                fetchStrategy = "live"
            }

            , TimeSpan.FromSeconds(90), token);
        }
        catch (QoderDaemonException)
        {
            listing = await daemon.CommandAsync("daemon.get_models", new
            {
                fetchStrategy = "cache"
            }

            , TimeSpan.FromSeconds(20), token);
        }

        var models = new List<ModelInfo>();
        if (listing.Result is { } result && result.TryGetProperty("models", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;
                var id = Text(item, "value");
                if (string.IsNullOrWhiteSpace(id) || (item.TryGetProperty("isEnabled", out var enabled) && enabled.ValueKind == JsonValueKind.False))
                    continue;
                var free = item.TryGetProperty("isFree", out var isFree) && isFree.ValueKind == JsonValueKind.True;
                models.Add(new ModelInfo(id, Enum.GetValues<WorkKind>(), DiscoverySource: "Qoder daemon catalog; " + (free ? "free model" : "paid from credits")));
            }
        }

        return (models, $"{models.Count} models from the Qoder daemon catalog (daemon {(init.Result is { } initResult ? Text(initResult, "daemon_version") : null) ?? "?"}).");
    }

    public static async Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> workerArgs, string cwd, string prompt, int limit, Action<string>? output, CancellationToken token)
    {
        using var runDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        runDeadline.CancelAfter(TimeSpan.FromMinutes(30));
        try
        {
            await using var daemon = Session.Start(executable);
            await daemon.CommandAsync("daemon.initialize", InitParams, HandshakeTimeout, runDeadline.Token);
            // The session resolves its model through the account landing that authenticate starts;
            // without it every session fails with ModelNotFoundError even for the default model.
            await daemon.CommandAsync("daemon.authenticate", null, TimeSpan.FromSeconds(30), runDeadline.Token);
            var opened = await daemon.CommandAsync("daemon.open_session", new
            {
                worker_args = workerArgs,
                cwd
            }

            , HandshakeTimeout, runDeadline.Token);
            var sessionId = opened.SessionId ?? throw new QoderDaemonException("Qoder daemon opened a session without an id.");
            daemon.Send(new
            {
                type = "user",
                session_id = sessionId,
                parent_tool_use_id = (string? )null,
                message = new
                {
                    role = "user",
                    content = new[]
                    {
                        new
                        {
                            type = "text",
                            text = prompt
                        }
                    }
                }
            });
            var text = new StringBuilder();
            var errors = new List<string>();
            var success = false;
            while (true)
            {
                var line = await daemon.ReadEventAsync(runDeadline.Token);
                if (line == null)
                    throw new QoderDaemonException("Qoder session ended without a result frame." + daemon.StderrTail());
                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    continue;
                }

                using (document)
                {
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                        continue;
                    var type = Text(root, "type");
                    if (type == "assistant" && root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object && message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var part in content.EnumerateArray())
                        {
                            if (part.ValueKind != JsonValueKind.Object || Text(part, "type") != "text")
                                continue;
                            var chunk = Text(part, "text");
                            if (string.IsNullOrEmpty(chunk))
                                continue;
                            if (text.Length < limit)
                            {
                                var keep = Math.Min(chunk.Length, limit - text.Length);
                                text.Append(chunk, 0, keep);
                            }

                            output?.Invoke(chunk);
                        }
                    }
                    else if (type == "result")
                    {
                        success = !(root.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True);
                        if (root.TryGetProperty("result", out var answer) && answer.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(answer.GetString()))
                        {
                            text.Clear();
                            var final = answer.GetString()!;
                            text.Append(final.Length > limit ? final[..limit] : final);
                        }

                        if (root.TryGetProperty("errors", out var list) && list.ValueKind == JsonValueKind.Array)
                            errors.AddRange(list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString() ?? "").Where(e => e.Length > 0));
                        break;
                    }
                    else if (type == "status_event" && Text(root, "status") == "daemon.session_updated" && root.TryGetProperty("data", out var data) && Text(data, "state") == "failed")
                    {
                        errors.Add(data.TryGetProperty("error", out var error) ? error.ToString() : "Qoder session failed.");
                        break;
                    }
                    else if (type == "protocol_error")
                    {
                        errors.Add("Qoder daemon protocol error: " + (Text(root, "message") ?? Text(root, "code") ?? line));
                        break;
                    }
                }
            }

            await daemon.CloseAsync(CancellationToken.None);
            return success
                ? new(0, text.ToString())
                : new(1, errors.Count > 0 ? string.Join("\n", errors) : text.ToString());
        }
        catch (OperationCanceledException)when (!token.IsCancellationRequested)
        {
            return new(FailureExit, "Qoder daemon run timed out after 30 minutes.");
        }

        catch (QoderDaemonException ex)
        {
            return new(FailureExit, ex.Message);
        }

        catch (Exception ex)when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new(FailureExit, "Qoder daemon could not run: " + ex.Message);
        }
    }

    private const int FailureExit = 1;
    private static string? Text(JsonElement element, string property) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private readonly record struct CommandResult(JsonElement? Result, string? SessionId);
    private sealed class QoderDaemonException(string message): Exception(message);
    private sealed class Session : IAsyncDisposable
    {
        private readonly Process process;
        private readonly Channel<string> frames = Channel.CreateUnbounded<string>();
        private readonly StringBuilder stderr = new();
        private readonly object stderrGate = new();
        private readonly string authDirectory;
        private int requestCounter;
        private Session(Process process, string authDirectory)
        {
            this.process = process;
            this.authDirectory = authDirectory;
        }

        public static Session Start(string executable)
        {
            var authDirectory = Path.Combine(Path.GetTempPath(), "employee-qoder-sdk-" + Guid.NewGuid().ToString("N")[..10]);
            Directory.CreateDirectory(authDirectory);
            var payload = Path.Combine(authDirectory, "payload.json");
            File.WriteAllText(payload, "{\"type\":\"qodercli\"}");
            var info = new ProcessStartInfo(executable)
            {
                WorkingDirectory = Path.GetTempPath(),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            info.ArgumentList.Add("--sdk");
            foreach (var key in new[]
            {
                "OPENAI_API_KEY",
                "CODEX_API_KEY",
                "ANTHROPIC_API_KEY",
                "GEMINI_API_KEY",
                "GOOGLE_API_KEY",
                "OPENROUTER_API_KEY",
                "AZURE_OPENAI_API_KEY",
                "DEEPSEEK_API_KEY",
                "MISTRAL_API_KEY",
                "XAI_API_KEY",
                "GROQ_API_KEY",
                "TOGETHER_API_KEY",
                "FIREWORKS_API_KEY"
            }

            )
                info.Environment.Remove(key);
            info.Environment["NO_COLOR"] = "1";
            info.Environment["QODER_SDK_AUTH_PAYLOAD_FILE"] = payload;
            var process = new Process
            {
                StartInfo = info
            };
            try
            {
                process.Start();
            }
            catch
            {
                try
                {
                    Directory.Delete(authDirectory, true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }

                throw;
            }

            var session = new Session(process, authDirectory);
            session.Pump();
            return session;
        }

        private void Pump()
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    string? line;
                    while ((line = await process.StandardOutput.ReadLineAsync(CancellationToken.None)) != null)
                        frames.Writer.TryWrite(line);
                }
                catch (Exception error)when (error is IOException or InvalidOperationException or ObjectDisposedException)
                {
                }
                finally
                {
                    frames.Writer.TryComplete();
                }
            });
            _ = Task.Run(async () =>
            {
                try
                {
                    var buffer = new char[2048];
                    int count;
                    while ((count = await process.StandardError.ReadAsync(buffer, CancellationToken.None)) > 0)
                        lock (stderrGate)
                            if (stderr.Length < 20_000)
                                stderr.Append(buffer, 0, count);
                }
                catch (Exception error)when (error is IOException or InvalidOperationException or ObjectDisposedException)
                {
                }
            });
        }

        public string StderrTail()
        {
            string tail;
            lock (stderrGate)
                tail = stderr.ToString().Trim();
            return tail.Length == 0 ? "" : " Daemon stderr: " + tail;
        }

        public void Send(object frame)
        {
            process.StandardInput.WriteLine(JsonSerializer.Serialize(frame));
            process.StandardInput.Flush();
        }

        public async Task<CommandResult> CommandAsync(string type, object? parameters, TimeSpan timeout, CancellationToken token)
        {
            var id = "req_" + Interlocked.Increment(ref requestCounter);
            var request = new Dictionary<string, object?>
            {
                ["type"] = type
            };
            if (parameters != null)
                foreach (var property in JsonSerializer.SerializeToElement(parameters).EnumerateObject())
                    request[property.Name] = property.Value;
            Send(new Dictionary<string, object?>
            {
                ["type"] = "control_request",
                ["request_id"] = id,
                ["request"] = request
            });
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                var line = await ReadFrameAsync(deadline - DateTime.UtcNow, token);
                if (line == null)
                    throw new QoderDaemonException($"{type} was not answered within {timeout.TotalSeconds:N0}s." + StderrTail());
                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    continue;
                }

                using (document)
                {
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                        continue;
                    var frameType = Text(root, "type");
                    if (frameType == "protocol_error")
                        throw new QoderDaemonException("Qoder daemon protocol error: " + (Text(root, "message") ?? Text(root, "code") ?? line));
                    if (frameType != "control_response" || !root.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Object || Text(response, "request_id") != id)
                        continue;
                    if (Text(response, "subtype") != "success")
                        throw new QoderDaemonException(Text(response, "error") ?? "Qoder daemon rejected " + type + ".");
                    var result = response.TryGetProperty("response", out var value) && value.ValueKind == JsonValueKind.Object ? value.Clone() : (JsonElement? )null;
                    return new(result, Text(root, "session_id"));
                }
            }
        }

        private async Task<string?> ReadFrameAsync(TimeSpan timeout, CancellationToken token)
        {
            if (timeout <= TimeSpan.Zero)
                return null;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(timeout);
            try
            {
                return await frames.Reader.ReadAsync(cts.Token);
            }
            catch (OperationCanceledException)when (!token.IsCancellationRequested)
            {
                return null;
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }

        public async Task<string?> ReadEventAsync(CancellationToken token)
        {
            try
            {
                return await frames.Reader.ReadAsync(token);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }

        public async Task CloseAsync(CancellationToken token)
        {
            try
            {
                await CommandAsync("daemon.shutdown", null, TimeSpan.FromSeconds(5), token);
            }
            catch (Exception error)when (error is QoderDaemonException or OperationCanceledException or IOException or InvalidOperationException)
            { /* Stopping the daemon is best effort; the process is killed on dispose. */
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception error)when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }

            try
            {
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
            }

            process.Dispose();
            try
            {
                Directory.Delete(authDirectory, true);
            }
            catch (Exception error)when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
