namespace Employee.Core;
public sealed class ModelRouter(IReadOnlyList<ModelReliability>? reliability = null, IReadOnlyList<ModelOutcome>? outcomes = null, DateTimeOffset? learningSince = null)
{
    public static ModelRouter For(IEmployeeStore store) => new(store.Reliability(), store.ModelOutcomes(), store.LoadSettings().LearningResetAt);
    private bool TemporarilyUnreliable(string model, WorkKind kind, IReadOnlyList<Evaluation> evaluations)
    {
        var sample = reliability?.FirstOrDefault(r => r.ModelId == model && r.Kind == kind);
        return sample is { Samples: >= 5, Invalid: >= 3 } && sample.LastAt > DateTimeOffset.UtcNow.AddMinutes(-15) && !evaluations.Any(e => e.ModelId == model && e.Kind == kind && e.At > sample.LastAt && e.Accuracy == 1);
    }

    // Rated quality for the step's capability plus the offset learned from judged results for that
    // model and work type (difficulty-aware, fading, at most ±12). A model that cannot do the
    // capability stays at 0.
    private int Quality(ProviderSettings provider, ModelInfo model, WorkCapability capability, ManagerSettings settings)
    {
        var rated = ModelRatings.QualityFor(provider.Id, model, capability);
        if (rated == 0)
            return 0;
        return Math.Clamp(rated + (int)Math.Round(Offset(provider.Id, model.Id, capability, rated, settings)), 1, 100);
    }

    private double Offset(string providerId, string modelId, WorkCapability capability, int rated, ManagerSettings settings) => !settings.UsePerformanceHistory || outcomes == null ? 0 : ModelLearning.Offset(outcomes.Where(o => o.ProviderId == providerId && o.ModelId == modelId && o.Capability == capability), rated, DateTimeOffset.UtcNow, learningSince);
    // Learned offset for display: uses the model's rating from settings when it is in the catalog.
    public double Offset(string providerId, string modelId, WorkCapability capability, ManagerSettings settings)
    {
        var model = settings.Providers.FirstOrDefault(p => p.Id == providerId)?.Models.FirstOrDefault(m => m.Id == modelId) ?? new ModelInfo(modelId, []);
        var rated = ModelRatings.QualityFor(providerId, model, capability);
        return rated == 0 ? 0 : Offset(providerId, modelId, capability, rated, settings);
    }

    public int Learned(string providerId, string modelId, WorkCapability capability, ManagerSettings settings) => (int)Math.Round(Offset(providerId, modelId, capability, settings));
    // Chance this model's result is accepted without being sent back, from how far its learned
    // quality (rating + offset) sits above or below this step's bar; never above 95%. Qualified
    // local models are trusted by measurement. A model whose result was already sent back on this
    // work is strongly discounted so the step escalates instead of repeating it.
    public double Acceptance(ProviderSettings provider, ModelInfo model, WorkItem work, WorkCapability capability, int rated, int required, ManagerSettings settings)
    {
        if (rated == 0)
            return 0.01;
        var chance = provider.Id == "ollama" ? 0.9 : ModelLearning.Acceptance(rated + Offset(provider.Id, model.Id, capability, rated, settings), required);
        if (work.RejectedRoutes.Contains(provider.Id + "|" + model.Id))
            chance *= 0.4;
        return Math.Clamp(chance, 0.02, 0.95);
    }

    // Expected usage to reach an accepted result: each try costs the model's usage rank, and every
    // miss also costs a send-back (your review time plus redoing the work). Cheap models win on easy
    // work; on work they often miss, a stronger model is cheaper overall.
    public static double ExpectedCost(int usage, double acceptance, RoutingPreference preference)
    {
        // A send-back costs about as much as a mid-tier try: your review time, re-reading context and
        // redoing the work. Economy weighs it less, Performance more.
        var missCost = preference switch
        {
            RoutingPreference.Economy => 20.0,
            RoutingPreference.Performance => 80.0,
            _ => 40.0
        };
        return Math.Max(usage, 0.5) / acceptance + missCost * (1 - acceptance) / acceptance;
    }

