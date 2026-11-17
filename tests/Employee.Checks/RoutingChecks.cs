using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Employee.Core;
using Employee.Infrastructure;
using static Harness;

// Model routing, ratings, learning, providers and fallback.
static class RoutingChecks
{
    public static async Task Run()
    {
        await Check("agy native installation is discovered without PATH and explicit paths remain authoritative", () =>
        {
            var local = Path.Combine(root, Guid.NewGuid().ToString("N"));
            var binary = Path.Combine(local, "agy", "bin", "agy.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
            File.WriteAllText(binary, "fixture");
            var search = new CliSearchContext([], local);
            Assert(ManagerSettings.DefaultProviderIds.Contains("antigravity"));
            Assert(ProcessRunner.ResolveCli("agy", "antigravity", search)?.Executable == binary);
            Assert(ProcessRunner.ResolveCli(Path.Combine(local, "missing.exe"), "antigravity", search) == null);
            return Task.CompletedTask;
        });
        await Check("Model availability diagnostics are model-scoped; successful text and coding errors never trigger fallback", () =>
        {
            foreach (var message in new[] { "The model 'missing' does not exist", "Model sonnet is not supported for this account", "Model gemini is overloaded", "You do not have access to the model opus", "Model luna is not available" })
            {
                Assert(ProviderErrors.Classify(1, message) == FailureKind.ModelUnavailable, message);
                Assert(ProviderErrors.Classify(0, message) == FailureKind.None, "Successful generated text is not an error");
            }
            foreach (var message in new[] { "fetch failed", "ECONNRESET", "overloaded_error", "502 Bad Gateway" })
                Assert(ProviderErrors.Classify(1, message) == FailureKind.Unavailable, message);
            Assert(ProviderErrors.Classify(1, "Compilation failed: cannot convert CustomerModel to string") == FailureKind.TaskError);
            return Task.CompletedTask;
        });
        await Check("An unavailable model falls back within the same AI without spending the single work attempt", async () =>
        {
            using var f = New(textOnly: true);
            f.Settings.MaxAttempts = 1;
            var config = f.Settings.Providers.Single(p => p.Id == f.First.Id);
            config.Models.Add(new("backup", Enum.GetValues<WorkKind>()));
            f.First.Response = request => request.ModelId == "a"
                ? new(ProviderErrors.Classify(1, "The model 'a' does not exist"), "The model 'a' does not exist")
                : new(FailureKind.None, "done");
            await f.Manager.RunAsync(f.Work, f.Settings, f.Health, new(f.First.Id, "a", "user"));
            Assert(f.First.Calls == 2 && f.Second.Calls == 0 && f.Work.ModelId == "backup", f.Work.Notes);
            Assert(f.Work.State == WorkState.NeedsReview && f.Work.WorkAttempts == 1 && f.Work.ProviderWaits == 1);
            Assert(config.CooldownUntil == null, "One bad model must not cool down its entire provider");
        });
        await Check("All unavailable models are tried once before another AI finishes the task", async () =>
        {
            using var f = New(textOnly: true);
            f.Settings.MaxAttempts = 1;
            var config = f.Settings.Providers.Single(p => p.Id == f.First.Id);
            config.Models.Add(new("backup", Enum.GetValues<WorkKind>()));
            f.First.Result = new(FailureKind.ModelUnavailable, "Model is not available");
            await f.Manager.RunAsync(f.Work, f.Settings, f.Health, new(f.First.Id, "a", "user"));
            Assert(f.First.Calls == 2 && f.Second.Calls == 1 && f.Work.State == WorkState.NeedsReview, f.Work.Notes);
            Assert(f.Work.WorkAttempts == 1 && f.Work.ProviderWaits == 2 && f.Work.Switches == 2);
        });
        await Check("A provider detection exception does not stop redetection and fallback to a healthy AI", async () =>
        {
            using var f = New(textOnly: true);
            f.First.Result = new(FailureKind.Unavailable, "fetch failed");
            f.First.DetectionError = new IOException("runtime unreachable");
            await f.Manager.RunAsync(f.Work, f.Settings, [f.Health[0]]);
            Assert(f.Second.Calls == 1 && f.Work.State == WorkState.NeedsReview, f.Work.Notes);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            f.First.DetectionError = new OperationCanceledException(cancelled.Token);
            try
            {
                await f.Manager.DetectAsync(f.Settings, cancelled.Token);
                throw new Exception("User cancellation must propagate");
            }
            catch (OperationCanceledException) { }
        });
        await Check("Unqualified local model excluded; per-kind benchmark qualifies only tested kind", () =>
        {
            var router = new ModelRouter();
            var s = new ManagerSettings();
            var p = s.Providers.Single(p => p.Id == "ollama");
            p.Enabled = true;
            p.Models.Add(new("local", [WorkKind.Classification, WorkKind.Extraction]));
            var w = new WorkItem
            {
                Kind = WorkKind.Classification
            };
            ProviderHealth[] health = [new("ollama", true, true, false, "")];
            Assert(router.Select(w, s, health, []) == null);
            Evaluation[] evaluations = [new("local", WorkKind.Classification, 3, 3, 100, DateTimeOffset.UtcNow)];
            Assert(router.Select(w, s, health, evaluations)?.ModelId == "local");
            w.Kind = WorkKind.Extraction;
            Assert(router.Select(w, s, health, evaluations) == null);
            return Task.CompletedTask;
        });
        await Check("Automatic routing prefers an exact discovered model over CLI default", () =>
        {
            var router = new ModelRouter();
            var settings = new ManagerSettings
            {
                Providers = [new ProviderSettings
                {
                    Id = "codex",
                    Enabled = true,
                    Models = [new("", [WorkKind.Coding]), new("gpt-exact", [WorkKind.Coding])]
                }

                ]
            };
            var work = new WorkItem
            {
                Kind = WorkKind.Coding,
                Access = AccessLevel.ReadProject
            };
            var selected = router.Select(work, settings, [new("codex", true, true, true, "")], []);
            Assert(selected?.ModelId == "gpt-exact", "Automatic routing selected the CLI default while an exact model was available.");
            return Task.CompletedTask;
        });
        await Check("Automatic routing picks the least-usage model that meets the quality bar", () =>
        {
            var router = new ModelRouter();
            var all = Enum.GetValues<WorkKind>();
            var settings = new ManagerSettings
            {
                Providers = [new ProviderSettings
                {
                    Id = "codex",
                    Enabled = true,
                    AllowProjectWrite = true,
                    Models = [new("", all), new("gpt-6-astra", all), new("gpt-6-sol", all), new("gpt-6-luna", all)]
                }, new ProviderSettings
                {
                    Id = "claude",
                    Enabled = true,
                    Models = [new("opus", all), new("sonnet", all)]
                }

                ]
            };
            ProviderHealth[] health = [new("codex", true, true, true, ""), new("claude", true, true, true, "")];
            var light = router.Select(new WorkItem { Kind = WorkKind.Research, Access = AccessLevel.ReadProject, Effort = WorkEffort.Light }, settings, health, []);
            Assert(light?.ModelId == "gpt-6-luna", "Light work should use the cheapest adequate model, got " + light?.ModelId);
            var routine = router.Select(new WorkItem { Kind = WorkKind.Coding, Access = AccessLevel.ReadProject, Request = "Add a pause button to the HUD" }, settings, health, []);
            Assert(routine?.ModelId == "gpt-6-luna", "Routine coding should use a cheap model that is good enough, got " + routine?.ModelId);
            var hard = new WorkItem
            {
                Kind = WorkKind.Coding,
                Access = AccessLevel.ReadProject,
                Difficulty = 95
            };
            Assert(hard.DifficultyScore == 95 && hard.RoutingEffort == WorkEffort.Heavy);
            Assert(router.Select(hard, settings, health, [])?.ModelId is "opus" or "sonnet", "Difficulty 95 coding should use a strong coding model");
            var image = new WorkItem
            {
                Kind = WorkKind.Coding,
                Access = AccessLevel.ReadProject,
                Request = "Generate images for the main menu buttons"
            };
            var imageRoute = router.Select(image, settings, health, []);
            Assert(image.Capability == WorkCapability.ImageGeneration && imageRoute?.ProviderId == "codex", "Image generation must go to a worker with an image tool, got " + imageRoute?.ProviderId);
            // No worker with an image tool: the step waits rather than run a model that cannot do it.
            settings.Providers[0].Enabled = false;
            Assert(router.Select(image, settings, health, []) == null && router.ExplainUnavailable(image, settings, health, []).Contains("no image tool"), "Image generation without an image tool must wait");
            Assert(router.Select(image, settings, health, [], explicitChoice: new("claude", "sonnet", "user"))?.ModelId == "sonnet", "A model the user picked still runs");
            settings.Providers[0].Enabled = true;
            // A quality floor (escalation) keeps weaker models out.
            var floor = router.QualityOf("claude", "sonnet", WorkCapability.Research, settings);
            var lightWork = new WorkItem { Kind = WorkKind.Research, Access = AccessLevel.ReadProject, Effort = WorkEffort.Light };
            var floored = router.Select(lightWork, settings, health, [], new HashSet<string> { "claude|sonnet" }, minQuality: floor);
            Assert(floored != null && floored.ModelId != "gpt-6-luna" && router.QualityOf(floored.ProviderId, floored.ModelId, WorkCapability.Research, settings) >= floor, "A quality floor must exclude weaker models, got " + floored?.ModelId);
            // "mini" must not match inside "gemini".
            Assert(new ModelInfo("gemini-3-pro", all).EffectiveCostRank != new ModelInfo("gpt-5-mini", all).EffectiveCostRank && new ModelInfo("gpt-5-mini", all).RatingSource.StartsWith("Built-in") && new ModelInfo("gemini-3-flash", all).EffectiveCostRank == 8 && new ModelInfo("minimax-m2", all).RatingSource.StartsWith("Unrated"), "Rating patterns must match whole name parts");
            settings.Providers[1].Models[0] = settings.Providers[1].Models[0] with
            {
                Quality = 80,
                CostRank = 60,
                RatedByUser = true
            };
            Assert(router.Select(hard, settings, health, [])?.ModelId is { } afterRating && afterRating != "opus", "A user rating must override the built-in rating");
            Assert(new WorkItem { Kind = WorkKind.Coding, Effort = WorkEffort.Light, Difficulty = 90 }.DifficultyScore < WorkComplexity.LightBelow, "An explicit user effort must bound the planner's difficulty");
            return Task.CompletedTask;
        });
        await Check("Game/app capabilities split routing where models really differ", () =>
        {
            // Every built-in rating row covers every capability.
            foreach (var id in new[]
            {
                "gpt-6.1-astra",
                "gpt-6.1-sol",
                "gpt-6.1-terra",
                "gpt-6.1-luna",
                "gpt-6-astra",
                "gpt-6-sol",
                "gpt-6-terra",
                "gpt-6-luna",
                "gpt-5.6-sol",
                "gpt-5.6-terra",
                "gpt-5.6-luna",
                "fable",
                "opus",
                "sonnet",
                "haiku",
                "glm-x",
                "x-flash",
                "x-mini",
                "x-nano"
            }

            )
                foreach (var capability in Enum.GetValues<WorkCapability>())
                    ModelRatings.QualityFor("codex", new(id, []), capability);
            WorkItem Step(string request, WorkKind kind = WorkKind.Coding) => new()
            {
                Kind = kind,
                Access = AccessLevel.ReadProject,
                Request = request
            };
            Assert(Step("Fix crash when loading the save file").Capability == WorkCapability.Debugging);
            Assert(Step("Write a water shader with refraction").Capability == WorkCapability.GraphicsMath);
            Assert(Step("Write unit tests for the inventory").Capability == WorkCapability.Testing);
            Assert(Step("Viết lời thoại cho NPC thợ rèn", WorkKind.Content).Capability == WorkCapability.CreativeWriting);
            Assert(Step("Cân bằng drop rate của boss", WorkKind.Content).Capability == WorkCapability.GameDesign);
            Assert(Step("Thêm dịch vụ lưu điểm").Capability == WorkCapability.Coding, "'dịch vụ' must not be read as translation");
            var hinted = Step("Update the inventory");
            hinted.CapabilityHint = "Performance";
            Assert(hinted.Capability == WorkCapability.Performance);
            var shot = Step("Make it look like this");
            shot.Attachments.Add(new("mockup.png", [1]));
            Assert(shot.Capability == WorkCapability.ImageUnderstanding);
            var router = new ModelRouter();
            var all = Enum.GetValues<WorkKind>();
            var settings = new ManagerSettings
            {
                Providers = [new ProviderSettings
                {
                    Id = "codex",
                    Enabled = true,
                    Models = [new("gpt-6-astra", all), new("gpt-6-sol", all), new("gpt-6-luna", all)]
                }, new ProviderSettings
                {
                    Id = "claude",
                    Enabled = true,
                    Models = [new("opus", all), new("sonnet", all)]
                }

                ]
            };
            ProviderHealth[] health = [new("codex", true, true, true, ""), new("claude", true, true, true, "")];
            WorkItem Rated(string request, int difficulty)
            {
                var w = Step(request);
                w.Difficulty = difficulty;
                return w;
            }

            Assert(router.Select(Rated("Write unit tests for the inventory", 70), settings, health, [])?.ModelId == "gpt-6-luna", "Tests are fine on a cheap model");
            Assert(router.Select(Rated("Fix crash when loading the save file", 70), settings, health, [])?.ModelId == "sonnet", "Same difficulty debugging needs a stronger model");
            Assert(router.Select(Rated("Redesign the main menu UI layout", 80), settings, health, [])?.ModelId == "sonnet", "UI work at 80 should prefer the stronger UI model at equal usage");
            Assert(router.Select(shot, settings, health, [])?.ProviderId == "codex", "Only workers that receive attachments can read images");
            return Task.CompletedTask;
        });
        await Check("Permission and model gates remain enforced; historical cooldown does not block routing", () =>
        {
            using var f = New();
            var router = new ModelRouter();
            f.Work.Access = AccessLevel.WriteProject;
            Assert(router.Select(f.Work, f.Settings, f.Health, []) == null);
            f.Settings.Providers[0].AllowProjectWrite = true;
            Assert(router.Select(f.Work, f.Settings, f.Health, []) != null);
            f.Settings.Providers[0].Models[0] = f.Settings.Providers[0].Models[0] with
            {
                Blocked = true
            };
            Assert(router.Select(f.Work, f.Settings, f.Health, []) == null);
            f.Settings.Providers[0].Models[0] = f.Settings.Providers[0].Models[0] with
            {
                Blocked = false
            };
            var h = f.Health.Select(h => h with { CooldownUntil = DateTimeOffset.UtcNow.AddMinutes(1) }).ToArray();
            Assert(router.Select(f.Work, f.Settings, h, []) != null, "Historical health cooldown must not prevent a fresh request");
            f.Settings.Providers[0].Enabled = false;
            Assert(router.Select(f.Work, f.Settings, h, []) == null, "Disabled providers remain excluded");
            // Seen live: a read-only ZapBop export preparation that mentioned "export" and "integration" got
            // no model at all ("no eligible model") and the task stopped.
            f.Settings.Providers[0].Enabled = true;
            f.Settings.Providers[0].Models.Add(new("extract", [WorkKind.Extraction]));
            var prep = new WorkItem { ProjectId = f.Work.ProjectId, Kind = WorkKind.Extraction, Access = AccessLevel.ReadProject, Request = "Input preparation for ZapBop export. Match each project to a cue. Return only {\"toolInputs\":[...]} for the projects to export. Also give the mapping so the integration step can add it." };
            Assert(TaskIntent.RequiresImplementation(prep.Request) && router.Select(prep, f.Settings, h, []) != null, "A read-only extraction is routed even when its text sounds like implementation");
            return Task.CompletedTask;
        });
        await Check("AI CLIs turned off: no CLI is routed or called, switching to one is refused", async () =>
        {
            using var f = New(textOnly: true);
            f.Settings.UseAiCli = false;
            f.Store.SaveSettings(f.Settings);
            var settings = f.Store.LoadSettings();
            Assert(!settings.UseAiCli, "The setting is persisted");
            await f.Manager.RunAsync(f.Work, settings, f.Health);
            Assert(f.First.Calls == 0 && f.Second.Calls == 0, $"No CLI may be called: first={f.First.Calls} second={f.Second.Calls}");
            Assert(f.Work.State == WorkState.WaitingForProvider && f.Work.Notes.Contains("AI CLIs"), "The task waits with a clear reason: " + f.Work.Notes);
            // Detection must not start a CLI either, even if health was reported earlier.
            var detected = await f.Manager.DetectAsync(settings);
            Assert(detected.Count == 2 && detected.All(h => h.ExcludedBySetting), "CLI health is reported as excluded without calling the CLI");
            Assert(new ModelRouter().Select(f.Work, settings, detected, []) == null, "Excluded health never routes");
            try
            {
                await f.Manager.SwitchAsync(f.Work, settings, detected, new(f.First.Id, "a", "manual"));
                throw new Exception("Switch AI to a CLI must be refused while CLIs are off");
            }
            catch (InvalidOperationException error)
            {
                Assert(error.Message.Contains("AI CLIs are turned off"), error.Message);
            }
            Assert(f.First.Calls == 0, "Switch AI to a CLI is refused before calling it");
        });
        await Check("AI CLIs turned off: stale CLI health does not route, local AI stays available", () =>
        {
            var all = Enum.GetValues<WorkKind>();
            var settings = new ManagerSettings
            {
                UseAiCli = false,
                Providers = [new ProviderSettings
                {
                    Id = "codex",
                    Enabled = true,
                    Models = [new("a", all)]
                }, new ProviderSettings
                {
                    Id = "ollama",
                    Enabled = true,
                    Models = [new("local", [WorkKind.Classification])]
                }

                ]
            };
            ProviderHealth[] health = [new("codex", true, true, true, ""), new("ollama", true, true, false, "")];
            var work = new WorkItem
            {
                Kind = WorkKind.Classification,
                Access = AccessLevel.TextOnly
            };
            var router = new ModelRouter();
            Assert(router.Select(work, settings, health, []) == null, "Codex is not routed while CLIs are off");
            Evaluation[] evaluations = [new("local", WorkKind.Classification, 3, 3, 100, DateTimeOffset.UtcNow)];
            Assert(router.Select(work, settings, health, evaluations)?.ProviderId == "ollama", "Local AI is unaffected by the CLI setting");
            Assert(router.ExplainUnavailable(work, settings, health, []).Contains("AI CLIs are turned off"), "The reason explains the setting");
            settings.UseAiCli = true;
            var flagged = new[] { new ProviderHealth("codex", true, true, true, "") { ExcludedBySetting = true } };
            Assert(ModelRouter.Usable(flagged, settings).Count == 0, "An excluded CLI stays excluded even if the setting is back on until it is detected again");
            return Task.CompletedTask;
        });
        await Check("Manual mode requires explicit route and never falls back", async () =>
        {
            using var f = New();
            f.Settings.Mode = RoutingMode.Manual;
            await f.Manager.RunAsync(f.Work, f.Settings, f.Health);
            Assert(f.First.Calls == 0 && f.Work.State == WorkState.WaitingForProvider);
            f.First.Result = new(FailureKind.RateLimit, "quota exceeded");
            await f.Manager.RunAsync(f.Work, f.Settings, f.Health, new("codex", "a", "manual"));
            Assert(f.Second.Calls == 0 && f.Work.State == WorkState.WaitingForProvider);
        });
        await Check("Automatic fallback, checkpoint, no loop and bounded execution", async () =>
        {
            using var f = New(textOnly: true);
            f.First.Result = new(FailureKind.RateLimit, "quota exceeded");
            f.Second.Result = new(FailureKind.Authentication, "not logged in");
            await f.Manager.RunAsync(f.Work, f.Settings, f.Health);
            Assert(f.First.Calls == 1 && f.Second.Calls == 1);
            Assert(f.Work.State == WorkState.WaitingForProvider && f.Work.Switches == 1);
            Assert(f.Store.LatestHandoff(f.Work.Id) != null);
        });
        await Check("When the AI list the run started with has no other AI, it is checked again before giving up", async () =>
        {
            using var f = New(textOnly: true);
            f.First.Result = new(FailureKind.RateLimit, "You've hit your usage limit.");
            // The second AI was not detected when the run started (stale list); detection now finds it.
            await f.Manager.RunAsync(f.Work, f.Settings, [f.Health[0]]);
            Assert(f.First.Calls == 1 && f.Second.Calls == 1 && f.Work.Switches == 1, $"first {f.First.Calls}, second {f.Second.Calls}, switches {f.Work.Switches}");
            Assert(f.Store.Logs(f.Work.Id).Any(l => l.Event == "ProvidersRedetected" && l.Message.Contains("gemini")), "The re-check is logged");
        });
        await Check("A provider that cannot take the prompt is skipped without spending attempts or switches", async () =>
        {
            using var f = New(textOnly: true);
            f.Store.SaveSettings(f.Settings);
            f.First.Result = new(FailureKind.InputTooLarge, "", "prompt exceeds command-line limit");
            await f.Manager.RunAsync(f.Work, f.Settings, f.Health);
            Assert(f.First.Calls == 1 && f.Second.Calls == 1, "The next provider must run");
            Assert(f.Work.Switches == 0 && f.Work.Attempts == 1, "Nothing ran on the first provider, so no switch or attempt is charged");
            Assert(f.Settings.Providers.Single(p => p.Id == f.First.Id).CooldownUntil == null, "The provider stays usable for other steps");
            var verified = f.Store.LoadSettings().Providers.Single(p => p.Id == f.Second.Id).Models.Single(m => m.Id == "b");
            Assert(verified.AccountAvailabilityVerified, "A successful run proves account access to the exact model");
        });
        await Check("A pinned provider is requested despite stale quota history and falls back on a fresh failure", async () =>
        {
            using var f = New(textOnly: true);
            var pin = new Route(f.First.Id, "", "Automatic model selection", AutoModel: true);
            f.Work.PreferredRoute = pin;
            f.Work.RouteIsAdvisory = false;
            f.First.Result = new(FailureKind.RateLimit, "quota exceeded now");
            var cooling = f.Health.Select(h => h.Id == f.First.Id ? h with { CooldownUntil = DateTimeOffset.UtcNow.AddHours(1) } : h).ToArray();
            await f.Manager.RunAsync(f.Work, f.Settings, cooling, pin, true);
            Assert(f.First.Calls == 1 && f.Second.Calls == 1, "The pinned provider receives a fresh request; its current quota failure triggers fallback");
            using var semi = New(textOnly: true);
            semi.First.Result = new(FailureKind.RateLimit, "quota exceeded now");
            semi.Settings.Mode = RoutingMode.SemiAutomatic;
            semi.Work.PreferredRoute = pin;
            semi.Work.RouteIsAdvisory = false;
            await semi.Manager.RunAsync(semi.Work, semi.Settings, semi.Health.Select(h => h.Id == semi.First.Id ? h with { CooldownUntil = DateTimeOffset.UtcNow.AddHours(1) } : h).ToArray(), pin, true);
            Assert(semi.First.Calls == 1 && semi.Second.Calls == 0 && semi.Work.State == WorkState.AwaitingApproval, "SemiAutomatic requests again but requires approval to switch after a fresh failure");
        });
        await Check("One routing decision: usable user choice wins, otherwise ratings decide; planner hints never decide", () =>
        {
            var all = Enum.GetValues<WorkKind>();
            var settings = new ManagerSettings
            {
                Providers = [new ProviderSettings
                {
                    Id = "codex",
                    Enabled = true,
                    Models = [new("gpt-6-luna", all), new("gpt-6-sol", all)]
                }, new ProviderSettings
                {
                    Id = "claude",
                    Enabled = true,
                    Models = [new("sonnet", all)]
                }

                ]
            };
            ProviderHealth[] health = [new("codex", true, true, true, ""), new("claude", true, true, true, "")];
            var router = new ModelRouter();
            var work = new WorkItem
            {
                Kind = WorkKind.Coding,
                Access = AccessLevel.ReadProject,
                Request = "Add a pause button"
            };
            var pick = new Route("claude", "sonnet", "user");
            Assert(router.Choose(work, settings, health, [], null, pick)?.ProviderId == "claude", "A usable user choice is honoured");
            settings.Providers[1].CooldownUntil = DateTimeOffset.UtcNow.AddHours(1);
            Assert(router.Choose(work, settings, health, [], null, pick)?.ProviderId == "claude", "Historical quota must not block the user choice from receiving a fresh request");
            var fallback = router.Choose(work, settings, health, [], new HashSet<string> { "claude" }, pick);
            Assert(fallback?.ProviderId == "codex" && fallback.Reason.Contains("not usable now"), "A provider excluded after a fresh failure falls back to ratings");
            settings.Providers[1].CooldownUntil = null;
            var hinted = new WorkItem
            {
                Kind = WorkKind.Coding,
                Access = AccessLevel.ReadProject,
                Request = "Add a pause button",
                RouteIsAdvisory = true
            };
            Assert(router.Choose(hinted, settings, health, [], null, pick)?.ModelId == "gpt-6-luna", "A planner hint does not override the rating-based choice");
            settings.Mode = RoutingMode.Manual;
            Assert(router.Choose(work, settings, health, []) == null && router.Choose(work, settings, health, [], null, pick)?.ProviderId == "claude", "Manual mode runs only the user's choice");
            return Task.CompletedTask;
        });
        await Check("No AI switch limit: fallback keeps going while any AI is usable, and provider waits never use up attempts", async () =>
        {
            using var f = New(textOnly: true);
            f.Settings.MaxSwitches = 0;
            f.Settings.MaxAttempts = 1;
            f.First.Result = new(FailureKind.RateLimit, "You've hit your usage limit. Try again at 11:14 PM.");
            await f.Manager.RunAsync(f.Work, f.Settings, f.Health);
            Assert(f.First.Calls == 1 && f.Second.Calls == 1 && f.Work.State == WorkState.NeedsReview, "Fallback runs although the old switch limit was 0 and only 1 attempt is allowed");
            Assert(f.Work.Attempts == 2 && f.Work.ProviderWaits == 1 && f.Work.WorkAttempts == 1, "The usage-limit call is recorded but is not a work attempt");
            Assert(f.Settings.Providers.Single(p => p.Id == f.First.Id).CooldownUntil == null, "The response must not lock future runs");
            var quotaLog = f.Store.Logs(f.Work.Id).Last(log => log.Event == "ProviderUsageLimit");
            using var quota = JsonDocument.Parse(quotaLog.Message);
            var reset = quota.RootElement.GetProperty("RetryAfter").GetDateTimeOffset().ToLocalTime();
            Assert(quota.RootElement.GetProperty("ResetStatedByProvider").GetBoolean() && reset.Hour == 23 && reset.Minute == 15, "The stated reset time remains in diagnostic history: " + reset);
        });
        await Check("Provider reset times are read from CLI messages", () =>
        {
            var now = new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 4, 10, 0, 0)));
            Assert(ProviderCooldown.RetryAt(FailureKind.RateLimit, "try again at 11:14 PM", now).ToLocalTime().Hour == 23);
            var codexReset = ProviderCooldown.RetryAt(FailureKind.RateLimit, "You've hit your usage limit. Upgrade to Pro, or try again at Nov 10th, 2026 12:34 AM.", new DateTimeOffset(2026, 11, 5, 21, 30, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 5))));
            Assert(codexReset.ToLocalTime().Day == 10 && codexReset.ToLocalTime().Hour == 0 && codexReset.ToLocalTime().Minute >= 34, "A written date (Codex) is read: " + codexReset);
            Assert(ProviderCooldown.RetryAt(FailureKind.RateLimit, "try again at 9:00 AM", now) > now.AddHours(22), "A clock time already passed today means tomorrow");
            Assert(Math.Abs((ProviderCooldown.RetryAt(FailureKind.RateLimit, "Limit reached, resets in 3 hours", now) - now).TotalMinutes - 181) < 1);
            Assert(Math.Abs((ProviderCooldown.RetryAt(FailureKind.Unavailable, "Retry-After: 120", now) - now).TotalMinutes - 3) < 0.1);
            Assert(ProviderCooldown.RetryAt(FailureKind.RateLimit, "quota exceeded", now) == now.AddMinutes(15) && !ProviderCooldown.ResetStated("quota exceeded"), "No stated time uses the default backoff");
            return Task.CompletedTask;
        });
        await Check("A provider that fails to launch is skipped for the step without a cooldown", async () =>
        {
            using var f = New(textOnly: true);
            f.First.Result = new(FailureKind.LaunchFailed, "", "could not be started");
            await f.Manager.RunAsync(f.Work, f.Settings, f.Health);
            Assert(f.Second.Calls == 1 && f.Work.Attempts == 1 && f.Settings.Providers.Single(p => p.Id == f.First.Id).CooldownUntil == null);
        });
        await Check("A model whose safety filter declines a step is swapped for another model, without a cooldown", async () =>
        {
            using var f = New(textOnly: true);
            f.Settings.MaxAttempts = 1;
            const string refusal = "API Error: Opus 5.5's safeguards flagged this message (https://www.anthropic.com/legal/aup). Claude Code can't respond to this message with Opus 5.5. Try rephrasing the request in a new session or change your model.";
            Assert(ProviderErrors.Classify(1, refusal) == FailureKind.ModelRefused, "The refusal is recognised");
            f.First.Result = new(FailureKind.ModelRefused, refusal, "safeguards flagged this message");
            await f.Manager.RunAsync(f.Work, f.Settings, f.Health);
            Assert(f.First.Calls == 1 && f.Second.Calls == 1 && f.Work.State == WorkState.NeedsReview, "Another model finished the step, got " + f.Work.State);
            Assert(f.Work.WorkAttempts == 1 && f.Settings.Providers.Single(p => p.Id == f.First.Id).CooldownUntil == null, "No attempt used up and no cooldown");
        });
        await Check("A cheap model that keeps getting sent back loses to a stronger one; a sent-back step escalates", () =>
        {
            using var f = New();
            var all = Enum.GetValues<WorkKind>();
            var settings = new ManagerSettings
            {
                Providers = [new ProviderSettings
                {
                    Id = "codex",
                    Enabled = true,
                    Models = [new("gpt-6-luna", all), new("gpt-6-sol", all)]
                }, new ProviderSettings
                {
                    Id = "claude",
                    Enabled = true,
                    Models = [new("sonnet", all)]
                }

                ]
            };
            ProviderHealth[] health = [new("codex", true, true, true, ""), new("claude", true, true, true, "")];
            var ui = new WorkItem
            {
                Kind = WorkKind.Coding,
                Access = AccessLevel.ReadProject,
                Request = "Polish the settings screen layout",
                Difficulty = 40
            };
            Assert(ModelRouter.For(f.Store).Select(ui, settings, health, [])?.ModelId == "gpt-6-luna", "Easy UI work starts on the cheap model");
            // Five luna UI results were sent back, one accepted.
            foreach (var i in Enumerable.Range(0, 6))
                f.Store.SaveModelRun(new($"ui{i}", $"ui{i}", "codex", "gpt-6-luna", WorkKind.Coding, FailureKind.None, 1000, false, DateTimeOffset.UtcNow, null, WorkCapability.UiDesign));
            f.Store.RecordRework(Enumerable.Range(0, 5).Select(i => $"ui{i}"));
            f.Store.RecordApproval(["ui5"]);
            var learned = ModelRouter.For(f.Store).Select(ui, settings, health, []);
            Assert(learned?.ModelId != "gpt-6-luna", "After repeated send-backs a stronger model is cheaper overall, got " + learned?.ModelId);
            Assert(ModelRouter.For(f.Store).Select(new WorkItem { Kind = WorkKind.Coding, Access = AccessLevel.ReadProject, Request = "Write unit tests for the inventory", Difficulty = 40 }, settings, health, [])?.ModelId == "gpt-6-luna", "Send-backs on UI do not affect other work types");
            var fresh = new ModelRouter();
            var step = new WorkItem
            {
                Kind = WorkKind.Coding,
                Access = AccessLevel.ReadProject,
                Request = "Write unit tests for the inventory",
                Difficulty = 40
            };
            Assert(fresh.Select(step, settings, health, [])?.ModelId == "gpt-6-luna");
            step.Reworks = 1;
            step.RejectedRoutes = ["codex|gpt-6-luna"];
            var escalated = fresh.Select(step, settings, health, []);
            Assert(escalated?.ModelId != "gpt-6-luna" && escalated!.Reason.Contains("send-back"), "A step sent back moves off the model that missed and raises its bar");
            return Task.CompletedTask;
        });
        await Check("A change request on an unapproved result counts as a miss for the model and escalates the task", async () =>
        {
            using var f = New();
            var flow = UnifiedFor(f);
            await flow.StartAsync(f.Work, f.Settings, f.Health);
            var goal = f.Store.WorkItems().Single(w => w.Id == f.Work.Id);
            Assert(goal.State == WorkState.NeedsReview, "Precondition: a result is waiting for review");
            var step = flow.Steps(goal).Single();
            var route = step.ProviderId + "|" + step.ModelId;
            goal = flow.AddComment(goal, "The layout is still wrong, make the buttons larger", [], continueUpdating: true);
            goal = f.Store.WorkItems().Single(w => w.Id == goal.Id);
            Assert(goal.Reworks == 1 && goal.RejectedRoutes.Contains(route), "The task records the send-back and the route that missed");
            Assert(f.Store.ModelPerformance().Any(h => h.ProviderId == step.ProviderId && h.ModelId == step.ModelId && h.Poor == 1), "The model's result is recorded as a miss");
            Assert(flow.Steps(goal).Any(w => w.Reworks == 1 && w.RejectedRoutes.Contains(route)), "The step that redoes the work inherits the escalation");
        });
        await Check("A change request on a task done with a tool is re-planned with the tool, not handed to a plain AI step", async () =>
        {
            using var f = New();
            var flow = UnifiedFor(f);
            await flow.StartAsync(f.Work, f.Settings, f.Health);
            var goal = f.Store.WorkItems().Single(w => w.Id == f.Work.Id);
            f.Store.SaveWork(new WorkItem { ProjectId = goal.ProjectId, SessionId = goal.SessionId, ParentTaskId = goal.Id, Kind = WorkKind.Content, State = WorkState.Completed, Request = "Generate the animation", Tool = new ToolTaskSpec("pixel", "employee-pixel-art", "generate") });
            goal = flow.AddComment(goal, "The sprite is too big and does not match my sample, fix it", [], continueUpdating: true);
            Assert(!flow.Steps(goal).Any(w => w.Request.StartsWith("Handle only the latest user comment", StringComparison.Ordinal)), "No plain AI step is queued for a tool task's change request");
            Assert(f.Store.Logs(goal.Id).Any(l => l.Event == "TaskUpdateRequested"), "The task is re-planned so the tool step can run again");
        });
        await Check("A step that integrates a tool's generated image goes to an AI that writes files directly", async () =>
        {
            using var f = New();
            var tool = new WorkItem { ProjectId = f.Project.Id, Kind = WorkKind.Content, State = WorkState.Completed, Request = "Generate the animation", Tool = new ToolTaskSpec("pixel", "employee-pixel-art", "generate") };
            f.Store.SaveWork(tool);
            f.Work.Access = AccessLevel.WriteProject;
            f.Work.DependsOn = [tool.Id];
            f.Store.SaveWork(f.Work);
            Assert(Orchestrator.NeedsDirectFileWrites(f.Work, f.Store), "Integrating a tool result needs direct writes");
            foreach (var p in f.Settings.Providers.Where(p => p.Id == f.First.Id || p.Id == f.Second.Id)) p.AllowProjectWrite = true;
            var health = f.Health.Select(h => h with { CanWrite = true }).ToArray();
            await f.Manager.RunAsync(f.Work, f.Settings, health, new Route(f.Second.Id, "b", "user choice"));
            Assert(f.Second.Calls == 0 && f.First.Calls == 1 && f.First.Id == "codex", $"The proposal-only provider was not used, even when chosen: first={f.First.Calls} second={f.Second.Calls} state={f.Work.State} notes={f.Work.Notes}");
            var plain = new WorkItem { ProjectId = f.Project.Id, Kind = WorkKind.Coding, Access = AccessLevel.WriteProject, Request = "Refactor", DependsOn = [] };
            Assert(!Orchestrator.NeedsDirectFileWrites(plain, f.Store), "Ordinary steps keep every provider");
            // Codex out of usage: the other AI integrates with text edits instead of the task stopping.
            f.First.Result = new(FailureKind.RateLimit, "You've hit your usage limit.");
            f.Work.State = WorkState.Queued;
            f.Store.SaveWork(f.Work);
            await f.Manager.RunAsync(f.Work, f.Settings, health);
            Assert(f.Second.Calls == 1 && f.Store.Logs(f.Work.Id).Any(l => l.Event == "IntegrationFallback"), $"Codex out of usage hands the integration on: first={f.First.Calls} second={f.Second.Calls} notes={f.Work.Notes}");
        });
        await Check("A long-running task keeps taking updates: only open steps count against the step limit", async () =>
        {
            using var f = New();
            var flow = UnifiedFor(f);
            await flow.StartAsync(f.Work, f.Settings, f.Health);
            var goal = f.Store.WorkItems().Single(w => w.Id == f.Work.Id);
            var session = flow.SessionFor(goal)!;
            for (var i = flow.Steps(goal).Count; i < session.MaxTasks; i++)
                f.Store.SaveWork(new WorkItem { ProjectId = goal.ProjectId, SessionId = goal.SessionId, ParentTaskId = goal.Id, Kind = WorkKind.Coding, State = WorkState.Completed, Request = "Earlier work " + i });
            foreach (var step in flow.Steps(goal).Where(s => s.State != WorkState.Completed))
            {
                step.State = WorkState.Completed;
                f.Store.SaveWork(step);
            }
            Assert(flow.Steps(goal).Count >= session.MaxTasks, "Precondition: the task already has as many steps as the limit");
            goal = flow.AddComment(goal, "One more change please", [], continueUpdating: true);
            Assert(flow.Steps(goal).Count > session.MaxTasks, "The update is accepted although finished steps fill the old limit");
            Assert(session.MaxTasks == 0 && !session.StepLimitReached(Enumerable.Range(0, 500).Select(_ => new WorkItem { State = WorkState.Queued })), "No step limit by default");
            session.MaxTasks = 10;
            var open = Enumerable.Range(0, session.MaxTasks).Select(_ => new WorkItem { State = WorkState.Queued }).ToArray();
            Assert(session.StepLimitReached(open) && !session.StepLimitReached(open.Take(session.MaxTasks - 1)), "A chosen step limit still bounds open steps");
        });
        await Check("Run / resume retries a failed tool step, whatever stopped it", async () =>
        {
            using var f = New();
            var flow = UnifiedFor(f);
            await flow.StartAsync(f.Work, f.Settings, f.Health);
            var goal = f.Store.WorkItems().Single(w => w.Id == f.Work.Id);
            var tool = new WorkItem { ProjectId = goal.ProjectId, SessionId = goal.SessionId, ParentTaskId = goal.Id, Kind = WorkKind.Content, State = WorkState.Failed, Request = "Animate the cat", Tool = new ToolTaskSpec("pixel", "employee-pixel-art", "generate"), ToolRunId = "never-sent", Notes = "V5 tool step: Tool input needs a completed, declared dependency in the same project: t3" };
            f.Store.SaveWork(tool);
            f.Store.SaveDecision(new PendingDecision { SessionId = goal.SessionId!, WorkId = tool.Id, Kind = "ToolError" });
            await flow.Engine.RunAsync(goal.SessionId!, f.Settings, f.Health);
            Assert(f.Store.Logs(tool.Id).Any(l => l.Event == "RetryToolStep"), "The failed tool step was retried");
            Assert(f.Store.Decisions(goal.SessionId!).Where(d => d.WorkId == tool.Id && d.Kind == "ToolError").All(d => d.State != DecisionState.Pending || d.At > f.Store.Logs(tool.Id).First(l => l.Event == "RetryToolStep").At), "The old error decision no longer blocks the task");

            // Seen live: the preparation said "blocked: no Mystic PNG exists yet and I may not make media".
            // A retry that reads the same answer fails the same way; the preparation is asked again, told why.
            var prep = new WorkItem { ProjectId = goal.ProjectId, SessionId = goal.SessionId, ParentTaskId = goal.Id, PlanKey = "t8", Kind = WorkKind.Review, Access = AccessLevel.ReadProject, State = WorkState.Completed, Request = "Prepare the Mystic generate input" };
            f.Store.SaveWork(prep);
            var mystic = new WorkItem { ProjectId = goal.ProjectId, SessionId = goal.SessionId, ParentTaskId = goal.Id, PlanKey = "t9", Kind = WorkKind.Content, State = WorkState.Failed, DependsOn = [prep.Id], Request = "Make the Mystic art", Tool = new ToolTaskSpec("pixel", "employee-pixel-art", "generate", null, prep.Id), Notes = "V5 tool step: Input preparation blocked: no Mystic PNG exists yet; this step may not make media." };
            f.Store.SaveWork(mystic);
            await flow.Engine.RunAsync(goal.SessionId!, f.Settings, f.Health);
            Assert(f.Store.Logs(mystic.Id).Any(l => l.Event == "RetryToolStep" && l.Message.Contains("preparing the input again", StringComparison.Ordinal)), "A blocked preparation is prepared again on retry");
            Assert(f.Store.Logs(prep.Id).Any(l => l.Event == StepStatus.Continuing && l.Message.Contains("no Mystic PNG", StringComparison.Ordinal) && l.Message.Contains("not asked to make the media", StringComparison.Ordinal)), "The preparation is told why its answer was wrong");
        });
        await Check("Connected tools require explicit use of a named tool", () =>
        {
            using var f = New();
            f.Store.SaveTool(new ConnectedTool { Id = "pixel-foundry", Name = "Pixel Art Tool", Connector = "pixel" });
            f.Store.SaveTool(new ConnectedTool { Id = "trailerforge", Name = "TrailerForge", Connector = "video" });
            f.Store.SaveTool(new ConnectedTool { Id = "rikerat", Name = "Rikerat", Connector = "marketing" });
            var registry = new ToolSkillRegistry(f.Store, "missing-skills-folder");
            foreach (var phrase in new[] { "dùng pixel tool", "hãy sử dụng Pixel Art Tool", "gọi pixel-foundry", "chạy pixel tool", "invoke pixel tool", "use the pixel art tool", "create with pixel tool" })
                Assert(registry.RequestedTools(phrase).Any(t => t.Id == "pixel-foundry"), phrase);
            Assert(registry.RequestedTools("use video tool").Any(t => t.Id == "trailerforge"), "video alias");
            Assert(registry.RequestedTools("sử dụng marketing tool").Any(t => t.Id == "rikerat"), "marketing alias");
            foreach (var phrase in new[] { "Pixel Art Tool", "compare pixel tool with video tool", "do not use pixel tool", "đừng dùng pixel tool", "for example use pixel tool", "write 'use pixel tool' as an example", "use another tool; pixel tool is mentioned" })
                Assert(registry.RequestedTools(phrase).Count == 0, phrase);
            foreach (var phrase in new[] { "xài pixel art tool", "gọi tool pixel", "dùng công cụ pixel art để vẽ mèo", "use PixelFoundry", "run the pixel generator", "hãy dùng tool vẽ sprite" })
                Assert(registry.RequestedTools(phrase).Any(t => t.Id == "pixel-foundry"), phrase);
            foreach (var phrase in new[] { "dùng TrailerForge", "hãy dùng tool dựng video", "sử dụng công cụ video", "chạy tool render trailer", "use the trailer maker", "invoke video editing tool", "gọi video tool" })
                Assert(registry.RequestedTools(phrase).Any(t => t.Id == "trailerforge"), phrase);
            foreach (var phrase in new[] { "dùng Rikerat", "sử dụng tool marketing", "hãy dùng công cụ marketing", "chạy social scheduler", "use the marketing tool", "gọi tool lên lịch đăng bài", "run Rike Rat" })
                Assert(registry.RequestedTools(phrase).Any(t => t.Id == "rikerat"), phrase);
            // A "dùng" (use) earlier in the request must not be mistaken for "đừng" (don't).
            var both = registry.RequestedTools("dùng video tool rồi dùng pixel tool").Select(t => t.Id).ToArray();
            Assert(both.Contains("trailerforge") && both.Contains("pixel-foundry"), "two commands in one request");
            Assert(registry.RequestedTools("đừng dùng video tool, hãy dùng pixel tool").Select(t => t.Id).SequenceEqual(["pixel-foundry"]), "only the non-negated tool");
            foreach (var phrase in new[] { "video tool", "TrailerForge", "marketing tool is nice", "Rikerat", "pixel art tool", "tôi thích pixel tool", "đừng dùng TrailerForge", "không dùng marketing tool", "for example use video tool", "ví dụ: dùng marketing tool", "write \"dùng pixel tool\" as an example", "vẽ mèo pixel art", "dựng video trailer cho game", "lên lịch đăng bài marketing" })
                Assert(registry.RequestedTools(phrase).Count == 0, phrase);
            f.Store.SaveTool(new ConnectedTool { Id = "zapbop", Name = "ZapBop Music Producer", Connector = "music" });
            foreach (var phrase in new[] { "dùng ZapBop", "hãy dùng tool làm nhạc", "use the music producer tool", "gọi ZapBop Music Producer", "chạy tool xuất nhạc" })
                Assert(registry.RequestedTools(phrase).Select(t => t.Id).SequenceEqual(["zapbop"]), phrase);
            foreach (var phrase in new[] { "ZapBop", "đừng dùng ZapBop", "ví dụ: dùng ZapBop", "làm nhạc nền cho game", "music tool is nice" })
                Assert(registry.RequestedTools(phrase).Count == 0, phrase);
            return Task.CompletedTask;
        });
        await Check("ZapBop connector exports saved projects through its bridge, inside its workspace and run folder", async () =>
        {
            using var f = New();
            var baseDir = Path.GetDirectoryName(f.Project.Root)!;
            var appRoot = Path.Combine(baseDir, "app");
            var source = Path.Combine(baseDir, "zapbop");
            var dataRoot = Path.Combine(baseDir, "data");
            Directory.CreateDirectory(Path.Combine(appRoot, "connectors"));
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(appRoot, "connectors", "zapbop_bridge.py"), "# fixture");
            foreach (var name in new[] { "main.py", "pro_engine.py", "audio_engine.py" })
                File.WriteAllText(Path.Combine(source, name), "# fixture");
            var python = Path.Combine(source, "python.exe");
            File.WriteAllText(python, "");
            var song = Path.Combine(f.Project.Root, "song.json");
            File.WriteAllText(song, "{}");

            var skillsFolder = AppContext.BaseDirectory;
            while (skillsFolder != null && !Directory.Exists(Path.Combine(skillsFolder, ".agents", "skills", "employee-music")))
                skillsFolder = Path.GetDirectoryName(skillsFolder);
            Assert(skillsFolder != null, "The ZapBop skill is in the repository");
            var registry = new ToolSkillRegistry(f.Store, Path.Combine(skillsFolder!, ".agents", "skills"));
            Assert(registry.Get("employee-music").Operations.SequenceEqual(ToolExecutionService.Capabilities("music").Select(c => c.Id)) &&!registry.Status.Contains("employee-music"), "The skill matches the implemented operations: " + registry.Status);
            Assert(ToolExecutionService.Capabilities("music").Single(c => c.Id == "export") is { Writes: true, UsesAI: false, Publishes: false }, "Export writes a file but calls no AI");

            var runner = new ZapBopRunner();
            using var service = new ToolExecutionService(f.Store, f.Store, runner, appRoot, dataRoot, _ => new NoopLease());
            var tool = new ConnectedTool { Id = "zapbop", Name = "ZapBop Music Producer", Connector = "music", SourcePath = source, Executable = python, Enabled = true, AllowWrites = true };
            f.Store.SaveTool(tool);

            // Without an allowed workspace nothing is sent to the bridge.
            var blocked = service.Create(tool.Id, "inspect", JsonSerializer.Serialize(new { projectFile = song }));
            Assert(blocked.State == ToolRunState.Draft, "Inspect is read-only and needs no approval");
            await service.ExecuteAsync(blocked.Id);
            blocked = f.Store.ToolRuns().Single(r => r.Id == blocked.Id);
            Assert(blocked.State == ToolRunState.Failed && blocked.Error.Contains("workspace") && runner.Calls.Count == 0, "Missing workspace is reported before running anything");

            tool.WorkspaceRoot = f.Project.Root;
            f.Store.SaveTool(tool);
            var export = service.Create(tool.Id, "export", JsonSerializer.Serialize(new { projectFile = song }));
            Assert(export.State == ToolRunState.WaitingApproval, "Export waits for approval of the exact input");
            var refused = false;
            try { await service.ExecuteAsync(export.Id); } catch (InvalidOperationException) { refused = true; }
            Assert(refused && runner.Calls.Count == 0 && f.Store.ToolRuns().Single(r => r.Id == export.Id).State == ToolRunState.WaitingApproval, "Nothing runs before approval");
            service.Approve(export.Id);
            await service.ExecuteAsync(export.Id);
            export = f.Store.ToolRuns().Single(r => r.Id == export.Id);
            var call = runner.Calls.Single();
            var runFolder = Path.Combine(dataRoot, "artifacts", "tool-runs", export.Id);
            Assert(call.Executable == python && call.Args[0] == "-I" && call.Args.Contains("export") && call.Args[call.Args.IndexOf("--root") + 1] == f.Project.Root && call.Args[call.Args.IndexOf("--output-dir") + 1] == runFolder, "Bridge runs isolated with the workspace and this run's folder: " + string.Join(" ", call.Args));
            Assert(call.Input.Contains("song.json"), "The approved input is passed on stdin");
            var output = System.Text.Json.Nodes.JsonNode.Parse(export.Output)!;
            Assert(export.State == ToolRunState.NeedsReview && output["artifactPath"]?.GetValue<string>() == Path.Combine(runFolder, "song_120bpm.wav") && output["artifactSha256"]?.GetValue<string>()?.Length == 64, "The WAV is recorded with its hash and awaits review: " + export.Error + export.Output);

            // A bridge error is shown as the bridge's last line, and a non-WAV result is rejected.
            runner.Fail = "ValueError: Project file is outside the approved workspace root.";
            var outside = service.Create(tool.Id, "inspect", JsonSerializer.Serialize(new { projectFile = "C:/elsewhere/song.json" }));
            await service.ExecuteAsync(outside.Id);
            outside = f.Store.ToolRuns().Single(r => r.Id == outside.Id);
            Assert(outside.State == ToolRunState.Failed && outside.Error == "ZapBop: " + runner.Fail, "Bridge error is reported: " + outside.Error);
            runner.Fail = null;
            runner.WriteWav = false;
            var bad = service.Create(tool.Id, "export", JsonSerializer.Serialize(new { projectFile = song }));
            service.Approve(bad.Id);
            await service.ExecuteAsync(bad.Id);
            Assert(f.Store.ToolRuns().Single(r => r.Id == bad.Id) is { State: ToolRunState.Failed } failed && failed.Error.Contains("did not produce a WAV"), "A missing WAV is not reported as success");

            // ZapBop's API: sound effects and renders without AI; composing uses an AI CLI and so needs model calls.
            runner.WriteWav = true;
            Assert(ToolExecutionService.Capabilities("music").Single(c => c.Id == "sfx") is { Writes: true, UsesAI: false } && ToolExecutionService.Capabilities("music").Single(c => c.Id == "compose") is { Writes: true, UsesAI: true }, "Only compose calls AI");
            var sfx = service.Create(tool.Id, "sfx", JsonSerializer.Serialize(new { kind = "Coin", name = "coin_pickup" }));
            service.Approve(sfx.Id);
            await service.ExecuteAsync(sfx.Id);
            sfx = f.Store.ToolRuns().Single(r => r.Id == sfx.Id);
            var sfxCall = runner.Calls.Last();
            var sfxFolder = Path.Combine(dataRoot, "artifacts", "tool-runs", sfx.Id);
            Assert(sfx.State == ToolRunState.NeedsReview && sfxCall.Args.Contains("sfx") && sfxCall.Args[sfxCall.Args.IndexOf("--output-dir") + 1] == sfxFolder && sfxCall.Args.Contains("--artifacts-root")
                && System.Text.Json.Nodes.JsonNode.Parse(sfx.Output)!["artifactSha256"]?.GetValue<string>()?.Length == 64, "A sound effect is made in its own run folder and hashed: " + sfx.Error + sfx.Output);
            runner.ReportedPath = song;
            var escape = service.Create(tool.Id, "render", JsonSerializer.Serialize(new { projectFile = song }));
            service.Approve(escape.Id);
            await service.ExecuteAsync(escape.Id);
            Assert(f.Store.ToolRuns().Single(r => r.Id == escape.Id) is { State: ToolRunState.Failed } leaked && leaked.Error.Contains("run folder"), "A file reported outside the run folder is refused");
            runner.ReportedPath = null;
            runner.ValidWav = false;
            var broken = service.Create(tool.Id, "sfx", JsonSerializer.Serialize(new { kind = "Jump" }));
            service.Approve(broken.Id);
            await service.ExecuteAsync(broken.Id);
            Assert(f.Store.ToolRuns().Single(r => r.Id == broken.Id) is { State: ToolRunState.Failed } invalid && invalid.Error.Contains("not a valid WAV"), "A broken WAV is refused");
            runner.ValidWav = true;
            var compose = service.Create(tool.Id, "compose", JsonSerializer.Serialize(new { description = "calm garden loop", seconds = 30 }));
            service.Approve(compose.Id);
            var composeCalls = runner.Calls.Count;
            try { await service.ExecuteAsync(compose.Id); } catch (InvalidOperationException) { }
            Assert(runner.Calls.Count == composeCalls && f.Store.ToolRuns().Single(r => r.Id == compose.Id).State != ToolRunState.NeedsReview, "Compose does not run while the tool may not call AI");
        });
        await Check("ZapBop configuration, connection status, supported operations and bridge failures are explicit", async () =>
        {
            using var f = New();
            var baseDir = Path.GetDirectoryName(f.Project.Root)!;
            var appRoot = Path.Combine(baseDir, "app");
            var source = Path.Combine(baseDir, "zapbop");
            var dataRoot = Path.Combine(baseDir, "data");
            Directory.CreateDirectory(Path.Combine(appRoot, "connectors"));
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(appRoot, "connectors", "zapbop_bridge.py"), "# fixture");
            foreach (var name in new[] { "main.py", "pro_engine.py", "audio_engine.py" })
                File.WriteAllText(Path.Combine(source, name), "# fixture");
            var python = Path.Combine(source, "python.exe");
            File.WriteAllText(python, "");
            var song = Path.Combine(f.Project.Root, "song.json");
            File.WriteAllText(song, "{}");
            var runner = new ZapBopRunner();
            using var service = new ToolExecutionService(f.Store, f.Store, runner, appRoot, dataRoot, _ => new NoopLease());
            var tool = new ConnectedTool { Id = "zapbop-state", Name = "ZapBop", Connector = "music", SourcePath = source, WorkspaceRoot = f.Project.Root, Executable = python, Enabled = true, AllowWrites = false };
            f.Store.SaveTool(tool);
            var saved = f.Store.Tools().Single(t => t.Id == tool.Id);
            Assert(saved.Connector == "music" && saved.SourcePath == source && saved.WorkspaceRoot == f.Project.Root && saved.Executable == python && saved.Enabled && !saved.AllowWrites, "Connector configuration and write permission persist");
            Assert(ToolExecutionService.Capabilities("music").Select(c => c.Id).SequenceEqual(["status", "inspect", "export", "sfx", "compose", "render"]),"Only implemented ZapBop operations are advertised");

            await service.CheckAsync(saved);
            Assert(saved.Status == "Engine imports OK; WAV render not exercised" && saved.Version == "ZapBop source; numpy 2.0" && saved.CheckedAt != null, "Connection check reports engine import readiness without claiming render verification");
            Assert(runner.Calls.Single().Args.Contains("status") && runner.Calls[0].Input == "{}", "Connection check makes a status-only bridge call");

            var inspect = service.Create(saved.Id, "inspect", JsonSerializer.Serialize(new { projectFile = song }));
            await service.ExecuteAsync(inspect.Id);
            inspect = f.Store.ToolRuns().Single(r => r.Id == inspect.Id);
            Assert(inspect.State == ToolRunState.Completed && runner.Calls.Last().Args.Contains("inspect") && runner.Calls.Last().Input.Contains("song.json"), "Inspect is read-only, executes, and completes without write approval");

            runner.RawOutput = "library diagnostic\n{\"title\":\"Song\",\"bpm\":120}\ntrailing diagnostic\n";
            var noisy = service.Create(saved.Id, "inspect", JsonSerializer.Serialize(new { projectFile = song }));
            await service.ExecuteAsync(noisy.Id);
            noisy = f.Store.ToolRuns().Single(r => r.Id == noisy.Id);
            Assert(noisy.State == ToolRunState.Completed && JsonNode.Parse(noisy.Output)?["title"]?.GetValue<string>() == "Song", "A valid bridge result is retained when diagnostics surround it");
            runner.RawOutput = null;

            runner.Fail = "ModuleNotFoundError: audio engine dependency";
            await service.CheckAsync(saved);
            Assert(saved.Status.StartsWith("Unavailable: ZapBop: ModuleNotFoundError", StringComparison.Ordinal) && saved.CheckedAt != null, "Connection import failure is persisted as unavailable");
            runner.Fail = null;
            runner.RawOutput = "Traceback noise\nnot json\n";
            var invalid = service.Create(saved.Id, "inspect", JsonSerializer.Serialize(new { projectFile = song }));
            await service.ExecuteAsync(invalid.Id);
            Assert(f.Store.ToolRuns().Single(r => r.Id == invalid.Id) is { State: ToolRunState.Failed } invalidRun && invalidRun.Error.Contains("no JSON object"), "Malformed bridge output fails clearly");
            runner.RawOutput = "";
            var empty = service.Create(saved.Id, "inspect", JsonSerializer.Serialize(new { projectFile = song }));
            await service.ExecuteAsync(empty.Id);
            Assert(f.Store.ToolRuns().Single(r => r.Id == empty.Id) is { State: ToolRunState.Failed } emptyRun && emptyRun.Error.Contains("no JSON"), "Empty bridge output fails clearly: " + f.Store.ToolRuns().Single(r => r.Id == empty.Id).Error);
            // The result file wins over stdout, so an engine log line that happens to be JSON cannot replace it.
            runner.RawOutput = "{\"title\":\"Decoy log\"}\n";
            runner.ResultFileJson = "{\"title\":\"Song\",\"bpm\":120}";
            var filed = service.Create(saved.Id, "inspect", JsonSerializer.Serialize(new { projectFile = song }));
            await service.ExecuteAsync(filed.Id);
            filed = f.Store.ToolRuns().Single(r => r.Id == filed.Id);
            Assert(filed.State == ToolRunState.Completed && JsonNode.Parse(filed.Output)?["title"]?.GetValue<string>() == "Song", "The bridge result file is used instead of stdout: " + filed.Output + filed.Error);
            Assert(runner.LastResultFile != null && runner.LastResultFile.StartsWith(Path.Combine(dataRoot, "artifacts", "bridge-results"), StringComparison.OrdinalIgnoreCase) && !File.Exists(runner.LastResultFile), "The result file is manager-owned and removed after reading");
            runner.ResultFileJson = "[1,2]";
            var arrayResult = service.Create(saved.Id, "inspect", JsonSerializer.Serialize(new { projectFile = song }));
            await service.ExecuteAsync(arrayResult.Id);
            Assert(f.Store.ToolRuns().Single(r => r.Id == arrayResult.Id) is { State: ToolRunState.Failed } arrayRun && arrayRun.Error.Contains("not a JSON object"), "A non-object result file is rejected");
            runner.ResultFileJson = null;
            runner.RawOutput = null;
            runner.ValidWav = false;
            saved.AllowWrites = true;
            f.Store.SaveTool(saved);
            var badWav = service.Create(saved.Id, "export", JsonSerializer.Serialize(new { projectFile = song }));
            service.Approve(badWav.Id);
            await service.ExecuteAsync(badWav.Id);
            Assert(f.Store.ToolRuns().Single(r => r.Id == badWav.Id) is { State: ToolRunState.Failed } wavRun && wavRun.Error.Contains("not a valid WAV"), "Invalid RIFF/WAVE header is rejected after approval");
        });
        await Check("Pixel connector follows the job API: queued/running polling, opaque IDs, required PNG, resumable 5xx", async () =>
        {
            using var f = New();
            var dataRoot = Path.Combine(Path.GetDirectoryName(f.Project.Root)!, "data");
            var png = new byte[33];
            new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(png, 0);
            png[19] = 2; // width 2
            png[23] = 3; // height 3
            var image = "data:image/png;base64," + Convert.ToBase64String(png);
            var polls = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
            using var backend = new FakeHttpBackend((method, path) =>
            {
                if (path == "/api/codex/status")
                    return (200, "{\"loggedIn\":true,\"version\":\"2.1\"}");
                if (method == "POST" && path == "/api/codex/jobs")
                    return (200, "{\"id\":\"job_" + (polls.Count + 42) + "\",\"status\":\"queued\"}");
                var id = path.Split('/').Last();
                var count = polls.AddOrUpdate(id, 1, (_, n) => n + 1);
                return id switch
                {
                    "job_42" => count < 2 ? (200, "{\"status\":\"running\"}") : (200, JsonSerializer.Serialize(new { status = "completed", result = new { image } })),
                    "job_43" => (200, "{\"id\":\"job_43\",\"status\":\"completed\",\"result\":{}}"),
                    // Seen live: one status check failed while the job went on and completed.
                    "job_45" => count == 1 ? (503, "{\"error\":\"busy\"}") : (200, JsonSerializer.Serialize(new { status = "completed", result = new { image } })),
                    _ => (503, "{\"error\":\"restarting\"}")
                };
            });
            using var service = new ToolExecutionService(f.Store, f.Store, new NullRunner(), f.Project.Root, dataRoot, _ => new NoopLease());
            var tool = new ConnectedTool { Id = "pixel-contract", Name = "Pixel", Connector = "pixel", Endpoint = backend.Endpoint, Enabled = true, AllowWrites = true, AllowModelCalls = true, TimeoutSeconds = 60 };
            f.Store.SaveTool(tool);
            async Task<ToolRun> Generate()
            {
                var run = service.Create(tool.Id, "generate", JsonSerializer.Serialize(new { action = "create", prompt = "cat" }));
                service.Approve(run.Id);
                await service.ExecuteAsync(run.Id);
                return f.Store.ToolRuns().Single(r => r.Id == run.Id);
            }

            var ok = await Generate();
            var output = JsonNode.Parse(ok.Output)!;
            Assert(ok.State == ToolRunState.NeedsReview && ok.RemoteId == "job_42", "A queued job with a non-GUID ID is polled to completion: " + ok.State + " " + ok.Error);
            Assert(File.Exists(output["artifactPath"]?.GetValue<string>()) && output["imageWidth"]?.GetValue<int>() == 2 && output["imageHeight"]?.GetValue<int>() == 3 && output["result"]?["image"] == null, "The PNG is saved and base64 dropped from output");

            var noImage = await Generate();
            Assert(noImage.State == ToolRunState.Failed && noImage.Error.Contains("without a PNG"), "A completed job without a PNG is not a success: " + noImage.Error);

            var flaky = await Generate();
            Assert(flaky.State == ToolRunState.Interrupted && flaky.RemoteId == "job_44" && flaky.Error.Contains("temporarily unavailable"), "A 5xx while polling keeps the job resumable: " + flaky.State + " " + flaky.Error);
            var oneBadPoll = await Generate();
            Assert(oneBadPoll.State == ToolRunState.NeedsReview && oneBadPoll.RemoteId == "job_45", "One failed status check does not stop a job that then completes: " + oneBadPoll.State + " " + oneBadPoll.Error);
            Assert(ToolExecutionService.IsRemoteJobId("3f2504e0-4f89-11d3-9a0c-0305e82c3301") && !ToolExecutionService.IsRemoteJobId("../jobs") && !ToolExecutionService.IsRemoteJobId(""), "Job IDs are opaque but path-safe");

            await service.CheckAsync(tool);
            Assert(tool.Status == "Backend reachable" && tool.Version == "2.1", "Status reads loggedIn/version: " + tool.Status);
        });
        await Check("Connector HTTP accepts empty success bodies and rejects non-JSON ones clearly", async () =>
        {
            using var f = New();
            var reply = (200, "");
            using var backend = new FakeHttpBackend((_, _) => reply);
            using var service = new ToolExecutionService(f.Store, f.Store, new NullRunner(), f.Project.Root, f.Project.Root, _ => new NoopLease());
            var tool = new ConnectedTool { Id = "pixel-empty", Name = "Pixel", Connector = "pixel", Endpoint = backend.Endpoint, Enabled = true };
            f.Store.SaveTool(tool);
            await service.CheckAsync(tool);
            Assert(tool.Status == "Backend reachable; Codex login is not confirmed" && tool.Version == "Unknown", "An empty 2xx body is an acknowledgement: " + tool.Status);
            reply = (200, "<html>proxy</html>");
            await service.CheckAsync(tool);
            Assert(tool.Status.StartsWith("Unavailable:", StringComparison.Ordinal) && tool.Status.Contains("non-JSON"), "HTML on 2xx is reported as non-JSON: " + tool.Status);
        });
        await Check("TrailerForge export is accepted only with a real video file inside the workspace", async () =>
        {
            using var f = New();
            var baseDir = Path.GetDirectoryName(f.Project.Root)!;
            var appRoot = Path.Combine(baseDir, "app");
            var source = Path.Combine(baseDir, "trailerforge");
            Directory.CreateDirectory(Path.Combine(appRoot, "connectors"));
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(appRoot, "connectors", "trailerforge_bridge.py"), "# fixture");
            File.WriteAllText(Path.Combine(source, "editor_core.py"), "# fixture");
            var python = Path.Combine(source, "python.exe");
            File.WriteAllText(python, "");
            var project = Path.Combine(f.Project.Root, "trailer");
            Directory.CreateDirectory(Path.Combine(project, "export"));
            var mp4 = Path.Combine(project, "export", "trailer.mp4");
            var artifact = mp4;
            var runner = new ResultFileRunner(_ => JsonSerializer.Serialize(new { artifactPath = artifact, projectFolder = project }));
            using var service = new ToolExecutionService(f.Store, f.Store, runner, appRoot, Path.Combine(baseDir, "data"), _ => new NoopLease());
            var tool = new ConnectedTool { Id = "trailerforge", Name = "TrailerForge", Connector = "video", SourcePath = source, WorkspaceRoot = f.Project.Root, Executable = python, Enabled = true, AllowWrites = true };
            f.Store.SaveTool(tool);
            async Task<ToolRun> Export()
            {
                var run = service.Create(tool.Id, "export", JsonSerializer.Serialize(new { projectFolder = project }));
                service.Approve(run.Id);
                await service.ExecuteAsync(run.Id);
                return f.Store.ToolRuns().Single(r => r.Id == run.Id);
            }

            File.WriteAllBytes(mp4, [0, 0, 0, 24, .. "ftypisom"u8.ToArray(), 0, 0, 0, 0]);
            var ok = await Export();
            Assert(ok.State == ToolRunState.NeedsReview && JsonNode.Parse(ok.Output)?["artifactSha256"]?.GetValue<string>()?.Length == 64, "A valid MP4 is hashed and awaits review: " + ok.Error);
            Assert(runner.Calls[0].Contains("--result-file"), "The bridge is given a result file");
            File.WriteAllBytes(mp4, "not a video file"u8.ToArray());
            Assert((await Export()) is { State: ToolRunState.Failed } bad && bad.Error.Contains("not a valid MP4"), "A non-MP4 file is rejected");
            artifact = Path.Combine(baseDir, "elsewhere.mp4");
            Assert((await Export()) is { State: ToolRunState.Failed } outside && outside.Error.Contains("inside the approved workspace"), "An artifact outside the workspace is rejected");
        });
        await Check("A tool task is planned like any task, with the chosen tool and settings fixed on its tool step", () =>
        {
            using var f = New();
            var skillsFolder = AppContext.BaseDirectory;
            while (skillsFolder != null && !Directory.Exists(Path.Combine(skillsFolder, ".agents", "skills", "employee-pixel-art")))
                skillsFolder = Path.GetDirectoryName(skillsFolder);
            Assert(skillsFolder != null, "The repository skills folder is found");
            f.Store.SaveTool(new ConnectedTool { Id = "pixel-foundry", Name = "Pixel Art Tool", Connector = "pixel", Enabled = true, AllowWrites = true, AllowModelCalls = true });
            f.Manager.Skills = new ToolSkillRegistry(f.Store, Path.Combine(skillsFolder!, ".agents", "skills"));
            var flow = UnifiedFor(f);
            var png = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13 };
            var settings = new System.Text.Json.Nodes.JsonObject { ["action"] = "animation", ["image"] = new System.Text.Json.Nodes.JsonObject { ["$attachmentPng"] = "cat.png" }, ["frames"] = 8, ["fps"] = 12, ["direction"] = "SW" };
            var goal = flow.CreateToolTask(new ToolTaskForm(f.Project.Id, "pixel-foundry", "generate", settings, [new WorkAttachment("cat.png", png)], "Replace the cat idle animation"), f.Settings);
            Assert(goal.ToolPreset?.ToolId == "pixel-foundry" && goal.ToolPreset.SkillId == "employee-pixel-art" && goal.PlanningOverride == PlanningPolicy.Always && goal.Access == AccessLevel.WriteProject, "The task is planned in the project, with the tool and settings pinned");
            var saved = f.Store.WorkItems().Single(w => w.Id == goal.Id);
            Assert(saved.ToolPreset?.Input?["fps"]?.GetValue<int>() == 12 && saved.ToolPreset.Input["image"]?["$attachmentPng"]?.GetValue<string>() == "cat.png", "The chosen settings survive saving and reloading");
            Assert(flow.Steps(goal).Count == 0 && goal.Request.Contains("Replace the cat idle animation") && goal.Attachments.Single().Name == "cat.png", "Nothing is decided before planning except the tool");

            // The planner's tool step gets exactly the chosen settings.
            var step = new WorkItem { SessionId = "s", ParentTaskId = goal.Id, Tool = new ToolTaskSpec("pixel-foundry", "employee-pixel-art", "generate") };
            var planned = new System.Text.Json.Nodes.JsonObject { ["action"] = "animation", ["frames"] = 4, ["fps"] = 8, ["prompt"] = "planner text" };
            Assert(ToolTaskExecutor.ApplyPreset(step, step.Tool, goal, planned, [step]) != null, "The preset applies to the only tool step");
            Assert(planned["frames"]!.GetValue<int>() == 8 && planned["fps"]!.GetValue<int>() == 12 && planned["direction"]!.GetValue<string>() == "SW" && planned["prompt"]!.GetValue<string>() == "planner text", "Chosen values win; the planner's other fields stay");
            var edit = new WorkItem { SessionId = "s", ParentTaskId = goal.Id, Tool = new ToolTaskSpec("pixel-foundry", "employee-pixel-art", "generate") };
            var editInput = new System.Text.Json.Nodes.JsonObject { ["action"] = "edit" };
            Assert(ToolTaskExecutor.ApplyPreset(edit, edit.Tool, goal, editInput, [step, edit]) == null && editInput["frames"] == null, "With several tool steps, only the one doing the chosen action gets the settings");
            var sentBack = new WorkItem { SessionId = "s", ParentTaskId = goal.Id, PlanKey = "t7", Request = "Redo the cat art with the prepared input", Tool = new ToolTaskSpec("pixel-foundry", "employee-pixel-art", "generate") };
            var sentBackInput = new System.Text.Json.Nodes.JsonObject();
            Assert(ToolTaskExecutor.ApplyPreset(sentBack, sentBack.Tool, goal, sentBackInput, [step, edit, sentBack]) != null && sentBackInput["fps"]!.GetValue<int>() == 12, "A tool step added by a send-back still gets the chosen settings");

            // Create, then animate: two presets, each on its own step, run in that order.
            var both = flow.CreateToolTask(new ToolTaskForm(f.Project.Id, "pixel-foundry", "generate", new System.Text.Json.Nodes.JsonObject { ["action"] = "animation", ["frames"] = 8, ["fps"] = 10 }, [new WorkAttachment("cat.png", png)], "Cat on its back, idle",
                new System.Text.Json.Nodes.JsonObject { ["action"] = "create", ["styleGuide"] = new System.Text.Json.Nodes.JsonObject { ["$attachmentPng"] = "cat.png" }, ["outputWidth"] = 128 }), f.Settings);
            Assert(both.ToolPresetFirst?.Input?["action"]?.GetValue<string>() == "create" && both.ToolPreset?.Input?["action"]?.GetValue<string>() == "animation", "Both tool calls are pinned");
            var makeStep = new WorkItem { SessionId = "s2", ParentTaskId = both.Id, PlanKey = "t2", Tool = new ToolTaskSpec("pixel-foundry", "employee-pixel-art", "generate") };
            var animateStep = new WorkItem { SessionId = "s2", ParentTaskId = both.Id, PlanKey = "t3", Tool = new ToolTaskSpec("pixel-foundry", "employee-pixel-art", "generate") };
            var made = new System.Text.Json.Nodes.JsonObject();
            var animated = new System.Text.Json.Nodes.JsonObject { ["action"] = "animation" };
            ToolTaskExecutor.ApplyPreset(makeStep, makeStep.Tool, both, made, [makeStep, animateStep]);
            ToolTaskExecutor.ApplyPreset(animateStep, animateStep.Tool, both, animated, [makeStep, animateStep]);
            Assert(made["action"]!.GetValue<string>() == "create" && made["outputWidth"]!.GetValue<int>() == 128 && animated["fps"]!.GetValue<int>() == 10 && animated["styleGuide"] == null, "Each step gets its own settings");
            var unordered = new WorkPlan("p", [
                new PlannedTask("t1", f.Project.Id, "make", WorkKind.Content, AccessLevel.ReadProject, [], Tool: new ToolTaskSpec("pixel-foundry", "employee-pixel-art", "generate", new System.Text.Json.Nodes.JsonObject())),
                new PlannedTask("t2", f.Project.Id, "animate", WorkKind.Content, AccessLevel.ReadProject, [], Tool: new ToolTaskSpec("pixel-foundry", "employee-pixel-art", "generate", new System.Text.Json.Nodes.JsonObject()))]);
            var ordered = PlanningService.OrderPresetSteps(unordered, both);
            Assert(ordered.Tasks[1].DependsOn.SequenceEqual(["t1"]), "The second tool call waits for the first");
            Assert(ordered.Tasks[0].Tool!.Input!["action"]!.GetValue<string>() == "create" && ordered.Tasks[1].Tool!.Input!["action"]!.GetValue<string>() == "animation"
                && ordered.Tasks[1].Tool!.Input!["image"]?["$imageFrom"]?.GetValue<string>() == "t1", "Each call is told what it makes; the animation works from this round's new image");

            // A send-back plans new calls next to the finished ones: the new still-image step still gets the create settings.
            makeStep.State = WorkState.Completed; animateStep.State = WorkState.Completed;
            var remake = new WorkItem { SessionId = "s2", ParentTaskId = both.Id, PlanKey = "t9", Request = "Use Pixel Art Tool to create the cat lying on its back, belly up", Tool = new ToolTaskSpec("pixel-foundry", "employee-pixel-art", "generate") };
            var reanimate = new WorkItem { SessionId = "s2", ParentTaskId = both.Id, PlanKey = "t10", Request = "Use Pixel Art Tool to make the 8-frame spritesheet from t9", Tool = new ToolTaskSpec("pixel-foundry", "employee-pixel-art", "generate") };
            var remade = new System.Text.Json.Nodes.JsonObject();
            var reanimated = new System.Text.Json.Nodes.JsonObject();
            ToolTaskExecutor.ApplyPreset(remake, remake.Tool, both, remade, [makeStep, animateStep, remake, reanimate]);
            ToolTaskExecutor.ApplyPreset(reanimate, reanimate.Tool, both, reanimated, [makeStep, animateStep, remake, reanimate]);
            Assert(remade["action"]!.GetValue<string>() == "create" && reanimated["action"]!.GetValue<string>() == "animation", "In a later round the still image and the animation still get their own settings");
            Assert(ToolTaskExecutor.PresetRole("Dùng Pixel Art Tool, operation generate, để tạo ảnh mèo mướp cam nằm ngửa") == 0
                && ToolTaskExecutor.PresetRole("tạo spritesheet animation 8 khung từ ảnh mèo nằm ngửa ở t9") == 1, "A step's description says which half it is");
            var lone = PlanningService.OrderPresetSteps(new WorkPlan("p", [new PlannedTask("t6", f.Project.Id, "Redo the spritesheet with cleaner pixels", WorkKind.Content, AccessLevel.ReadProject, [], Tool: new ToolTaskSpec("pixel-foundry", "employee-pixel-art", "generate", new System.Text.Json.Nodes.JsonObject()))]), both);
            Assert(lone.Tasks[0].Tool!.Input!["action"]!.GetValue<string>() == "animation", "A lone call in a later round redoes the animation");

            // Redoing only the animation starts from the still image the task already made, not the style sample.
            var stillStep = new WorkItem { SessionId = "s2", ParentTaskId = both.Id, PlanKey = "t2", State = WorkState.Completed, ToolRunId = "run-still", Tool = new ToolTaskSpec("pixel-foundry", "employee-pixel-art", "generate") };
            var redo = new WorkItem { SessionId = "s2", ParentTaskId = both.Id, PlanKey = "t6", Tool = new ToolTaskSpec("pixel-foundry", "employee-pixel-art", "generate") };
            var redoInput = new System.Text.Json.Nodes.JsonObject { ["action"] = "animation" };
            ToolTaskExecutor.ChooseImages(redo, redoInput, [stillStep, redo], [new WorkAttachment("cat.png", png)], w => w.Id == stillStep.Id);
            Assert(redoInput["image"]?["$imageFrom"]?.GetValue<string>() == stillStep.Id && ToolTaskExecutor.FindDependency(redo, stillStep.Id, [stillStep, redo]) == stillStep, "The redone animation uses the task's own still image");

            // A preset request is still completed for the tool: Create new with a style sample sends the
            // sample as the reference image (the tool refuses a style sample without one).
            var header = new byte[33];
            new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(header, 0);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(16), 128);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(20), 128);
            var sample = "data:image/png;base64," + Convert.ToBase64String(header);
            var create = new System.Text.Json.Nodes.JsonObject { ["action"] = "create", ["prompt"] = "cat on its back, 64 frames", ["preserveStyle"] = true, ["styleGuide"] = sample };
            ToolTaskExecutor.FinishPixelRequest(create, PixelToolOptions.Read(new ConnectedTool()), "cat on its back, 64 frames", presetApplied: true, goalText: "make 4 frames");
            Assert(create["image"]?.GetValue<string>() == sample && create["styleGuide"] == null && create["exactFrames"]?.GetValue<bool>() == true, "The sample becomes the create reference and the request is completed");
            Assert(create["frames"]!.GetValue<int>() == 8, "Text in the request does not override settings picked in the form");
            return Task.CompletedTask;
        });
        await Check("Run / resume plans again when the last planning produced no plan", async () =>
        {
            using var f = New();
            var skillsFolder = AppContext.BaseDirectory;
            while (skillsFolder != null && !Directory.Exists(Path.Combine(skillsFolder, ".agents", "skills", "employee-pixel-art")))
                skillsFolder = Path.GetDirectoryName(skillsFolder);
            f.Store.SaveTool(new ConnectedTool { Id = "pixel-foundry", Name = "Pixel Art Tool", Connector = "pixel", Enabled = true, AllowWrites = true, AllowModelCalls = true });
            f.Manager.Skills = new ToolSkillRegistry(f.Store, Path.Combine(skillsFolder!, ".agents", "skills"));
            f.Manager.ToolTasks = null;
            var flow = UnifiedFor(f);
            var goal = flow.CreateToolTask(new ToolTaskForm(f.Project.Id, "pixel-foundry", "generate", new System.Text.Json.Nodes.JsonObject { ["action"] = "create", ["prompt"] = "a cat" }, [], "Draw a cat with Pixel Art Tool"), f.Settings);
            f.First.Result = new(FailureKind.None, "this is not a plan");
            f.Second.Result = new(FailureKind.None, "this is not a plan");
            await flow.StartAsync(goal, f.Settings, f.Health);
            var calls = f.First.Calls + f.Second.Calls;
            goal = f.Store.WorkItems().Single(w => w.Id == goal.Id);
            Assert(calls > 0 && flow.Steps(goal).Count == 0 && goal.State == WorkState.Paused, $"Precondition: planning failed and the task paused ({calls} calls, {flow.Steps(goal).Count} steps, {goal.State}: {goal.Notes})");
            await flow.StartAsync(goal, f.Settings, f.Health);
            Assert(f.First.Calls + f.Second.Calls > calls, "The resume asked the planner again");
            var notes = f.Store.WorkItems().Single(w => w.Id == goal.Id).Notes;
            Assert(notes.Split("could not prepare an executable plan").Length <= 2, "The reason is not repeated in the notes: " + notes);
        });
        await Check("A worker that reports it is blocked is not run again", async () =>
        {
            using var f = New();
            f.Settings.MaxAttempts = 0;
            f.First.Result = new(FailureKind.None, "No Pixel Art Tool here.\nSTEP STATUS: BLOCKED: needs Pixel Art Tool");
            await f.Manager.RunAsync(f.Work, f.Settings, f.Health);
            Assert(f.First.Calls == 1 && f.Work.State == WorkState.NeedsReview && f.Work.Notes.Contains("needs Pixel Art Tool"), "Blocked stops at once, got " + f.First.Calls);
            Assert(StepStatus.Latest(f.Store.Logs(f.Work.Id), StepStatus.GaveUp) == "needs Pixel Art Tool", "The session holds it for review");
        });
        await Check("Learning cannot inflate a model: difficulty-aware, capped, fading, and resettable", () =>
        {
            var all = Enum.GetValues<WorkKind>();
            var settings = new ManagerSettings
            {
                Providers = [new ProviderSettings
                {
                    Id = "codex",
                    Enabled = true,
                    Models = [new("gpt-6-luna", all), new("gpt-6-sol", all)]
                }, new ProviderSettings
                {
                    Id = "claude",
                    Enabled = true,
                    Models = [new("sonnet", all)]
                }

                ]
            };
            ProviderHealth[] health = [new("codex", true, true, true, ""), new("claude", true, true, true, "")];
            ModelOutcome Ui(bool ok, int difficulty, OutcomeSource source = OutcomeSource.Rating, int daysAgo = 0) => new("codex", "gpt-6-luna", WorkCapability.UiDesign, difficulty, ok, source, DateTimeOffset.UtcNow.AddDays(-daysAgo));
            var hard = new WorkItem
            {
                Kind = WorkKind.Coding,
                Access = AccessLevel.ReadProject,
                Request = "Rebuild the main menu UI layout",
                Difficulty = 90
            };
            // Twenty approved easy UI tweaks barely move luna: they were expected.
            var easyWins = Enumerable.Range(0, 20).Select(_ => Ui(true, 30)).ToArray();
            var offset = ModelLearning.Offset(easyWins, 79, DateTimeOffset.UtcNow);
            Assert(offset is> 0 and < 3, "Easy wins add almost nothing, got " + offset);
            Assert(new ModelRouter(null, easyWins).Select(hard, settings, health, [])?.ModelId != "gpt-6-luna", "Easy wins must not hand hard UI work to the cheap model");
            // Hard wins count; misses on hard work cost little, misses on easy work cost a lot.
            Assert(ModelLearning.Offset([Ui(true, 90), Ui(true, 90)], 79, DateTimeOffset.UtcNow) > ModelLearning.Offset([Ui(true, 30), Ui(true, 30)], 79, DateTimeOffset.UtcNow) + 3);
            Assert(ModelLearning.Offset([Ui(false, 90)], 79, DateTimeOffset.UtcNow) > ModelLearning.Offset([Ui(false, 30)], 79, DateTimeOffset.UtcNow) + 3);
            // Capped at ±12 however much evidence piles up.
            Assert(ModelLearning.Offset(Enumerable.Range(0, 200).Select(_ => Ui(true, 100)), 79, DateTimeOffset.UtcNow) <= ModelLearning.MaxOffset);
            Assert(ModelLearning.Offset(Enumerable.Range(0, 200).Select(_ => Ui(false, 10)), 79, DateTimeOffset.UtcNow) >= -ModelLearning.MaxOffset);
            // Old misses fade: four-month-old send-backs weigh far less than fresh ones.
            var misses = Enumerable.Range(0, 5).Select(_ => Ui(false, 40, OutcomeSource.Rework)).ToArray();
            var oldMisses = Enumerable.Range(0, 5).Select(_ => Ui(false, 40, OutcomeSource.Rework, 120)).ToArray();
            Assert(ModelLearning.Offset(oldMisses, 79, DateTimeOffset.UtcNow) > ModelLearning.Offset(misses, 79, DateTimeOffset.UtcNow) / 3, "Old results fade");
            // An approval weighs half of an explicit good rating.
            Assert(ModelLearning.Offset([Ui(true, 80, OutcomeSource.Approval)], 79, DateTimeOffset.UtcNow) < ModelLearning.Offset([Ui(true, 80)], 79, DateTimeOffset.UtcNow));
            // Reset learning ignores everything judged before it.
            Assert(ModelLearning.Offset(misses, 79, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1)) == 0);
            return Task.CompletedTask;
        });
        await Check("Usage level picks the lowest, middle, upper or top model that can do the step", () =>
        {
            var all = Enum.GetValues<WorkKind>();
            var settings = new ManagerSettings
            {
                Providers = [new ProviderSettings
                {
                    Id = "codex",
                    Enabled = true,
                    Models = [..new[]
                    {
                        "gpt-6-astra",
                        "gpt-6.1-sol",
                        "gpt-6-sol",
                        "gpt-6.1-terra",
                        "gpt-6-luna",
                        "gpt-5.6-sol",
                        "gpt-5.6-terra",
                        "gpt-5.6-luna"
                    }.Select(id => new ModelInfo(id, all))]
                }, new ProviderSettings
                {
                    Id = "claude",
                    Enabled = true,
                    Models = [new("fable", all), new("opus", all), new("sonnet", all)]
                }

                ]
            };
            ProviderHealth[] health = [new("codex", true, true, true, ""), new("claude", true, true, true, "")];
            var router = new ModelRouter();
            string? Pick(UsageLevel level, WorkItem step)
            {
                settings.UsageLevel = level;
                return router.Select(step, settings, health, [])?.ModelId;
            }

            var step = new WorkItem
            {
                Kind = WorkKind.Coding,
                Access = AccessLevel.ReadProject,
                Request = "Implement inventory sorting",
                Difficulty = 50
            };
            Assert(Pick(UsageLevel.Low, step) == "gpt-5.6-luna", "Low takes the lowest-rated model that meets the bar");
            // Medium targets the middle (gpt-6-sol, 90) but terra (88, usage 20) is within 3 points and cheaper.
            Assert(Pick(UsageLevel.Medium, step) == "gpt-6.1-terra", "Medium takes the cheapest model near the middle");
            Assert(Pick(UsageLevel.High, step) == "sonnet", "High takes the cheapest model near the upper quarter");
            // Highest targets astra (95); sonnet (92, usage 45) is within 3 points and far cheaper.
            Assert(Pick(UsageLevel.Highest, step) == "sonnet", "Highest takes the cheapest model within 3 points of the top");
            var ui = new WorkItem
            {
                Kind = WorkKind.Coding,
                Access = AccessLevel.ReadProject,
                Request = "Redesign the main menu UI layout",
                Difficulty = 75
            };
            Assert(Pick(UsageLevel.Medium, ui) != "fable", "Medium never pays for fable when sonnet is as good and cheaper");
            // A hard step narrows the usable set, so even Low moves up.
            var hard = new WorkItem
            {
                Kind = WorkKind.Coding,
                Access = AccessLevel.ReadProject,
                Request = "Implement inventory sorting",
                Difficulty = 90
            };
            Assert(Pick(UsageLevel.Low, hard) == "gpt-6-sol", "Low still only uses models good enough for the step (coding 90 at bar 90), got " + Pick(UsageLevel.Low, hard));
            // A send-back skips the model that missed.
            step.Reworks = 1;
            step.RejectedRoutes = ["codex|gpt-5.6-luna"];
            Assert(Pick(UsageLevel.Low, step) != "gpt-5.6-luna", "Low skips a model already sent back on this work");
            settings.UsageLevel = UsageLevel.Auto;
            return Task.CompletedTask;
        });
        await Check("Blocked work carries a machine-readable code, not just text", async () =>
        {
            using var f = New();
            f.Work.Access = AccessLevel.WriteProject;
            f.Work.Request = "Review weekly quest code and write the fix";
            await f.Manager.RunAsync(f.Work, f.Settings, f.Health);
            Assert(f.Work.State == WorkState.WrongAccess && f.Work.BlockCode == BlockCode.MissingWritePermission, "Missing write permission is coded, state=" + f.Work.State + ", code=" + f.Work.BlockCode);
            f.Settings.Providers[0].AllowProjectWrite = true;
            await f.Manager.RunAsync(f.Work, f.Settings, f.Health);
            Assert(f.Work.BlockCode == BlockCode.None && f.Work.State == WorkState.NeedsReview, "Running again clears the code");
        });
        await Check("A crashing model hands the step to the next one; a rejected result does not", async () =>
        {
            using (var f = New(textOnly: true))
            {
                f.First.Result = new(FailureKind.TaskError, "compiler error");
                await f.Manager.RunAsync(f.Work, f.Settings, f.Health);
                Assert(f.First.Calls == 1 && f.Second.Calls == 1 && f.Work.State == WorkState.NeedsReview, "A model that errors out is skipped for this run and the next model takes over");
            }
            using (var f = New(textOnly: true))
            {
                f.First.Result = new(FailureKind.Validation, "invalid edit proposal");
                await f.Manager.RunAsync(f.Work, f.Settings, f.Health);
                Assert(f.Second.Calls == 0 && f.Work.State == WorkState.Failed, "A rejected result is not handed to another AI");
            }
        });
        await Check("Claude's 5-hour limit is a usage limit with its stated reset time", () =>
        {
            const string text = "You've hit your 5-hour limit · resets 8:20pm (Asia/Ho_Chi_Minh)";
            Assert(ProviderErrors.Classify(1, text) == FailureKind.RateLimit && ProviderErrors.Classify(1, "You've hit your weekly limit") == FailureKind.RateLimit, "Usage limit not recognised");
            var now = new DateTimeOffset(2026, 11, 13, 16, 31, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 13)));
            var at = ProviderCooldown.RetryAt(FailureKind.RateLimit, text, now).ToLocalTime();
            Assert(ProviderCooldown.ResetStated(text) && at.Hour == 20 && at.Minute >= 20 && at.Minute <= 25 && at.Date == now.ToLocalTime().Date, "Reset time not read: " + at);
            Assert(!ProviderCooldown.ResetStated("Compiled 3 files in 12:30 seconds"), "A clock-like number without a reset phrase is not a reset time");
            return Task.CompletedTask;
        });
        await Check("One writer per project; cancel waits for executor; user switch handoff", async () =>
        {
            using var f = New(textOnly: true);
            f.First.Block = true;
            var execution = f.Manager.RunAsync(f.Work, f.Settings, f.Health);
            await f.First.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var other = new WorkItem
            {
                ProjectId = f.Project.Id,
                Kind = WorkKind.Content,
                Access = AccessLevel.TextOnly
            };
            bool rejected = false;
            try
            {
                await f.Manager.RunAsync(other, f.Settings, f.Health);
            }
            catch (InvalidOperationException)
            {
                rejected = true;
            }

            Assert(rejected);
            f.Manager.Stop(f.Work.Id);
            await execution;
            Assert(f.First.Exited && f.Work.State == WorkState.Paused);
            await f.Manager.SwitchAsync(f.Work, f.Settings, f.Health, new("gemini", "b", "user switch"));
            Assert(f.Second.Calls == 1 && f.Work.State == WorkState.NeedsReview);
            Assert(f.Store.Logs(f.Work.Id).Any(l => l.Event == "UserSwitch"));
        });
        await Check("Checkpoint exists before provider receives work; completion is NeedsReview", async () =>
        {
            using var f = New();
            f.First.Before = () => Assert(f.Store.LatestHandoff(f.Work.Id) != null);
            await f.Manager.RunAsync(f.Work, f.Settings, f.Health);
            Assert(f.Work.State == WorkState.NeedsReview);
            Assert(f.Store.Logs(f.Work.Id).Any(l => l.Event == "Execution"));
        });
        await Check("CLI error classification cannot treat successful content as quota error", () =>
        {
            Assert(ProviderErrors.Classify(0, "rate limit") == FailureKind.None);
            Assert(ProviderErrors.Classify(1, "rate limit exceeded") == FailureKind.RateLimit);
            Assert(ProviderErrors.Classify(1, "compiler failed") == FailureKind.TaskError);
            return Task.CompletedTask;
        });
        await Check("CLI adapters use provider-specific flags and never full-access switches", () =>
        {
            using var f = New();
            foreach (var id in new[]
            {
                "codex",
                "claude",
                "copilot",
                "gemini",
                "opencode"
            }

            )
            {
                f.Work.Access = id == "codex" ? AccessLevel.WriteProject : AccessLevel.TextOnly;
                var a = CliProvider.Arguments(id, new(f.Project, f.Work, "user-model", "", 1000), "prompt", "policy.toml");
                Assert(a.Contains("--model") && !a.Any(v => v.Contains("bypass") || v == "--yolo"));
                if (id == "codex")
                    Assert(a.Contains("workspace-write") && a.Contains("--ignore-user-config"));
                if (id == "claude")
                    Assert(a.Contains("--tools") && a.Contains("--strict-mcp-config"));
                if (id == "copilot")
                    Assert(a.Contains("--deny-tool"));
            }

            return Task.CompletedTask;
        });
        await Check("Ollama discovery, benchmark grading and unload request (mock API)", async () =>
        {
            using var f = New();
            var handler = new FakeOllama();
            using var local = new OllamaProvider(handler);
            Assert((await local.ListModelsAsync(CancellationToken.None)).Single().Id == "test-local");
            var evaluations = await new LocalEvaluator(local, f.Store).EvaluateAsync("test-local", CancellationToken.None);
            Assert(evaluations.All(e => e.Passed == e.Total) && evaluations.Count == 2);
            Assert(handler.GenerateCalls == 6 && handler.AllUnload && handler.AllThinkOff && handler.AllContextBounded);
            Assert(f.Store.Evaluations().Count == 2);
        });
        await Check("Cross-provider coding can fall back to a reviewed edit proposal", async () =>
        {
            using var f = New();
            f.Work.Access = AccessLevel.WriteProject;
            f.Settings.Providers.Single(p => p.Id == "codex").AllowProjectWrite = true;
            f.Settings.Providers.Single(p => p.Id == "gemini").AllowProjectWrite = true;
            var health = f.Health.Select(h => h with { CanWrite = true }).ToArray();
            f.First.Result = new(FailureKind.RateLimit, "quota exceeded");
            f.Second.Result = new(FailureKind.None, JsonSerializer.Serialize(new EditProposal("New quest", [new("Quest.cs", "NEW", "// reviewed implementation")])));
            await f.Manager.RunAsync(f.Work, f.Settings, health);
            Assert(f.Work.State == WorkState.NeedsReview && f.Second.Calls == 1);
            Assert(!File.Exists(Path.Combine(f.Project.Root, "Quest.cs")));
            f.Manager.ApplyReviewedEdits(f.Work);
            Assert(File.Exists(Path.Combine(f.Project.Root, "Quest.cs")));
            bool duplicate = false;
            try
            {
                f.Manager.ApplyReviewedEdits(f.Work);
            }
            catch (InvalidOperationException)
            {
                duplicate = true;
            }

            Assert(duplicate);
        });
        await Check("Local qualification fails closed on timeout and invalid answers", async () =>
        {
            using var f = New();
            using var invalid = new OllamaProvider(new FakeOllama(invalid: true));
            var invalidResult = await new LocalEvaluator(invalid, f.Store).EvaluateAsync("invalid", CancellationToken.None);
            Assert(invalidResult.All(e => e.Passed == 0 && e.Errors == 3));
            using var slow = new OllamaProvider(new FakeOllama(timeout: true));
            var slowResult = await new LocalEvaluator(slow, f.Store).EvaluateAsync("slow", CancellationToken.None);
            Assert(slowResult.All(e => e.Passed == 0 && e.Timeouts == 3));
        });
        await Check("Freebuff CLI is catalogued but blocked without a noninteractive mode", async () =>
        {
            var provider = new CliProvider("freebuff", new HelpRunner("Usage: freebuff [options] [command]\n  --continue [conversation-id]\n  --cwd <directory>\n  login"));
            var h = await provider.DetectAsync(new() { Id = "freebuff", Executable = Environment.ProcessPath! }, CancellationToken.None);
            Assert(h.Installed && !h.CanExecute && h.Detail.Contains("interactive", StringComparison.OrdinalIgnoreCase));
            bool refused = false;
            try
            {
                CliProvider.Arguments("freebuff", new(new("p", "fixture", root), new WorkItem { Access = AccessLevel.TextOnly }, "", "", 1000), "prompt");
            }
            catch (NotSupportedException)
            {
                refused = true;
            }

            Assert(refused, "Freebuff arguments must never be constructed without a headless mode.");
            Assert(new ManagerSettings().Providers.Any(p => p.Id == "freebuff"), "Freebuff must be a default provider entry.");
            var settingsFile = Path.Combine(root, "freebuff-settings.json");
            File.WriteAllText(settingsFile, "{\"freebuffModelDefaultMigration\":\"glm-5.3-flash-2026-09-05\"}");
            var models = CliModelCatalog.FreebuffSettings(out _, settingsFile);
            Assert(models.Count == 1 && models[0].Id == "glm-5.3-flash-2026-09-05", "Freebuff must catalogue its locally recorded default model.");
            File.WriteAllText(settingsFile, "{\"freebuffModelDefaultMigration\":\"glm-5.3-flash-2026-09-05\",\"freebuffModel\":\"mimo-v2.6-flash-fast\"}");
            models = CliModelCatalog.FreebuffSettings(out _, settingsFile);
            Assert(models.Single().Id == "mimo-v2.6-flash-fast", "A model selected in Freebuff must override the default.");
        });
        await Check("CLI capability detection fails closed on unsupported help", async () =>
        {
            var provider = new CliProvider("codex", new HelpRunner("old unsupported help"));
            var h = await provider.DetectAsync(new() { Id = "codex", Executable = Environment.ProcessPath! }, CancellationToken.None);
            Assert(h.Installed && !h.CanExecute);
            provider = new("codex", new HelpRunner("--sandbox --model --json --skip-git-repo-check --ignore-user-config --ephemeral --ask-for-approval"));
            h = await provider.DetectAsync(new() { Id = "codex", Executable = Environment.ProcessPath! }, CancellationToken.None);
            Assert(h.CanExecute && h.CanWrite);
        });
        await Check("Only recognized npm launchers resolve; no command shell is used", () =>
        {
            var folder = Path.Combine(root, "npm-fixture");
            Directory.CreateDirectory(Path.Combine(folder, "node_modules", "@google", "gemini-cli", "dist"));
            File.WriteAllText(Path.Combine(folder, "node.exe"), "");
            var script = Path.Combine(folder, "node_modules", "@google", "gemini-cli", "dist", "index.js");
            File.WriteAllText(script, "// never executed");
            var shim = Path.Combine(folder, "gemini.cmd");
            File.WriteAllText(shim, "@echo off\n\"%dp0%\\node.exe\" \"%dp0%\\node_modules\\@google\\gemini-cli\\dist\\index.js\" %*");
            var launch = ProcessRunner.ResolveCli(shim, "gemini");
            Assert(launch?.Prefix.Single() == script);
            Assert(ProcessRunner.ResolveCli(shim, "claude") == null);
            return Task.CompletedTask;
        });
        await Check("Codex Desktop discovery works without PATH and ignores empty version folders", () =>
        {
            var local = Path.Combine(root, "desktop-install");
            var bin = Path.Combine(local, "OpenAI", "Codex", "bin");
            Directory.CreateDirectory(Path.Combine(bin, "empty-new-version"));
            Directory.CreateDirectory(Path.Combine(bin, "old"));
            Directory.CreateDirectory(Path.Combine(bin, "new"));
            var old = Path.Combine(bin, "old", "codex.exe");
            var latest = Path.Combine(bin, "new", "codex.exe");
            File.WriteAllText(old, "");
            File.WriteAllText(latest, "");
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-1));
            var search = new CliSearchContext([], local);
            Assert(ProcessRunner.ResolveCli("codex", "codex", search)?.Executable == latest);
            Assert(ProcessRunner.ResolveCli(Path.Combine(local, "missing", "codex.exe"), "codex", search) == null);
            Assert(ProcessRunner.ResolveCli("wrong-relative-name", "codex", search) == null);
            Assert(ProcessRunner.ResolveCli("claude", "claude", search) == null);
            return Task.CompletedTask;
        });
        await Check("Codex adapter preserves only the configured Windows sandbox and passes root permissions", () =>
        {
            Assert(CodexWindowsSettings.ParseMode("model_provider='custom'\n[windows]\nsandbox = \"unelevated\" # existing setup\n[other]\nsandbox='bad'") == "unelevated");
            Assert(CodexWindowsSettings.ParseMode("[other]\nsandbox='unelevated'") == null);
            Assert(CodexWindowsSettings.ParseMode("[windows]\nsandbox='danger-full-access'") == null);
            using var f = New();
            f.Work.Access = AccessLevel.WriteProject;
            var command = CliProvider.Arguments("codex", new(f.Project, f.Work, "", "", 1000), "request", codexWindowsSandbox: "unelevated");
            Assert(command.Contains("windows.sandbox=\"unelevated\"") && command.Contains("--ignore-user-config"));
            Assert(command.IndexOf("--sandbox") < command.IndexOf("exec"));
            return Task.CompletedTask;
        });
        await Check("Explicit Codex permission refusal is blocked rather than a reviewable success", async () =>
        {
            var refusal = JsonSerializer.Serialize(new { type = "item.completed", item = new { type = "agent_message", text = "M\u00ecnh ch\u01b0a th\u1ec3 c\u1eadp nh\u1eadt giao di\u1ec7n v\u00ec phi\u00ean hi\u1ec7n t\u1ea1i ch\u1ec9 c\u00f3 quy\u1ec1n \u0111\u1ecdc." } });
            Assert(CodexWindowsSettings.IsPermissionRefusal(refusal), "An explicit Vietnamese read-only refusal must be recognized.");
            Assert(!CodexWindowsSettings.IsPermissionRefusal("Updated the read-only label successfully."), "A successful label edit must not be treated as permission refusal.");
            using var f = New();
            f.First.Result = new(FailureKind.Permission, refusal, "Runtime read-only");
            await f.Manager.RunAsync(f.Work, f.Settings, f.Health);
            Assert(f.Work.State == WorkState.WrongAccess && f.Second.Calls == 0);
            Assert(f.Store.LatestHandoff(f.Work.Id) != null);
        });
        await Check("SemiAutomatic defers routing with no inference until user approval", async () =>
        {
            using var f = New();
            var session = SessionFor(f, mode: RoutingMode.SemiAutomatic);
            var engine = EngineFor(f);
            await engine.RunAsync(session.Id, f.Settings, f.Health);
            Assert(f.First.Calls == 0);
            var decision = f.Store.Decisions(session.Id).Single(d => d.Kind == "Approval");
            engine.ResolveDecision(session.Id, decision.Id, true, f.Settings, f.Health);
            await engine.RunAsync(session.Id, f.Settings, f.Health);
            Assert(f.First.Calls == 1 && f.Store.WorkItems().Single(w => w.Id == f.Work.Id).State == WorkState.NeedsReview);
        });
        await Check("Session manual Switch AI waits for old executor and resumes through checkpoint", async () =>
        {
            using var f = New();
            var session = SessionFor(f);
            f.First.Block = true;
            var engine = EngineFor(f);
            var run = engine.RunAsync(session.Id, f.Settings, f.Health);
            await f.First.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            engine.SwitchAI(session.Id, f.Work.Id, new("gemini", "b", "User override"), f.Settings, f.Health);
            await run;
            var work = f.Store.WorkItems().Single(w => w.Id == f.Work.Id);
            Assert(f.First.Exited && f.First.Calls == 1 && f.Second.Calls == 1 && work.ProviderId == "gemini" && work.Switches == 1);
            Assert(f.Store.Logs(work.Id).Any(l => l.Event == "UserSwitch"));
        });
        await Check("Control protocol reads final Codex payload after progress messages", () =>
        {
            var payload = "{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"Progress update\"}}\n{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"{\\\"delegation\\\":{\\\"providerId\\\":\\\"gemini\\\",\\\"modelId\\\":\\\"x\\\",\\\"reason\\\":\\\"Fit\\\"}}\"}}";
            Assert(AgentProtocol.Delegation(payload)?.ProviderId == "gemini");
            return Task.CompletedTask;
        });
        await Check("Coordinator fallback honors Planning allowlists and the shared call budget", async () =>
        {
            using var f = New();
            var session = SessionFor(f);
            session.Coordinator = new("codex", "a", "planner");
            session.MaxCalls = 2;
            f.Store.SaveSession(session);
            f.First.Result = new(FailureKind.RateLimit, "quota exceeded");
            f.Second.Result = new(FailureKind.None, JsonSerializer.Serialize(new WorkPlan("Fallback plan", [new("a", f.Project.Id, "Review", WorkKind.Review, AccessLevel.ReadProject, [])]), AgentProtocol.Json));
            var result = await new PlanningService(f.Store, f.Store, f.Manager).GenerateAsync(session, f.Settings, f.Health, CancellationToken.None);
            Assert(result != null && f.First.Calls == 1 && f.Second.Calls == 1 && session.CallsUsed == 2 && session.SwitchesUsed == 1);
        });
        await Check("Provider resume: each explicit resume requests again without looping within one invocation", async () =>
        {
            using var f = New();
            f.First.Result = new(FailureKind.RateLimit, "quota exceeded");
            var flow = UnifiedFor(f);
            f.Settings.Providers.Single(p => p.Id == "gemini").Enabled = false;
            await flow.StartAsync(f.Work, f.Settings, f.Health);
            var goal = f.Store.WorkItems().Single(w => w.Id == f.Work.Id);
            for (var i = 0; i < 2; i++)
            {
                var before = f.First.Calls;
                await flow.StartAsync(goal, f.Settings, f.Health);
                Assert(f.First.Calls == before + 1, "Each explicit resume requests the provider exactly once");
            }
            var step = flow.Steps(goal).Single();
            Assert(f.First.Calls == 3 && step.Attempts == 3 && step.State == WorkState.WaitingForProvider);
            Assert(step.Notes.Contains("codex", StringComparison.OrdinalIgnoreCase) && step.Notes.Contains("new run/resume", StringComparison.OrdinalIgnoreCase) && flow.Decisions(goal).Count == 1);
            Assert(f.Store.LoadSettings().Providers.Single(p => p.Id == "codex").CooldownUntil == null, "Quota history must not lock future runs");
        });
        await Check("Provider resume: permission waits and exhausted budgets do not execute", async () =>
        {
            using var f = New();
            var flow = UnifiedFor(f);
            f.First.Result = new(FailureKind.Permission, "permission denied");
            await flow.StartAsync(f.Work, f.Settings, f.Health);
            var goal = f.Store.WorkItems().Single(w => w.Id == f.Work.Id);
            await flow.StartAsync(goal, f.Settings, f.Health);
            Assert(f.First.Calls == 1 && flow.Steps(goal).Single().State == WorkState.WrongAccess);
            var step = flow.Steps(goal).Single();
            step.State = WorkState.WaitingForProvider;
            f.Store.SaveWork(step);
            var session = flow.SessionFor(goal)!;
            session.MaxCalls = session.CallsUsed;
            f.Store.SaveSession(session);
            await flow.StartAsync(goal, f.Settings, f.Health);
            Assert(f.First.Calls == 1 && flow.SessionFor(goal)!.State == SessionState.BudgetReached);
        });
        await Check("Provider resume: SemiAutomatic still requires routing approval", async () =>
        {
            using var f = New();
            f.Settings.Providers.Single(p => p.Id == "gemini").Enabled = false;
            var flow = UnifiedFor(f);
            f.First.Result = new(FailureKind.RateLimit, "quota exceeded");
            await flow.StartAsync(f.Work, f.Settings, f.Health);
            var goal = f.Store.WorkItems().Single(w => w.Id == f.Work.Id);
            var session = flow.SessionFor(goal)!;
            session.Mode = RoutingMode.SemiAutomatic;
            f.Store.SaveSession(session);
            var resumed = new UnifiedTaskService(f.Store, f.Store, new Orchestrator(f.Store, [f.First, f.Second], new NullRunner()), new NullRunner());
            await resumed.StartAsync(goal, f.Settings, f.Health);
            Assert(f.First.Calls == 1 && resumed.Steps(goal).Single().State == WorkState.AwaitingApproval && resumed.Decisions(goal).Count == 1);
        });
        await Check("Simple local tasks do not spend an extra planning call", () =>
        {
            Assert(!UnifiedTaskService.NeedsPlan("Fix typo", WorkKind.Coding, PlanningPolicy.Auto));
            Assert(UnifiedTaskService.NeedsPlan("Xây dựng hệ thống báo cáo", WorkKind.Coding, PlanningPolicy.Auto));
            Assert(!UnifiedTaskService.NeedsPlan("Classify", WorkKind.Classification, PlanningPolicy.Always));
            return Task.CompletedTask;
        });
    }
}
