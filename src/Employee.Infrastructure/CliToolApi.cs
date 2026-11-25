using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Employee.Core;

namespace Employee.Infrastructure;

public sealed record CliToolAgent(
    string Id,
    string Name,
    string Binary,
    bool Installed,
    string Path,
    string Version,
    string BaseUrl,
    string ApiKey,
    string Authentication,
    string Usage,
    IReadOnlyList<string>? Models = null);

public sealed record CliToolProbe(bool Ok, IReadOnlyList<string> Models, string Detail, string Error);

public sealed record CliToolDiscoveryResult(bool Reachable, IReadOnlyList<ProviderHealth> Health, int Total, int Installed, int Usable, string Detail);

// Reads the local aihub (CLI Tool) HTTP API: agent detection and model probing live there, so the
// manager does not scan installed CLIs itself.
public sealed class CliToolApiClient : IDisposable
{
    private const int MaxResponseBytes = 1_000_000;
    private static readonly Regex ModelId = new("^[A-Za-z0-9][A-Za-z0-9._:/-]{0,159}$", RegexOptions.CultureInvariant);
    private readonly HttpClient http;

    public CliToolApiClient(HttpMessageHandler? handler = null)
    {
        http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public static bool TryParseBaseUrl(string? value, out Uri? baseUri)
    {
        if (value == null || value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
        {
            baseUri = null;
            return false;
        }

        baseUri = new Uri(uri.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
        return true;
    }

    public async Task<IReadOnlyList<CliToolAgent>> ListAgentsAsync(Uri baseUri, string token, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, new Uri(baseUri, "api/agents"), token, cancellationToken);
        var body = await ReadBodyAsync(response, cancellationToken);
        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.NoContent))
            throw new InvalidOperationException(Describe(response.StatusCode, body));
        try
        {
            using var document = JsonDocument.Parse(body);
            JsonElement agents;
            if (document.RootElement.ValueKind == JsonValueKind.Array)
                agents = document.RootElement;
            else if (!document.RootElement.TryGetProperty("agents", out agents) || agents.ValueKind != JsonValueKind.Array)
                return [];
            var result = new List<CliToolAgent>();
            foreach (var item in agents.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;
                var id = Text(item, "id");
                if (string.IsNullOrWhiteSpace(id))
                    continue;
                result.Add(new(
                    id,
                    Text(item, "name"),
                    Text(item, "binary"),
                    Bool(item, "installed"),
                    Text(item, "path"),
                    Text(item, "version"),
                    FirstText(item, "baseUrl", "base_url"),
                    FirstText(item, "apiKey", "api_key", "key", "token"),
                    FirstText(item, "authentication", "auth", "login"),
                    FirstText(item, "usage", "quota", "credits"),
                    ReadModelIds(item, "models")));
            }

            return result;
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("The CLI Tool API agent list was not valid JSON.");
        }
    }

