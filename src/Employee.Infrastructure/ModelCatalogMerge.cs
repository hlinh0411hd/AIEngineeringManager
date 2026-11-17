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
}
