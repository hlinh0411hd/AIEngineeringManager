using Employee.Core;
using Employee.Infrastructure;
using System.Text.Json;
using static Harness;

static class ExternalCliChecks
{
    public static async Task Run()
    {
        await Check("Long prompts go to a file only for agents that can read project files", () =>
        {
            Assert(CliProvider.PromptFileCapable("kilo", AccessLevel.WriteProject) && CliProvider.PromptFileCapable("opencode", AccessLevel.ReadProject), "Project agents use a prompt file");
            Assert(!CliProvider.PromptFileCapable("opencode", AccessLevel.TextOnly) && !CliProvider.PromptFileCapable("copilot", AccessLevel.ReadProject), "Tool-less runs still refuse oversized prompts");
            Assert(CliProvider.PromptFilePointer(@"C:\p\a.md").Contains(@"C:\p\a.md") && CliProvider.PromptFilePointer("x").Length < 1000, "Pointer is short and names the file");
            return Task.CompletedTask;
        });

        await Check("Custom CLI config, protected API key, prompt transport and project-access gate", async () =>
        {
            var folder = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var executable = Path.Combine(folder, "agent.exe");
            File.WriteAllText(executable, "fixture");
            using var fixture = New(textOnly: true);
            var runner = new CustomCliRunner();
            var settings = new ProviderSettings
            {
                Id = "custom-cli",
                Executable = executable,
                ExternalArguments = ["run", "--model", "{model}", "--prompt", "{prompt}"],
                ExternalApiKeyEnvironmentVariable = "DEEPSEEK_API_KEY",
                ProtectedExternalApiKey = SecretProtection.Protect("fixture-api-key")
            };
            Assert(!JsonSerializer.Serialize(settings).Contains("fixture-api-key", StringComparison.Ordinal), "Saved settings do not contain the plaintext API key");
            var provider = new ExternalCliProvider(runner);
            var detected = await provider.DetectAsync(settings, CancellationToken.None);
            Assert(detected.CanExecute && !detected.CanWrite, "Configured executable is detected without granting write permission");

            var result = await provider.ExecuteAsync(
                new(fixture.Project, fixture.Work, "model-a", "context", 10000),
                _ => { },
                CancellationToken.None);
            Assert(result.Success && runner.Calls == 1, "TextOnly task executes through the configured CLI");
            Assert(runner.Arguments.SequenceEqual(["run", "--model", "model-a", "--prompt", "Request:\nWrite a short quest announcement\n\ncontext"]), "Argument templates receive the selected model and prompt");
            Assert(runner.Input == null && runner.Environment["DEEPSEEK_API_KEY"] == "fixture-api-key", "API key is passed only through the configured environment variable");
            Assert(runner.Directory != fixture.Project.Root, "Custom CLI does not run from the project directory");

            fixture.Work.Access = AccessLevel.ReadProject;
            var denied = await provider.ExecuteAsync(new(fixture.Project, fixture.Work, "", "", 10000), _ => { }, CancellationToken.None);
            Assert(denied.Failure == FailureKind.Permission && runner.Calls == 1, "Project access is rejected before the executable is called");

            fixture.Work.Access = AccessLevel.TextOnly;
            settings.ExternalArguments = ["run", "--model", "{model}"];
            await provider.DetectAsync(settings, CancellationToken.None);
            await provider.ExecuteAsync(new(fixture.Project, fixture.Work, "", "context", 10000), _ => { }, CancellationToken.None);
            Assert(runner.Input == "Request:\nWrite a short quest announcement\n\ncontext" && runner.Arguments.SequenceEqual(["run", "--model", ""]), "Without {prompt}, the prompt is sent on stdin");

            settings.ExternalApiKeyEnvironmentVariable = "UNRESTRICTED_SECRET";
            var rejected = await provider.DetectAsync(settings, CancellationToken.None);
            Assert(!rejected.CanExecute, "Unrecognized environment variables cannot receive API keys");
        });
        await Check("CLI Tool API parsing and backup detection for claude, codex, antigravity when unreachable", async () =>
        {
            using var api = new CliToolApiClient(new StubHandler(req =>
            {
                var path = req.RequestUri!.AbsolutePath;
                if (path.EndsWith("/api/agents", StringComparison.Ordinal))
                    return new(System.Net.HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"agents":[{"id":"opencode","name":"OpenCode","installed":true,"path":"opencode.exe","authentication":"SignedIn","usage":"Free","models":["opencode/qwen4-max-free",{"id":"opencode/big-pickle"}]}]}""")
                    };
                if (path.EndsWith("/probe", StringComparison.Ordinal))
                    return new(System.Net.HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"ok":true,"models":["opencode/qwen4-max-free"]}""")
                    };
                return new(System.Net.HttpStatusCode.NotFound);
            }));
            var agents = await api.ListAgentsAsync(new Uri("http://127.0.0.1:4777/"), "", CancellationToken.None);
            Assert(agents.Count == 1 && agents[0].Authentication == "SignedIn" && agents[0].Usage == "Free" && agents[0].Models?.Count == 2, "Agent list parses auth, usage and inline models");
            var probe = await api.ProbeModelsAsync(new Uri("http://127.0.0.1:4777/"), "", "opencode", CancellationToken.None);
            Assert(probe is { Ok: true } && probe.Models.SequenceEqual(["opencode/qwen4-max-free"]), "Probe parses flat or nested probe responses");

            using var failingApi = new CliToolApiClient(new StubHandler(_ => throw new HttpRequestException("connection refused")));
            var settings = new ManagerSettings { CliToolApiUrl = "http://127.0.0.1:4777", Providers = [] };
            var backup = new List<IAIProvider>
            {
                new BackupStubProvider("claude", canExecute: true, models: ["sonnet"]),
                new BackupStubProvider("codex", canExecute: false, models: []),
                new BackupStubProvider("antigravity", canExecute: true, models: ["gemini-4-neon-medium"])
            };
            var result = await new CliToolProviderDiscovery(backup, failingApi).DiscoverAsync(settings, CancellationToken.None);
            Assert(!result.Reachable && result.Health.Any(h => h.Id == "claude" && h.CanExecute) && result.Health.Any(h => h.Id == "codex") && result.Health.Any(h => h.Id == "antigravity" && h.CanExecute), "Unreachable API falls back to backup CLIs");
            CliToolProviderDiscovery.EnableUsable(settings, result.Health);
            Assert(settings.Providers.Single(p => p.Id == "claude").Enabled && settings.Providers.Single(p => p.Id == "antigravity").Enabled && !settings.Providers.Single(p => p.Id == "codex").Enabled, "Usable backup CLIs are enabled");
            Assert(settings.Providers.Single(p => p.Id == "antigravity").Models.Any(m => m.Id == "gemini-4-neon-medium"), "Backup CLI lists its models");
        });
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class BackupStubProvider(string id, bool canExecute, string[] models) : IAIProvider
    {
        public string Id { get; } = id;
        public Task<ProviderHealth> DetectAsync(ProviderSettings settings, CancellationToken token) =>
            Task.FromResult(new ProviderHealth(Id, canExecute, canExecute, canExecute, canExecute ? "Backup ready" : "Missing"));
        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken token) =>
            Task.FromResult<IReadOnlyList<ModelInfo>>(models.Select(m => new ModelInfo(m, Enum.GetValues<WorkKind>())).ToArray());
        public Task<AgentResult> ExecuteAsync(ExecutionRequest request, Action<string> output, CancellationToken token) =>
            Task.FromResult(new AgentResult(FailureKind.None, "", ""));
    }

    private sealed class CustomCliRunner : IProcessRunner
    {
        public int Calls { get; private set; }
        public string[] Arguments { get; private set; } = [];
        public string? Input { get; private set; }
        public string Directory { get; private set; } = "";
        public IReadOnlyDictionary<string, string> Environment { get; private set; } = new Dictionary<string, string>();

        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> args, string directory, string? input, int limit, Action<string>? output, CancellationToken token, IReadOnlyDictionary<string, string>? environment = null)
        {
            Calls++;
            Arguments = [.. args];
            Directory = directory;
            Input = input;
            Environment = environment ?? new Dictionary<string, string>();
            return Task.FromResult(new ProcessResult(0, "custom response"));
        }
    }
}