    // Returns null when the running CLI Tool build has no model probe endpoint.
    public async Task<CliToolProbe?> ProbeModelsAsync(Uri baseUri, string token, string agentId, CancellationToken cancellationToken)
    {
        var path = "api/agents/" + Uri.EscapeDataString(agentId) + "/probe";
        using var response = await SendAsync(HttpMethod.Post, new Uri(baseUri, path), token, cancellationToken);
        var body = await ReadBodyAsync(response, cancellationToken);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
            return null;
        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.NoContent))
            return new(false, [], "", Describe(response.StatusCode, body));
        try
        {
            using var document = JsonDocument.Parse(body);
            var probe = document.RootElement.TryGetProperty("probe", out var nested) && nested.ValueKind == JsonValueKind.Object
                ? nested
                : document.RootElement;
            if (probe.ValueKind != JsonValueKind.Object || (!probe.TryGetProperty("ok", out _) && !probe.TryGetProperty("models", out _)))
                return new(false, [], "", "The CLI Tool API probe response had no probe object.");
            var models = ReadModelIds(probe, "models");
            var ok = Bool(probe, "ok") || (!probe.TryGetProperty("ok", out _) && models.Length > 0);
            var error = Text(probe, "error");
            var detail = Text(probe, "detail");
            return new(ok, models, detail, error);
        }
        catch (JsonException)
        {
            return new(false, [], "", "The CLI Tool API probe response was not valid JSON.");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri uri, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri);
        if (token.Length > 0)
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
                break;
            if (buffer.Length + read > MaxResponseBytes)
                throw new InvalidOperationException("The CLI Tool API response is too large.");
            buffer.Write(chunk, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string Describe(HttpStatusCode status, string body)
    {
        var text = body.Length > 240 ? body[..240] : body;
        var description = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return $"CLI Tool API returned HTTP {(int)status}" + (description.Length > 0 ? ": " + description : ".");
    }

    private static string Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static string FirstText(JsonElement item, params string[] names)
    {
        foreach (var name in names)
        {
            var value = Text(item, name);
            if (value.Length > 0)
                return value;
        }
        return "";
    }

    private static string[] ReadModelIds(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array)
            return [];
        var models = new List<string>();
        foreach (var entry in list.EnumerateArray())
        {
            var id = entry.ValueKind == JsonValueKind.String ? entry.GetString()
                : entry.ValueKind == JsonValueKind.Object ? FirstText(entry, "id", "name", "model")
                : null;
            if (!string.IsNullOrWhiteSpace(id) && ModelId.IsMatch(id))
                models.Add(id);
        }

        return models.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static bool Bool(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    public void Dispose() => http.Dispose();
}

public sealed class CliToolProviderDiscovery(IList<IAIProvider> providers, CliToolApiClient client)
{
    private static readonly string[] SpecialProviderIds = ["custom-cli", "responses-api", "ollama"];
    // Built-in backup CLIs detected directly when the external CLI Tool API is unreachable.
    private static readonly string[] BackupCliIds = ["claude", "codex", "antigravity"];
    // Registered by the CLI Tool API but not an AI agent: the Cloudflare resource CLI (cf).
    private static readonly HashSet<string> NonAiAgentIds = new(StringComparer.OrdinalIgnoreCase) { "cloudflare" };
    private static readonly Regex SafeAgentId = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);

    public async Task<CliToolDiscoveryResult> DiscoverAsync(ManagerSettings settings, CancellationToken cancellationToken)
    {
        var url = settings.CliToolApiUrl?.Trim() ?? "";
        if (url.Length == 0 || !CliToolApiClient.TryParseBaseUrl(url, out var baseUri))
            return await UnreachableAsync("CLI Tool API address is empty or invalid. Enter the aihub URL, for example http://127.0.0.1:4777.", settings, cancellationToken);
        string token;
        try
        {
            token = SecretProtection.Unprotect(settings.ProtectedCliToolApiToken);
        }
        catch (Exception error) when (error is FormatException or System.ComponentModel.Win32Exception)
        {
            return await UnreachableAsync("The saved CLI Tool API token could not be decrypted. Clear it and enter it again.", settings, cancellationToken);
        }

        IReadOnlyList<CliToolAgent> agents;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            agents = await client.ListAgentsAsync(baseUri!, token, timeout.Token);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested && error is HttpRequestException or IOException or OperationCanceledException or InvalidOperationException or JsonException)
        {
            return await UnreachableAsync("CLI Tool API is not reachable at " + url + ": " + error.Message, settings, cancellationToken);
        }

        var previous = new Dictionary<string, ProviderSettings>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in settings.Providers)
            previous.TryAdd(provider.Id, provider);
        var validAgents = agents
            .Where(agent => SafeAgentId.IsMatch(agent.Id) && !SpecialProviderIds.Contains(agent.Id, StringComparer.OrdinalIgnoreCase) && !NonAiAgentIds.Contains(agent.Id))
            .Select(agent => agent with { Id = CliProvider.NormalizeId(agent.Id) })
            .DistinctBy(agent => agent.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var installed = validAgents.Where(agent => agent.Installed).ToArray();

        // The agent list is the source of truth for CLI providers: one settings row per installed
        // agent means the Settings toggles and provider picker follow however many agents exist.
        var next = new List<ProviderSettings>();
        foreach (var agent in validAgents)
        {
            var config = previous.TryGetValue(agent.Id, out var existing) ? existing : new ProviderSettings { Id = agent.Id };
            config.Id = agent.Id;
            config.DisplayName = string.IsNullOrWhiteSpace(agent.Name) ? agent.Id : agent.Name;
            config.FromCliToolApi = true;
            // Only an exact .exe is stored; shims keep searching PATH so later verification still resolves them.
            // A path the user already overrode is kept while it still exists; a stale one follows the API.
            if (agent.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && (config.Executable.Length == 0 || !File.Exists(config.Executable)))
                config.Executable = agent.Path;
            if (config.ResponsesApiBaseUrl.Length == 0 && Uri.TryCreate(agent.BaseUrl, UriKind.Absolute, out var baseAddress) && baseAddress.Scheme is "http" or "https")
                config.ResponsesApiBaseUrl = agent.BaseUrl;
            if (config.ProtectedResponsesApiKey.Length == 0 && agent.ApiKey.Length > 0)
                config.ProtectedResponsesApiKey = SecretProtection.Protect(agent.ApiKey);
            next.Add(config);
        }

        foreach (var id in SpecialProviderIds)
            next.Add(previous.TryGetValue(id, out var special) ? special : new ProviderSettings { Id = id });
        settings.Providers = next;
        settings.ProvidersManagedByCliToolApi = true;

        var health = new List<ProviderHealth>();
        foreach (var agent in validAgents)
        {
            var canWrite = agent.Id is not ("freebuff" or "commandcode" or "clixad");
            var version = string.IsNullOrWhiteSpace(agent.Version) ? "" : " · " + agent.Version;
            if (CliProvider.HasExecutionProfile(agent.Id))
            {
                var provider = providers.OfType<CliProvider>().FirstOrDefault(p => p.Id.Equals(agent.Id, StringComparison.OrdinalIgnoreCase));
                var ready = provider?.ApplyApiDetection(agent.Path) == true;
                var auth = string.IsNullOrWhiteSpace(agent.Authentication) ? "Unknown" : agent.Authentication;
                var usage = string.IsNullOrWhiteSpace(agent.Usage) ? "Unknown" : agent.Usage;
                health.Add(new(agent.Id, true, ready, ready && canWrite, ready
                    ? "Detected by CLI Tool API" + version + ". Headless flags were not re-probed; authentication: " + auth + "; usage: " + usage + "."
                    : "CLI Tool API reports this agent installed, but the manager could not resolve a launchable executable. Configure its path in provider settings.",
                    auth, usage));
            }
            else if (Uri.TryCreate(agent.BaseUrl, UriKind.Absolute, out _))
            {
                EnsureEndpointProvider(agent.Id);
                var config = settings.Providers.First(p => p.Id.Equals(agent.Id, StringComparison.OrdinalIgnoreCase));
                var ready = agent.Installed && !string.IsNullOrWhiteSpace(config.ProtectedResponsesApiKey);
                health.Add(new(agent.Id, agent.Installed, ready, false,
                    ready ? "OpenAI-compatible endpoint from CLI Tool API" : "Agent exposes a URL but no usable API key was supplied; configure its key in provider settings.",
                    string.IsNullOrWhiteSpace(agent.Authentication) ? (ready ? "Configured" : "Unknown") : agent.Authentication,
                    string.IsNullOrWhiteSpace(agent.Usage) ? "Unknown" : agent.Usage));
            }
            else if (CliProvider.IsDetectedOnly(agent.Id))
                health.Add(new(agent.Id, true, false, false, "Detected by CLI Tool API" + version + ". This manager intentionally does not execute it headlessly."));
            else
                health.Add(new(agent.Id, agent.Installed, false, false, "Detected by CLI Tool API" + version + ". No execution adapter is available for this agent in this manager."));
        }

        var probed = await ApplyProbeModelsAsync(baseUri!, token, installed, settings, cancellationToken);
        await ListLocalModelsAsync(installed, probed, settings, health, cancellationToken);
        await DetectSpecialProvidersAsync(settings, health, cancellationToken);

        var usable = health.Count(h => h.CanExecute);
        var detail = $"CLI Tool API {url}: {validAgents.Length} agents tracked, {installed.Length} installed, {usable} usable by this manager.";
        return new(true, health, agents.Count, installed.Length, usable, detail);
    }

    private void EnsureEndpointProvider(string id)
    {
        if (providers.Any(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            return;
        providers.Add(new ResponsesApiProvider(id));
    }

    private async Task<CliToolDiscoveryResult> UnreachableAsync(string detail, ManagerSettings settings, CancellationToken cancellationToken)
    {
        var health = new List<ProviderHealth>();
        EnsureBackupProviders(settings);
        await DetectProvidersAsync(BackupCliIds, settings, health, cancellationToken);
        await DetectSpecialProvidersAsync(settings, health, cancellationToken);
        var backupReady = health.Where(h => BackupCliIds.Contains(h.Id, StringComparer.OrdinalIgnoreCase) && h.CanExecute).Select(h => h.Id).ToArray();
        var summary = backupReady.Length > 0
            ? $"{detail} Backup CLIs ready: {string.Join(", ", backupReady)}."
            : $"{detail} Backup CLIs checked: {string.Join(", ", BackupCliIds)}.";
        return new(false, health, BackupCliIds.Length + SpecialProviderIds.Length, health.Count(h => h.Installed), health.Count(h => h.CanExecute), summary);
    }

    private void EnsureBackupProviders(ManagerSettings settings)
    {
        var runner = new ProcessRunner();
        foreach (var id in BackupCliIds)
        {
            if (!settings.Providers.Any(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                settings.Providers.Add(new ProviderSettings { Id = id, DisplayName = id == "antigravity" ? "Antigravity" : char.ToUpperInvariant(id[0]) + id[1..] });
            if (!providers.Any(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                providers.Add(new CliProvider(id, runner));
        }
    }

    // Enables exactly the agents the CLI Tool API reports as runnable; providers the user switched
    // off stay off, and a first model ID is catalogued so the router has something to send.
    public static IReadOnlyList<ProviderSetupResult> EnableUsable(ManagerSettings settings, IReadOnlyList<ProviderHealth> health)
    {
        var results = new List<ProviderSetupResult>();
        foreach (var config in settings.Providers)
        {
            var found = health.FirstOrDefault(h => string.Equals(h.Id, config.Id, StringComparison.OrdinalIgnoreCase));
            if (config.TurnedOff)
            {
                results.Add(new(config.Id, found?.Installed == true, found?.CanExecute == true, false, found?.Authentication ?? "Unknown", "Turned off in Settings; not checked or enabled.", []));
                continue;
            }

            if (found == null)
                continue;
            if (found.CanExecute && !found.StandaloneCliMissing)
            {
                config.Enabled = true;
                config.AllowProjectWrite = config.Id is not ("ollama" or "custom-cli" or "responses-api" or "clixad");
                if (config.Models.Count == 0)
                    config.Models.Add(new("", Enum.GetValues<WorkKind>(), DiscoverySource: "Installed agent; exact model ID not reported"));
            }

            results.Add(new(config.Id, found.Installed, found.CanExecute, config.Enabled, found.Authentication, found.Detail, []));
        }

        return results;
    }

    private async Task<HashSet<string>> ApplyProbeModelsAsync(Uri baseUri, string token, IReadOnlyList<CliToolAgent> installed, ManagerSettings settings, CancellationToken cancellationToken)
    {
        var probes = new Dictionary<string, CliToolProbe?>(StringComparer.OrdinalIgnoreCase);
        var gate = new SemaphoreSlim(6);
        try
        {
            var tasks = installed.Select(async agent =>
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(15));
                    var probe = await client.ProbeModelsAsync(baseUri, token, agent.Id, timeout.Token);
                    lock (probes)
                        probes[agent.Id] = probe;
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested && error is HttpRequestException or IOException or OperationCanceledException or InvalidOperationException or JsonException)
                {
                    lock (probes)
                        probes[agent.Id] = new(false, [], "", "Model probe failed: " + error.Message);
                }
                finally
                {
                    gate.Release();
                }
            }).ToArray();
            await Task.WhenAll(tasks);
        }
        finally
        {
            gate.Dispose();
        }

        var fromProbe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var agent in installed)
        {
            var config = settings.Providers.FirstOrDefault(p => string.Equals(p.Id, agent.Id, StringComparison.OrdinalIgnoreCase));
            if (config == null)
                continue;
            if (CliProvider.HasAccountAwareCatalog(agent.Id))
            {
                config.ModelDiscoveryStatus = "Model catalog is read from the CLI so only models this account can run are kept.";
                config.ModelCatalogCheckedAt = DateTimeOffset.UtcNow;
                continue;
            }

            probes.TryGetValue(agent.Id, out var probe);
            if (probe is { Ok: true, Models.Count: > 0 })
            {
                ModelCatalogMerge.ApplyEligible(config, probe.Models.Select(id => new ModelInfo(id, Enum.GetValues<WorkKind>(), DiscoverySource: "CLI Tool API probe; account access unverified")).ToArray());
                config.ModelDiscoveryStatus = $"{probe.Models.Count} models from the CLI Tool API probe.";
                fromProbe.Add(agent.Id);
                continue;
            }

            if (agent.Models is { Count: > 0 })
            {
                ModelCatalogMerge.ApplyEligible(config, agent.Models.Select(id => new ModelInfo(id, Enum.GetValues<WorkKind>(), DiscoverySource: "CLI Tool API agent list; account access unverified")).ToArray());
                config.ModelDiscoveryStatus = $"{agent.Models.Count} models from the CLI Tool API agent list.";
                fromProbe.Add(agent.Id);
                continue;
            }

            if (probe == null)
            {
                config.ModelDiscoveryStatus = "CLI Tool API build has no model probe endpoint; add an exact model ID manually or update aihub.";
                config.ModelCatalogCheckedAt = DateTimeOffset.UtcNow;
                continue;
            }

            if (!probe.Ok)
            {
                config.ModelDiscoveryStatus = string.IsNullOrWhiteSpace(probe.Error) ? "CLI Tool API model probe failed." : probe.Error;
                config.ModelCatalogCheckedAt = DateTimeOffset.UtcNow;
                continue;
            }

            config.ModelDiscoveryStatus = string.IsNullOrWhiteSpace(probe.Detail)
                ? "CLI Tool API returned no models. Configure a base URL for this agent or add an exact ID manually."
                : probe.Detail;
            config.ModelCatalogCheckedAt = DateTimeOffset.UtcNow;
        }

        return fromProbe;
    }

    // The CLI Tool probes HTTP base URLs; CLIs that print their own model list are asked directly,
    // only after the API has confirmed the agent is installed.
    private async Task ListLocalModelsAsync(IReadOnlyList<CliToolAgent> installed, HashSet<string> probed, ManagerSettings settings, IReadOnlyList<ProviderHealth> health, CancellationToken cancellationToken)
    {
        var ready = installed.Where(agent => (!probed.Contains(agent.Id) || CliProvider.HasAccountAwareCatalog(agent.Id)) && health.Any(h => h.Id.Equals(agent.Id, StringComparison.OrdinalIgnoreCase) && h.CanExecute)).ToArray();
        var gate = new SemaphoreSlim(4);
        try
        {
            var tasks = ready.Select(async agent =>
            {
                var config = settings.Providers.FirstOrDefault(p => string.Equals(p.Id, agent.Id, StringComparison.OrdinalIgnoreCase));
                var provider = providers.OfType<CliProvider>().FirstOrDefault(p => p.Id.Equals(agent.Id, StringComparison.OrdinalIgnoreCase));
                if (config == null || provider == null)
                    return;
                await gate.WaitAsync(cancellationToken);
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(12));
                    var models = await provider.ListModelsAsync(timeout.Token);
                    ModelCatalogMerge.ApplyEligible(config, models);
                    config.ModelDiscoveryStatus = provider.ModelDiscoveryDetail;
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested && error is IOException or OperationCanceledException or InvalidOperationException or JsonException or System.ComponentModel.Win32Exception)
                {
                    config.ModelDiscoveryStatus = "Model listing failed: " + error.Message;
                    config.ModelCatalogCheckedAt = DateTimeOffset.UtcNow;
                }
                finally
                {
                    gate.Release();
                }
            }).ToArray();
            await Task.WhenAll(tasks);
        }
        finally
        {
            gate.Dispose();
        }
    }

    private Task DetectSpecialProvidersAsync(ManagerSettings settings, List<ProviderHealth> health, CancellationToken cancellationToken) =>
        DetectProvidersAsync(SpecialProviderIds, settings, health, cancellationToken);

    private async Task DetectProvidersAsync(IReadOnlyList<string> ids, ManagerSettings settings, List<ProviderHealth> health, CancellationToken cancellationToken)
    {
        foreach (var id in ids)
        {
            var provider = providers.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            var config = settings.Providers.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (provider == null || config == null)
                continue;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                var result = await provider.DetectAsync(config, timeout.Token);
                health.Add(result);
                if (result.CanExecute)
                {
                    try
                    {
                        var models = await provider.ListModelsAsync(timeout.Token);
                        if (CliProvider.HasAccountAwareCatalog(id))
                            ModelCatalogMerge.ApplyEligible(config, models);
                        else
                            ModelCatalogMerge.Apply(config, models);
                        config.ModelDiscoveryStatus = (provider as IModelCatalogStatus)?.ModelDiscoveryDetail
                            ?? $"{models.Count} installed local models from runtime metadata.";
                    }
                    catch (Exception error) when (!cancellationToken.IsCancellationRequested)
                    {
                        config.ModelDiscoveryStatus = "Model listing failed: " + error.Message;
                        config.ModelCatalogCheckedAt = DateTimeOffset.UtcNow;
                    }
                }
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                health.Add(new(id, false, false, false, "Detection failed: " + error.Message));
            }
        }
    }
}
