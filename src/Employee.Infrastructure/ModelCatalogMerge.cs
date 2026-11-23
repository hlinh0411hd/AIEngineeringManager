using Employee.Core;

namespace Employee.Infrastructure;
public static class ModelCatalogMerge
{
    public static int AssignAutomaticRoles(ManagerSettings settings, IReadOnlyList<ProviderHealth> health, IEmployeeStore store)
    {
        foreach (var provider in settings.Providers)
            for (var i = 0; i < provider.Models.Count; i++)
                if (provider.Models[i].AutomaticPrimaryKinds.Length > 0)
                    provider.Models[i] = provider.Models[i] with
                    {
                        AutomaticPrimaryKinds = []
                    };
        var router = ModelRouter.For(store);
        var evaluations = store.Evaluations();
        var assigned = 0;
        foreach (var kind in Enum.GetValues<WorkKind>())
        {
            var access = kind switch
            {
                WorkKind.Classification or WorkKind.Extraction => AccessLevel.TextOnly,
                WorkKind.Coding => AccessLevel.WriteProject,
                _ => AccessLevel.ReadProject
            };
            var work = new WorkItem
            {
                Kind = kind,
                Access = access,
                IsCoordinator = kind == WorkKind.Planning
            };
            var excluded = settings.Providers.SelectMany(provider => provider.Models.Where(model => string.IsNullOrWhiteSpace(model.Id) || (model.ManualRoleKinds.Contains(kind) && !model.PrimaryKinds.Contains(kind))).Select(model => provider.Id + "|" + model.Id)).ToHashSet(StringComparer.Ordinal);
            var route = router.Select(work, settings, health, evaluations, excluded);
            if (route == null)
                continue;
            var selected = settings.Providers.Single(provider => provider.Id == route.ProviderId);
            var index = selected.Models.FindIndex(model => model.Id == route.ModelId);
            var selectedModel = selected.Models[index];
            if (!selectedModel.Kinds.Contains(kind) || selectedModel.PrimaryKinds.Contains(kind))
                continue;
            selected.Models[index] = selectedModel with
            {
                AutomaticPrimaryKinds = [..selectedModel.AutomaticPrimaryKinds, kind]
            };
            assigned++;
        }

        return assigned;
    }

    public static void Apply(ProviderSettings provider, IReadOnlyList<ModelInfo> models)
    {
        foreach (var item in models)
        {
            var model = provider.Id == "ollama" ? item with
            {
                DiscoverySource = "Installed local models: Ollama /api/tags"
            }

            : item;
            var index = provider.Models.FindIndex(m => m.Id == model.Id);
            if (index < 0)
                provider.Models.Add(model);
            else
                provider.Models[index] = provider.Models[index] with
                {
                    DiscoverySource = model.DiscoverySource,
                    AccountAvailabilityVerified = provider.Models[index].AccountAvailabilityVerified || model.AccountAvailabilityVerified
                };
        }

        provider.ModelCatalogCheckedAt = DateTimeOffset.UtcNow;
    }

    // Every model the CLI still lists for this account stays available; only entries that are no
    // longer reported (or are not chat models at all) are dropped. User choices, successful-run
    // evidence and the CLI default entry are never removed by a catalog refresh.
    private static readonly string[] NonChatModelTerms = ["embed", "whisper", "tts", "text-to-speech", "moderation", "rerank", "transcribe", "bge-", "clip-", "guard"];
    public static void ApplyEligible(ProviderSettings provider, IReadOnlyList<ModelInfo> models)
    {
        Apply(provider, models);
        if (!provider.FromCliToolApi)
            return;
        var discovered = models.Select(model => model.Id).Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal);
        if (discovered.Count == 0)
            return;
        static bool Protected(ModelInfo model) => model.RatedByUser || model.Blocked || string.IsNullOrWhiteSpace(model.Id)
            || model.PrimaryKinds.Length > 0 || model.ManualRoleKinds.Length > 0 || model.AccountAvailabilityVerified
            || string.Equals(model.DiscoverySource, "User catalog", StringComparison.Ordinal);
        provider.Models.RemoveAll(model => !Protected(model)
            && (!discovered.Contains(model.Id) || NonChatModelTerms.Any(term => model.Id.Contains(term, StringComparison.OrdinalIgnoreCase))));
    }
}
