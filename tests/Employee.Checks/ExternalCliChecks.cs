using Employee.Core;
using Employee.Infrastructure;
using System.Text.Json;
using static Harness;

static class ExternalCliChecks
{
    public static async Task Run()
    {
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
