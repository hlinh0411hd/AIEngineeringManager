using Employee.Core;

namespace Employee.Infrastructure;

public sealed class ExternalCliProvider(IProcessRunner runner) : IAIProvider, IModelCatalogStatus
{
    private static readonly HashSet<string> ApiKeyVariables = new(StringComparer.Ordinal)
    {
        "OPENAI_API_KEY",
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
    };

    private string? executable;
    private ProviderSettings? configured;
    private string apiKey = "";
    private bool ready;

    public string Id => "custom-cli";
    public string ModelDiscoveryDetail => ready ? "Custom CLI; add exact model IDs manually or use the CLI default." : "Configure and detect the custom CLI first.";

    public static bool IsSupportedApiKeyVariable(string? name) => name != null && ApiKeyVariables.Contains(name);

    public static void ValidateArguments(IReadOnlyList<string>? arguments)
    {
        if (arguments is null || arguments.Count is < 1 or > 30 || arguments.Any(argument => argument == null || argument.Length > 4096))
            throw new InvalidOperationException("Custom CLI arguments must contain 1–30 entries, each no longer than 4096 characters.");
    }

    public Task<ProviderHealth> DetectAsync(ProviderSettings settings, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ready = false;
        configured = null;
        executable = null;
        apiKey = "";

        try
        {
            ValidateArguments(settings.ExternalArguments);
        }
        catch (InvalidOperationException error)
        {
            return Task.FromResult(new ProviderHealth(Id, false, false, false, error.Message));
        }

        if (!IsSupportedApiKeyVariable(settings.ExternalApiKeyEnvironmentVariable))
            return Task.FromResult(new ProviderHealth(Id, false, false, false, "Choose a supported API-key environment variable."));

        executable = string.IsNullOrWhiteSpace(settings.Executable) ? null : ProcessRunner.FindExecutable(settings.Executable);
        if (executable == null)
            return Task.FromResult(new ProviderHealth(Id, false, false, false, "Configure an existing executable (.exe) or a command available on PATH."));
        if (new[] { "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "mshta.exe" }
            .Contains(Path.GetFileName(executable), StringComparer.OrdinalIgnoreCase))
        {
            executable = null;
            return Task.FromResult(new ProviderHealth(Id, true, false, false, "Shell and script launchers are not accepted. Configure the agent executable directly."));
        }

        try
        {
            apiKey = SecretProtection.Unprotect(settings.ProtectedExternalApiKey);
        }
        catch (Exception error) when (error is FormatException or System.ComponentModel.Win32Exception)
        {
            executable = null;
            return Task.FromResult(new ProviderHealth(Id, true, false, false, "The saved API key could not be decrypted. Clear it and enter it again."));
        }

        configured = new ProviderSettings
        {
            Id = settings.Id,
            ExternalArguments = [.. settings.ExternalArguments],
            ExternalApiKeyEnvironmentVariable = settings.ExternalApiKeyEnvironmentVariable,
            ProtectedExternalApiKey = settings.ProtectedExternalApiKey
        };
        ready = true;
        return Task.FromResult(new ProviderHealth(Id, true, true, false,
            "Custom CLI configured. Detection did not start it. Only TextOnly tasks are allowed; the manager does not provide a project path or project files.",
            apiKey.Length == 0 ? "Unknown" : "Configured"));
    }

    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        IReadOnlyList<ModelInfo> models = ready
            ? [new("", Enum.GetValues<WorkKind>(), DiscoverySource: "Custom CLI default; exact model unknown")]
            : [];
        return Task.FromResult(models);
    }

    public async Task<AgentResult> ExecuteAsync(ExecutionRequest request, Action<string> output, CancellationToken token)
    {
        if (!ready || executable == null || configured == null)
            return new(FailureKind.Unavailable, "", "Detect the configured custom CLI before using it.");
        if (request.Work.Access != AccessLevel.TextOnly)
            return new(FailureKind.Permission, "", "Custom CLI is restricted to TextOnly tasks; the manager does not provide project files.");

        var prompt = $"Request:\n{request.Work.Request}\n\n{request.Context}";
        var args = configured.ExternalArguments
            .Select(argument => argument.Replace("{model}", request.ModelId, StringComparison.Ordinal)
                .Replace("{prompt}", prompt, StringComparison.Ordinal))
            .ToArray();
        var promptInArguments = configured.ExternalArguments.Any(argument => argument.Contains("{prompt}", StringComparison.Ordinal));
        if (promptInArguments && OperatingSystem.IsWindows() && executable.Length + args.Sum(argument => argument.Length + 3) > 30000)
            return new(FailureKind.InputTooLarge, "", "The custom CLI prompt exceeds the Windows command-line limit. Configure the CLI to read its prompt from stdin; it was not started.");
        var environment = new Dictionary<string, string>();
        if (apiKey.Length > 0)
            environment[configured.ExternalApiKeyEnvironmentVariable] = apiKey;

        try
        {
            var result = await runner.RunAsync(
                executable,
                args,
                Path.GetTempPath(),
                promptInArguments ? null : prompt,
                request.MaxOutputChars,
                output,
                token,
                environment);
            var failure = ProviderErrors.Classify(result.ExitCode, result.Output);
            return new(failure, result.Output, failure == FailureKind.None ? "" : ProviderFailureDetails.Describe(failure, result.Output), result.ExitCode);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            return new(FailureKind.LaunchFailed, "", $"Custom CLI could not be started: {error.Message}");
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            return new(FailureKind.Unavailable, "", error.Message);
        }
    }
}
