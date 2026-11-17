using System.Windows.Controls;
using Employee.Core;
using Employee.Infrastructure;

namespace Employee.Desktop;
public partial class MainWindow
{
    private sealed record ProviderOption(string? Id, string Label);
    private bool refreshingTaskRoutes;
    private void RefreshTaskRouteChoices()
    {
        RefreshTaskRoutePair(CreateProviderChoice, CreateModelChoice);
        RefreshTaskRoutePair(RunProviderChoice, RouteChoice);
    }

    private void RefreshTaskRoutePair(ComboBox provider, ComboBox model, Route? route = null)
    {
        route ??= (model.SelectedItem as RouteOption)?.Route;
        refreshingTaskRoutes = true;
        try
        {
            var options = new List<ProviderOption>
            {
                new(null, "Automatic selection")
            };
            options.AddRange(settings.Providers.Where(p => p.Enabled).Select(p => new ProviderOption(p.Id, p.Id)));
            if (route != null && options.All(p => p.Id != route.ProviderId))
                options.Add(new(route.ProviderId, route.ProviderId + " (unavailable)"));
            provider.ItemsSource = options;
            provider.SelectedItem = options.FirstOrDefault(p => p.Id == route?.ProviderId) ?? options[0];
            PopulateTaskModels(provider, model, route);
        }
        finally
        {
            refreshingTaskRoutes = false;
        }
    }

    private void PopulateTaskModels(ComboBox provider, ComboBox model, Route? selected = null)
    {
        var id = (provider.SelectedItem as ProviderOption)?.Id;
        var options = new List<RouteOption>();
        if (id == null)
            options.Add(new(null, "Automatic model selection"));
        else
        {
            var config = settings.Providers.FirstOrDefault(p => p.Id == id && p.Enabled);
            if (config?.Models.Any(m => !m.Blocked) == true)
                options.Add(new(new(id, "", "Automatic model selection", AutoModel: true), "Automatic model selection"));
            if (config != null)
                options.AddRange(config.Models.Where(m => !m.Blocked).Select(m => new RouteOption(new(id, m.Id, "User selected"), Orchestrator.DisplayModel(m.Id))));
            if (selected != null && options.All(o => o.Route?.ModelId != selected.ModelId || o.Route?.AutoModel != selected.AutoModel))
                options.Add(new(selected, Orchestrator.DisplayModel(selected.ModelId) + " (unavailable)"));
            if (options.Count == 0)
                options.Add(new(null, "No permitted models — configure AI providers"));
        }

        model.ItemsSource = options;
        model.SelectedItem = options.FirstOrDefault(o => selected != null && o.Route?.ModelId == selected.ModelId && o.Route?.AutoModel == selected.AutoModel) ?? options[0];
        model.IsEnabled = id != null;
    }

    private void TaskProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (refreshingTaskRoutes || settings == null)
            return;
        if (ReferenceEquals(sender, CreateProviderChoice))
            PopulateTaskModels(CreateProviderChoice, CreateModelChoice);
        else
            PopulateTaskModels(RunProviderChoice, RouteChoice);
    }

    private Route? SelectedTaskRoute(ComboBox provider, ComboBox model)
    {
        var id = (provider.SelectedItem as ProviderOption)?.Id;
        if (id == null)
            return null;
        var route = (model.SelectedItem as RouteOption)?.Route;
        if (route == null || !settings.Providers.Any(p => p.Id == id && p.Enabled && p.Models.Any(m => !m.Blocked && (route.AutoModel || m.Id == route.ModelId))))
            throw new InvalidOperationException("Choose an enabled provider and a permitted model in AI providers.");
        return route;
    }

    private void ShowTaskRoute(Route? route)
    {
        // Clear the previous task's selection before restoring this task's saved route.
        RouteChoice.SelectedItem = null;
        RefreshTaskRoutePair(RunProviderChoice, RouteChoice, route);
    }

    private void VerifyModelSelectionSmoke()
    {
        var original = settings;
        try
        {
            settings = new ManagerSettings
            {
                Providers = [new ProviderSettings
                {
                    Id = "fixture",
                    Enabled = true,
                    Models = [new("model-a", [WorkKind.Coding]), new("model-b", [WorkKind.Review]), new("blocked", [WorkKind.Coding], Blocked: true)]
                }

                ]
            };
            RefreshTaskRouteChoices();
            CreateProviderChoice.SelectedIndex = 1;
            if (CreateModelChoice.Items.Count != 3)
                throw new InvalidOperationException("Model filter failed.");
            CreateModelChoice.SelectedIndex = 2;
            var route = SelectedTaskRoute(CreateProviderChoice, CreateModelChoice)!;
            if (route.ModelId != "model-b")
                throw new InvalidOperationException("Exact model selection failed.");
            RefreshTaskRouteChoices();
            if (SelectedTaskRoute(CreateProviderChoice, CreateModelChoice)?.ModelId != "model-b")
                throw new InvalidOperationException("Model refresh lost selection.");
            ShowTaskRoute(route);
            if (SelectedTaskRoute(RunProviderChoice, RouteChoice) != route)
                throw new InvalidOperationException("Saved task model restore failed.");
            ShowTaskRoute(null);
            if (SelectedTaskRoute(RunProviderChoice, RouteChoice) != null)
                throw new InvalidOperationException("Task model leaked into automatic task.");
            settings.Providers[0].Models.Clear();
            CreateProviderChoice.SelectedIndex = 0;
            CreateProviderChoice.SelectedIndex = 1;
            try
            {
                SelectedTaskRoute(CreateProviderChoice, CreateModelChoice);
                throw new Exception("Empty model catalog accepted.");
            }
            catch (InvalidOperationException)
            {
            }
        }
        finally
        {
            settings = original;
            CreateModelChoice.SelectedItem = null;
            RouteChoice.SelectedItem = null;
            RefreshTaskRouteChoices();
        }
    }
}
