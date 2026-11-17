using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Employee.Core;
using Employee.Infrastructure;

sealed class Fixture : IDisposable
{
    public SqliteStore Store { get; }
    public ProjectInfo Project { get; }
    public WorkItem Work { get; }
    public ManagerSettings Settings { get; } = new();
    public ProviderHealth[] Health { get; }
    public FakeProvider First { get; }
    public FakeProvider Second { get; }
    public Orchestrator Manager { get; }

    public Fixture(string directory, bool textOnly = false)
    {
        Directory.CreateDirectory(directory);
        Store = new(Path.Combine(directory, "db", "test.db"));
        var project = Path.Combine(directory, "project");
        Directory.CreateDirectory(project);
        Project = new ProjectIndexer(Store).Register(project, "Garden", "");
        // General orchestration fixtures perform work compatible with their deliberate Read/TextOnly scope.
        // Implementation with insufficient access has dedicated checks; generic fixture work must be internally consistent.
        Work = new()
        {
            ProjectId = Project.Id,
            Kind = textOnly ? WorkKind.Content : WorkKind.Coding,
            Access = textOnly ? AccessLevel.TextOnly : AccessLevel.ReadProject,
            Request = textOnly ? "Write a short quest announcement" : "Review weekly quest code"
        };
        Store.SaveWork(Work);
        First = new(textOnly ? "claude" : "codex");
        Second = new("gemini");
        foreach (var p in Settings.Providers)
        {
            p.Enabled = p.Id == First.Id || p.Id == Second.Id;
            p.Priority = p.Id == First.Id ? 90 : 50;
            p.Models.Add(new(p.Id == First.Id ? "a" : "b", Enum.GetValues<WorkKind>()));
        }

        // Router requires Codex for filesystem access. Text-only tests exercise cross-provider fallback.
        Health = [new(First.Id, true, true, First.Id == "codex", ""), new(Second.Id, true, true, false, "")];
        Manager = new(Store, [First, Second], new NullRunner());
    }

    public void Dispose() => Store.Dispose();
}

sealed class FakeProvider(string id) : IAIProvider
{
    public string Id => id;
    public int Calls { get; private set; }
    public AgentResult Result { get; set; } = new(FailureKind.None, "done");
    public Func<ExecutionRequest, AgentResult>? Response { get; set; }
    public bool Block { get; set; }
    public bool Exited { get; private set; }
    public Action? Before { get; set; }
    public Exception? DetectionError { get; set; }
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<ProviderHealth> DetectAsync(ProviderSettings s, CancellationToken t) => DetectionError == null ? Task.FromResult(new ProviderHealth(id, true, true, id == "codex", "")) : Task.FromException<ProviderHealth>(DetectionError);
    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken t) => Task.FromResult<IReadOnlyList<ModelInfo>>([]);
    public async Task<AgentResult> ExecuteAsync(ExecutionRequest request, Action<string> output, CancellationToken t)
    {
        Calls++;
        Before?.Invoke();
        Started.TrySetResult();
        try
        {
            if (Block)
                await Task.Delay(Timeout.Infinite, t);
            var result = Response?.Invoke(request) ?? Result;
            output(result.Output);
            return result;
        }
        finally
        {
            Exited = true;
        }
    }
}

sealed class NullRunner : IProcessRunner
{
    public Task<ProcessResult> RunAsync(string e, IReadOnlyList<string> a, string d, string? i, int l, Action<string>? o, CancellationToken t, IReadOnlyDictionary<string, string>? environment = null) => Task.FromResult(new ProcessResult(0, ""));
}

sealed class FakeOllama(bool invalid = false, bool timeout = false) : HttpMessageHandler
{
    public int GenerateCalls { get; private set; }
    public bool AllUnload { get; private set; } = true;
    public bool AllThinkOff { get; private set; } = true;
    public bool AllContextBounded { get; private set; } = true;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (request.Method == HttpMethod.Get)
            return Json("{\"models\":[{\"name\":\"test-local\"}]}");
        if (request.RequestUri!.AbsolutePath == "/api/show")
            return Json("{\"capabilities\":[\"completion\",\"thinking\"],\"thinking\":{\"values\":[false,true],\"default\":true}}");
        if (timeout)
            throw new OperationCanceledException(token);
        GenerateCalls++;
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        AllUnload &= body.RootElement.GetProperty("keep_alive").GetInt32() == 0;
        AllThinkOff &= body.RootElement.GetProperty("think").ValueKind == JsonValueKind.False;
        AllContextBounded &= body.RootElement.GetProperty("options").GetProperty("num_ctx").GetInt32() == 4096;
        var prompt = body.RootElement.GetProperty("prompt").GetString()!;
        var answer = prompt.Contains("null reference") ? "{\"kind\":\"coding\"}" : prompt.Contains("launch caption") ? "{\"kind\":\"content\"}" : prompt.Contains("battery") ? "{\"kind\":\"research\"}" : prompt.Contains("Chinese Garden") ? "{\"project\":\"Chinese Garden\"}" : prompt.Contains("12 sprites") ? "{\"count\":12}" : "{\"status\":\"paused\"}";
        return Json(JsonSerializer.Serialize(new { response = invalid ? "not json" : answer }));
    }

    private static HttpResponseMessage Json(string s) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(s, Encoding.UTF8, "application/json")
    };
}

sealed class NoopLease : IDisposable
{
    public void Dispose() { }
}

