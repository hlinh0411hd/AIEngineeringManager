using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Employee.Core;

namespace Employee.Infrastructure;

public sealed record WebRatingsUpdateResult(
    RatingSet RatingSet,
    string ActiveSummary,
    int MatchedAgentModels,
    int TotalAgentModels,
    IReadOnlyList<string> ConsultedSources,
    IReadOnlyList<string> Warnings);

// Fetches public HTTPS model pricing, capability, and benchmark catalogs (OpenRouter, LiteLLM,
// SWE-bench, LMArena, LiveBench), matches them against configured agent models using normalized slugs, and synthesizes
// a validated RatingSet ready for ModelRatings.Apply and model-ratings.json persistence.
public static partial class WebModelRatingsUpdater
{
    public const int MaxFeedBytes = 15_000_000;
    public static readonly TimeSpan PerSourceTimeout = TimeSpan.FromSeconds(15);
    private const string UserAgentValue = "AIEmployeeManager/1.0";

    private static readonly Dictionary<string, int> SourceByteLimits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["openrouter"] = 4_000_000,
        ["litellm"] = 10_000_000,
        ["model_prices"] = 10_000_000,
        ["swe-bench"] = 10_000_000,
        ["leaderboards"] = 10_000_000,
        ["lmarena"] = 2_000_000,
        ["datasets-server"] = 2_000_000,
        ["huggingface.co/datasets"] = 2_000_000,
        ["livebench"] = 2_000_000
    };

    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "openrouter.ai",
        "raw.githubusercontent.com",
        "datasets-server.huggingface.co",
        "huggingface.co",
        "livebench.ai"
    };

    public static IReadOnlyList<(string Name, string Url)> DefaultSources { get; } =
    [
        ("OpenRouter", "https://openrouter.ai/api/v1/models"),
        ("LiteLLM", "https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json"),
        ("SWE-bench", "https://raw.githubusercontent.com/swe-bench/swe-bench.github.io/master/data/leaderboards.json"),
        ("LMArena-text", "https://datasets-server.huggingface.co/rows?dataset=lmarena-ai%2Fleaderboard-dataset&config=text&split=latest&offset=0&length=100"),
        ("LMArena-webdev", "https://datasets-server.huggingface.co/rows?dataset=lmarena-ai%2Fleaderboard-dataset&config=webdev&split=latest&offset=0&length=100"),
        ("LMArena-vision", "https://datasets-server.huggingface.co/rows?dataset=lmarena-ai%2Fleaderboard-dataset&config=vision&split=latest&offset=0&length=100"),
        ("LMArena-agent", "https://datasets-server.huggingface.co/rows?dataset=lmarena-ai%2Fleaderboard-dataset&config=agent&split=latest&offset=0&length=100"),
        ("LMArena-text_to_image", "https://datasets-server.huggingface.co/rows?dataset=lmarena-ai%2Fleaderboard-dataset&config=text_to_image&split=latest&offset=0&length=100"),
        ("LiveBench-categories", "https://livebench.ai/data/20261106/categories.json"),
        ("LiveBench-table", "https://livebench.ai/data/20261106/table.csv")
    ];

    private static readonly HashSet<string> GatewayPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "opencode", "kilo", "cline", "openrouter", "azure", "azure_ai", "vertex_ai",
        "vertex_ai-language-models", "bedrock", "bedrock_converse", "together_ai",
        "fireworks_ai", "groq", "perplexity", "deepinfra", "hosted_vllm", "ollama", "ollama_chat"
    };

    private static readonly HashSet<string> VendorPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "openai", "anthropic", "google", "deepseek", "deepseek-ai", "meta-llama", "meta",
        "mistralai", "nvidia", "qwen", "zai-org", "z-ai", "x-ai", "moonshotai", "inclusionai",
        "thinkingmachines", "radixark", "recursive", "minimax", "cohere", "microsoft"
    };

    private static readonly HashSet<string> NonChatModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "embedding", "moderation", "rerank", "audio_transcription", "audio_speech", "realtime"
    };

    private static readonly HashSet<string> PrimaryLiteLlmProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "openai", "anthropic", "gemini", "vertex_ai", "deepseek", "mistral", "openrouter", "xai", "cohere"
    };

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._:/-]{1,80}$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidPatternRegex();

    [GeneratedRegex(@":(?<bsize>\d+(?:\.\d+)?)b(?:-[a-z0-9_]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex LocalParamTagRegex();

    [GeneratedRegex(@":(?:free|batch|online|nitro|exacto|extended)$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingColonTagRegex();

    [GeneratedRegex(@"-(?<effort>xhigh|high|medium|low)$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingEffortSlugRegex();

    [GeneratedRegex(@"\s*\((?<effort>xhigh|high|medium|low|max|thinking)\)$", RegexOptions.CultureInvariant)]
    private static partial Regex ParenthesizedEffortRegex();

    [GeneratedRegex(@"-(?:20\d{2}-\d{2}-\d{2}|20\d{6}|\d{4})$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingDateRegex();

    [GeneratedRegex(@"(?<=\d)[.-](?=\d)", RegexOptions.CultureInvariant)]
    private static partial Regex DigitSeparatorRegex();

    [GeneratedRegex(@"[^a-z0-9._:/-]+", RegexOptions.CultureInvariant)]
    private static partial Regex InvalidPatternCharsRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    public static async Task<WebRatingsUpdateResult> FetchAndBuildAsync(
        ManagerSettings settings,
        RatingSet? existing = null,
        IReadOnlyList<(string Name, string Url)>? sources = null,
        HttpMessageHandler? httpHandler = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var activeSources = sources is { Count: > 0 } ? sources : DefaultSources;

        var consulted = new List<string>();
        var warnings = new List<string>();
        var catalog = new WebCatalogIndex();

        using var client = CreateHttpClient(httpHandler);
        using var overallCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        overallCts.CancelAfter(TimeSpan.FromSeconds(35));
        var semaphore = new SemaphoreSlim(4);

        var fetchTasks = activeSources.Select(async src =>
        {
            await semaphore.WaitAsync(overallCts.Token);
            try
            {
                return await FetchSourceAsync(client, src.Name, src.Url, overallCts.Token);
            }
            finally
            {
                semaphore.Release();
            }
        }).ToArray();

        var fetched = await Task.WhenAll(fetchTasks);

        foreach (var item in fetched)
        {
            if (item.Warning != null)
                warnings.Add(item.Warning);
            if (item.Payload == null)
                continue;
            try
            {
                var parsed = ParseSource(item.Name, item.Url, item.Payload, catalog);
                if (parsed > 0)
                    consulted.Add(item.Name);
                else if (!item.Name.Contains("categories", StringComparison.OrdinalIgnoreCase))
                    warnings.Add($"{item.Name}: no model records found in response.");
            }
            catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
            {
                warnings.Add($"{item.Name}: failed to parse feed ({ex.Message}).");
            }
        }

        if (consulted.Count == 0)
        {
            var detail = warnings.Count > 0 ? " " + string.Join("; ", warnings) : "";
            throw new InvalidOperationException("No web ratings source could be fetched." + detail);
        }

        var agentModels = CollectAgentModels(settings);
        var builtInSet = ModelRatings.BuiltIn();
        var builtInEntries = builtInSet.Models;

        var mergedByPattern = new Dictionary<string, RatingEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in builtInEntries)
            AddIfValid(mergedByPattern, entry);
        if (existing?.Models != null)
            foreach (var entry in existing.Models)
                AddIfValid(mergedByPattern, entry);

        var matchedCount = 0;
        foreach (var agentModel in agentModels)
        {
            var norm = NormalizeModelId(agentModel.ModelId, agentModel.IsOllama, agentModel.IsFreeByDiscovery);
            var signal = catalog.Match(norm);
            if (signal == null)
                continue;

            matchedCount++;
            var pattern = BuildValidPattern(agentModel.ModelId, norm);
            if (pattern == null)
            {
                warnings.Add($"Skipped invalid model pattern for '{agentModel.ModelId}'.");
                continue;
            }

            var builtInPrior = FindBestPrior(agentModel.ModelId, norm, builtInEntries);
            var entry = ScoreModel(pattern, norm, signal, builtInPrior);
            AddIfValid(mergedByPattern, entry);
        }

        var priority = existing?.Priority ?? builtInSet.Priority;
        var sourceLabel = $"Web ({string.Join(", ", consulted)})";
        var now = DateTimeOffset.UtcNow;
        var ratingSet = new RatingSet(
            sourceLabel,
            now,
            [.. mergedByPattern.Values.OrderByDescending(e => e.Pattern.Length).ThenBy(e => e.Pattern, StringComparer.Ordinal)],
            priority);

        var summary = $"Ratings from {sourceLabel}, {now:yyyy-MM-dd} ({ratingSet.Models.Length} models) + built-in";
        return new WebRatingsUpdateResult(
            ratingSet,
            summary,
            matchedCount,
            agentModels.Count,
            consulted,
            warnings);
    }

    private static HttpClient CreateHttpClient(HttpMessageHandler? httpHandler)
    {
        if (httpHandler != null)
            return new HttpClient(httpHandler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };

        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static bool IsAllowedRedirect(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps)
            return false;

        var host = uri.Host.ToLowerInvariant();
        if (host == "localhost" || host == "127.0.0.1" || host == "::1" || host.StartsWith("169.254.", StringComparison.Ordinal))
            return false;

        if (IsPrivateIp(host))
            return false;

        if (!AllowedHosts.Contains(host))
            return false;

        return true;
    }

    private static bool IsPrivateIp(string host)
    {
        if (IPAddress.TryParse(host, out var ip))
        {
            var bytes = ip.GetAddressBytes();
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                if (bytes[0] == 10) return true;
                if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
                if (bytes[0] == 192 && bytes[1] == 168) return true;
                if (bytes[0] == 127) return true;
                if (bytes[0] == 169 && bytes[1] == 254) return true;
            }
            else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (IPAddress.IsLoopback(ip)) return true;
                if (bytes.Length >= 2 && bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80) return true; // fe80::/10 link-local
                if (bytes.Length >= 2 && bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0xC0) return true; // fec0::/10 site-local (deprecated but still used)
            }
        }
        return false;
    }

    private static bool IsLoopbackHost(string host)
    {
        var lower = host.ToLowerInvariant();
        if (lower == "localhost" || lower == "127.0.0.1" || lower == "::1")
            return true;
        if (IPAddress.TryParse(host, out var ip))
        {
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var bytes = ip.GetAddressBytes();
                if (bytes[0] == 127)
                    return true;
            }
            else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (IPAddress.IsLoopback(ip))
                    return true;
            }
        }
        return false;
    }

    private sealed record FetchedSource(string Name, string Url, byte[]? Payload, string? Warning);

    private static async Task<FetchedSource> FetchSourceAsync(HttpClient client, string name, string url, CancellationToken token)
    {
        var safeUrl = (url ?? "").Trim();
        var label = string.IsNullOrWhiteSpace(name) ? safeUrl : name.Trim();
        if (!Uri.TryCreate(safeUrl, UriKind.Absolute, out var uri))
            return new(label, safeUrl, null, $"{label}: invalid URL.");

        var isLoopback = IsLoopbackHost(uri.Host);
        if (!isLoopback && uri.Scheme != Uri.UriSchemeHttps)
            return new(label, safeUrl, null, $"{label}: HTTPS required for non-loopback hosts.");

        var host = uri.Host.ToLowerInvariant();
        if (!isLoopback && !AllowedHosts.Contains(host))
            return new(label, safeUrl, null, $"{label}: host '{host}' not in allowlist.");

        using var sourceCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        sourceCts.CancelAfter(PerSourceTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgentValue);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, sourceCts.Token);

            var finalUri = response.RequestMessage?.RequestUri ?? uri;
            if (!isLoopback && !IsAllowedRedirect(finalUri))
                return new(label, safeUrl, null, $"{label}: redirect to disallowed host.");

            if (!response.IsSuccessStatusCode)
                return new(label, safeUrl, null, $"{label}: HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");

            var limit = GetByteLimitForUrl(safeUrl);
            if (response.Content.Headers.ContentLength is { } contentLength && contentLength > limit)
                return new(label, safeUrl, null, $"{label}: response exceeds {limit / 1_000_000} MB limit.");

            await using var stream = await response.Content.ReadAsStreamAsync(sourceCts.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[32768];
            int read;
            while ((read = await stream.ReadAsync(chunk, sourceCts.Token)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > limit)
                    return new(label, safeUrl, null, $"{label}: response exceeded {limit / 1_000_000} MB limit.");
            }

            return new(label, safeUrl, buffer.ToArray(), null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new(label, safeUrl, null, $"{label}: timed out after {(int)PerSourceTimeout.TotalSeconds}s.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return new(label, safeUrl, null, $"{label}: {ex.Message}");
        }
    }

    private static int GetByteLimitForUrl(string url)
    {
        var lower = url.ToLowerInvariant();
        foreach (var kvp in SourceByteLimits)
        {
            if (lower.Contains(kvp.Key, StringComparison.Ordinal))
                return kvp.Value;
        }
        return MaxFeedBytes;
    }

    private static int ParseSource(string name, string url, byte[] payload, WebCatalogIndex catalog)
    {
        var hint = $"{name} {url}".ToLowerInvariant();
        if (hint.Contains("openrouter", StringComparison.Ordinal))
            return ParseOpenRouter(payload, catalog);
        if (hint.Contains("swe-bench", StringComparison.Ordinal) || hint.Contains("swebench", StringComparison.Ordinal) || hint.Contains("leaderboards.json", StringComparison.Ordinal))
            return ParseSweBench(payload, catalog);
        if (hint.Contains("litellm", StringComparison.Ordinal) || hint.Contains("model_prices", StringComparison.Ordinal))
            return ParseLiteLlm(payload, catalog);
        if (hint.Contains("lmarena", StringComparison.Ordinal) || hint.Contains("huggingface.co/datasets", StringComparison.Ordinal) || hint.Contains("datasets-server", StringComparison.Ordinal))
            return ParseLmArena(payload, catalog, name);
        if (hint.Contains("livebench", StringComparison.Ordinal) || url.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) || url.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return ParseLiveBench(payload, catalog, name, url);

        var reader = new Utf8JsonReader(payload, new JsonReaderOptions { MaxDepth = 16, CommentHandling = JsonCommentHandling.Skip });
        if (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
        {
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    var prop = reader.GetString();
                    reader.Read();
                    if (prop == "data" && reader.TokenType == JsonTokenType.StartArray)
                        return ParseOpenRouter(payload, catalog);
                    if (prop == "leaderboards" && reader.TokenType == JsonTokenType.StartArray)
                        return ParseSweBench(payload, catalog);
                    if (prop == "rows" && reader.TokenType == JsonTokenType.StartArray)
                        return ParseLmArena(payload, catalog, name);
                    reader.Skip();
                }
            }
        }

        return ParseLiteLlm(payload, catalog);
    }

    private static int ParseOpenRouter(byte[] payload, WebCatalogIndex catalog)
    {
        using var doc = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 16, CommentHandling = JsonCommentHandling.Skip });
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return 0;

        var count = 0;
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;
            var id = GetString(item, "id");
            if (string.IsNullOrWhiteSpace(id))
                continue;

            var norm = NormalizeModelId(id, isOllama: false, isFreeByDiscovery: false);
            var sig = new WebModelSignal { HasOpenRouter = true, IsBatchPrice = norm.IsBatch };

            if (TryGetInt(item, "context_length", out var ctx) && ctx > 0)
                sig.ContextLength = ctx;

            if (item.TryGetProperty("architecture", out var arch) && arch.ValueKind == JsonValueKind.Object)
            {
                if (arch.TryGetProperty("input_modalities", out var inMods) && inMods.ValueKind == JsonValueKind.Array)
                    sig.SupportsVision = ArrayContainsIgnoreCase(inMods, "image");
                if (arch.TryGetProperty("output_modalities", out var outMods) && outMods.ValueKind == JsonValueKind.Array)
                    sig.SupportsImageGeneration = ArrayContainsIgnoreCase(outMods, "image");

                if ((!sig.SupportsVision.HasValue || !sig.SupportsImageGeneration.HasValue) && GetString(arch, "modality") is { Length: > 0 } mod)
                {
                    var parts = mod.Split("->", StringSplitOptions.TrimEntries);
                    if (parts.Length == 2)
                    {
                        sig.SupportsVision ??= parts[0].Contains("image", StringComparison.OrdinalIgnoreCase);
                        sig.SupportsImageGeneration ??= parts[1].Contains("image", StringComparison.OrdinalIgnoreCase);
                    }
                }
            }

            if (item.TryGetProperty("pricing", out var pricing) && pricing.ValueKind == JsonValueKind.Object)
            {
                var hasPrompt = TryGetDouble(pricing, "prompt", out var prompt) && prompt >= 0;
                var hasCompletion = TryGetDouble(pricing, "completion", out var completion) && completion >= 0;
                if (hasPrompt)
                    sig.InputCostPerToken = prompt;
                if (hasCompletion)
                    sig.OutputCostPerToken = completion;
                if (TryGetDouble(pricing, "internal_reasoning", out var reasoningCost) && reasoningCost >= 0)
                    sig.ReasoningCostPerToken = reasoningCost;
                if (TryGetDouble(pricing, "request", out var reqCost) && reqCost >= 0)
                    sig.RequestCost = reqCost;
                if (hasPrompt && hasCompletion && prompt == 0 && completion == 0 && (sig.RequestCost ?? 0) == 0)
                    sig.IsFreePrice = true;
            }

            if (item.TryGetProperty("supported_parameters", out var sp) && sp.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in sp.EnumerateArray())
                {
                    if (p.ValueKind != JsonValueKind.String)
                        continue;
                    var val = p.GetString();
                    if (val is "reasoning" or "reasoning_effort" or "include_reasoning")
                        sig.SupportsReasoning = true;
                    else if (val is "tools" or "tool_choice" or "structured_outputs" or "response_format")
                        sig.SupportsStructuredOrTools = true;
                }
            }

            if (item.TryGetProperty("reasoning", out var reasoning) && reasoning.ValueKind == JsonValueKind.Object)
            {
                if (GetBool(reasoning, "mandatory") == true || GetBool(reasoning, "default_enabled") == true)
                    sig.SupportsReasoning = true;
                if (reasoning.TryGetProperty("supported_efforts", out var efforts) && efforts.ValueKind == JsonValueKind.Array && efforts.GetArrayLength() > 0)
                    sig.SupportsReasoning = true;
            }

            if (item.TryGetProperty("benchmarks", out var benchmarks) && benchmarks.ValueKind == JsonValueKind.Object)
            {
                if (benchmarks.TryGetProperty("artificial_analysis", out var aa) && aa.ValueKind == JsonValueKind.Object)
                {
                    if (TryGetDouble(aa, "intelligence_index", out var ii) && ii >= 0)
                        sig.AaIntelligenceIndex = ii;
                    if (TryGetDouble(aa, "coding_index", out var ci) && ci >= 0)
                        sig.AaCodingIndex = ci;
                    if (TryGetDouble(aa, "engineering_index", out var ei) && ei >= 0)
                        sig.AaEngineeringIndex = ei;
                    if (TryGetDouble(aa, "multilingual_index", out var mi) && mi >= 0)
                        sig.AaMultilingualIndex = mi;
                }

                if (benchmarks.TryGetProperty("design_arena", out var da) && da.ValueKind == JsonValueKind.Array)
                {
                    foreach (var row in da.EnumerateArray())
                    {
                        if (row.ValueKind != JsonValueKind.Object)
                            continue;
                        var category = GetString(row, "category")?.ToLowerInvariant();
                        if (!TryGetDouble(row, "elo", out var elo) || elo <= 0)
                            continue;
                        switch (category)
                        {
                            case "codecategories":
                                sig.DesignArenaCodeElo = Max(sig.DesignArenaCodeElo, elo);
                                break;
                            case "uicomponent":
                            case "website":
                                sig.DesignArenaUiElo = Max(sig.DesignArenaUiElo, elo);
                                break;
                            case "gamedev":
                                sig.DesignArenaGameDevElo = Max(sig.DesignArenaGameDevElo, elo);
                                break;
                            case "3d":
                                sig.DesignArena3dElo = Max(sig.DesignArena3dElo, elo);
                                break;
                        }
                    }
                }
            }

            if (GetString(item, "expiration_date") is { Length: > 0 } expDate)
                sig.DeprecationNotice = $"Deprecated {expDate}";

            if (item.TryGetProperty("endpoints", out var endpoints) && endpoints.ValueKind == JsonValueKind.Array)
            {
                var allBatch = true;
                var hasEndpoints = false;
                foreach (var ep in endpoints.EnumerateArray())
                {
                    if (ep.ValueKind == JsonValueKind.String)
                    {
                        hasEndpoints = true;
                        var epStr = ep.GetString();
                        if (epStr != null && !epStr.Contains("batches", StringComparison.OrdinalIgnoreCase))
                        {
                            allBatch = false;
                            break;
                        }
                    }
                }
                if (hasEndpoints && allBatch)
                {
                    sig.IsBatchPrice = true;
                }
            }

            catalog.Add(id, norm, sig, preferPricingForCanonical: !norm.IsFree && !norm.IsBatch && !sig.IsBatchPrice);
            if (GetString(item, "canonical_slug") is { Length: > 0 } slug)
            {
                var slugNorm = NormalizeModelId(slug, isOllama: false, isFreeByDiscovery: false);
                catalog.Add(slug, slugNorm, sig, preferPricingForCanonical: !norm.IsFree && !norm.IsBatch);
            }

            count++;
        }

        return count;
    }

    private static int ParseLiteLlm(byte[] payload, WebCatalogIndex catalog)
    {
        using var doc = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 16, CommentHandling = JsonCommentHandling.Skip });
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return 0;

        var count = 0;
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.NameEquals("sample_spec") || prop.Value.ValueKind != JsonValueKind.Object)
                continue;

            var obj = prop.Value;
            var mode = GetString(obj, "mode");
            if (mode != null && NonChatModes.Contains(mode))
                continue;

            var provider = GetString(obj, "litellm_provider") ?? "";
            var isOllama = provider.StartsWith("ollama", StringComparison.OrdinalIgnoreCase);
            var norm = NormalizeModelId(prop.Name, isOllama, isFreeByDiscovery: false);
            var sig = new WebModelSignal
            {
                HasLiteLlm = true,
                IsOllama = isOllama,
                IsBatchPrice = norm.IsBatch
            };

            if (TryGetDouble(obj, "input_cost_per_token", out var inCost) && inCost >= 0)
                sig.InputCostPerToken = inCost;
            if (TryGetDouble(obj, "output_cost_per_token", out var outCost) && outCost >= 0)
                sig.OutputCostPerToken = outCost;
            if (TryGetDouble(obj, "output_cost_per_reasoning_token", out var rCost) && rCost >= 0)
                sig.ReasoningCostPerToken = rCost;
            if (sig.InputCostPerToken == 0 && sig.OutputCostPerToken == 0)
                sig.IsFreePrice = true;

            if (TryGetInt(obj, "max_input_tokens", out var maxIn) && maxIn > 0)
                sig.ContextLength = maxIn;
            else if (TryGetInt(obj, "max_tokens", out var maxTok) && maxTok > 0)
                sig.ContextLength = maxTok;

            if (GetBool(obj, "supports_vision") is { } vis)
                sig.SupportsVision = vis;
            if (obj.TryGetProperty("supported_modalities", out var inMods) && inMods.ValueKind == JsonValueKind.Array && ArrayContainsIgnoreCase(inMods, "image"))
                sig.SupportsVision = true;

            if (string.Equals(mode, "image_generation", StringComparison.OrdinalIgnoreCase))
                sig.SupportsImageGeneration = true;
            else if (obj.TryGetProperty("supported_output_modalities", out var outMods) && outMods.ValueKind == JsonValueKind.Array)
                sig.SupportsImageGeneration = ArrayContainsIgnoreCase(outMods, "image");

            if (GetBool(obj, "supports_reasoning") is { } rsn)
                sig.SupportsReasoning = rsn;
            if (GetBool(obj, "supports_function_calling") == true || GetBool(obj, "supports_tool_choice") == true || GetBool(obj, "supports_response_schema") == true)
                sig.SupportsStructuredOrTools = true;

            var deprecationDate = GetString(obj, "deprecation_date");
            var successorModel = GetString(obj, "successor_model");
            if (!string.IsNullOrWhiteSpace(deprecationDate))
            {
                if (!string.IsNullOrWhiteSpace(successorModel))
                    sig.DeprecationNotice = $"Deprecated {deprecationDate} -> {successorModel}";
                else
                    sig.DeprecationNotice = $"Deprecated {deprecationDate}";
            }

            var preferPricing = PrimaryLiteLlmProviders.Contains(provider) && !norm.IsFree && !norm.IsBatch;
            catalog.Add(prop.Name, norm, sig, preferPricingForCanonical: preferPricing);
            count++;
        }

        return count;
    }

    private static int ParseSweBench(byte[] payload, WebCatalogIndex catalog)
    {
        var reader = new Utf8JsonReader(payload, new JsonReaderOptions { MaxDepth = 16, CommentHandling = JsonCommentHandling.Skip });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            return 0;

        var count = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;
            var rootProp = reader.GetString();
            reader.Read();
            if (rootProp != "leaderboards" || reader.TokenType != JsonTokenType.StartArray)
            {
                reader.Skip();
                continue;
            }

            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    reader.Skip();
                    continue;
                }

                string boardName = "";
                var boardResults = new List<SweBenchRow>();

                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.TokenType != JsonTokenType.PropertyName)
                        continue;
                    var prop = reader.GetString();
                    reader.Read();
                    if (prop == "name" && reader.TokenType == JsonTokenType.String)
                        boardName = reader.GetString() ?? "";
                    else if (prop == "results" && reader.TokenType == JsonTokenType.StartArray)
                    {
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                        {
                            if (reader.TokenType != JsonTokenType.StartObject)
                            {
                                reader.Skip();
                                continue;
                            }

                            var row = ReadSweBenchRow(ref reader);
                            if (row != null)
                                boardResults.Add(row);
                        }
                    }
                    else
                        reader.Skip();
                }

                foreach (var row in boardResults)
                {
                    if (row.Resolved is < 0 or > 100 || !string.IsNullOrWhiteSpace(row.Warning))
                        continue;

                    var modelRaw = ExtractSweBenchModel(row);
                    if (string.IsNullOrWhiteSpace(modelRaw))
                        continue;

                    var norm = NormalizeModelId(modelRaw, isOllama: false, isFreeByDiscovery: false);
                    if (string.IsNullOrWhiteSpace(norm.Effort) && !string.IsNullOrWhiteSpace(row.ReasoningEffort))
                    {
                        var eff = NormalizeEffort(row.ReasoningEffort);
                        if (eff != null)
                            norm = norm with { Effort = eff };
                    }

                    var sig = new WebModelSignal { HasSweBench = true };
                    var lowerBoard = boardName.ToLowerInvariant();
                    if (lowerBoard.Contains("verified", StringComparison.Ordinal))
                        sig.SweBenchVerified = row.Resolved;
                    else if (lowerBoard.Contains("multilingual", StringComparison.Ordinal))
                    {
                        sig.SweBenchMultilingual = row.Resolved;
                        sig.SweBenchOther = row.Resolved;
                    }
                    else if (lowerBoard.Contains("multimodal", StringComparison.Ordinal))
                    {
                        sig.SweBenchMultimodal = true;
                        sig.SweBenchOther = row.Resolved;
                    }
                    else
                        sig.SweBenchOther = row.Resolved;

                    catalog.Add(modelRaw, norm, sig, preferPricingForCanonical: false);
                    count++;
                }
            }
        }

        return count;
    }

    private sealed class SweBenchRow
    {
        public double? Resolved;
        public List<string> Tags = [];
        public string? ModelDisplay;
        public string? Name;
        public string? Folder;
        public string? ReasoningEffort;
        public string? Warning;
    }

    private static SweBenchRow? ReadSweBenchRow(ref Utf8JsonReader reader)
    {
        var row = new SweBenchRow();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;
            var prop = reader.GetString();
            reader.Read();
            switch (prop)
            {
                case "per_instance_details":
                    reader.Skip();
                    break;
                case "resolved":
                    if (reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out var r))
                        row.Resolved = r;
                    else if (reader.TokenType == JsonTokenType.String && double.TryParse(reader.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var rs))
                        row.Resolved = rs;
                    else
                        reader.Skip();
                    break;
                case "tags":
                    if (reader.TokenType == JsonTokenType.StartArray)
                    {
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                            if (reader.TokenType == JsonTokenType.String && reader.GetString() is { } t)
                                row.Tags.Add(t);
                    }
                    else
                        reader.Skip();
                    break;
                case "model_display":
                    row.ModelDisplay = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                    if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                        reader.Skip();
                    break;
                case "name":
                    row.Name = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                    if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                        reader.Skip();
                    break;
                case "folder":
                    row.Folder = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                    if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                        reader.Skip();
                    break;
                case "reasoning_effort":
                    row.ReasoningEffort = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                    if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                        reader.Skip();
                    break;
                case "warning":
                    row.Warning = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                    if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                        reader.Skip();
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        return row.Resolved.HasValue ? row : null;
    }

    private static string? ExtractSweBenchModel(SweBenchRow row)
    {
        var modelTags = row.Tags
            .Where(t => t.StartsWith("Model:", StringComparison.OrdinalIgnoreCase))
            .Select(t => t[6..].Trim())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (modelTags.Count > 1)
            return null;
        if (modelTags.Count == 1)
            return modelTags[0];
        if (!string.IsNullOrWhiteSpace(row.ModelDisplay))
            return row.ModelDisplay;
        if (!string.IsNullOrWhiteSpace(row.Name))
            return row.Name;
        return row.Folder;
    }

    private static int ParseLmArena(byte[] payload, WebCatalogIndex catalog, string sourceName)
    {
        var config = DetectLmArenaConfig(sourceName);
        using var doc = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 16, CommentHandling = JsonCommentHandling.Skip });
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
            return 0;

        var count = 0;
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("row", out var rowObj) || rowObj.ValueKind != JsonValueKind.Object)
                continue;

            var modelName = GetString(rowObj, "model_name");
            if (string.IsNullOrWhiteSpace(modelName))
                continue;

            var category = GetString(rowObj, "category");
            if (!string.Equals(category, "overall", StringComparison.OrdinalIgnoreCase))
                continue;

            if (rowObj.TryGetProperty("vote_count", out var vc) && vc.ValueKind == JsonValueKind.Number && vc.TryGetDouble(out var voteCount) && voteCount < 100)
                continue;
            if (rowObj.TryGetProperty("session_count", out var sc) && sc.ValueKind == JsonValueKind.Number && sc.TryGetDouble(out var sessionCount) && sessionCount < 100)
                continue;

            var norm = NormalizeModelId(modelName, isOllama: false, isFreeByDiscovery: false);
            var sig = new WebModelSignal { HasLmArena = true };

            if (rowObj.TryGetProperty("rating", out var rating) && rating.ValueKind == JsonValueKind.Number && rating.TryGetDouble(out var elo))
            {
                switch (config)
                {
                    case LmArenaConfig.Text:
                        sig.LmArenaTextElo = Max(sig.LmArenaTextElo ?? 0, elo);
                        break;
                    case LmArenaConfig.Webdev:
                        sig.LmArenaWebdevElo = Max(sig.LmArenaWebdevElo ?? 0, elo);
                        break;
                    case LmArenaConfig.Vision:
                        sig.LmArenaVisionElo = Max(sig.LmArenaVisionElo ?? 0, elo);
                        break;
                    case LmArenaConfig.TextToImage:
                        sig.LmArenaTextToImageElo = Max(sig.LmArenaTextToImageElo ?? 0, elo);
                        break;
                }
            }
            if (rowObj.TryGetProperty("score", out var score) && score.ValueKind == JsonValueKind.Number && score.TryGetDouble(out var agentScore))
            {
                if (config == LmArenaConfig.Agent)
                    sig.LmArenaAgentScore = Max(sig.LmArenaAgentScore ?? 0, agentScore);
            }

            catalog.Add(modelName, norm, sig, preferPricingForCanonical: false);
            count++;
        }

        return count;
    }

    private enum LmArenaConfig
    {
        Text,
        Webdev,
        Vision,
        Agent,
        TextToImage,
        Unknown
    }

    private static LmArenaConfig DetectLmArenaConfig(string sourceName)
    {
        var lower = sourceName.ToLowerInvariant();
        if (lower.Contains("webdev")) return LmArenaConfig.Webdev;
        if (lower.Contains("vision")) return LmArenaConfig.Vision;
        if (lower.Contains("agent")) return LmArenaConfig.Agent;
        if (lower.Contains("text_to_image") || lower.Contains("text-to-image")) return LmArenaConfig.TextToImage;
        if (lower.Contains("text")) return LmArenaConfig.Text;
        return LmArenaConfig.Unknown;
    }

    private static readonly Dictionary<string, List<string>> LiveBenchCategoryColumns = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object LiveBenchCategoryLock = new();

    private static int ParseLiveBench(byte[] payload, WebCatalogIndex catalog, string sourceName, string url)
    {
        var isCategories = sourceName.Contains("categories", StringComparison.OrdinalIgnoreCase) || url.EndsWith("categories.json", StringComparison.OrdinalIgnoreCase);
        var isTable = sourceName.Contains("table", StringComparison.OrdinalIgnoreCase) || url.EndsWith("table.csv", StringComparison.OrdinalIgnoreCase);

        if (isCategories)
        {
            ParseLiveBenchCategories(payload);
            return 0;
        }

        if (isTable)
        {
            return ParseLiveBenchTable(payload, catalog);
        }

        var text = Encoding.UTF8.GetString(payload);
        if (text.TrimStart().StartsWith("{"))
        {
            ParseLiveBenchCategories(payload);
            return 0;
        }
        return ParseLiveBenchTable(payload, catalog);
    }

    private static void ParseLiveBenchCategories(byte[] payload)
    {
        using var doc = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 16, CommentHandling = JsonCommentHandling.Skip });
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return;

        var newMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var category = prop.Name;
            if (prop.Value.ValueKind == JsonValueKind.Array)
            {
                var columns = new List<string>();
                foreach (var item in prop.Value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } col)
                        columns.Add(col);
                }
                if (columns.Count > 0)
                    newMap[category] = columns;
            }
        }

        lock (LiveBenchCategoryLock)
        {
            LiveBenchCategoryColumns.Clear();
            foreach (var kvp in newMap)
                LiveBenchCategoryColumns[kvp.Key] = kvp.Value;
        }
    }

    private static int ParseLiveBenchTable(byte[] payload, WebCatalogIndex catalog)
    {
        var csvText = Encoding.UTF8.GetString(payload);
        using var reader = new StringReader(csvText);
        var header = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(header))
            return 0;

        var headers = header.Split(',', StringSplitOptions.None);
        var modelCol = Array.FindIndex(headers, h => h.Trim().Equals("model", StringComparison.OrdinalIgnoreCase));
        if (modelCol < 0)
            return 0;

        Dictionary<string, List<string>> categoryCols;
        lock (LiveBenchCategoryLock)
        {
            categoryCols = LiveBenchCategoryColumns.Count > 0
                ? new Dictionary<string, List<string>>(LiveBenchCategoryColumns, StringComparer.OrdinalIgnoreCase)
                : BuildHeuristicCategoryMap(headers);
        }

        var colIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < headers.Length; i++)
            colIndex[headers[i].Trim()] = i;

        var count = 0;
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var cols = line.Split(',', StringSplitOptions.None);
            if (cols.Length <= modelCol)
                continue;

            var modelName = cols[modelCol].Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(modelName))
                continue;

            var norm = NormalizeModelId(modelName, isOllama: false, isFreeByDiscovery: false);
            var sig = new WebModelSignal { HasLiveBench = true };

            sig.LiveBenchCoding = ComputeCategoryScore(cols, colIndex, categoryCols, ["Coding"]);
            sig.LiveBenchAgentic = ComputeCategoryScore(cols, colIndex, categoryCols, ["Long Agentic", "Agentic Coding"]);
            sig.LiveBenchReasoning = ComputeCategoryScore(cols, colIndex, categoryCols, ["Reasoning"]);
            sig.LiveBenchMath = ComputeCategoryScore(cols, colIndex, categoryCols, ["Mathematics"]);
            sig.LiveBenchDataAnalysis = ComputeCategoryScore(cols, colIndex, categoryCols, ["Data Analysis"]);
            sig.LiveBenchLanguage = ComputeCategoryScore(cols, colIndex, categoryCols, ["Language"]);
            sig.LiveBenchIf = ComputeCategoryScore(cols, colIndex, categoryCols, ["IF"]);
            sig.LiveBenchDesign = ComputeCategoryScore(cols, colIndex, categoryCols, ["Design"]);

            var researchScore = ComputeCategoryScore(cols, colIndex, categoryCols, ["Research"]);
            if (!double.IsNaN(researchScore))
                sig.LiveBenchReasoning = researchScore;

            catalog.Add(modelName, norm, sig, preferPricingForCanonical: false);
            count++;
        }

        return count;
    }

    private static Dictionary<string, List<string>> BuildHeuristicCategoryMap(string[] headers)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in headers)
        {
            var trimmed = h.Trim();
            if (trimmed.Contains("Coding", StringComparison.OrdinalIgnoreCase))
                map["Coding"] = [trimmed];
            else if (trimmed.Contains("Agentic", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("Long Agentic", StringComparison.OrdinalIgnoreCase))
                map["Long Agentic"] = [trimmed];
            else if (trimmed.Contains("Reasoning", StringComparison.OrdinalIgnoreCase))
                map["Reasoning"] = [trimmed];
            else if (trimmed.Contains("Mathematics", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("Math", StringComparison.OrdinalIgnoreCase))
                map["Mathematics"] = [trimmed];
            else if (trimmed.Contains("Data Analysis", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("DataAnalysis", StringComparison.OrdinalIgnoreCase))
                map["Data Analysis"] = [trimmed];
            else if (trimmed.Contains("Language", StringComparison.OrdinalIgnoreCase))
                map["Language"] = [trimmed];
            else if (trimmed.Equals("IF", StringComparison.OrdinalIgnoreCase))
                map["IF"] = [trimmed];
            else if (trimmed.Contains("Design", StringComparison.OrdinalIgnoreCase))
                map["Design"] = [trimmed];
            else if (trimmed.Contains("Research", StringComparison.OrdinalIgnoreCase))
                map["Research"] = [trimmed];
        }
        return map;
    }

    private static double ComputeCategoryScore(string[] cols, Dictionary<string, int> colIndex, Dictionary<string, List<string>> categoryCols, string[] categoryNames)
    {
        var values = new List<double>();
        foreach (var cat in categoryNames)
        {
            if (categoryCols.TryGetValue(cat, out var subtaskCols))
            {
                foreach (var subCol in subtaskCols)
                {
                    if (colIndex.TryGetValue(subCol, out var idx) && idx < cols.Length)
                    {
                        var val = cols[idx].Trim();
                        if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d >= 0 && d <= 100)
                            values.Add(d);
                    }
                }
            }
        }
        return values.Count > 0 ? values.Average() : double.NaN;
    }

    private sealed record AgentModelTarget(string ModelId, bool IsOllama, bool IsFreeByDiscovery);

    private static List<AgentModelTarget> CollectAgentModels(ManagerSettings settings)
    {
        var map = new Dictionary<string, AgentModelTarget>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in settings.Providers)
        {
            var isOllama = string.Equals(provider.Id, "ollama", StringComparison.OrdinalIgnoreCase);
            foreach (var model in provider.Models)
            {
                if (string.IsNullOrWhiteSpace(model.Id))
                    continue;
                var id = model.Id.Trim();
                var freeDiscovery = !string.IsNullOrWhiteSpace(model.DiscoverySource) &&
                                    model.DiscoverySource.Contains("free", StringComparison.OrdinalIgnoreCase);
                if (map.TryGetValue(id, out var existing))
                {
                    map[id] = existing with
                    {
                        IsOllama = existing.IsOllama || isOllama,
                        IsFreeByDiscovery = existing.IsFreeByDiscovery || freeDiscovery
                    };
                }
                else
                    map[id] = new(id, isOllama, freeDiscovery);
            }
        }

        return [.. map.Values];
    }

    internal sealed record NormalizedModel(
        string RawLower,
        string PostPrefixPreserveVariant,
        string CanonicalKey,
        string Effort,
        bool IsFree,
        bool IsBatch,
        bool IsLocal,
        bool IsLatest,
        double? LocalParamB);

    internal static NormalizedModel NormalizeModelId(string id, bool isOllama, bool isFreeByDiscovery)
    {
        var raw = id.Trim().ToLowerInvariant();
        var isFree = isFreeByDiscovery ||
                     raw.EndsWith(":free", StringComparison.Ordinal) ||
                     raw.EndsWith("-free", StringComparison.Ordinal) ||
                     raw.EndsWith("/free", StringComparison.Ordinal) ||
                     raw == "free";
        var isBatch = raw.EndsWith(":batch", StringComparison.Ordinal);

        double? localParamB = null;
        var paramMatch = LocalParamTagRegex().Match(raw);
        if (paramMatch.Success && double.TryParse(paramMatch.Groups["bsize"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var bVal))
            localParamB = bVal;
        var isLocal = isOllama || localParamB.HasValue;

        var postPrefixPreserve = StripProviderPrefixes(raw);

        var work = postPrefixPreserve;
        string effort = "";

        var parenEffort = ParenthesizedEffortRegex().Match(work);
        if (parenEffort.Success)
        {
            effort = NormalizeEffort(parenEffort.Groups["effort"].Value) ?? "";
            work = work[..parenEffort.Index].Trim();
        }

        if (paramMatch.Success)
            work = LocalParamTagRegex().Replace(work, "");
        work = TrailingColonTagRegex().Replace(work, "");
        if (work.EndsWith("-free", StringComparison.Ordinal) && work.Length > 5)
            work = work[..^5];

        work = WhitespaceRegex().Replace(work.Replace('_', '-'), "-").Trim('-');

        if (string.IsNullOrEmpty(effort))
        {
            var slugEffort = TrailingEffortSlugRegex().Match(work);
            if (slugEffort.Success)
            {
                effort = NormalizeEffort(slugEffort.Groups["effort"].Value) ?? "";
                work = work[..slugEffort.Index];
            }
        }

        var isLatest = false;
        if (work.EndsWith("-latest", StringComparison.Ordinal) && work.Length > 7)
        {
            isLatest = true;
            work = work[..^7];
        }

        if (work.EndsWith("-decisions", StringComparison.Ordinal) && work.Length > 10)
            work = work[..^10];
        if (work.EndsWith("-thinking", StringComparison.Ordinal) && work.Length > 9)
            work = work[..^9];

        work = TrailingDateRegex().Replace(work, "");
        if (work.EndsWith("-instruct", StringComparison.Ordinal) && work.Length > 9)
            work = work[..^9];
        else if (work.EndsWith("-it", StringComparison.Ordinal) && work.Length > 3)
            work = work[..^3];

        var canonical = DigitSeparatorRegex().Replace(work, ".");
        return new NormalizedModel(raw, postPrefixPreserve, canonical, effort, isFree, isBatch, isLocal, isLatest, localParamB);
    }

    private static string StripProviderPrefixes(string raw)
    {
        var segments = raw.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length <= 1)
            return raw;

        var start = 0;
        var minKeep = segments[^1] is "free" or "auto" ? 2 : 1;
        while (segments.Length - start > minKeep)
        {
            var seg = segments[start];
            var clean = seg.TrimStart('~');
            if (GatewayPrefixes.Contains(clean) || VendorPrefixes.Contains(clean) || seg.StartsWith('~'))
                start++;
            else
                break;
        }

        return string.Join('/', segments[start..]);
    }

    private static string? NormalizeEffort(string? raw) => (raw ?? "").Trim().ToLowerInvariant() switch
    {
        "low" => "low",
        "medium" or "med" => "medium",
        "high" => "high",
        "xhigh" or "max" => "xhigh",
        _ => null
    };

    private static string? BuildValidPattern(string originalModelId, NormalizedModel norm)
    {
        var rawLower = originalModelId.Trim().ToLowerInvariant();
        var candidates = new List<string> { norm.PostPrefixPreserveVariant, rawLower };

        foreach (var candidate in candidates)
        {
            var trimmed = candidate.Trim();
            if (ValidPatternRegex().IsMatch(trimmed) && MatchesPattern(rawLower, trimmed))
                return trimmed;

            var shrunk = trimmed;
            while (shrunk.Length > 81)
            {
                var sep = shrunk.IndexOfAny(['/', '-']);
                if (sep < 0 || sep >= shrunk.Length - 2)
                    break;
                shrunk = shrunk[(sep + 1)..];
            }

            if (ValidPatternRegex().IsMatch(shrunk) && MatchesPattern(rawLower, shrunk))
                return shrunk;
        }

        var parts = InvalidPatternCharsRegex().Split(rawLower)
            .Select(p => p.TrimStart('.', '_', ':', '/', '-'))
            .Where(p => p.Length >= 2)
            .OrderByDescending(p => p.Length);
        foreach (var part in parts)
        {
            var slice = part.Length > 81 ? part[^81..] : part;
            slice = slice.TrimStart('.', '_', ':', '/', '-');
            if (ValidPatternRegex().IsMatch(slice) && MatchesPattern(rawLower, slice))
                return slice;
        }

        return null;
    }

    internal static bool MatchesPattern(string id, string pattern)
    {
        for (var at = id.IndexOf(pattern, StringComparison.Ordinal); at >= 0; at = id.IndexOf(pattern, at + 1, StringComparison.Ordinal))
        {
            var end = at + pattern.Length;
            var before = at > 0 && char.IsLetter(id[at - 1]) && char.IsLetter(pattern[0]);
            var after = end < id.Length && char.IsLetter(id[end]) && char.IsLetter(pattern[^1]);
            if (!before && !after)
                return true;
        }

        return false;
    }

    private static RatingEntry? FindBestPrior(string rawModelId, NormalizedModel norm, RatingEntry[] builtIns)
    {
        var lower = rawModelId.Trim().ToLowerInvariant();
        RatingEntry? best = null;
        foreach (var entry in builtIns)
        {
            if (MatchesPattern(lower, entry.Pattern) || MatchesPattern(norm.CanonicalKey, entry.Pattern))
            {
                if (best == null || entry.Pattern.Length > best.Pattern.Length)
                    best = entry;
            }
        }

        return best;
    }

    private static RatingEntry ScoreModel(string pattern, NormalizedModel norm, WebModelSignal signal, RatingEntry? prior)
    {
        var cost = ComputeCost(norm, signal, prior, out var blendedPricePer1M);
        var quality = ComputeQuality(norm, signal, prior, blendedPricePer1M);
        var deprecation = signal.DeprecationNotice;
        if (!string.IsNullOrWhiteSpace(deprecation))
        {
            var sb = new StringBuilder(deprecation.Length);
            foreach (var ch in deprecation)
                if (ch >= ' ' || ch == '\t')
                    sb.Append(ch);
            deprecation = sb.ToString();
            if (deprecation.Length > 160)
                deprecation = deprecation[..160];
        }
        return new RatingEntry(pattern, cost, quality, deprecation);
    }

    private static int ComputeCost(NormalizedModel norm, WebModelSignal signal, RatingEntry? prior, out double blendedPricePer1M)
    {
        blendedPricePer1M = 0;
        if (norm.IsLocal || signal.IsOllama)
            return 0;

        if (norm.IsFree)
            return 2;

        if (signal.InputCostPerToken.HasValue || signal.OutputCostPerToken.HasValue)
        {
            var pIn = (signal.InputCostPerToken ?? 0) * 1_000_000.0;
            var pOutBase = (signal.OutputCostPerToken ?? 0) * 1_000_000.0;
            if (pIn == 0 && pOutBase == 0 && (signal.RequestCost ?? 0) == 0)
                return 2;

            var pReason = (signal.ReasoningCostPerToken ?? 0) * 1_000_000.0;
            var pOutEff = pReason > pOutBase ? 0.5 * pOutBase + 0.5 * pReason : pOutBase;

            var effortMult = norm.Effort switch
            {
                "low" => 0.70,
                "high" => 1.45,
                "xhigh" => 2.10,
                _ => 1.00
            };
            var batchMult = norm.IsBatch && !signal.IsBatchPrice ? 0.50 : 1.00;
            blendedPricePer1M = batchMult * (3.0 * pIn + effortMult * pOutEff) / 4.0;
            return Math.Clamp((int)Math.Round(2.0 + 6.0 * Math.Pow(Math.Max(0, blendedPricePer1M), 0.70)), 3, 100);
        }

        var baseCost = prior?.Cost ?? 25;
        var effortDelta = norm.Effort switch
        {
            "low" => -4,
            "high" => 4,
            "xhigh" => 8,
            _ => 0
        };
        return Math.Clamp(baseCost + effortDelta, 3, 100);
    }

    private static Dictionary<string, int> ComputeQuality(NormalizedModel norm, WebModelSignal signal, RatingEntry? prior, double blendedPricePer1M)
    {
        var sCode = WeightedAverage([
            (signal.SweBenchVerified.HasValue ? Clamp(52.0 + 0.55 * signal.SweBenchVerified.Value, 45, 98) : null, 0.40),
            (!signal.SweBenchVerified.HasValue && signal.SweBenchOther.HasValue ? Clamp(48.0 + 0.55 * signal.SweBenchOther.Value, 45, 94) : null, 0.25),
            ((signal.AaCodingIndex ?? signal.AaEngineeringIndex) is { } aaCode ? Clamp(50.0 + 0.62 * aaCode, 45, 97) : null, 0.20),
            (signal.LmArenaWebdevElo is { } webdevElo ? Clamp(50.0 + 0.058 * (webdevElo - 1050.0), 45, 97) : null, 0.15),
            (signal.DesignArenaCodeElo is { } daCode ? Clamp(50.0 + 0.092 * (daCode - 980.0), 45, 96) : null, 0.15)
        ]);

        var liveBenchReasoningMean = signal.LiveBenchReasoning.HasValue && signal.LiveBenchMath.HasValue && signal.LiveBenchDataAnalysis.HasValue
            ? (signal.LiveBenchReasoning.Value + signal.LiveBenchMath.Value + signal.LiveBenchDataAnalysis.Value) / 3.0
            : (signal.LiveBenchReasoning ?? signal.LiveBenchMath ?? signal.LiveBenchDataAnalysis);

        var sReason = WeightedAverage([
            (signal.AaIntelligenceIndex is { } aaInt ? Clamp(50.0 + 0.65 * aaInt, 45, 97) : null, 0.30),
            (liveBenchReasoningMean.HasValue ? Clamp(52.0 + 0.58 * liveBenchReasoningMean.Value, 45, 97) : null, 0.30),
            (signal.LmArenaTextElo is { } textElo ? Clamp(48.0 + 0.088 * (textElo - 1020.0), 45, 97) : null, 0.20),
            (signal.LmArenaAgentScore is { } agentScore ? Clamp(58.0 + 135.0 * agentScore, 50, 97) : null, 0.20)
        ]);

        var liveBenchLangMean = signal.LiveBenchLanguage.HasValue && signal.LiveBenchIf.HasValue
            ? (signal.LiveBenchLanguage.Value + signal.LiveBenchIf.Value) / 2.0
            : (signal.LiveBenchLanguage ?? signal.LiveBenchIf);

        var sLang = WeightedAverage([
            (signal.LmArenaTextElo is { } textEloL ? Clamp(48.0 + 0.088 * (textEloL - 1020.0), 45, 97) : null, 0.45),
            (liveBenchLangMean.HasValue ? Clamp(52.0 + 0.56 * liveBenchLangMean.Value, 45, 96) : null, 0.35),
            (signal.AaIntelligenceIndex is { } aaIntL ? Clamp(50.0 + 0.62 * aaIntL, 45, 96) : null, 0.20)
        ]);

        var multi = WeightedAverage([
            (signal.AaMultilingualIndex is { } aaMul ? Clamp(50.0 + 0.62 * aaMul, 45, 96) : null, 0.40),
            (signal.SweBenchMultilingual is { } sweMul ? Clamp(48.0 + 0.55 * sweMul, 45, 94) : null, 0.20),
            (sLang, 0.40)
        ]) ?? sLang ?? 0;

        var hasBenchmarks = sCode.HasValue || sReason.HasValue || sLang.HasValue ||
                            signal.DesignArenaUiElo.HasValue || signal.DesignArenaGameDevElo.HasValue || signal.DesignArena3dElo.HasValue ||
                            signal.LmArenaTextElo.HasValue || signal.LmArenaWebdevElo.HasValue || signal.LmArenaVisionElo.HasValue ||
                            signal.LmArenaAgentScore.HasValue || signal.LmArenaTextToImageElo.HasValue ||
                            signal.LiveBenchCoding.HasValue || signal.LiveBenchReasoning.HasValue || signal.LiveBenchMath.HasValue ||
                            signal.LiveBenchDataAnalysis.HasValue || signal.LiveBenchLanguage.HasValue || signal.LiveBenchIf.HasValue ||
                            signal.LiveBenchDesign.HasValue || signal.LiveBenchAgentic.HasValue;

        var imgIn = signal.SupportsVision.HasValue
            ? signal.SupportsVision.Value || signal.SweBenchMultimodal || signal.LmArenaVisionElo.HasValue
            : signal.SweBenchMultimodal || signal.LmArenaVisionElo.HasValue || (prior?.Quality.GetValueOrDefault(nameof(WorkCapability.ImageUnderstanding)) ?? 0) > 0;
        var imgOut = signal.SupportsImageGeneration.HasValue
            ? signal.SupportsImageGeneration.Value || signal.LmArenaTextToImageElo.HasValue
            : signal.LmArenaTextToImageElo.HasValue || (prior?.Quality.GetValueOrDefault(nameof(WorkCapability.ImageGeneration)) ?? 0) > 0;

        var bCtx = signal.ContextLength switch
        {
            >= 500_000 => 2.0,
            >= 128_000 => 1.0,
            >= 32_000 => 0.0,
            > 0 => -3.0,
            _ => 0.0
        };
        var bStruct = signal.SupportsStructuredOrTools == true ? 2.0 : 0.0;

        if (!hasBenchmarks && prior != null)
        {
            var result = new Dictionary<string, int>(20, StringComparer.Ordinal);
            var avgPrior = prior.Quality.Values.Where(v => v > 0).DefaultIfEmpty(75).Average();
            foreach (var cap in Enum.GetValues<WorkCapability>())
            {
                var key = cap.ToString();
                if (cap == WorkCapability.ImageGeneration)
                {
                    result[key] = imgOut ? ClampInt(prior.Quality.GetValueOrDefault(key, (int)Math.Round(avgPrior)), 50, 96) : 0;
                    continue;
                }

                if (cap == WorkCapability.ImageUnderstanding)
                {
                    var baseVis = prior.Quality.GetValueOrDefault(key, 0);
                    result[key] = imgIn ? ClampInt((baseVis > 0 ? baseVis : (int)Math.Round(avgPrior)) + EffortDelta(cap, norm.Effort), 1, 99) : 0;
                    continue;
                }

                var baseVal = prior.Quality.GetValueOrDefault(key, (int)Math.Round(avgPrior));
                var adjusted = baseVal + EffortDelta(cap, norm.Effort);
                if (cap is WorkCapability.Classification or WorkCapability.Extraction && bStruct > 0)
                    adjusted += 1;
                result[key] = ClampInt(adjusted, 1, 99);
            }

            return result;
        }

        var fallbackBase = ComputeColdStartBase(norm, signal, prior, blendedPricePer1M);
        var code = sCode ?? (prior?.Quality.GetValueOrDefault(nameof(WorkCapability.Coding)) is > 0 and var pc ? pc : sReason ?? fallbackBase);
        var reason = sReason ?? (prior?.Quality.GetValueOrDefault(nameof(WorkCapability.Planning)) is > 0 and var pp ? pp : sCode ?? fallbackBase);
        var lang = sLang ?? (prior?.Quality.GetValueOrDefault(nameof(WorkCapability.Content)) is > 0 and var pl ? pl : 0.5 * reason + 0.5 * code);

        var sUi = signal.DesignArenaUiElo is { } uiElo
            ? Clamp(50.0 + 0.092 * (uiElo - 980.0), 45, 96)
            : (signal.LmArenaWebdevElo is { } webdevEloU
                ? Clamp(50.0 + 0.058 * (webdevEloU - 1050.0), 45, 97)
                : (signal.LiveBenchDesign.HasValue
                    ? Clamp(52.0 + 0.58 * signal.LiveBenchDesign.Value, 45, 96)
                    : 0.65 * code + 0.35 * lang));

        var sGameDev = signal.DesignArenaGameDevElo is { } gdElo
            ? 0.40 * Clamp(50.0 + 0.092 * (gdElo - 980.0), 45, 96) + 0.60 * (0.55 * reason + 0.45 * lang)
            : 0.55 * reason + 0.45 * lang;

        double? s3d = signal.DesignArena3dElo is { } d3Elo
            ? Clamp(50.0 + 0.092 * (d3Elo - 980.0), 45, 96)
            : null;

        var scores = new Dictionary<string, int>(20, StringComparer.Ordinal)
        {
            [nameof(WorkCapability.Coding)] = ScoreCap(WorkCapability.Coding, 0.85 * code + 0.15 * reason, norm.Effort),
            [nameof(WorkCapability.Review)] = ScoreCap(WorkCapability.Review, 0.55 * code + 0.35 * reason + 0.10 * lang + 0.5 * bCtx, norm.Effort),
            [nameof(WorkCapability.Research)] = ScoreCap(WorkCapability.Research, 0.55 * reason + 0.45 * lang + bCtx, norm.Effort),
            [nameof(WorkCapability.Content)] = ScoreCap(WorkCapability.Content, 0.75 * lang + 0.25 * reason, norm.Effort),
            [nameof(WorkCapability.Classification)] = ScoreCap(WorkCapability.Classification, 28.0 + 0.68 * (0.50 * lang + 0.50 * reason) + bStruct, norm.Effort),
            [nameof(WorkCapability.Extraction)] = ScoreCap(WorkCapability.Extraction, 28.0 + 0.68 * (0.45 * lang + 0.55 * reason) + bStruct, norm.Effort),
            [nameof(WorkCapability.Planning)] = ScoreCap(WorkCapability.Planning, 0.75 * reason + 0.25 * code + 0.5 * bCtx, norm.Effort),
            [nameof(WorkCapability.ImageGeneration)] = imgOut ? ClampInt(Math.Round(signal.LmArenaTextToImageElo is { } t2iElo
                ? Clamp(50.0 + 0.092 * (t2iElo - 980.0), 45, 96)
                : 0.50 * sUi + 0.50 * lang), 1, 99) : 0,
            [nameof(WorkCapability.UiDesign)] = ScoreCap(WorkCapability.UiDesign, sUi, norm.Effort),
            [nameof(WorkCapability.Debugging)] = ScoreCap(WorkCapability.Debugging, 0.65 * code + 0.35 * reason, norm.Effort),
            [nameof(WorkCapability.Refactoring)] = ScoreCap(WorkCapability.Refactoring, 0.75 * code + 0.25 * reason + 0.5 * bCtx, norm.Effort),
            [nameof(WorkCapability.Testing)] = ScoreCap(WorkCapability.Testing, 20.0 + 0.78 * code, norm.Effort),
            [nameof(WorkCapability.GraphicsMath)] = ScoreCap(WorkCapability.GraphicsMath, s3d.HasValue ? 0.55 * reason + 0.25 * code + 0.20 * s3d.Value : 0.65 * reason + 0.35 * code, norm.Effort),
            [nameof(WorkCapability.Performance)] = ScoreCap(WorkCapability.Performance, 0.55 * code + 0.45 * reason, norm.Effort),
            [nameof(WorkCapability.Architecture)] = ScoreCap(WorkCapability.Architecture, 0.70 * reason + 0.30 * code + bCtx, norm.Effort),
            [nameof(WorkCapability.GameDesign)] = ScoreCap(WorkCapability.GameDesign, sGameDev, norm.Effort),
            [nameof(WorkCapability.CreativeWriting)] = ScoreCap(WorkCapability.CreativeWriting, 0.80 * lang + 0.20 * reason, norm.Effort),
            [nameof(WorkCapability.Translation)] = ScoreCap(WorkCapability.Translation, 24.0 + 0.73 * multi, norm.Effort),
            [nameof(WorkCapability.ImageUnderstanding)] = imgIn ? ScoreCap(WorkCapability.ImageUnderstanding, signal.LmArenaVisionElo is { } visElo
                ? Clamp(50.0 + 0.092 * (visElo - 980.0), 45, 96)
                : 0.50 * reason + 0.50 * lang, norm.Effort) : 0,
            [nameof(WorkCapability.BuildDevOps)] = ScoreCap(WorkCapability.BuildDevOps, 18.0 + 0.80 * (0.75 * code + 0.25 * reason), norm.Effort)
        };

        return scores;
    }

    private static double ComputeColdStartBase(NormalizedModel norm, WebModelSignal signal, RatingEntry? prior, double blendedPricePer1M)
    {
        if (prior != null)
            return prior.Quality.Values.Where(v => v > 0).DefaultIfEmpty(72).Average();
        if (norm.IsLocal && norm.LocalParamB is > 0)
            return Clamp(Math.Round(46.0 + 8.0 * Math.Log(1.0 + norm.LocalParamB.Value)), 50, 82);
        var reasoningBonus = signal.SupportsReasoning == true ? 3.0 : 0.0;
        return Clamp(Math.Round(62.0 + 7.5 * Math.Log(1.0 + Math.Max(0, blendedPricePer1M)) + reasoningBonus), 54, 86);
    }

    private static int ScoreCap(WorkCapability cap, double raw, string effort) =>
        ClampInt(Math.Round(raw) + EffortDelta(cap, effort), 1, 99);

    private static int EffortDelta(WorkCapability cap, string effort) => effort switch
    {
        "low" => cap switch
        {
            WorkCapability.Coding or WorkCapability.Review or WorkCapability.Planning or WorkCapability.Debugging
                or WorkCapability.GraphicsMath or WorkCapability.Performance or WorkCapability.Architecture => -5,
            WorkCapability.Research => -4,
            WorkCapability.Refactoring or WorkCapability.GameDesign => -3,
            WorkCapability.ImageGeneration => 0,
            _ => -2
        },
        "high" => cap switch
        {
            WorkCapability.Coding or WorkCapability.Review or WorkCapability.Research or WorkCapability.Planning
                or WorkCapability.Debugging or WorkCapability.GraphicsMath or WorkCapability.Performance
                or WorkCapability.Architecture => 2,
            WorkCapability.ImageGeneration => 0,
            _ => 1
        },
        "xhigh" => cap switch
        {
            WorkCapability.Coding or WorkCapability.Review or WorkCapability.Planning or WorkCapability.Debugging
                or WorkCapability.GraphicsMath or WorkCapability.Performance or WorkCapability.Architecture => 4,
            WorkCapability.Research => 3,
            WorkCapability.UiDesign or WorkCapability.Refactoring or WorkCapability.GameDesign
                or WorkCapability.ImageUnderstanding => 2,
            WorkCapability.ImageGeneration => 0,
            _ => 1
        },
        _ => 0
    };

    private static void AddIfValid(Dictionary<string, RatingEntry> target, RatingEntry entry)
    {
        var pattern = (entry.Pattern ?? "").Trim().ToLowerInvariant();
        if (!ValidPatternRegex().IsMatch(pattern))
            return;
        if (entry.Cost is < 0 or > 100 || entry.Quality == null || entry.Quality.Count == 0)
            return;

        var cleanQuality = new Dictionary<string, int>(entry.Quality.Count, StringComparer.Ordinal);
        foreach (var (name, value) in entry.Quality)
        {
            if (!Enum.TryParse<WorkCapability>(name, true, out var cap) || !Enum.IsDefined(cap))
                continue;
            cleanQuality[cap.ToString()] = Math.Clamp(value, 0, 100);
        }

        if (cleanQuality.Count == 0)
            return;

        var deprecation = entry.Deprecation;
        if (!string.IsNullOrWhiteSpace(deprecation))
        {
            var sb = new StringBuilder(deprecation.Length);
            foreach (var ch in deprecation)
                if (ch >= ' ' || ch == '\t')
                    sb.Append(ch);
            deprecation = sb.ToString();
            if (deprecation.Length > 160)
                deprecation = deprecation[..160];
        }

        target[pattern] = new RatingEntry(pattern, Math.Clamp(entry.Cost, 0, 100), cleanQuality, deprecation);
    }

    private static double? WeightedAverage((double? Value, double Weight)[] items)
    {
        double sum = 0;
        double weight = 0;
        foreach (var (v, w) in items)
        {
            if (v.HasValue && w > 0)
            {
                sum += v.Value * w;
                weight += w;
            }
        }

        return weight > 0 ? sum / weight : null;
    }

    private static double Clamp(double value, double min, double max) => Math.Clamp(value, min, max);
    private static int ClampInt(double value, int min, int max) => Math.Clamp((int)Math.Round(value), min, max);
    private static double? Max(double? a, double b) => a.HasValue ? Math.Max(a.Value, b) : b;

    private static string? GetString(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static bool? GetBool(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var el) && el.ValueKind is JsonValueKind.True or JsonValueKind.False ? el.GetBoolean() : null;

    private static bool TryGetInt(JsonElement obj, string property, out int value)
    {
        value = 0;
        if (!obj.TryGetProperty(property, out var el))
            return false;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out value))
            return true;
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            return true;
        return false;
    }

    private static bool TryGetDouble(JsonElement obj, string property, out double value)
    {
        value = 0;
        if (!obj.TryGetProperty(property, out var el))
            return false;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out value) && !double.IsNaN(value) && !double.IsInfinity(value))
            return true;
        if (el.ValueKind == JsonValueKind.String && double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !double.IsNaN(value) && !double.IsInfinity(value))
            return true;
        return false;
    }

    private static bool ArrayContainsIgnoreCase(JsonElement arr, string target)
    {
        foreach (var item in arr.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && string.Equals(item.GetString(), target, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private sealed class WebModelSignal
    {
        public bool HasOpenRouter;
        public bool HasLiteLlm;
        public bool HasSweBench;
        public bool HasLmArena;
        public bool HasLiveBench;

        public double? InputCostPerToken;
        public double? OutputCostPerToken;
        public double? ReasoningCostPerToken;
        public double? RequestCost;
        public bool IsFreePrice;
        public bool IsBatchPrice;
        public bool IsOllama;

        public int? ContextLength;
        public bool? SupportsVision;
        public bool? SupportsImageGeneration;
        public bool? SupportsReasoning;
        public bool? SupportsStructuredOrTools;

        public double? SweBenchVerified;
        public double? SweBenchMultilingual;
        public double? SweBenchOther;
        public bool SweBenchMultimodal;

        public double? AaIntelligenceIndex;
        public double? AaCodingIndex;
        public double? AaEngineeringIndex;
        public double? AaMultilingualIndex;
        public double? DesignArenaCodeElo;
        public double? DesignArenaUiElo;
        public double? DesignArenaGameDevElo;
        public double? DesignArena3dElo;

        public double? LmArenaTextElo;
        public double? LmArenaWebdevElo;
        public double? LmArenaVisionElo;
        public double? LmArenaAgentScore;
        public double? LmArenaTextToImageElo;

        public double? LiveBenchCoding;
        public double? LiveBenchReasoning;
        public double? LiveBenchMath;
        public double? LiveBenchDataAnalysis;
        public double? LiveBenchLanguage;
        public double? LiveBenchIf;
        public double? LiveBenchDesign;
        public double? LiveBenchAgentic;

        public string? DeprecationNotice;

        public WebModelSignal Clone() => (WebModelSignal)MemberwiseClone();

        public void MergeFrom(WebModelSignal other, bool preferOtherPricing)
        {
            HasOpenRouter |= other.HasOpenRouter;
            HasLiteLlm |= other.HasLiteLlm;
            HasSweBench |= other.HasSweBench;
            HasLmArena |= other.HasLmArena;
            HasLiveBench |= other.HasLiveBench;
            IsOllama |= other.IsOllama;

            var otherHasPricing = other.InputCostPerToken.HasValue || other.OutputCostPerToken.HasValue;
            var selfHasPricing = InputCostPerToken.HasValue || OutputCostPerToken.HasValue;
            if (otherHasPricing && (!selfHasPricing || preferOtherPricing))
            {
                InputCostPerToken = other.InputCostPerToken ?? InputCostPerToken;
                OutputCostPerToken = other.OutputCostPerToken ?? OutputCostPerToken;
                ReasoningCostPerToken = other.ReasoningCostPerToken ?? ReasoningCostPerToken;
                RequestCost = other.RequestCost ?? RequestCost;
                IsFreePrice = other.IsFreePrice;
                IsBatchPrice = other.IsBatchPrice;
            }
            else if (!ReasoningCostPerToken.HasValue && other.ReasoningCostPerToken.HasValue)
            {
                ReasoningCostPerToken = other.ReasoningCostPerToken;
            }

            if (other.ContextLength.HasValue && (!ContextLength.HasValue || other.ContextLength.Value > ContextLength.Value))
                ContextLength = other.ContextLength;

            if (other.SupportsVision.HasValue)
                SupportsVision = SupportsVision.HasValue ? SupportsVision.Value || other.SupportsVision.Value : other.SupportsVision.Value;
            if (other.SupportsImageGeneration.HasValue)
                SupportsImageGeneration = SupportsImageGeneration.HasValue ? SupportsImageGeneration.Value || other.SupportsImageGeneration.Value : other.SupportsImageGeneration.Value;
            if (other.SupportsReasoning.HasValue)
                SupportsReasoning = SupportsReasoning.HasValue ? SupportsReasoning.Value || other.SupportsReasoning.Value : other.SupportsReasoning.Value;
            if (other.SupportsStructuredOrTools.HasValue)
                SupportsStructuredOrTools = SupportsStructuredOrTools.HasValue ? SupportsStructuredOrTools.Value || other.SupportsStructuredOrTools.Value : other.SupportsStructuredOrTools.Value;

            if (other.SweBenchVerified.HasValue)
                SweBenchVerified = Max(SweBenchVerified, other.SweBenchVerified.Value);
            if (other.SweBenchMultilingual.HasValue)
                SweBenchMultilingual = Max(SweBenchMultilingual, other.SweBenchMultilingual.Value);
            if (other.SweBenchOther.HasValue)
                SweBenchOther = Max(SweBenchOther, other.SweBenchOther.Value);
            SweBenchMultimodal |= other.SweBenchMultimodal;

            if (other.AaIntelligenceIndex.HasValue)
                AaIntelligenceIndex = Max(AaIntelligenceIndex, other.AaIntelligenceIndex.Value);
            if (other.AaCodingIndex.HasValue)
                AaCodingIndex = Max(AaCodingIndex, other.AaCodingIndex.Value);
            if (other.AaEngineeringIndex.HasValue)
                AaEngineeringIndex = Max(AaEngineeringIndex, other.AaEngineeringIndex.Value);
            if (other.AaMultilingualIndex.HasValue)
                AaMultilingualIndex = Max(AaMultilingualIndex, other.AaMultilingualIndex.Value);
            if (other.DesignArenaCodeElo.HasValue)
                DesignArenaCodeElo = Max(DesignArenaCodeElo, other.DesignArenaCodeElo.Value);
            if (other.DesignArenaUiElo.HasValue)
                DesignArenaUiElo = Max(DesignArenaUiElo, other.DesignArenaUiElo.Value);
            if (other.DesignArenaGameDevElo.HasValue)
                DesignArenaGameDevElo = Max(DesignArenaGameDevElo, other.DesignArenaGameDevElo.Value);
            if (other.DesignArena3dElo.HasValue)
                DesignArena3dElo = Max(DesignArena3dElo, other.DesignArena3dElo.Value);

            if (other.LmArenaTextElo.HasValue)
                LmArenaTextElo = Max(LmArenaTextElo, other.LmArenaTextElo.Value);
            if (other.LmArenaWebdevElo.HasValue)
                LmArenaWebdevElo = Max(LmArenaWebdevElo, other.LmArenaWebdevElo.Value);
            if (other.LmArenaVisionElo.HasValue)
                LmArenaVisionElo = Max(LmArenaVisionElo, other.LmArenaVisionElo.Value);
            if (other.LmArenaAgentScore.HasValue)
                LmArenaAgentScore = Max(LmArenaAgentScore, other.LmArenaAgentScore.Value);
            if (other.LmArenaTextToImageElo.HasValue)
                LmArenaTextToImageElo = Max(LmArenaTextToImageElo, other.LmArenaTextToImageElo.Value);

            if (other.LiveBenchCoding.HasValue)
                LiveBenchCoding = Max(LiveBenchCoding, other.LiveBenchCoding.Value);
            if (other.LiveBenchReasoning.HasValue)
                LiveBenchReasoning = Max(LiveBenchReasoning, other.LiveBenchReasoning.Value);
            if (other.LiveBenchMath.HasValue)
                LiveBenchMath = Max(LiveBenchMath, other.LiveBenchMath.Value);
            if (other.LiveBenchDataAnalysis.HasValue)
                LiveBenchDataAnalysis = Max(LiveBenchDataAnalysis, other.LiveBenchDataAnalysis.Value);
            if (other.LiveBenchLanguage.HasValue)
                LiveBenchLanguage = Max(LiveBenchLanguage, other.LiveBenchLanguage.Value);
            if (other.LiveBenchIf.HasValue)
                LiveBenchIf = Max(LiveBenchIf, other.LiveBenchIf.Value);
            if (other.LiveBenchDesign.HasValue)
                LiveBenchDesign = Max(LiveBenchDesign, other.LiveBenchDesign.Value);
            if (other.LiveBenchAgentic.HasValue)
                LiveBenchAgentic = Max(LiveBenchAgentic, other.LiveBenchAgentic.Value);

            if (!string.IsNullOrWhiteSpace(other.DeprecationNotice) && string.IsNullOrWhiteSpace(DeprecationNotice))
                DeprecationNotice = other.DeprecationNotice;
        }
    }

    private sealed class WebCatalogIndex
    {
        private readonly Dictionary<string, WebModelSignal> exact = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, WebModelSignal> canonical = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, WebModelSignal> effort = new(StringComparer.OrdinalIgnoreCase);

        public void Add(string rawId, NormalizedModel norm, WebModelSignal signal, bool preferPricingForCanonical)
        {
            var rawKey = rawId.Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(rawKey))
                MergeInto(exact, rawKey, signal, preferOtherPricing: signal.HasOpenRouter);

            if (!string.IsNullOrEmpty(norm.PostPrefixPreserveVariant) && norm.PostPrefixPreserveVariant != rawKey)
                MergeInto(exact, norm.PostPrefixPreserveVariant, signal, preferOtherPricing: signal.HasOpenRouter);

            if (!string.IsNullOrEmpty(norm.CanonicalKey))
            {
                var canonicalSignal = signal;
                if (norm.IsFree || norm.IsBatch)
                {
                    canonicalSignal = signal.Clone();
                    canonicalSignal.InputCostPerToken = null;
                    canonicalSignal.OutputCostPerToken = null;
                    canonicalSignal.ReasoningCostPerToken = null;
                    canonicalSignal.RequestCost = null;
                    canonicalSignal.IsFreePrice = false;
                    canonicalSignal.IsBatchPrice = false;
                }

                MergeInto(canonical, norm.CanonicalKey, canonicalSignal, preferOtherPricing: preferPricingForCanonical && signal.HasOpenRouter);
                if (!string.IsNullOrEmpty(norm.Effort))
                    MergeInto(effort, norm.CanonicalKey + "|" + norm.Effort, canonicalSignal, preferOtherPricing: preferPricingForCanonical);
            }
        }

        public WebModelSignal? Match(NormalizedModel norm)
        {
            WebModelSignal? combined = null;
            void MergeCandidate(WebModelSignal? candidate, bool preferPricing)
            {
                if (candidate == null)
                    return;
                if (combined == null)
                    combined = candidate.Clone();
                else
                    combined.MergeFrom(candidate, preferPricing);
            }

            if (exact.TryGetValue(norm.RawLower, out var ex1))
                MergeCandidate(ex1, preferPricing: true);
            if (exact.TryGetValue(norm.PostPrefixPreserveVariant, out var ex2))
                MergeCandidate(ex2, preferPricing: combined == null);

            if (!string.IsNullOrEmpty(norm.CanonicalKey))
            {
                if (!string.IsNullOrEmpty(norm.Effort) && effort.TryGetValue(norm.CanonicalKey + "|" + norm.Effort, out var effHit))
                    MergeCandidate(effHit, preferPricing: combined == null);

                if (canonical.TryGetValue(norm.CanonicalKey, out var canHit))
                    MergeCandidate(canHit, preferPricing: combined == null);

                if (combined == null && norm.IsLatest)
                {
                    var prefix = norm.CanonicalKey + "-";
                    var latestKey = canonical.Keys
                        .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(k => k, StringComparer.OrdinalIgnoreCase)
                        .FirstOrDefault();
                    if (latestKey != null)
                        MergeCandidate(canonical[latestKey], preferPricing: true);
                }
            }

            return combined;
        }

        private static void MergeInto(Dictionary<string, WebModelSignal> dict, string key, WebModelSignal signal, bool preferOtherPricing)
        {
            if (dict.TryGetValue(key, out var existing))
                existing.MergeFrom(signal, preferOtherPricing);
            else
                dict[key] = signal.Clone();
        }
    }
}