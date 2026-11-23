using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using Employee.Core;

namespace Employee.Infrastructure;
/// <summary>Starts local backends only on demand; never installs dependencies or invokes a model.
/// A backend the manager started is stopped again once no tool operation has used it for
/// <see cref="IdleShutdown"/>, and when the manager closes. A backend the user started is never stopped.</summary>
internal sealed class ToolBackendLauncher : IDisposable
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new();
    private readonly ConcurrentDictionary<string, Process> owned = new();
    private readonly ConcurrentDictionary<string, int> users = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> pendingStops = new();
    // Ends the started backends if the manager itself crashes or is killed.
    private readonly KillOnCloseJob job = new();
    // Short grace so consecutive tool steps of one plan reuse the backend instead of restarting it.
    public TimeSpan IdleShutdown { get; init; } = TimeSpan.FromMinutes(2);
    // Where a started backend's console output goes (<folder>/<tool id>/backend.log), so a backend
    // that dies can be diagnosed. Seen live: the Pixel Art server stopped mid-task without a trace.
    public string? LogFolder { get; init; }
    private readonly ConcurrentDictionary<string, string> logs = new();
    private readonly ConcurrentDictionary<string, string> stoppedByUs = new();
    public event Action<string>? Stopped;
    private static readonly IDisposable NoLease = new Lease(() => { });
    internal static Uri Endpoint(ConnectedTool tool)
    {
        if (!Uri.TryCreate(tool.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "http" || !new[]
        {
            "127.0.0.1",
            "localhost",
            "[::1]",
            "::1"
        }.Contains(endpoint.Host, StringComparer.OrdinalIgnoreCase) || endpoint.UserInfo.Length != 0)
            throw new InvalidOperationException("Only HTTP addresses on this machine are supported (127.0.0.1/localhost).");
        return endpoint;
    }

    private static async Task<bool> Listening(Uri endpoint, CancellationToken token)
    {
        using var socket = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await socket.ConnectAsync(endpoint.DnsSafeHost, endpoint.Port, timeout.Token);
            return true;
        }
        catch (SocketException ex)when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return false;
        }
        catch (OperationCanceledException)when (!token.IsCancellationRequested)
        {
            throw new InvalidOperationException("Port check timed out; no extra backend is started while its state is unknown.");
        }
    }

    // Makes sure the backend answers, starting it when allowed. Dispose the returned lease when the
    // operation is over: that is what lets an auto-started backend be stopped again.
    public async Task<IDisposable> EnsureAsync(ConnectedTool tool, Func<CancellationToken, Task> probe, Action<string> report, CancellationToken token)
    {
        if (tool.Connector is "video" or "aivideo" or "music")
            return NoLease; // Renderer runs per operation through a bridge, without a resident backend.
        var endpoint = Endpoint(tool);
        var key = $"loopback:{endpoint.Port}";
        var gate = gates.GetOrAdd(key, _ => new(1, 1));
        await gate.WaitAsync(token);
        try
        {
            if (await Listening(endpoint, token))
            {
                using var check = CancellationTokenSource.CreateLinkedTokenSource(token);
                check.CancelAfter(TimeSpan.FromSeconds(8));
                try
                {
                    await probe(check.Token);
                } // HTTP/auth errors must never cause a duplicate launch.
                catch (OperationCanceledException)when (!token.IsCancellationRequested)
                {
                    throw new InvalidOperationException("A backend holds the port but has not responded after 8 seconds. Not starting a duplicate; check the running backend.");
                }

                report("Connected to the running backend.");
                return Acquire(key);
            }

            if (!tool.AutoStartBackend)
                throw new InvalidOperationException("Backend is not running. Enable automatic backend start in Settings → Tools or start it manually.");
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
            startup.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(tool.BackendStartupTimeoutSeconds, 5, 600)));
            if (owned.TryGetValue(key, out var previous) && previous.HasExited)
            {
                owned.TryRemove(key, out _);
                previous.Dispose();
            }

            if (!owned.ContainsKey(key))
            {
                var info = StartInfo(tool, endpoint);
                var logFile = LogFolder == null ? null : Path.Combine(LogFolder, tool.Id, "backend.log");
                if (logFile != null)
                {
                    info.RedirectStandardOutput = true;
                    info.RedirectStandardError = true;
                }
                report("Backend not running; starting it and waiting for a connection…");
                var process = new Process
                {
                    StartInfo = info,
                    EnableRaisingEvents = true
                };
                try
                {
                    if (logFile != null)
                        StartLog(process, logFile, key);
                    if (!process.Start())
                        throw new InvalidOperationException("Could not start the backend.");
                    if (logFile != null)
                    {
                        process.BeginOutputReadLine();
                        process.BeginErrorReadLine();
                    }
                    owned[key] = process;
                    stoppedByUs.TryRemove(key, out _);
                    if (!job.Assign(process))
                        report("Backend started; it could not be tied to the manager's lifetime, so it is stopped on idle or close only.");
                    if (tool.Connector == "marketing" && string.IsNullOrWhiteSpace(tool.BackendExecutable))
                    {
                        // Rikerat's inspected native protocol accepts the configured session token on stdin.
                        await process.StandardInput.WriteLineAsync(SecretProtection.Unprotect(tool.ProtectedToken).AsMemory(), startup.Token);
                        await process.StandardInput.FlushAsync(startup.Token);
                    }
                }
                catch
                {
                    if (!owned.ContainsKey(key))
                        process.Dispose();
                    throw;
                }
            }

            report("Waiting for the backend to be ready…");
            while (true)
            {
                startup.Token.ThrowIfCancellationRequested();
                if (owned.TryGetValue(key, out var process) && process.HasExited)
                    throw new InvalidOperationException($"Backend exited (code {process.ExitCode}). Check the executable, arguments and dependencies; the manager installs nothing.");
                if (await Listening(endpoint, startup.Token))
                {
                    using var check = CancellationTokenSource.CreateLinkedTokenSource(startup.Token);
                    check.CancelAfter(TimeSpan.FromSeconds(5));
                    try
                    {
                        await probe(check.Token);
                        report("Backend ready; continuing the task.");
                        return Acquire(key);
                    }
                    catch (HttpRequestException)
                    { /* Listener may still be starting. No operation has been submitted. */
                    }
                    catch (OperationCanceledException)when (!startup.IsCancellationRequested)
                    {
                    }
                }

                await Task.Delay(500, startup.Token);
            }
        }
        catch (OperationCanceledException)when (!token.IsCancellationRequested)
        {
            var message = $"Backend start/connection took longer than {Math.Clamp(tool.BackendStartupTimeoutSeconds, 5, 600)} seconds. No operation was sent and no model was called; see Settings → Tools → Startup wait.";
            report(message);
            throw new InvalidOperationException(message);
        }
        catch (Exception ex)when (ex is not OperationCanceledException)
        {
            report("Could not connect to/start the backend: " + ex.Message);
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    // Appends the backend's output, with times, to its log (the previous run's log is kept as .old).
    private void StartLog(Process process, string file, string key)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        try
        {
            if (File.Exists(file))
                File.Move(file, file + ".old", true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        logs[key] = file;
        var gate = new object();
        void Write(string? line)
        {
            if (line == null)
                return;
            lock (gate)
            {
                try { File.AppendAllText(file, $"{DateTimeOffset.Now:HH:mm:ss} {line}{Environment.NewLine}"); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        process.OutputDataReceived += (_, e) => Write(e.Data);
        process.ErrorDataReceived += (_, e) => Write(e.Data);
        process.Exited += (_, _) =>
        {
            int? code = null;
            try { code = process.ExitCode; } catch (InvalidOperationException) { }
            Write($"[manager] backend exited with code {code?.ToString() ?? "?"}{(stoppedByUs.ContainsKey(key) ? " (stopped by the manager: idle or closing)" : " (NOT stopped by the manager)")}");
        };
    }

    // Why a backend this manager started is gone, for a tool error: exit code and its last output.
    public string ExitNote(ConnectedTool tool)
    {
        Uri endpoint;
        try { endpoint = Endpoint(tool); } catch (InvalidOperationException) { return ""; }
        var key = $"loopback:{endpoint.Port}";
        if (!owned.TryGetValue(key, out var process) || !process.HasExited)
            return "";
        var code = "?";
        try { code = process.ExitCode.ToString(); } catch (InvalidOperationException) { }
        var tail = "";
        if (logs.TryGetValue(key, out var file) && File.Exists(file))
            try { tail = string.Join(" | ", File.ReadLines(file).TakeLast(6)); } catch (IOException) { }
        return $" The {tool.Name} backend the manager started has stopped (exit code {code}){(tail.Length > 0 ? "; its last output: " + (tail.Length > 900 ? tail[^900..] : tail) : "")}{(logs.TryGetValue(key, out var path) ? " (full log: " + path + ")" : "")}.";
    }

    private static ProcessStartInfo StartInfo(ConnectedTool tool, Uri endpoint)
    {
        var cwd = string.IsNullOrWhiteSpace(tool.BackendWorkingDirectory) ? tool.SourcePath : tool.BackendWorkingDirectory;
        if (!Path.IsPathFullyQualified(cwd) || !Directory.Exists(cwd))
            throw new InvalidOperationException("Configure an absolute, existing backend folder in Settings → Tools.");
        var info = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = cwd
        };
        if (!string.IsNullOrWhiteSpace(tool.BackendExecutable))
        {
            info.FileName = ProcessRunner.FindExecutable(tool.BackendExecutable) ?? throw new InvalidOperationException("The configured backend executable was not found.");
            if (new[]
            {
                "cmd.exe",
                "powershell.exe",
                "pwsh.exe",
                "wscript.exe",
                "cscript.exe",
                "mshta.exe"
            }.Contains(Path.GetFileName(info.FileName), StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("Use the backend executable directly with a JSON argument list; shell/script launchers are not allowed.");
            foreach (var argument in tool.BackendArguments)
                info.ArgumentList.Add(argument);
        }
        else if (tool.Connector == "pixel")
        {
            if (endpoint.DnsSafeHost.Contains(':'))
                throw new InvalidOperationException("Pixel Foundry listens on IPv4; use http://127.0.0.1 with the correct port.");
            if (!File.Exists(Path.Combine(cwd, "server.mjs")))
                throw new InvalidOperationException("Pixel Foundry server.mjs was not found in the backend folder.");
            info.FileName = ProcessRunner.FindExecutable("node") ?? throw new InvalidOperationException("Node.js not found. Install Node >=22 or configure the backend executable.");
            info.ArgumentList.Add("--env-file-if-exists=.env");
            info.ArgumentList.Add("server.mjs");
            info.Environment["PORT"] = endpoint.Port.ToString();
        }
        else if (tool.Connector == "marketing")
        {
            if (endpoint.DnsSafeHost.Contains(':'))
                throw new InvalidOperationException("Rikerat listens on IPv4; use http://127.0.0.1 with the correct port.");
            var python = Path.Combine(cwd, ".venv", "Scripts", "python.exe");
            if (!File.Exists(python) || !File.Exists(Path.Combine(cwd, "services", "backend", "rikerat", "__main__.py")))
                throw new InvalidOperationException("The Rikerat backend and its installed Python .venv were not found. Configure the correct Source folder; the manager does not run setup.");
            if (!Path.IsPathFullyQualified(tool.BackendDataRoot))
                throw new InvalidOperationException("Choose the Marketing backend's data folder in Settings → Tools.");
            if (string.IsNullOrWhiteSpace(SecretProtection.Unprotect(tool.ProtectedToken)))
                throw new InvalidOperationException("Enter the Marketing session token before starting the backend automatically.");
            info.FileName = python;
            info.RedirectStandardInput = true;
            info.Environment["PYTHONPATH"] = Path.Combine(cwd, "services", "backend");
            foreach (var argument in new[]
            {
                "-m",
                "rikerat",
                "--data-root",
                tool.BackendDataRoot,
                "--port",
                endpoint.Port.ToString()
            }

            )
                info.ArgumentList.Add(argument);
        }
        else
            throw new InvalidOperationException("Configure the backend executable, folder and arguments in Settings → Tools.");
        foreach (var secret in new[]
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
            info.Environment.Remove(secret);
        return info;
    }

    private IDisposable Acquire(string key)
    {
        users.AddOrUpdate(key, 1, (_, count) => count + 1);
        if (pendingStops.TryRemove(key, out var pending))
            pending.Cancel();
        return new Lease(() => Release(key));
    }

    private void Release(string key)
    {
        if (users.AddOrUpdate(key, 0, (_, count) => Math.Max(0, count - 1)) > 0 || !owned.ContainsKey(key))
            return;
        var cancel = new CancellationTokenSource();
        if (pendingStops.TryRemove(key, out var previous))
            previous.Cancel();
        pendingStops[key] = cancel;
        _ = StopWhenIdleAsync(key, cancel);
    }

    private async Task StopWhenIdleAsync(string key, CancellationTokenSource cancel)
    {
        try
        {
            await Task.Delay(IdleShutdown, cancel.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var gate = gates.GetOrAdd(key, _ => new(1, 1));
        // A start in progress owns the gate: leave the backend running and let its lease decide.
        if (!await gate.WaitAsync(0))
            return;
        try
        {
            if (cancel.IsCancellationRequested || users.GetValueOrDefault(key) > 0 || !owned.TryRemove(key, out var process))
                return;
            pendingStops.TryRemove(new KeyValuePair<string, CancellationTokenSource>(key, cancel));
            stoppedByUs[key] = "idle";
            Shutdown(process);
            Stopped?.Invoke(key);
        }
        finally
        {
            gate.Release();
        }
    }

    // Orderly shutdown where the backend supports it (Rikerat reads "shutdown" on stdin), otherwise
    // the process tree the manager started is ended. Only processes in `owned` ever reach here.
    private static void Shutdown(Process process)
    {
        try
        {
            if (!process.HasExited && process.StartInfo.RedirectStandardInput)
            {
                process.StandardInput.WriteLine("shutdown");
                process.StandardInput.Flush();
                process.WaitForExit(10000);
            }

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
        catch (IOException)
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var pending in pendingStops.Values)
            pending.Cancel();
        pendingStops.Clear();
        foreach (var key in owned.Keys.ToArray())
            if (owned.TryRemove(key, out var process))
            {
                stoppedByUs[key] = "closing";
                Shutdown(process);
            }
        job.Dispose();
    }

    private sealed class Lease(Action release) : IDisposable
    {
        private int released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
                release();
        }
    }
}