// Stands in for Python + zapbop_bridge.py: records each call and, for export, writes a WAV into --output-dir.
sealed class ZapBopRunner : IProcessRunner
{
    public List<(string Executable, List<string> Args, string Input)> Calls { get; } = [];
    public string? Fail { get; set; }
    public bool WriteWav { get; set; } = true;
    public bool ValidWav { get; set; } = true;
    public string? RawOutput { get; set; }
    // When set, written to the --result-file the manager passes, like the real bridges do.
    public string? ResultFileJson { get; set; }
    public string? LastResultFile { get; private set; }
    // When set, the bridge claims it wrote this file (to test that files outside the run folder are refused).
    public string? ReportedPath { get; set; }

    public Task<ProcessResult> RunAsync(string e, IReadOnlyList<string> a, string d, string? i, int l, Action<string>? o, CancellationToken t, IReadOnlyDictionary<string, string>? environment = null)
    {
        var args = a.ToList();
        Calls.Add((e, args, i ?? ""));
        LastResultFile = args.Contains("--result-file") ? args[args.IndexOf("--result-file") + 1] : null;
        if (Fail != null)
            return Task.FromResult(new ProcessResult(1, "Traceback noise\n" + Fail + "\n"));
        if (ResultFileJson != null && LastResultFile != null)
            File.WriteAllText(LastResultFile, ResultFileJson);
        if (RawOutput != null)
            return Task.FromResult(new ProcessResult(0, RawOutput));
        if (!new[] { "export", "sfx", "compose", "render" }.Any(args.Contains))
            return Task.FromResult(new ProcessResult(0, args.Contains("status") ? "{\"numpy\":\"2.0\",\"engine\":\"ZapBop\"}\n" : "{\"title\":\"Song\",\"bpm\":120}\n"));
        var path = Path.Combine(args[args.IndexOf("--output-dir") + 1], "song_120bpm.wav");
        if (WriteWav)
        {
            var wav = new byte[64];
            Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
            Encoding.ASCII.GetBytes("WAVE").CopyTo(wav, 8);
            if (!ValidWav)
                Encoding.ASCII.GetBytes("NOPE").CopyTo(wav, 8);
            File.WriteAllBytes(path, wav);
        }
        return Task.FromResult(new ProcessResult(0, JsonSerializer.Serialize(new { artifactPath = ReportedPath ?? path, bpm = 120 }) + "\n"));
    }
}

// Stands in for a bridge that answers through --result-file; the handler gets the args and returns the result JSON.
sealed class ResultFileRunner(Func<List<string>, string> result) : IProcessRunner
{
    public List<List<string>> Calls { get; } = [];

    public Task<ProcessResult> RunAsync(string e, IReadOnlyList<string> a, string d, string? i, int l, Action<string>? o, CancellationToken t, IReadOnlyDictionary<string, string>? environment = null)
    {
        var args = a.ToList();
        Calls.Add(args);
        File.WriteAllText(args[args.IndexOf("--result-file") + 1], result(args));
        return Task.FromResult(new ProcessResult(0, "renderer log line\n"));
    }
}

// Minimal loopback HTTP backend: answers each request from a (method, path) handler, one connection per request.
sealed class FakeHttpBackend : IDisposable
{
    private readonly System.Net.Sockets.TcpListener listener = new(System.Net.IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    public List<string> Requests { get; } = [];
    public string Endpoint { get; }

    public FakeHttpBackend(Func<string, string, (int Status, string Body)> handle)
    {
        listener.Start();
        Endpoint = $"http://127.0.0.1:{((System.Net.IPEndPoint)listener.LocalEndpoint).Port}";
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                System.Net.Sockets.TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(stop.Token); } catch { return; }
                using (client)
                {
                    try
                    {
                        var stream = client.GetStream();
                        var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, true);
                        var line = await reader.ReadLineAsync();
                        if (string.IsNullOrEmpty(line))
                            continue;
                        var length = 0;
                        string? header;
                        while (!string.IsNullOrEmpty(header = await reader.ReadLineAsync()))
                            if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                                length = int.Parse(header[15..].Trim());
                        if (length > 0)
                        {
                            var body = new char[length];
                            await reader.ReadBlockAsync(body, 0, length);
                        }
                        var parts = line.Split(' ');
                        lock (Requests)
                            Requests.Add(parts[0] + " " + parts[1]);
                        var (status, text) = handle(parts[0], parts[1]);
                        var bytes = Encoding.UTF8.GetBytes(text);
                        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} X\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(head);
                        await stream.WriteAsync(bytes);
                    }
                    catch (IOException) { }
                }
            }
        });
    }

    public void Dispose()
    {
        stop.Cancel();
        listener.Stop();
    }
}

sealed class HelpRunner(string help) : IProcessRunner
{
    public Task<ProcessResult> RunAsync(string e, IReadOnlyList<string> a, string d, string? i, int l, Action<string>? o, CancellationToken t, IReadOnlyDictionary<string, string>? environment = null) => Task.FromResult(new ProcessResult(0, help));
}

sealed class ValidationRunner(Action? mutation = null) : IProcessRunner
{
    public Task<ProcessResult> RunAsync(string e, IReadOnlyList<string> a, string d, string? i, int l, Action<string>? o, CancellationToken t, IReadOnlyDictionary<string, string>? environment = null)
    {
        mutation?.Invoke();
        return Task.FromResult(new ProcessResult(a.Contains("fail") ? 1 : 0, "fixture validation"));
    }
}