    // Models this close to a usage level's target quality count as equivalent; the cheaper one wins.
    public const int UsageLevelTolerance = 3;
    // Historical quota failures do not exclude a provider from a fresh execution.
    // Local models only reach this point after passing qualification for the kind, so they meet
    // the bar by measurement and cost no cloud usage.
    private static bool MeetsBar(ProviderSettings provider, int quality, int required) => provider.Id == "ollama" || quality >= required;
    // With "Use AI CLIs" off, stale health from an earlier detection must not route to a CLI.
    public static IReadOnlyList<ProviderHealth> Usable(IReadOnlyList<ProviderHealth> health, ManagerSettings settings) => health.Where(h => !h.ExcludedBySetting && (settings.UseAiCli || !ManagerSettings.IsCli(h.Id))).ToArray();
    private static bool CompatibleProjectRole(WorkItem work, ProviderSettings provider, ModelInfo model) => provider.Id != "ollama" && model.Kinds.Contains(WorkKind.Coding) && ((work.Kind == WorkKind.Review && (work.Access is AccessLevel.ReadProject or AccessLevel.WriteProject)) || (work.Access == AccessLevel.ReadProject && (work.Kind == WorkKind.Research || (work.Kind == WorkKind.Planning && work.IsCoordinator))));
    // Learned quality (rating + offset) of one catalog model for a work type; 0 when unknown or unable.
    public int QualityOf(string providerId, string modelId, WorkCapability capability, ManagerSettings settings)
    {
        var provider = settings.Providers.FirstOrDefault(p => p.Id == providerId);
        var model = provider?.Models.FirstOrDefault(m => m.Id == modelId);
        return provider == null || model == null ? 0 : Quality(provider, model, capability, settings);
    }

