using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Employee.Core;

namespace Employee.Infrastructure;

public sealed class ResponsesApiProvider : IAIProvider, IModelCatalogStatus, IDisposable
{
    private const int MaxResponseBytes = 4_000_000;
    private readonly HttpClient http;
    private Uri? baseUri;
    private string apiKey = "";
    private IReadOnlyList<ModelInfo> models = [];
    private bool ready;

    public string Id => "responses-api";
    public string ModelDiscoveryDetail { get; private set; } = "API endpoint has not been checked.";

    public ResponsesApiProvider(HttpMessageHandler? handler = null)
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

    public static Uri ParseBaseUrl(string value) => TryParseBaseUrl(value, out var uri)
        ? uri!
        : throw new InvalidOperationException("Base URL must be an HTTPS URL (HTTP is allowed only for localhost), without credentials, query or fragment. Enter the API root, for example https://host/v1.");

    public async Task<ProviderHealth> DetectAsync(ProviderSettings settings, CancellationToken token)
    {
        ready = false;
        baseUri = null;
        apiKey = "";
        models = [];

        if (!TryParseBaseUrl(settings.ResponsesApiBaseUrl, out var parsedBaseUri) || parsedBaseUri == null)
        {
            ModelDiscoveryDetail = "Base URL must be an HTTPS URL (HTTP is allowed only for localhost), without credentials, query or fragment. Enter the API root, for example https://host/v1.";
            return new(Id, false, false, false, ModelDiscoveryDetail);
        }
        baseUri = parsedBaseUri;

        try
        {
            apiKey = SecretProtection.Unprotect(settings.ProtectedResponsesApiKey);
        }
        catch (Exception error) when (error is FormatException or System.ComponentModel.Win32Exception)
        {
            baseUri = null;
            ModelDiscoveryDetail = "The saved API key could not be decrypted.";
            return new(Id, true, false, false, "The saved API key could not be decrypted. Clear it and enter it again.");
        }

        if (apiKey.Length == 0 || apiKey.Length > 4096 || apiKey.Any(char.IsControl))
        {
            baseUri = null;
            ModelDiscoveryDetail = "Enter a valid API key.";
            return new(Id, true, false, false, ModelDiscoveryDetail);
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var response = await SendAsync(HttpMethod.Get, new Uri(parsedBaseUri, "models"), null, timeout.Token);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
            {
                ready = true;
                ModelDiscoveryDetail = "The endpoint does not expose a compatible /models list. Add an exact model ID manually.";
                return new(Id, true, true, true, ModelDiscoveryDetail, "Configured");
            }

            if ((int)response.StatusCode is < 200 or >= 300)
            {
                var detail = ErrorDetail(response.Body, response.StatusCode);
                ModelDiscoveryDetail = detail;
                baseUri = null;
                return new(Id, true, false, false, detail, AuthenticationFor(response.StatusCode));
            }

            try
            {
                models = ParseModels(response.Body);
                ModelDiscoveryDetail = models.Count == 0
                    ? "No model IDs were returned. Add an exact model ID manually."
                    : $"{models.Count} model IDs from /models; account access to each model remains unverified.";
            }
            catch (JsonException)
            {
                models = [];
                ModelDiscoveryDetail = "The /models response was not in the expected JSON format. Add an exact model ID manually.";
            }

            ready = true;
            return new(Id, true, true, true, "Responses API endpoint and API key verified without an inference call. " + ModelDiscoveryDetail, models.Count == 0 ? "Configured" : "Authenticated");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            baseUri = null;
            ModelDiscoveryDetail = "The API endpoint check timed out.";
            return new(Id, true, false, false, ModelDiscoveryDetail);
        }
        catch (HttpRequestException error)
        {
            baseUri = null;
            ModelDiscoveryDetail = "API endpoint is unreachable: " + error.Message;
            return new(Id, true, false, false, ModelDiscoveryDetail);
        }
    }

    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!ready)
            throw new InvalidOperationException("Check the Responses API endpoint before listing models.");
        return Task.FromResult(models);
    }

    public async Task<AgentResult> ExecuteAsync(ExecutionRequest request, Action<string> output, CancellationToken token)
    {
        if (!ready || baseUri == null || apiKey.Length == 0)
            return new(FailureKind.Unavailable, "", "Check the Responses API endpoint and API key before using it.");
        if (string.IsNullOrWhiteSpace(request.ModelId))
            return new(FailureKind.Validation, "", "Choose an exact model ID for the Responses API provider.");

        var body = JsonSerializer.Serialize(new
        {
            model = request.ModelId,
            instructions = request.Context,
            input = request.Work.Request,
            max_output_tokens = Math.Clamp(request.MaxOutputChars / 4, 256, 16384)
        });

        try
        {
            var response = await SendAsync(HttpMethod.Post, new Uri(baseUri, "responses"), body, token);
            if ((int)response.StatusCode is < 200 or >= 300)
            {
                var detail = ErrorDetail(response.Body, response.StatusCode);
                return new(Classify(response.StatusCode, response.Body), "", detail, (int)response.StatusCode);
            }

            string text;
            try
            {
                text = ParseOutput(response.Body);
            }
            catch (JsonException error)
            {
                return new(FailureKind.Validation, response.Body, "Responses API returned invalid JSON: " + error.Message);
            }

            if (string.IsNullOrWhiteSpace(text))
                return new(FailureKind.Validation, response.Body, "Responses API returned no output text.");
            text = text[..Math.Min(text.Length, request.MaxOutputChars)];
            output(text);
            return new(FailureKind.None, text);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException error)
        {
            return new(FailureKind.Unavailable, "", "Responses API request failed: " + error.Message);
        }
        catch (IOException error)
        {
            return new(FailureKind.Unavailable, "", error.Message);
        }
    }

    private async Task<(HttpStatusCode StatusCode, string Body)> SendAsync(HttpMethod method, Uri uri, string? json, CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        if (json != null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        var body = await ReadBodyAsync(response, token);
        return (response.StatusCode, body.Replace(apiKey, "[redacted]", StringComparison.Ordinal));
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken token)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var block = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(block, token)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
                throw new IOException("Responses API response exceeds 4 MB.");
            buffer.Write(block, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static IReadOnlyList<ModelInfo> ParseModels(string body)
    {
        using var json = JsonDocument.Parse(body);
        if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];
        return data.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null)
            .Where(id => !string.IsNullOrWhiteSpace(id) && id.Length <= 200)
            .Distinct(StringComparer.Ordinal)
            .Select(id => new ModelInfo(id!, Enum.GetValues<WorkKind>(), DiscoverySource: "Responses API /models; model access unverified"))
            .Take(500)
            .ToArray();
    }

    private static string ParseOutput(string body)
    {
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("API response must be a JSON object.");
        // A successful Responses envelope normally includes error: null.
        if (root.TryGetProperty("error", out var error) && error.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            throw new JsonException(error.ToString());
        if (root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String && status.GetString() is { } value && value != "completed")
            throw new JsonException("API response status was " + value + ".");
        if (root.TryGetProperty("output_text", out var outputText) && outputText.ValueKind == JsonValueKind.String)
            return outputText.GetString() ?? "";
        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
            return "";
        var parts = new List<string>();
        foreach (var item in output.EnumerateArray())
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                foreach (var part in content.EnumerateArray())
                    if (part.ValueKind == JsonValueKind.Object && part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                        parts.Add(text.GetString() ?? "");
        return string.Concat(parts);
    }

    private string ErrorDetail(string body, HttpStatusCode status)
    {
        var safeBody = body.Replace(apiKey, "[redacted]", StringComparison.Ordinal);
        return $"Responses API HTTP {(int)status}: {safeBody[..Math.Min(safeBody.Length, 1200)]}";
    }

    private static string AuthenticationFor(HttpStatusCode status) => status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? "NotAuthenticated" : "Unknown";

    private static FailureKind Classify(HttpStatusCode status, string body)
    {
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return FailureKind.Authentication;
        if (status == HttpStatusCode.TooManyRequests)
            return FailureKind.RateLimit;
        if (status == HttpStatusCode.NotFound)
            return FailureKind.ModelUnavailable;
        if ((int)status >= 500)
            return FailureKind.Unavailable;
        var classified = ProviderErrors.Classify(1, body);
        return classified == FailureKind.None ? FailureKind.TaskError : classified;
    }

    public void Dispose() => http.Dispose();
}
