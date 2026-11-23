using System.Net;
using System.Text;
using System.Text.Json;
using Employee.Core;
using Employee.Infrastructure;
using static Harness;

static class ResponsesApiChecks
{
    public static async Task Run()
    {
        await Check("Responses API uses Base URL, protected bearer key, model discovery and reviewed-provider output", async () =>
        {
            const string key = "test-responses-api-key";
            using var fixture = New(textOnly: true);
            var handler = new FakeResponsesHandler(key);
            using var provider = new ResponsesApiProvider(handler);
            var settings = new ProviderSettings
            {
                Id = "responses-api",
                ResponsesApiBaseUrl = "https://codex.fosta.app/v1",
                ProtectedResponsesApiKey = SecretProtection.Protect(key)
            };
            Assert(!JsonSerializer.Serialize(settings).Contains(key, StringComparison.Ordinal), "API key is not stored as plaintext");

            var health = await provider.DetectAsync(settings, CancellationToken.None);
            Assert(health.CanExecute && health.CanWrite && health.Authentication == "Authenticated", "Successful /models check verifies the endpoint without an inference call");
            Assert(handler.Requests.Single() is { Method: "GET", Path: "/v1/models", Authorization: "Bearer " + key }, "Base URL retains /v1 and sends the key only as a bearer header");
            Assert((await provider.ListModelsAsync(CancellationToken.None)).Single().Id == "model-test", "OpenAI-compatible models are discovered");

            var output = "";
            fixture.Work.Access = AccessLevel.WriteProject;
            var result = await provider.ExecuteAsync(new(fixture.Project, fixture.Work, "model-test", "bounded project context", 10000), text => output += text, CancellationToken.None);
            Assert(result.Success && output == "API answer", "Responses API text is returned to orchestration");
            Assert(handler.Requests.Last() is { Method: "POST", Path: "/v1/responses", Authorization: "Bearer " + key }, "Generation uses the Responses endpoint");
            using var request = JsonDocument.Parse(handler.Requests.Last().Body);
            Assert(request.RootElement.GetProperty("model").GetString() == "model-test"
                && request.RootElement.GetProperty("input").GetString() == fixture.Work.Request
                && request.RootElement.GetProperty("instructions").GetString() == "bounded project context", "Model, request and bounded context use the Responses request shape");

            Assert(!ResponsesApiProvider.TryParseBaseUrl("http://public.example/v1", out _), "Non-HTTPS remote Base URLs are rejected");
            Assert(ResponsesApiProvider.TryParseBaseUrl("http://127.0.0.1:8080/v1", out _), "Loopback HTTP remains available for local endpoints");
        });

        await Check("Responses API can route approved WriteProject tasks through reviewed edit proposals", async () =>
        {
            using var fixture = New();
            var key = "write-proposal-key";
            var handler = new FakeResponsesHandler(key)
            {
                ResponseBody = JsonSerializer.Serialize(new
                {
                    status = "completed",
                    output_text = JsonSerializer.Serialize(new EditProposal("Update quest", [new("Quest.cs", "NEW", "// reviewed API edit")]))
                })
            };
            using var provider = new ResponsesApiProvider(handler);
            var config = fixture.Settings.Providers.Single(p => p.Id == "responses-api");
            config.Enabled = true;
            config.AllowProjectWrite = true;
            config.Models.Add(new("model-test", [WorkKind.Coding]));
            var health = await provider.DetectAsync(new ProviderSettings
            {
                Id = "responses-api",
                ResponsesApiBaseUrl = "https://api.example.test/v1",
                ProtectedResponsesApiKey = SecretProtection.Protect(key)
            }, CancellationToken.None);
            fixture.Work.Access = AccessLevel.WriteProject;
            fixture.Store.SaveWork(fixture.Work);
            var orchestrator = new Orchestrator(fixture.Store, [provider], new NullRunner());
            await orchestrator.RunAsync(fixture.Work, fixture.Settings, [health]);

            var path = Path.Combine(fixture.Project.Root, "Quest.cs");
            Assert(fixture.Work.State == WorkState.NeedsReview && fixture.Store.Logs(fixture.Work.Id).Any(log => log.Event == "EditProposal"),
                $"WriteProject route returns a pending edit proposal; state={fixture.Work.State}, notes={fixture.Work.Notes}, requests={handler.Requests.Count}");
            Assert(!File.Exists(path), "The API cannot change project files before review");
            orchestrator.ApplyReviewedEdits(fixture.Work);
            Assert(File.ReadAllText(path) == "// reviewed API edit", "Applying the reviewed proposal writes only the approved project file");
        });

        await Check("Responses API accepts null errors with completed output and still rejects actual errors", async () =>
        {
            using var fixture = New(textOnly: true);
            var handler = new FakeResponsesHandler("regression-key");
            using var provider = new ResponsesApiProvider(handler);
            await provider.DetectAsync(new ProviderSettings
            {
                Id = "responses-api",
                ResponsesApiBaseUrl = "https://api.example.test/v1",
                ProtectedResponsesApiKey = SecretProtection.Protect("regression-key")
            }, CancellationToken.None);
            foreach (var response in new[]
            {
                """{"status":"completed","error":null,"output":[{"type":"reasoning","summary":[]},{"type":"message","content":[{"type":"output_text","text":"Valid answer"}]}]}""",
                """{"status":"completed","error":null,"output_text":"Valid answer"}"""
            })
            {
                handler.ResponseBody = response;
                var received = "";
                var result = await provider.ExecuteAsync(new(fixture.Project, fixture.Work, "model-test", "context", 10000), text => received += text, CancellationToken.None);
                Assert(result.Success && result.Output == "Valid answer" && received == "Valid answer", "A null error is not a failed response, for both text formats");
            }
            handler.ResponseBody = """{"status":"completed","error":{"message":"Actual API failure"},"output_text":"must not be accepted"}""";
            var failure = await provider.ExecuteAsync(new(fixture.Project, fixture.Work, "model-test", "context", 10000), _ => throw new Exception("Error output must not reach the worker"), CancellationToken.None);
            Assert(!failure.Success && failure.Detail.Contains("Actual API failure"), "Non-null API errors remain failures");
        });

        await Check("Responses API authentication failures are explicit and redact the key", async () =>
        {
            const string key = "another-secret-key";
            var handler = new FakeResponsesHandler(key) { ModelsStatus = HttpStatusCode.Unauthorized };
            using var provider = new ResponsesApiProvider(handler);
            var result = await provider.DetectAsync(new ProviderSettings
            {
                Id = "responses-api",
                ResponsesApiBaseUrl = "https://api.example.test/v1",
                ProtectedResponsesApiKey = SecretProtection.Protect(key)
            }, CancellationToken.None);
            Assert(!result.CanExecute && result.Authentication == "NotAuthenticated", "Unauthorized API key is blocked");
            Assert(!result.Detail.Contains(key, StringComparison.Ordinal), "API key is not included in error details");
        });
    }

    private sealed class FakeResponsesHandler(string apiKey) : HttpMessageHandler
    {
        public HttpStatusCode ModelsStatus { get; set; } = HttpStatusCode.OK;
        public string ResponseBody { get; set; } = """{"status":"completed","output_text":"API answer"}""";
        public List<(string Method, string Path, string? Authorization, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(token);
            var authorization = request.Headers.Authorization == null
                ? null
                : request.Headers.Authorization.Scheme + " " + request.Headers.Authorization.Parameter;
            Requests.Add((request.Method.Method, request.RequestUri!.AbsolutePath, authorization, body));
            if (request.Method == HttpMethod.Get)
                return Json(ModelsStatus, ModelsStatus == HttpStatusCode.OK ? """{"data":[{"id":"model-test"}]}""" : JsonSerializer.Serialize(new { error = new { message = "Invalid key: " + apiKey } }));
            return Json(HttpStatusCode.OK, ResponseBody);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }
}