    // minQuality: only models at least this good are considered (a step moved off a model that stalled
    // must not land on a weaker one).
    public Route? Select(WorkItem work, ManagerSettings settings, IReadOnlyList<ProviderHealth> health, IReadOnlyList<Evaluation> evaluations, ISet<string>? excluded = null, Route? explicitChoice = null, int minQuality = 0)
    {
        // Only a step that may write can implement; a planned read-only extraction (seen live: a ZapBop
        // export preparation mentioning "export" and "integration") must still get a model.
        if (work.Kind is WorkKind.Classification or WorkKind.Extraction && !work.IsCoordinator && work.Access == AccessLevel.WriteProject && TaskIntent.RequiresImplementation(work.Request))
            return null;
        var effort = work.RoutingEffort;
        var difficulty = work.DifficultyScore;
        var capability = work.Capability;
        // Each send-back on this work raises the bar so it moves to a stronger model.
        var required = Math.Min(99, ModelRatings.RequiredQuality(difficulty, settings.RoutingPreference) + 6 * Math.Min(work.Reworks + work.Escalations, 4));
        var eligible =
            from p in settings.Providers
            let h = health.FirstOrDefault(x => x.Id == p.Id)
            where p.Enabled && h is { CanExecute: true }
            where excluded?.Contains(p.Id) != true
            where work.Access == AccessLevel.TextOnly ? p.Id != "codex" : p.Id != "ollama"
            where work.Access != AccessLevel.WriteProject || (p.AllowProjectWrite && h.CanWrite)from m in p.Models
            where !m.Blocked && excluded?.Contains(p.Id + "|" + m.Id) != true
            where p.Id != "ollama" || !TemporarilyUnreliable(m.Id, work.Kind, evaluations)
            where p.Id != "ollama" || (work.Access == AccessLevel.TextOnly && work.Kind is WorkKind.Classification or WorkKind.Extraction && evaluations.Where(e => e.ModelId == m.Id && e.Kind == work.Kind).OrderByDescending(e => e.At).FirstOrDefault()is { } e && e.Accuracy >= settings.LocalPassThreshold)
            where explicitChoice == null || (p.Id == explicitChoice.ProviderId && (explicitChoice.AutoModel || m.Id == explicitChoice.ModelId))let rated = ModelRatings.QualityFor(p.Id, m, capability)let q = Quality(p, m, capability, settings)let cost = p.Id == "ollama" ? 0 : m.EffectiveCostRank
            let chance = Acceptance(p, m, work, capability, rated, required, settings)select new
            {
                p,
                m,
                Quality = q,
                Cost = cost,
                Meets = MeetsBar(p, q, required),
                Chance = chance,
                Expected = ExpectedCost(cost, chance, settings.RoutingPreference)
            };
        var available = eligible.Where(c => c.Quality >= minQuality).ToArray();
        var candidates = available.Where(c => c.m.Kinds.Contains(work.Kind)).ToArray();
        var compatible = false;
        if (candidates.Length == 0 && settings.Mode != RoutingMode.Manual)
        {
            candidates = available.Where(c => CompatibleProjectRole(work, c.p, c.m)).ToArray();
            compatible = candidates.Length > 0;
        }

        if (explicitChoice != null)
        {
            if (!explicitChoice.AutoModel && !compatible)
                return candidates.Length > 0 ? explicitChoice : null;
        }
        // No usable worker has an image tool: a tool-free model cannot produce the image, so the step
        // waits instead of spending a call. A model the user picked explicitly still runs.
        else if (capability == WorkCapability.ImageGeneration && !candidates.Any(c => c.Quality > 0))
            return null;

        // A worker able to do the capability at all (e.g. has an image tool) comes first. Then
        // explicit user choices: a manual primary role, then provider priority (all equal by
        // default). Otherwise the lowest expected usage to an accepted result wins. An empty model
        // ID ("CLI default") has unknown quality/cost and only fills gaps.
        var top = candidates.OrderByDescending(c => c.Quality > 0).ThenByDescending(c => c.m.RoleRank(work.Kind) == 2).ThenBy(c => string.IsNullOrWhiteSpace(c.m.Id) ? 1 : 0).ThenByDescending(c => c.p.Priority).FirstOrDefault();
        var group = top == null ? [] : candidates.Where(c => c.Quality > 0 == top.Quality > 0 && (c.m.RoleRank(work.Kind) == 2) == (top.m.RoleRank(work.Kind) == 2) && string.IsNullOrWhiteSpace(c.m.Id) == string.IsNullOrWhiteSpace(top.m.Id) && c.p.Priority == top.p.Priority).ToArray();
        var level = settings.UsageLevel;
        var how = "lowest expected usage to an accepted result";
        var best = group.OrderBy(c => c.Expected).ThenByDescending(c => c.Quality).ThenBy(c => c.p.Id).ThenBy(c => c.m.Id).FirstOrDefault();
        if (level != UsageLevel.Auto && group.Length > 0)
        {
            // Usage level: rank the models that meet the bar by quality and take the lowest, middle,
            // upper-quarter or top one. Models already sent back on this work are skipped if others fit.
            var usable = group.Where(c => c.Meets && !work.RejectedRoutes.Contains(c.p.Id + "|" + c.m.Id)).ToArray();
            if (usable.Length == 0)
                usable = group.Where(c => c.Meets).ToArray();
            if (usable.Length == 0)
            {
                best = group.OrderByDescending(c => c.Quality).ThenBy(c => c.Cost).ThenBy(c => c.p.Id).ThenBy(c => c.m.Id).First();
                how = $"usage level {level}: no model meets the bar, strongest available";
            }
            else
            {
                var ordered = usable.OrderBy(c => c.Quality).ThenBy(c => c.Cost).ThenBy(c => c.p.Id).ThenBy(c => c.m.Id).ToArray();
                var target = level switch
                {
                    UsageLevel.Low => ordered[0].Quality,
                    UsageLevel.Medium => ordered[(int)Math.Round((ordered.Length - 1) * 0.5, MidpointRounding.AwayFromZero)].Quality,
                    UsageLevel.High => ordered[(int)Math.Round((ordered.Length - 1) * 0.75, MidpointRounding.AwayFromZero)].Quality,
                    _ => ordered[^1].Quality
                };
                // Usage still comes first: take the cheapest model within a few points of the level's
                // target quality, then the one closest to the target.
                best = ordered.Where(c => Math.Abs(c.Quality - target) <= UsageLevelTolerance).OrderBy(c => c.Cost).ThenBy(c => Math.Abs(c.Quality - target)).ThenByDescending(c => c.Quality).ThenBy(c => c.p.Id).ThenBy(c => c.m.Id).First();
                how = $"usage level {level}: target quality {target} among {ordered.Length} models meeting the bar; cheapest within {UsageLevelTolerance} points";
            }
        }

        var reason = compatible ? $"Compatible role: permitted Coding model handles {work.Kind} with already approved {work.Access}; no project access or model catalog changed. " : "";
        return best is null ? null : new(best.p.Id, best.m.Id, reason + $"Rule-based: kind={work.Kind}; capability={capability}; difficulty={difficulty}/100 ({effort}); usage level={level}; required {capability} quality={required}{(work.Reworks + work.Escalations > 0 ? $" (raised after {work.Reworks} send-back(s) and {work.Escalations} escalation(s))" : "")}; " + (best.m.RoleRank(work.Kind) == 2 ? "manual primary role; " : best.Quality == 0 ? "no permitted worker can do " + capability + "; " : how + "; ") + $"quality={best.Quality}; usage rank={best.Cost}; first-try acceptance≈{best.Chance:P0}; expected usage={best.Expected:F0}; priority={best.p.Priority}; rating={best.m.RatingSource}. Approvals and send-backs for this model and work type refine the estimate. No AI call.");
    }

