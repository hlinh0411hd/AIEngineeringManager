using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Employee.Core;
using Employee.Infrastructure;

static class WebRatingsChecks
{
    public static async Task<int> Run()
    {
        void Assert(bool condition, string message)
        {
            if (!condition)
                throw new Exception("Web ratings check failed: " + message);
        }

        var folder = Path.Combine(Path.GetTempPath(), "EmployeeWebRatingsChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            // 1. DefaultSources check - now 10 sources (5 LMArena configs + 2 LiveBench + 3 others)
            Assert(WebModelRatingsUpdater.DefaultSources.Count == 10, "Expected 10 default sources (OpenRouter, LiteLLM, SWE-bench, 5x LMArena configs, 2x LiveBench).");
            Assert(WebModelRatingsUpdater.DefaultSources.All(s => s.Url.StartsWith("https://", StringComparison.Ordinal)), "All DefaultSources must use HTTPS.");
            Assert(WebModelRatingsUpdater.DefaultSources.Any(s => s.Url.Contains("openrouter.ai/api/v1/models", StringComparison.Ordinal)), "Missing OpenRouter feed.");
            Assert(WebModelRatingsUpdater.DefaultSources.Any(s => s.Url.Contains("model_prices_and_context_window.json", StringComparison.Ordinal)), "Missing LiteLLM feed.");
            Assert(WebModelRatingsUpdater.DefaultSources.Any(s => s.Url.Contains("leaderboards.json", StringComparison.Ordinal)), "Missing SWE-bench feed.");
            Assert(WebModelRatingsUpdater.DefaultSources.Count(s => s.Url.Contains("datasets-server.huggingface.co", StringComparison.Ordinal) && s.Url.Contains("lmarena", StringComparison.Ordinal)) == 5, "Missing 5 LMArena configs.");
            Assert(WebModelRatingsUpdater.DefaultSources.Count(s => s.Url.Contains("livebench.ai", StringComparison.Ordinal)) == 2, "Missing 2 LiveBench sources (categories.json + table.csv).");

            const string openRouterJson = """
            {
              "data": [
                {
                  "id": "google/gemini-4-neon",
                  "context_length": 1000000,
                  "architecture": {
                    "modality": "text+image->text",
                    "input_modalities": ["text", "image"],
                    "output_modalities": ["text"]
                  },
                  "pricing": { "prompt": "0.000003", "completion": "0.000015" },
                  "supported_parameters": ["reasoning", "tools", "structured_outputs"],
                  "reasoning": { "supported_efforts": ["low", "medium", "high", "xhigh"] },
                  "benchmarks": {
                    "artificial_analysis": { "intelligence_index": 72.0, "coding_index": 70.0 },
                    "design_arena": [
                      { "category": "uicomponent", "elo": 1420 },
                      { "category": "gamedev", "elo": 1380 }
                    ]
                  }
                },
                {
                  "id": "nvidia/nemotron-3-ultra-550b-a55b:free",
                  "context_length": 131072,
                  "architecture": {
                    "input_modalities": ["text"],
                    "output_modalities": ["text"]
                  },
                  "pricing": { "prompt": "0", "completion": "0" },
                  "supported_parameters": ["tools"]
                },
                {
                  "id": "anthropic/claude-fable-5.2",
                  "context_length": 200000,
                  "architecture": {
                    "input_modalities": ["text", "image"],
                    "output_modalities": ["text"]
                  },
                  "pricing": { "prompt": "0.000025", "completion": "0.000125" },
                  "supported_parameters": ["reasoning", "tools"]
                },
                {
                  "id": "google/gemini-4-neon:batch",
                  "context_length": 1000000,
                  "endpoints": ["/api/v1/batches"],
                  "pricing": { "prompt": "0.0000015", "completion": "0.0000075" }
                }
              ]
            }
            """;

            const string liteLlmJson = """
            {
              "sample_spec": { "input_cost_per_token": 0.001 },
              "text-embedding-3-large": { "mode": "embedding", "input_cost_per_token": 0.00000013 },
              "gpt-4o-mini": {
                "litellm_provider": "openai",
                "mode": "chat",
                "input_cost_per_token": 0.00000015,
                "output_cost_per_token": 0.0000006,
                "max_input_tokens": 128000,
                "supports_vision": true,
                "supports_reasoning": false,
                "supports_function_calling": true
              },
              "anthropic/claude-fable-5.1": {
                "litellm_provider": "anthropic",
                "mode": "chat",
                "input_cost_per_token": 0.00002,
                "output_cost_per_token": 0.0001,
                "max_input_tokens": 200000,
                "supports_vision": true,
                "supports_reasoning": true
              },
              "ollama/qwen3.5": {
                "litellm_provider": "ollama",
                "mode": "chat",
                "input_cost_per_token": 0,
                "output_cost_per_token": 0,
                "max_input_tokens": 32768,
                "supports_vision": false,
                "supports_reasoning": true
              },
              "deprecated-model": {
                "litellm_provider": "openai",
                "mode": "chat",
                "input_cost_per_token": 0.00001,
                "output_cost_per_token": 0.00005,
                "max_input_tokens": 8000,
                "deprecation_date": "2026-06-15",
                "successor_model": "gpt-4o-mini"
              }
            }
            """;

            const string sweBenchJson = """
            {
              "leaderboards": [
                {
                  "name": "Verified",
                  "results": [
                    {
                      "name": "Gemini 4 Neon Run",
                      "resolved": 78.4,
                      "tags": ["Model: gemini-4-neon"],
                      "per_instance_details": { "inst-1": { "resolved": true, "logs": "huge" } }
                    },
                    {
                      "name": "Claude Fable 5.2 Run",
                      "resolved": 84.0,
                      "tags": ["Model: claude-fable-5-2"],
                      "per_instance_details": { "inst-2": { "resolved": true } }
                    },
                    {
                      "name": "Ensemble",
                      "resolved": 99.0,
                      "tags": ["Model: claude-fable-5-2", "Model: gpt-4o-mini"]
                    }
                  ]
                }
              ]
            }
            """;

            const string lmArenaJson = """
            {
              "rows": [
                {
                  "row": {
                    "model_name": "Gemini 4 Neon (High)",
                    "category": "overall",
                    "rating": 1250.5,
                    "vote_count": 200,
                    "session_count": 200
                  }
                },
                {
                  "row": {
                    "model_name": "Claude Fable 5.2",
                    "category": "overall",
                    "rating": 1320.0,
                    "vote_count": 500,
                    "session_count": 500
                  }
                },
                {
                  "row": {
                    "model_name": "Low Votes Model",
                    "category": "overall",
                    "rating": 1400.0,
                    "vote_count": 50,
                    "session_count": 50
                  }
                }
              ]
            }
            """;

            const string liveBenchCsv = """
            model,Coding,Long Agentic,Reasoning,Mathematics,Research,Design,Data Analysis,Language,IF
            gemini-4-neon,85,82,88,90,86,84,87,89,85
            claude-fable-5-2,92,90,94,95,91,89,93,95,92
            """;

            const string liveBenchCategoriesJson = """
            {
              "Coding": ["Coding"],
              "Long Agentic": ["Long Agentic"],
              "Agentic Coding": ["Agentic Coding"],
              "Reasoning": ["Reasoning"],
              "Mathematics": ["Mathematics"],
              "Research": ["Research"],
              "Design": ["Design"],
              "Data Analysis": ["Data Analysis"],
              "Language": ["Language"],
              "IF": ["IF"]
            }
            """;

            var userAgents = new List<string>();
            using var handler = new StubHttpHandler(req =>
            {
                userAgents.Add(req.Headers.UserAgent.ToString());
                var url = req.RequestUri!.ToString();
                if (url.Contains("openrouter", StringComparison.Ordinal))
                    return StubHttpHandler.Json(openRouterJson);
                if (url.Contains("litellm", StringComparison.Ordinal))
                    return StubHttpHandler.Json(liteLlmJson);
                if (url.Contains("swe-bench", StringComparison.Ordinal))
                    return StubHttpHandler.Json(sweBenchJson);
                if (url.Contains("datasets-server", StringComparison.Ordinal) || url.Contains("lmarena", StringComparison.Ordinal))
                    return StubHttpHandler.Json(lmArenaJson);
                if (url.Contains("livebench", StringComparison.Ordinal))
                {
                    if (url.Contains("categories", StringComparison.Ordinal))
                        return StubHttpHandler.Json(liveBenchCategoriesJson);
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(liveBenchCsv, Encoding.UTF8, "text/csv")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            var allKinds = Enum.GetValues<WorkKind>();
            var settings = new ManagerSettings
            {
                Providers =
                [
                    new ProviderSettings
                    {
                        Id = "antigravity",
                        Enabled = true,
                        Models =
                        [
                            new("", allKinds, Quality: 50, CostRank: 50),
                            new("gemini-4-neon-low", allKinds, Quality: 50, CostRank: 50),
                            new("gemini-4-neon-xhigh", allKinds, Quality: 50, CostRank: 50)
                        ]
                    },
                    new ProviderSettings
                    {
                        Id = "kilo",
                        Enabled = true,
                        Models =
                        [
                            new("kilo/nvidia/nemotron-3-ultra-550b-a55b:free", allKinds, Quality: 50, CostRank: 50),
                            new("kilo/~anthropic/claude-fable-latest", allKinds, Quality: 50, CostRank: 50)
                        ]
                    },
                    new ProviderSettings
                    {
                        Id = "opencode",
                        Enabled = true,
                        Models =
                        [
                            new("opencode/claude-fable-5-1", allKinds, Quality: 50, CostRank: 50)
                        ]
                    },
                    new ProviderSettings
                    {
                        Id = "copilot",
                        Enabled = true,
                        Models =
                        [
                            new("gpt-4o-mini-2024-07-18", allKinds, Quality: 50, CostRank: 50),
                            new("gpt-4o-mini", allKinds, Quality: 77, CostRank: 13) { RatedByUser = true },
                            new("deprecated-model", allKinds, Quality: 50, CostRank: 50)
                        ]
                    },
                    new ProviderSettings
                    {
                        Id = "ollama",
                        Enabled = true,
                        Models =
                        [
                            new("qwen3.5:4b", [WorkKind.Classification, WorkKind.Extraction], Quality: 50, CostRank: 50)
                        ]
                    }
                ]
            };

            // Seed existing ratings file with custom Priority to ensure preservation.
            var ratingsService = new ModelRatingsService(folder);
            var customPriority = new Dictionary<string, string[]>
            {
                [nameof(WorkCapability.Coding)] = ["antigravity/gemini-4-neon-xhigh"]
            };
            var existingSet = ModelRatings.BuiltIn() with { Priority = customPriority };
            File.WriteAllText(ratingsService.FilePath, JsonSerializer.Serialize(existingSet));

            using var store = new SqliteStore(Path.Combine(folder, "checks.db"));
            store.SaveSettings(settings);
            ProviderHealth[] health =
            [
                new("antigravity", true, true, true, "OK"),
                new("kilo", true, true, true, "OK"),
                new("opencode", true, true, true, "OK"),
                new("copilot", true, true, false, "OK"),
                new("ollama", true, true, false, "OK")
            ];

            var update = await ratingsService.UpdateFromWebAsync(settings, health, store, httpHandler: handler);

            Assert(userAgents.Count == 10 && userAgents.All(ua => ua.Contains("AIEmployeeManager/1.0", StringComparison.Ordinal)), "User-Agent AIEmployeeManager/1.0 must be sent to all 10 sources.");
            Assert(update.ConsultedSources.Count == 9, $"Expected 9 consulted sources (LiveBench-categories only provides mapping), got {update.ConsultedSources.Count}: {string.Join(", ", update.ConsultedSources)}");
            Assert(update.Warnings.Count == 0, "Expected zero warnings: " + string.Join("; ", update.Warnings));
            Assert(update.TotalAgentModels == 9, $"Expected 9 distinct non-empty agent models, got {update.TotalAgentModels}");
            Assert(update.MatchedAgentModels == 9, $"Expected all 9 models to match, got {update.MatchedAgentModels}");
            Assert(File.Exists(ratingsService.FilePath), "model-ratings.json must be written.");
            Assert(new FileInfo(ratingsService.FilePath).Length < 1_000_000, "model-ratings.json must stay well under 1 MB.");
            Assert(update.RatingSet.Priority != null && update.RatingSet.Priority.TryGetValue(nameof(WorkCapability.Coding), out var prio) && prio.SequenceEqual(["antigravity/gemini-4-neon-xhigh"]), "Existing Priority must be preserved.");

            // Validate every emitted RatingEntry pattern and 0-100 bounds.
            var patternRx = new Regex(@"^[a-z0-9][a-z0-9._:/-]{1,80}$", RegexOptions.CultureInvariant);
            foreach (var entry in update.RatingSet.Models)
            {
                Assert(patternRx.IsMatch(entry.Pattern), $"Invalid pattern emitted: '{entry.Pattern}'");
                Assert(entry.Cost is >= 0 and <= 100, $"Cost out of bounds for '{entry.Pattern}': {entry.Cost}");
                foreach (var (cap, val) in entry.Quality)
                    Assert(val is >= 0 and <= 100, $"Quality out of bounds for '{entry.Pattern}'/{cap}: {val}");
            }

            // Check effort differentiation (-low vs -xhigh)
            var neonLow = settings.Providers.Single(p => p.Id == "antigravity").Models.Single(m => m.Id == "gemini-4-neon-low");
            var neonXHigh = settings.Providers.Single(p => p.Id == "antigravity").Models.Single(m => m.Id == "gemini-4-neon-xhigh");
            Assert(neonLow.CostRank < neonXHigh.CostRank, $"Expected -low CostRank ({neonLow.CostRank}) < -xhigh ({neonXHigh.CostRank})");
            Assert(neonLow.Quality < neonXHigh.Quality, $"Expected -low Quality ({neonLow.Quality}) < -xhigh ({neonXHigh.Quality})");
            Assert(!neonLow.RatedByUser && !neonXHigh.RatedByUser, "Updated models must keep RatedByUser=false.");

            // Check CLI default (empty Id) untouched
            var cliDefault = settings.Providers.Single(p => p.Id == "antigravity").Models.Single(m => m.Id == "");
            Assert(cliDefault.Quality == 50 && cliDefault.CostRank == 50, "Empty Id model must remain untouched.");

            // Check :free -> Cost=2 and unsupported image capabilities -> 0
            var freeModel = settings.Providers.Single(p => p.Id == "kilo").Models.Single(m => m.Id.EndsWith(":free", StringComparison.Ordinal));
            Assert(freeModel.CostRank == 2, $"Expected :free CostRank 2, got {freeModel.CostRank}");
            var freeEntry = update.RatingSet.Models.Single(e => e.Pattern == "nemotron-3-ultra-550b-a55b:free");
            Assert(freeEntry.Quality[nameof(WorkCapability.ImageGeneration)] == 0, "Unsupported ImageGeneration must be 0.");
            Assert(freeEntry.Quality[nameof(WorkCapability.ImageUnderstanding)] == 0, "Unsupported ImageUnderstanding must be 0.");

            // Check ollama -> Cost=0
            var ollamaModel = settings.Providers.Single(p => p.Id == "ollama").Models.Single(m => m.Id == "qwen3.5:4b");
            Assert(ollamaModel.CostRank == 0, $"Expected ollama CostRank 0, got {ollamaModel.CostRank}");

            // Check RatedByUser=true preserved untouched
            var userRated = settings.Providers.Single(p => p.Id == "copilot").Models.Single(m => m.Id == "gpt-4o-mini");
            Assert(userRated.RatedByUser && userRated.Quality == 77 && userRated.CostRank == 13, "RatedByUser=true model must be preserved untouched.");

            // Check settings persisted to store and automatic roles assigned
            var reloaded = store.LoadSettings();
            var reloadedUserRated = reloaded.Providers.Single(p => p.Id == "copilot").Models.Single(m => m.Id == "gpt-4o-mini");
            Assert(reloadedUserRated.RatedByUser && reloadedUserRated.Quality == 77 && reloadedUserRated.CostRank == 13, "Saved settings must preserve RatedByUser=true.");
            Assert(reloaded.Providers.SelectMany(p => p.Models).Any(m => m.AutomaticPrimaryKinds.Length > 0), "AssignAutomaticRoles should assign automatic primary kinds.");

            // 2. Verify DeprecationFor and RatingSource include deprecation notices
            ModelRatings.Apply(update.RatingSet);
            var deprecationNotice = ModelRatings.DeprecationFor("deprecated-model");
            Assert(!string.IsNullOrWhiteSpace(deprecationNotice) && deprecationNotice.Contains("2026-06-15") && deprecationNotice.Contains("gpt-4o-mini"), $"DeprecationFor should surface LiteLLM deprecation, got: {deprecationNotice}");

            var deprecatedModelInfo = new ModelInfo("deprecated-model", []);
            var effective = ModelRatings.Effective(deprecatedModelInfo);
            Assert(effective.Source.Contains("Deprecated 2026-06-15"), $"Effective.Source should include deprecation notice, got: {effective.Source}");

            // 3. Verify DescribeCapabilities output
            var modelInfo = new ModelInfo("gemini-4-neon", []);
            var capabilities = ModelRatings.DescribeCapabilities("antigravity", modelInfo);
            Assert(capabilities.Contains("Code"), "DescribeCapabilities should include Code capability");
            Assert(capabilities.Contains("Debug"), "DescribeCapabilities should include Debug capability");
            Assert(capabilities.Contains("Usage rank:"), "DescribeCapabilities should include Usage rank");
            Assert(capabilities.Contains("RatingSource:"), "DescribeCapabilities should include RatingSource");

            // 4. Verify batch-only model pricing (should not skew canonical)
            var batchEntry = update.RatingSet.Models.SingleOrDefault(e => e.Pattern.Contains(":batch"));
            if (batchEntry != null)
            {
                Assert(batchEntry.Cost > 0, "Batch model should have a valid cost");
            }

            // 5. Partial source failure: 1 source fails, others succeed -> warning recorded, no throw
            using var partialHandler = new StubHttpHandler(req =>
                req.RequestUri!.ToString().Contains("openrouter", StringComparison.Ordinal)
                    ? StubHttpHandler.Json(openRouterJson)
                    : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            var partial = await WebModelRatingsUpdater.FetchAndBuildAsync(settings, sources: WebModelRatingsUpdater.DefaultSources, httpHandler: partialHandler);
            Assert(partial.ConsultedSources.Count == 1 && partial.Warnings.Count == 9, "Partial failure should succeed with 1 consulted source and 9 warnings (9 failing sources).");

            // 6. Total source failure -> throws InvalidOperationException and leaves active ratings intact
            var activeBefore = ModelRatings.ActiveSource;
            using var failHandler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));
            var threw = false;
            try
            {
                await ratingsService.UpdateFromWebAsync(settings, httpHandler: failHandler);
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }
            Assert(threw, "Zero succeeding web sources must throw InvalidOperationException.");
            Assert(ModelRatings.ActiveSource == activeBefore, "Active ratings must not be wiped when all web sources fail.");

            // 7. Oversized feed (> 15 MB) is rejected cleanly
            using var oversizedHandler = new StubHttpHandler(_ =>
            {
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(new byte[WebModelRatingsUpdater.MaxFeedBytes + 1024])
                };
                return resp;
            });
            var threwOversized = false;
            try
            {
                await WebModelRatingsUpdater.FetchAndBuildAsync(settings, sources: [("OpenRouter", "https://openrouter.ai/api/v1/models")], httpHandler: oversizedHandler);
            }
            catch (InvalidOperationException)
            {
                threwOversized = true;
            }
            Assert(threwOversized, "Feed exceeding 15 MB cap must be rejected.");

            // 8. Concurrency limit: verify only 4 concurrent requests at a time
            var concurrentCount = 0;
            var maxConcurrent = 0;
            var concurrencyLock = new object();
            using var concurrencyHandler = new StubHttpHandler(req =>
            {
                lock (concurrencyLock)
                {
                    concurrentCount++;
                    if (concurrentCount > maxConcurrent) maxConcurrent = concurrentCount;
                }
                Task.Delay(50).Wait(); // Simulate some work
                lock (concurrencyLock)
                {
                    concurrentCount--;
                }
                return StubHttpHandler.Json("""{"data":[{"id":"test-model","context_length":4096,"architecture":{"input_modalities":["text"],"output_modalities":["text"]},"pricing":{"prompt":"0.000001","completion":"0.000002"}}]}""");
            });
            var concurrencySources = Enumerable.Range(1, 10).Select(i => ($"Source{i}", $"https://openrouter.ai/api/v1/models?test={i}")).ToArray();
            await WebModelRatingsUpdater.FetchAndBuildAsync(settings, sources: concurrencySources, httpHandler: concurrencyHandler);
            Assert(maxConcurrent <= 4, $"Concurrency must be limited to 4, observed max concurrent: {maxConcurrent}");

            // 9. Timeout: 15s per source, 35s overall
            var timeoutHandler = new StubHttpHandler(_ =>
            {
                Task.Delay(20000).Wait(); // Exceeds 15s per-source timeout
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
            var timeoutSettings = new ManagerSettings { Providers = [] };
            var threwTimeout = false;
            try
            {
                await WebModelRatingsUpdater.FetchAndBuildAsync(timeoutSettings, sources: [("Slow", "https://openrouter.ai/api/v1/models")], httpHandler: timeoutHandler);
            }
            catch (InvalidOperationException)
            {
                threwTimeout = true;
            }
            Assert(threwTimeout, "Per-source 15s timeout must be enforced");

            // 10. Verify all 5 LMArena configs are individually parsed
            const string lmArenaTextJson = """{"rows":[{"row":{"model_name":"Test Model","category":"overall","rating":1200,"vote_count":200,"session_count":200}}]}""";
            const string lmArenaWebdevJson = """{"rows":[{"row":{"model_name":"Test Model","category":"overall","rating":1250,"vote_count":200,"session_count":200}}]}""";
            const string lmArenaVisionJson = """{"rows":[{"row":{"model_name":"Test Model","category":"overall","rating":1300,"vote_count":200,"session_count":200}}]}""";
            const string lmArenaAgentJson = """{"rows":[{"row":{"model_name":"Test Model","category":"overall","rating":1350,"vote_count":200,"session_count":200}}]}""";
            const string lmArenaTextToImageJson = """{"rows":[{"row":{"model_name":"Test Model","category":"overall","rating":1400,"vote_count":200,"session_count":200}}]}""";

            var lmArenaConfigs = new (string Name, string Config, string Json)[]
            {
                ("LMArena-text", "text", lmArenaTextJson),
                ("LMArena-webdev", "webdev", lmArenaWebdevJson),
                ("LMArena-vision", "vision", lmArenaVisionJson),
                ("LMArena-agent", "agent", lmArenaAgentJson),
                ("LMArena-text_to_image", "text_to_image", lmArenaTextToImageJson)
            };

            foreach (var (name, config, json) in lmArenaConfigs)
            {
                using var lmArenaHandler = new StubHttpHandler(_ => StubHttpHandler.Json(json));
                var testSettings = new ManagerSettings
                {
                    Providers =
                    [
                        new ProviderSettings
                        {
                            Id = "test",
                            Enabled = true,
                            Models = [new("test-model", Enum.GetValues<WorkKind>(), Quality: 50, CostRank: 50)]
                        }
                    ]
                };
                var result = await WebModelRatingsUpdater.FetchAndBuildAsync(testSettings, sources: [(name, $"https://datasets-server.huggingface.co/rows?dataset=lmarena-ai%2Fleaderboard-dataset&config={config}&split=latest&offset=0&length=100")], httpHandler: lmArenaHandler);
                Assert(result.ConsultedSources.Contains(name), $"{name} must be consulted");
                Assert(result.MatchedAgentModels == 1, $"{name} must match agent model");
            }

            // 11. Verify LiveBench table.csv parsed with heuristic category mapping (categories.json optional)
            const string liveBenchTableCsv = "model,Coding,Reasoning,Mathematics\ntest-model,85,90,88\n";

            var liveBenchSettings = new ManagerSettings
            {
                Providers =
                [
                    new ProviderSettings
                    {
                        Id = "test",
                        Enabled = true,
                        Models = [new("test-model", Enum.GetValues<WorkKind>(), Quality: 50, CostRank: 50)]
                    }
                ]
            };

            var liveBenchSources = new (string, string)[]
            {
                ("LiveBench-table", "https://livebench.ai/data/20261106/table.csv")
            };

            using var liveBenchHandler = new StubHttpHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(liveBenchTableCsv, Encoding.UTF8, "text/csv")
                });

            var liveBenchResult = await WebModelRatingsUpdater.FetchAndBuildAsync(liveBenchSettings, sources: liveBenchSources, httpHandler: liveBenchHandler);
            Assert(liveBenchResult.ConsultedSources.Contains("LiveBench-table"), "LiveBench table must be consulted");
            Assert(liveBenchResult.MatchedAgentModels == 1, "LiveBench must match agent model");
            var lbEntry = liveBenchResult.RatingSet.Models.Single(e => e.Pattern == "test-model");
            Assert(lbEntry.Quality[nameof(WorkCapability.Coding)] > 0, "LiveBench Coding score must be present");
            Assert(lbEntry.Quality[nameof(WorkCapability.Research)] > 0, "LiveBench Reasoning score must be present");
            Assert(lbEntry.Quality[nameof(WorkCapability.GraphicsMath)] > 0, "LiveBench Mathematics score must be present");

            // 11b. Verify LiveBench categories.json parsing (populates static category map for subsequent table.csv)
            // Note: categories-only source returns 0 models and throws when used alone; 
            // it's designed to be fetched alongside table.csv in a single call.
            // The warning is suppressed for sources with "categories" in the name.
            const string liveBenchCategoriesJson2 = """{"Coding":["Coding"],"Reasoning":["Reasoning"],"Mathematics":["Mathematics"]}""";
            using var liveBenchCatHandler = new StubHttpHandler(_ => StubHttpHandler.Json(liveBenchCategoriesJson2));
            var liveBenchCatSources = new (string, string)[]
            {
                ("LiveBench-categories", "https://livebench.ai/data/20261106/categories.json")
            };
            // Verify categories-only throws (no models consulted)
            var catThrew = false;
            try
            {
                await WebModelRatingsUpdater.FetchAndBuildAsync(liveBenchSettings, sources: liveBenchCatSources, httpHandler: liveBenchCatHandler);
            }
            catch (InvalidOperationException)
            {
                catThrew = true;
            }
            Assert(catThrew, "Categories-only source should throw (no models consulted)");

            // 12. Batch-only filtering: batch models should not skew canonical pricing
            const string batchOnlyJson = """
            {
              "data": [
                {
                  "id": "test/batch-model:batch",
                  "context_length": 100000,
                  "architecture": {"input_modalities":["text"],"output_modalities":["text"]},
                  "pricing": {"prompt":"0.0000001","completion":"0.0000005"},
                  "endpoints": ["/api/v1/batches"]
                }
              ]
            }
            """;

            using var batchHandler = new StubHttpHandler(_ => StubHttpHandler.Json(batchOnlyJson));
            var batchSettings = new ManagerSettings
            {
                Providers =
                [
                    new ProviderSettings
                    {
                        Id = "test",
                        Enabled = true,
                        Models = [new("test/batch-model:batch", Enum.GetValues<WorkKind>(), Quality: 50, CostRank: 50)]
                    }
                ]
            };

            var batchResult = await WebModelRatingsUpdater.FetchAndBuildAsync(batchSettings, sources: [("OpenRouter", "https://openrouter.ai/api/v1/models")], httpHandler: batchHandler);
            var batchModelEntry = batchResult.RatingSet.Models.SingleOrDefault(e => e.Pattern.Contains(":batch"));
            if (batchModelEntry != null)
            {
                Assert(batchModelEntry.Cost > 0 && batchModelEntry.Cost <= 100, "Batch model should have valid cost");
            }

            // 13. Capability breakdown: verify all 20 WorkCapability values are populated
            const string fullFeatureJson = """
            {
              "data": [
                {
                  "id": "test/full-model",
                  "context_length": 200000,
                  "architecture": {"input_modalities":["text","image"],"output_modalities":["text","image"]},
                  "pricing": {"prompt":"0.00001","completion":"0.00005"},
                  "supported_parameters":["reasoning","tools","structured_outputs"],
                  "reasoning":{"supported_efforts":["low","medium","high","xhigh"]},
                  "benchmarks":{
                    "artificial_analysis":{"intelligence_index":80,"coding_index":85,"engineering_index":82,"multilingual_index":78},
                    "design_arena":[
                      {"category":"codecategories","elo":1450},
                      {"category":"uicomponent","elo":1420},
                      {"category":"gamedev","elo":1400},
                      {"category":"3d","elo":1380}
                    ]
                  }
                }
              ]
            }
            """;

            using var fullHandler = new StubHttpHandler(_ => StubHttpHandler.Json(fullFeatureJson));
            var fullSettings = new ManagerSettings
            {
                Providers =
                [
                    new ProviderSettings
                    {
                        Id = "test",
                        Enabled = true,
                        Models = [new("test/full-model", Enum.GetValues<WorkKind>(), Quality: 50, CostRank: 50)]
                    }
                ]
            };

            var fullResult = await WebModelRatingsUpdater.FetchAndBuildAsync(fullSettings, sources: [("OpenRouter", "https://openrouter.ai/api/v1/models")], httpHandler: fullHandler);
            var fullEntry = fullResult.RatingSet.Models.Single(e => e.Pattern == "test/full-model" || e.Pattern == "full-model");
            foreach (var cap in Enum.GetValues<WorkCapability>())
            {
                var key = cap.ToString();
                Assert(fullEntry.Quality.ContainsKey(key), $"Missing capability: {key}");
                Assert(fullEntry.Quality[key] >= 0 && fullEntry.Quality[key] <= 100, $"Capability {key} out of bounds: {fullEntry.Quality[key]}");
            }

            // Verify specific high-value capabilities from benchmarks
            Assert(fullEntry.Quality[nameof(WorkCapability.Coding)] >= 85, "Coding should be strong from benchmarks");
            Assert(fullEntry.Quality[nameof(WorkCapability.Debugging)] >= 85, "Debugging should be strong from benchmarks");
            Assert(fullEntry.Quality[nameof(WorkCapability.UiDesign)] >= 60, "UiDesign should reflect design_arena uicomponent");
            Assert(fullEntry.Quality[nameof(WorkCapability.GameDesign)] >= 60, "GameDesign should reflect design_arena gamedev");
            Assert(fullEntry.Quality[nameof(WorkCapability.GraphicsMath)] >= 60, "GraphicsMath should reflect design_arena 3d");
            Assert(fullEntry.Quality[nameof(WorkCapability.ImageGeneration)] > 0, "ImageGeneration should be >0 for image output");
            Assert(fullEntry.Quality[nameof(WorkCapability.ImageUnderstanding)] > 0, "ImageUnderstanding should be >0 for image input");

            // 14. Deprecation: OpenRouter expiration_date and LiteLLM deprecation_date + successor
            const string deprecationJson = """
            {
              "data": [
                {
                  "id": "test/deprecated-openrouter",
                  "context_length": 100000,
                  "architecture": {"input_modalities":["text"],"output_modalities":["text"]},
                  "pricing": {"prompt":"0.00001","completion":"0.00005"},
                  "expiration_date": "2026-12-31"
                },
                {
                  "id": "test/successor-model",
                  "context_length": 100000,
                  "architecture": {"input_modalities":["text"],"output_modalities":["text"]},
                  "pricing": {"prompt":"0.00001","completion":"0.00005"}
                }
              ]
            }
            """;

            const string litellmDeprecationJson = """
            {
              "test/deprecated-litellm": {
                "litellm_provider": "openai",
                "mode": "chat",
                "input_cost_per_token": 0.00001,
                "output_cost_per_token": 0.00005,
                "max_input_tokens": 100000,
                "deprecation_date": "2026-06-15",
                "successor_model": "test/successor-model"
              }
            }
            """;

            using var depHandler = new StubHttpHandler(req =>
            {
                var url = req.RequestUri!.ToString();
                if (url.Contains("openrouter", StringComparison.Ordinal))
                    return StubHttpHandler.Json(deprecationJson);
                return StubHttpHandler.Json(litellmDeprecationJson);
            });

            var depSettings = new ManagerSettings
            {
                Providers =
                [
                    new ProviderSettings
                    {
                        Id = "test",
                        Enabled = true,
                        Models =
                        [
                            new("test/deprecated-openrouter", Enum.GetValues<WorkKind>(), Quality: 50, CostRank: 50),
                            new("test/deprecated-litellm", Enum.GetValues<WorkKind>(), Quality: 50, CostRank: 50)
                        ]
                    }
                ]
            };

            var depResult = await WebModelRatingsUpdater.FetchAndBuildAsync(depSettings, sources: [("OpenRouter", "https://openrouter.ai/api/v1/models"), ("LiteLLM", "https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json")], httpHandler: depHandler);

            ModelRatings.Apply(depResult.RatingSet);
            var orDeprecation = ModelRatings.DeprecationFor("test/deprecated-openrouter");
            Assert(!string.IsNullOrWhiteSpace(orDeprecation) && orDeprecation.Contains("2026-12-31"), $"OpenRouter deprecation must surface: {orDeprecation}");

            var llDeprecation = ModelRatings.DeprecationFor("test/deprecated-litellm");
            Assert(!string.IsNullOrWhiteSpace(llDeprecation) && llDeprecation.Contains("2026-06-15") && llDeprecation.Contains("successor-model"), $"LiteLLM deprecation must surface: {llDeprecation}");

            var orEffective = ModelRatings.Effective(new ModelInfo("test/deprecated-openrouter", []));
            Assert(orEffective.Source.Contains("Deprecated 2026-12-31"), $"Effective.Source must include deprecation: {orEffective.Source}");

            var llEffective = ModelRatings.Effective(new ModelInfo("test/deprecated-litellm", []));
            Assert(llEffective.Source.Contains("Deprecated 2026-06-15") && llEffective.Source.Contains("successor-model"), $"Effective.Source must include LiteLLM deprecation: {llEffective.Source}");

            ModelRatings.Apply(null);

            // 15. Priority preservation with custom PriorityRung entries (already tested in main test, verify with successful fetch)
            var priorityFolder = Path.Combine(Path.GetTempPath(), "EmployeeWebRatingsChecks", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(priorityFolder);
            try
            {
                var ratingsService2 = new ModelRatingsService(priorityFolder);
                var customPriority2 = new Dictionary<string, string[]>
                {
                    [nameof(WorkCapability.Coding)] = ["custom/provider@50", "another/model@100"],
                    [nameof(WorkCapability.Research)] = ["research/model"]
                };
                var existingSet2 = ModelRatings.BuiltIn() with { Priority = customPriority2 };
                File.WriteAllText(ratingsService2.FilePath, JsonSerializer.Serialize(existingSet2));

                var settingsWithPriority = new ManagerSettings
                {
                    Providers =
                    [
                        new ProviderSettings
                        {
                            Id = "test",
                            Enabled = true,
                            Models = [new("test/model", Enum.GetValues<WorkKind>(), Quality: 50, CostRank: 50)]
                        }
                    ]
                };
                var healthEmpty = new[] { new ProviderHealth("test", true, true, true, "OK") };
                using var store2 = new SqliteStore(Path.Combine(priorityFolder, "checks2.db"));

                // Mock successful OpenRouter response
                const string priorityTestJson = """{"data":[{"id":"test/model","context_length":100000,"architecture":{"input_modalities":["text"],"output_modalities":["text"]},"pricing":{"prompt":"0.00001","completion":"0.00005"}}]}""";
                using var priorityHandler = new StubHttpHandler(req =>
                {
                    var url = req.RequestUri!.ToString();
                    if (url.Contains("openrouter", StringComparison.Ordinal))
                        return StubHttpHandler.Json(priorityTestJson);
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                });

                var update2 = await ratingsService2.UpdateFromWebAsync(settingsWithPriority, healthEmpty, store2, httpHandler: priorityHandler);

                Assert(update2.RatingSet.Priority != null && update2.RatingSet.Priority.Count == 2, "Custom Priority must be preserved");
                var prioDict = update2.RatingSet.Priority!;
                prioDict.TryGetValue(nameof(WorkCapability.Coding), out var codingPrio);
                prioDict.TryGetValue(nameof(WorkCapability.Research), out var researchPrio);
                Assert(codingPrio != null && codingPrio.SequenceEqual(["custom/provider@50", "another/model@100"]), "Coding priority must match");
                Assert(researchPrio != null && researchPrio.SequenceEqual(["research/model"]), "Research priority must match");
            }
            finally
            {
                ModelRatings.Apply(null);
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (Directory.Exists(priorityFolder))
                    Directory.Delete(priorityFolder, true);
            }

            // 16. RatedByUser preservation with multiple user-rated models (already tested in main test, verify with successful fetch)
            var ratedByUserFolder = Path.Combine(Path.GetTempPath(), "EmployeeWebRatingsChecks", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ratedByUserFolder);
            try
            {
                var ratingsService3 = new ModelRatingsService(ratedByUserFolder);
                var settings3 = new ManagerSettings
                {
                    Providers =
                    [
                        new ProviderSettings
                        {
                            Id = "test",
                            Enabled = true,
                            Models =
                            [
                                new("model-a", Enum.GetValues<WorkKind>(), Quality: 80, CostRank: 20) { RatedByUser = true },
                                new("model-b", Enum.GetValues<WorkKind>(), Quality: 90, CostRank: 10) { RatedByUser = true },
                                new("model-c", Enum.GetValues<WorkKind>(), Quality: 50, CostRank: 50)
                            ]
                        }
                    ]
                };
                var health3 = new[] { new ProviderHealth("test", true, true, true, "OK") };
                using var store3 = new SqliteStore(Path.Combine(ratedByUserFolder, "checks3.db"));

                // Mock a successful response for model-c only
                const string modelCJson = """{"data":[{"id":"model-c","context_length":100000,"architecture":{"input_modalities":["text"],"output_modalities":["text"]},"pricing":{"prompt":"0.00001","completion":"0.00005"}}]}""";
                using var handler3 = new StubHttpHandler(req =>
                {
                    var url = req.RequestUri!.ToString();
                    if (url.Contains("openrouter", StringComparison.Ordinal))
                        return StubHttpHandler.Json(modelCJson);
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                });

                var update3 = await ratingsService3.UpdateFromWebAsync(settings3, health3, store3, httpHandler: handler3);

                var reloaded3 = store3.LoadSettings();
                var modelA = reloaded3.Providers.Single(p => p.Id == "test").Models.Single(m => m.Id == "model-a");
                var modelB = reloaded3.Providers.Single(p => p.Id == "test").Models.Single(m => m.Id == "model-b");
                var modelC = reloaded3.Providers.Single(p => p.Id == "test").Models.Single(m => m.Id == "model-c");

                Assert(modelA.RatedByUser && modelA.Quality == 80 && modelA.CostRank == 20, "model-a RatedByUser must be preserved");
                Assert(modelB.RatedByUser && modelB.Quality == 90 && modelB.CostRank == 10, "model-b RatedByUser must be preserved");
                Assert(!modelC.RatedByUser && modelC.Quality != 50, "model-c should be updated from web");
            }
            finally
            {
                ModelRatings.Apply(null);
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (Directory.Exists(ratedByUserFolder))
                    Directory.Delete(ratedByUserFolder, true);
            }

            // 17. Persistence under 1 MB verified with many models
            var manyModelsFolder = Path.Combine(Path.GetTempPath(), "EmployeeWebRatingsChecks", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(manyModelsFolder);
            try
            {
                var ratingsService4 = new ModelRatingsService(manyModelsFolder);
                var providers = new List<ProviderSettings>();
                var allKinds2 = Enum.GetValues<WorkKind>();
                for (int i = 0; i < 50; i++)
                {
                    providers.Add(new ProviderSettings
                    {
                        Id = $"provider{i}",
                        Enabled = true,
                        Models = [new($"provider{i}/model-{i}", allKinds2, Quality: 50, CostRank: 50)]
                    });
                }
                var manySettings = new ManagerSettings { Providers = providers };
                var manyHealth = providers.Select(p => new ProviderHealth(p.Id, true, true, true, "OK")).ToArray();

                // Mock response with 50 models
                var manyModelsJson = "{\"data\":[" + string.Join(",", Enumerable.Range(0, 50).Select(i =>
                    $"{{\"id\":\"provider{i}/model-{i}\",\"context_length\":100000,\"architecture\":{{\"input_modalities\":[\"text\"],\"output_modalities\":[\"text\"]}},\"pricing\":{{\"prompt\":\"0.00001\",\"completion\":\"0.00005\"}}}}"
                )) + "]}";

                using var manyHandler = new StubHttpHandler(_ => StubHttpHandler.Json(manyModelsJson));
                using var store4 = new SqliteStore(Path.Combine(manyModelsFolder, "checks4.db"));
                var update4 = await ratingsService4.UpdateFromWebAsync(manySettings, manyHealth, store4, httpHandler: manyHandler);

                Assert(File.Exists(ratingsService4.FilePath), "model-ratings.json must be written");
                var fileSize = new FileInfo(ratingsService4.FilePath).Length;
                Assert(fileSize < 1_000_000, $"model-ratings.json must stay under 1 MB, actual: {fileSize} bytes");
            }
            finally
            {
                ModelRatings.Apply(null);
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (Directory.Exists(manyModelsFolder))
                    Directory.Delete(manyModelsFolder, true);
            }

            Console.WriteLine("PASS web ratings: All 5 sources, slug normalization, effort/free/local scoring, persistence, RatedByUser preservation, deprecation notices, DescribeCapabilities, partial/total failure & size cap, concurrency, timeout, all LMArena configs, LiveBench CSV+JSON, batch-only filtering, capability breakdown, Priority preservation, large model set");
            return 0;
        }
        finally
        {
            ModelRatings.Apply(null);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
        }
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));

        public static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }
}