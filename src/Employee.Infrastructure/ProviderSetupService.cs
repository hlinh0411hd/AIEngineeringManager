using Employee.Core;

namespace Employee.Infrastructure;
public sealed record ProviderSetupResult(string Provider, bool Installed, bool Ready, bool Enabled, string Authentication, string Detail, string[] QualifiedModels);
public sealed class ProviderSetupService(IEmployeeStore store, IReadOnlyList<IAIProvider> providers)
{
    public async Task<IReadOnlyList<ProviderSetupResult>> EnableAvailableAsync(CancellationToken token = default)
    {
        var settings = store.LoadSettings();
        var results = new List<ProviderSetupResult>();
        foreach (var provider in providers)
        {
            var config = settings.Providers.Single(p => p.Id == provider.Id);
            if (config.TurnedOff)
            {
                results.Add(new(provider.Id, false, false, false, "Unknown", "Turned off in Settings; not checked or enabled.", []));
                continue;
            }
            var health = await provider.DetectAsync(config, token);
            var qualified = new List<string>();
            var detail = health.Detail;
            if (provider.Id != "ollama" && health.Installed)
            {
                using var metadataTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                metadataTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                try
                {
                    ModelCatalogMerge.Apply(config, await provider.ListModelsAsync(metadataTimeout.Token));
                    config.ModelDiscoveryStatus = (provider as IModelCatalogStatus)?.ModelDiscoveryDetail ?? "Catalog metadata read.";
                }
                catch (Exception error)when (!token.IsCancellationRequested)
                {
                    config.ModelDiscoveryStatus = "Model listing unavailable: " + error.Message;
                }

                detail += " Models: " + config.ModelDiscoveryStatus;
            }

            if (provider.Id == "ollama" && health.CanExecute)
            {
                var installed = await provider.ListModelsAsync(token);
                foreach (var model in installed)
                {
                    var kinds = new[]
                    {
                        WorkKind.Classification,
                        WorkKind.Extraction
                    }.Where(k => store.Evaluations().Where(e => e.ModelId == model.Id && e.Kind == k).OrderByDescending(e => e.At).FirstOrDefault()is { } evaluation && evaluation.Total >= 3 && evaluation.Accuracy >= settings.LocalPassThreshold).ToArray();
                    if (kinds.Length == 0)
                        continue;
                    var existing = config.Models.FindIndex(m => m.Id == model.Id);
                    if (existing >= 0)
                    {
                        if (config.Models[existing].Blocked)
                            continue; // An explicit block remains authoritative.
                        config.Models[existing] = config.Models[existing] with
                        {
                            Kinds = kinds
                        };
                    }
                    else
                        config.Models.Add(model with { Kinds = kinds });
                    qualified.Add(model.Id + ": " + string.Join(", ", kinds));
                }

                if (qualified.Count > 0)
                {
                    config.Enabled = true;
                    detail += " Enabled only installed, previously qualified local categories; no inference or new evaluation performed.";
                }
                else
                    detail += " No unblocked installed model has a passing qualification. Evaluate explicitly before enabling.";
            }
            else if (health.CanExecute && health.Authentication == "Authenticated")
            {
                config.Enabled = true;
                if (config.Models.Count == 0)
                    config.Models.Add(new("", Enum.GetValues<WorkKind>()));
            }
            else if (health.CanExecute && !config.Enabled)
                detail += " Not enabled: authentication could not be verified without inference.";
            results.Add(new(provider.Id, health.Installed, health.CanExecute, config.Enabled, health.Authentication, detail, qualified.ToArray()));
        }

        store.SaveSettings(settings);
        return results;
    }
}