    // The one routing decision every flow uses (run, resume, approval, planning, assignment). A
    // user's explicit choice is honoured while it is usable; otherwise, and in every other case,
    // the model is chosen from ratings and the step's work type. Planner hints (advisory routes)
    // never decide. Manual mode only runs the user's choice.
    public Route? Choose(WorkItem work, ManagerSettings settings, IReadOnlyList<ProviderHealth> health, IReadOnlyList<Evaluation> evaluations, ISet<string>? excluded = null, Route? preference = null, int minQuality = 0)
    {
        if (preference != null && work.RouteIsAdvisory)
            preference = null;
        if (preference != null && Select(work, settings, health, evaluations, excluded, preference, minQuality)is { } chosen)
            return chosen;
        if (settings.Mode == RoutingMode.Manual)
            return null;
        var automatic = Select(work, settings, health, evaluations, excluded, minQuality: minQuality);
        if (automatic == null || preference == null)
            return automatic;
        return automatic with
        {
            Reason = $"Selected {preference.ProviderId} / {(preference.AutoModel ? "automatic model" : preference.ModelId)} is not usable now; chosen automatically instead. " + automatic.Reason
        };
    }

    public string ExplainUnavailable(WorkItem work, ManagerSettings settings, IReadOnlyList<ProviderHealth> health, IReadOnlyList<Evaluation> evaluations, Route? choice = null)
    {
        if (work.Kind is WorkKind.Classification or WorkKind.Extraction && !work.IsCoordinator && work.Access == AccessLevel.WriteProject && TaskIntent.RequiresImplementation(work.Request))
            return $"This {work.Kind} step asks for project changes; {work.Kind} models do not implement. Run it as a Coding step.";
        var details = settings.Providers.Where(p => choice == null || settings.Mode != RoutingMode.Manual || p.Id == choice.ProviderId).Select(p =>
        {
            var h = health.FirstOrDefault(x => x.Id == p.Id);
            var cliOff = !settings.UseAiCli && ManagerSettings.IsCli(p.Id);
            var reason = !p.Enabled ? "disabled" : cliOff || h?.ExcludedBySetting == true ? "AI CLIs are turned off in Settings > AI & Models" : h == null ? "capabilities not detected" : !h.Installed ? "CLI/runtime not found" : !h.CanExecute ? "CLI does not meet execution capability requirements" : work.Access == AccessLevel.TextOnly && p.Id == "codex" ? "Codex needs at least ReadProject access" : work.Access != AccessLevel.TextOnly && p.Id == "ollama" ? "Ollama supports TextOnly tasks" : work.Access == AccessLevel.WriteProject && (!p.AllowProjectWrite || !h.CanWrite) ? "project write permission is not enabled" : choice == null && work.Capability == WorkCapability.ImageGeneration && !WorkCapabilities.ProviderSupports(p.Id, work.Capability) ? "has no image tool, so it cannot generate images" : !p.Models.Any(m => !m.Blocked && m.Kinds.Contains(work.Kind) && (choice == null || p.Id != choice.ProviderId || choice.AutoModel || m.Id == choice.ModelId)) ? "no allowed " + work.Kind + " model (check model task types/blocked state)" : p.Id == "ollama" ? "local model is not qualified for this task kind" : "no eligible model";
            return p.Id + ": " + reason;
        });
        return $"No eligible AI for {work.Kind} / {work.Access}. " + string.Join("; ", details) + ". Configure AI providers or select Automatic selection. Disabled providers are never enabled automatically.";
    }
}
