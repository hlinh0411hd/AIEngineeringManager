using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using Employee.Core;
using Employee.Infrastructure;

namespace Employee.Desktop;
public sealed class ControlCenterView : UserControl, IDisposable
{
    private readonly SqliteStore store;
    private readonly string appRoot, dataRoot;
    private readonly Action beforeMutation;
    private readonly Func<IDisposable> freezeUpdate;
    private readonly Action closeForUpdate;
    private readonly ToolExecutionService tools;
    private readonly ToolWorkflowEngine workflows;
    private readonly TabControl pages = new();
    public ToolExecutionService ExecutionService => tools;

    private readonly TextBlock skillStatus = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 8, 0, 8)
    };
    private readonly DataGrid skillCatalog = new()
    {
        AutoGenerateColumns = true,
        IsReadOnly = true,
        Height = 140
    };
    private readonly CheckBox taskAutomation = new()
    {
        Content = "Allow Tasks to use tools on their own (never publishes)"
    };
    private ToolSkillRegistry? readiness;
    public void AttachSkillRegistry(ToolSkillRegistry registry)
    {
        readiness = registry;
        skillStatus.Text = registry.Status;
        skillCatalog.ItemsSource = registry.Definitions.Select(s => new { s.Id, s.Description, Executor = s.Connector == "ai" ? "AI guidance" : s.Connector, Operations = s.Connector == "ai" ? "Load guidance" : string.Join(", ", s.Operations), Source = s.Source }).ToArray();
    }

    public FrameworkElement SettingsContent { get; }

    private readonly ComboBox toolChoice = new()
    {
        DisplayMemberPath = "Name",
        SelectedValuePath = "Id"
    };
    public event Action<string>? SettingsRequested;
    private readonly DataGrid profiles = new()
    {
        AutoGenerateColumns = true,
        Height = 150
    }, runs = new()
    {
        AutoGenerateColumns = true,
        Height = 180
    }, flows = new()
    {
        AutoGenerateColumns = true,
        Height = 140
    };
    private readonly TextBox name = new(), source = new(), workspace = new(), executable = new(), desktopExecutable = new(), endpoint = new(), timeout = new()
    {
        Text = "600"
    };
    private readonly CheckBox autoStartBackend = new()
    {
        Content = "Start backend automatically when a task needs it",
        IsChecked = true
    };
    private readonly TextBox backendExecutable = new(), backendArguments = new()
    {
        Text = "[]"
    }, backendDirectory = new(), backendData = new(), startupTimeout = new()
    {
        Text = "90"
    };
    private readonly ComboBox connector = new()
    {
        ItemsSource = new[]
        {
            "pixel",
            "video",
            "aivideo",
            "music",
            "marketing"
        }
    }, operation = new()
    {
        DisplayMemberPath = "Description"
    };
    private readonly PasswordBox token = new();
    private readonly CheckBox enabled = new()
    {
        Content = "Enable connector"
    }, writes = new()
    {
        Content = "Allow writing/exporting files"
    }, models = new()
    {
        Content = "Allow AI calls inside the tool"
    }, publishing = new()
    {
        Content = "Allow public publishing (still approved each time)"
    };
    private readonly TextBox parameters = Editor(130), workflowEditor = Editor(240), result = Editor(200), updatePreview = Editor(220);
    private readonly TextBlock status = new()
    {
        TextWrapping = TextWrapping.Wrap
    };
    private string? selectedToolId;
    private bool refreshing, busy;
    private bool disposed;
    private CancellationTokenSource? operationCancellation;
    public bool ClosingForUpdate { get; private set; }

    private PreparedUpdate? prepared;
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };
    public bool IsBusy => busy || tools.IsBusy || workflows.IsBusy;
    public bool ExecutionBusy => tools.IsBusy || workflows.IsBusy;

    public event Action? Changed;
    public ControlCenterView(SqliteStore store, TaskExecutionPool pool, string appRoot, string dataRoot, Func<ManagerSettings> settings, Action beforeMutation, Func<IDisposable> freezeUpdate, Action closeForUpdate)
    {
        this.store = store;
        this.appRoot = appRoot;
        this.dataRoot = dataRoot;
        this.beforeMutation = beforeMutation;
        this.freezeUpdate = freezeUpdate;
        this.closeForUpdate = closeForUpdate;
        tools = new(store, store, new ProcessRunner(), appRoot, dataRoot, paths => pool.ReserveExternal(paths, settings()))
        {
            NormalizePixelBackground = PixelArtifactBackground.RemoveWhiteMatte
        };
        workflows = new(store, store, tools, paths => pool.ReserveExternal(paths, settings()));
        store.RecoverToolRuns();
        SeedTools();
        runs.AutoGenerateColumns = false;
        foreach (var field in new[]
        {
            "Id",
            "ToolId",
            "Operation",
            "State",
            "CreatedAt"
        }

        )
            runs.Columns.Add(new DataGridTextColumn { Header = field, Binding = new System.Windows.Data.Binding(field), Width = new DataGridLength(field == "Id" ? 190 : 140) });
        var root = new DockPanel
        {
            Margin = new Thickness(20)
        };
        DockPanel.SetDock(status, Dock.Bottom);
        root.Children.Add(status);
        root.Children.Add(pages);
        Content = root;
        var toolPanel = new StackPanel();
        var toolSettingsPanel = new StackPanel();
        toolSettingsPanel.Children.Add(Title("Connected tools"));
        toolSettingsPanel.Children.Add(profiles);
        var form = new WrapPanel();
        foreach (var pair in new (string, Control)[]
        {
            ("Name", name),
            ("Connector", connector),
            ("Source folder", source),
            ("Allowed workspace", workspace),
            ("Python/executable", executable),
            ("Windows app (.exe)", desktopExecutable),
            ("Local API URL", endpoint),
            ("New session token (leave blank to keep)", token),
            ("Operation timeout (seconds)", timeout),
            ("Backend .exe (blank = auto-detect)", backendExecutable),
            ("Backend arguments (JSON array)", backendArguments),
            ("Backend folder (blank = Source)", backendDirectory),
            ("Marketing backend DataRoot", backendData),
            ("Startup wait (5–600 seconds)", startupTimeout)
        }

        )
        {
            pair.Item2.Width = 240;
            var box = new StackPanel();
            box.Children.Add(new TextBlock { Text = pair.Item1 });
            box.Children.Add(pair.Item2);
            form.Children.Add(box);
        }

        toolSettingsPanel.Children.Add(form);
        var permissions = new WrapPanel();
        foreach (var control in new[]
        {
            enabled,
            writes,
            models,
            publishing,
            taskAutomation,
            autoStartBackend
        }

        )
            permissions.Children.Add(control);
        toolSettingsPanel.Children.Add(permissions);
        toolSettingsPanel.Children.Add(new TextBlock { Text = "Starts only when a task uses the tool and its port is not running. Pixel: Node + server.mjs; Marketing: Python .venv + entered token + DataRoot; Video and ZapBop (music) run directly through their bridges, with no backend or token (ZapBop: Source = ZapBop folder, Python = its .venv\\Scripts\\python.exe or blank, Allowed workspace = folder with saved .json projects). Custom backend: .exe and JSON arguments. Never installs dependencies or signs in.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) });
        toolSettingsPanel.Children.Add(Buttons(("Add connector", NewProfile), ("Save settings", SaveProfile), ("Check connection", CheckTool)));
        toolSettingsPanel.Children.Add(new TextBlock { Text = "Tokens are used only with loopback APIs and protected with Windows DPAPI. Tool errors/missing permissions are recorded clearly; dependencies are never installed automatically.", TextWrapping = TextWrapping.Wrap });
        var useSkills = new CheckBox
        {
            Content = "V5: AI picks skills and orchestrates tools in Tasks",
            IsChecked = settings().Automation.UseToolSkills
        };
        useSkills.Click += (_, _) =>
        {
            beforeMutation();
            var value = useSkills.IsChecked == true;
            settings().Automation.UseToolSkills = value;
            var saved = store.LoadSettings();
            saved.Automation.UseToolSkills = value;
            store.SaveSettings(saved);
        };
        toolSettingsPanel.Children.Add(useSkills);
        var autoApplyImages = new CheckBox
        {
            Content = "Continue automatically: generate images → import into project → integrate (reviewed on the parent task)",
            IsChecked = settings().Automation.AutoApplyGeneratedImages
        };
        autoApplyImages.Click += (_, _) =>
        {
            beforeMutation();
            var value = autoApplyImages.IsChecked == true;
            settings().Automation.AutoApplyGeneratedImages = value;
            var saved = store.LoadSettings();
            saved.Automation.AutoApplyGeneratedImages = value;
            store.SaveSettings(saved);
        };
        toolSettingsPanel.Children.Add(autoApplyImages);
        CheckBox AutomationOption(string text, Func<TaskAutomationSettings, bool> read, Action<TaskAutomationSettings, bool> write)
        {
            var box = new CheckBox { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, IsChecked = read(settings().Automation) };
            box.Click += (_, _) =>
            {
                beforeMutation();
                var value = box.IsChecked == true;
                write(settings().Automation, value);
                var saved = store.LoadSettings();
                write(saved.Automation, value);
                store.SaveSettings(saved);
            };
            return box;
        }
        toolSettingsPanel.Children.Add(AutomationOption("Let the planner choose tools: a goal may use any enabled tool with task automation allowed, without you naming it (off: only tools you name)", a => a.PlannerChoosesTools, (a, value) => a.PlannerChoosesTools = value));
        toolSettingsPanel.Children.Add(AutomationOption("Test milestones: after each milestone and at the end, a tester step runs the project against the plan's acceptance criteria; failures are fixed and tested again (up to 3 rounds)", a => a.VerifyAcceptance, (a, value) => a.VerifyAcceptance = value));
        toolSettingsPanel.Children.Add(AutomationOption("Sign off milestones: in large tasks, after a milestone passes its tester the task waits for your approval before the next milestone starts. Comment on the task instead to change that milestone; it is reworked and tested again", a => a.SignOffMilestones, (a, value) => a.SignOffMilestones = value));
        toolSettingsPanel.Children.Add(skillStatus);
        toolSettingsPanel.Children.Add(skillCatalog);
        toolSettingsPanel.Children.Add(new TextBlock { Text = "In Tasks, describe the result and preferred tool. V5 plans AI → Tool → AI and loads only the selected skill. Automatic mode plus tool self-use permission lets internal steps continue; the final result still needs review. New skills need a connector the app supports. Restart the app after changing skills.", TextWrapping = TextWrapping.Wrap });
        var settingsNotice = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap
        };
        settingsNotice.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(TextBlock.Text)) { Source = status });
        toolSettingsPanel.Children.Add(settingsNotice);
        SettingsContent = Scroll(toolSettingsPanel);
        toolPanel.Children.Add(Title("Tools"));
        toolPanel.Children.Add(toolChoice);
        toolPanel.Children.Add(Buttons(("Settings: Tools", (_, _) => SettingsRequested?.Invoke("Tools")), ("Check connection", CheckTool), ("Open Windows app", LaunchDesktop)));
        toolChoice.SelectionChanged += (_, _) =>
        {
            if (refreshing || toolChoice.SelectedValue is not string id || id == selectedToolId)
                return;
            profiles.SelectedItem = profiles.Items.Cast<object>().FirstOrDefault(v => v.GetType().GetProperty("Id")?.GetValue(v)?.ToString() == id);
        };
        toolPanel.Children.Add(operation);
        toolPanel.Children.Add(parameters);
        toolPanel.Children.Add(Buttons(("Prepare operation", CreateRequest), ("Approve selected operation", ApproveRequest), ("Execute", ExecuteRequest), ("Review result", ReviewRequest)));
        toolPanel.Children.Add(runs);
        toolPanel.Children.Add(result);
        result.IsReadOnly = true;
        pages.Items.Add(new TabItem { Header = "Tools", Content = Scroll(toolPanel) });
        var workflowPanel = new StackPanel();
        workflowPanel.Children.Add(Buttons(("Settings: Workflows", (_, _) => SettingsRequested?.Invoke("Workflows"))));
        workflowPanel.Children.Add(Title("Multi-tool workflow"));
        workflowPanel.Children.Add(new TextBlock { Text = "Enter a JSON plan with ProjectId, steps and DependsOn. Inputs can use an earlier step's result via {\"$from\":\"stepKey.field\"}. Steps that already wrote data are never re-run automatically.", TextWrapping = TextWrapping.Wrap });
        workflowPanel.Children.Add(flows);
        workflowPanel.Children.Add(workflowEditor);
        workflowPanel.Children.Add(Buttons(("Workflow template", NewWorkflow), ("Import JSON", ImportWorkflow), ("Save plan", SaveWorkflow), ("Run / continue", RunWorkflow), ("Confirm completion", ConfirmWorkflow), ("Stop tool/workflow", StopClicked)));
        pages.Items.Add(new TabItem { Header = "Workflows", Content = Scroll(workflowPanel) });
        var updatePanel = new StackPanel();
        updatePanel.Children.Add(Buttons(("Settings: Updates", (_, _) => SettingsRequested?.Invoke("Updates"))));
        updatePanel.Children.Add(Title("Safe update from an existing build"));
        updatePanel.Children.Add(new TextBlock { Text = "Choose a prepared build folder. Review the manifest/hash before applying. Nothing is built, downloaded or zipped automatically. Data, config and workspaces are protected. Close the tool being updated before applying; a manager self-update closes this window.", TextWrapping = TextWrapping.Wrap });
        updatePanel.Children.Add(Buttons(("Prepare manager update", PrepareSelfUpdate), ("Prepare update for selected tool", PrepareToolUpdate), ("Apply approved update", ApplyUpdate)));
        updatePanel.Children.Add(updatePreview);
        updatePreview.IsReadOnly = true;
        pages.Items.Add(new TabItem { Header = "Updates", Content = Scroll(updatePanel) });
        profiles.SelectionChanged += ProfileSelected;
        operation.SelectionChanged += (_, _) =>
        {
            if (operation.SelectedItem is ToolCapability c)
                parameters.Text = (connector.SelectedItem as string) switch
                {
                    "marketing" when c.Id == "export" => "{\"jobId\":\"ID\"}",
                    "video" when c.Id == "generate" => "{\"mode\":\"assets\",\"asset_folder\":\"CHOOSE_PROJECT_FOLDER_WITH_SCREENSHOTS\",\"title\":\"Game trailer\",\"duration\":30,\"format\":\"1080p\",\"style\":\"promo\",\"language\":\"en\"}",
                    "aivideo" when c.Id == "generate" => "{\"mode\":\"text\",\"topic\":\"A 30-second promo for my game\",\"duration\":30,\"format\":\"1080p\",\"style\":\"promo\",\"language\":\"en\"}",
                    "aivideo" when c.Id == "status" => "{}",
                    "music" => c.Id switch
                    {
                        "status" => "{}",
                        "sfx" => "{\"kind\":\"Coin\",\"name\":\"coin_pickup\",\"format\":\"WAV\"}",
                        "compose" => "{\"description\":\"Calm looping garden background music\",\"seconds\":60,\"mode\":\"loop\",\"name\":\"garden_bgm\",\"format\":\"OGG\"}",
                        "render" => "{\"projectFile\":\"CHOOSE_SAVED_ZAPBOP_PROJECT.json\",\"mode\":\"once\",\"format\":\"WAV\"}",
                        _ => "{\"projectFile\":\"CHOOSE_SAVED_ZAPBOP_PROJECT.json\"}"
                    },
                    _ => Example(c.Id)
                };
        };
        runs.SelectionChanged += (_, _) =>
        {
            if (runs.SelectedItem is ToolRun run)
                result.Text = $"{run.State}\n{run.Error}\nCONNECTION\n{run.ConnectionLog}\nINPUT\n{run.Input}\nOUTPUT\n{run.Output}";
        };
        flows.SelectionChanged += (_, _) =>
        {
            if (!refreshing && flows.SelectedItem is ToolWorkflow workflow)
                workflowEditor.Text = JsonSerializer.Serialize(workflow, Json);
        };
        tools.Changed += OnChanged;
        workflows.Changed += OnChanged;
        Refresh();
    }

    private static TextBox Editor(int height) => new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        Height = height,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto
    };
    private static TextBlock Title(string text) => new()
    {
        Text = text,
        FontSize = 23,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 0, 0, 14)
    };
    private static ScrollViewer Scroll(object content) => new()
    {
        Content = content,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto
    };
    private static WrapPanel Buttons(params (string Label, RoutedEventHandler Action)[] entries)
    {
        var panel = new WrapPanel();
        foreach (var entry in entries)
        {
            var button = new Button
            {
                Content = entry.Label
            };
            button.Click += entry.Action;
            panel.Children.Add(button);
        }

        return panel;
    }

    private async Task Guard(Func<Task> action)
    {
        if (busy)
        {
            status.Text = "Wait for the current operation or press Stop.";
            return;
        }

        busy = true;
        operationCancellation = new();
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            status.Text = "Operation stopped.";
        }
        catch (Exception ex)
        {
            status.Text = ex.Message;
            MessageBox.Show(ex.Message, "AI Employee Manager");
        }
        finally
        {
            operationCancellation?.Dispose();
            operationCancellation = null;
            busy = false;
            if (!disposed)
            {
                Refresh();
                Changed?.Invoke();
            }
        }
    }

    private void OnChanged() => Dispatcher.InvokeAsync(() =>
    {
        if (!disposed)
        {
            Refresh();
            Changed?.Invoke();
        }
    });
    public void Navigate(string page)
    {
        pages.SelectedIndex = page == "workflows" ? 1 : page == "updates" ? 2 : 0;
    }

    public void Refresh()
    {
        refreshing = true;
        toolChoice.ItemsSource = store.Tools();
        toolChoice.SelectedValue = selectedToolId;
        var runId = (runs.SelectedItem as ToolRun)?.Id;
        var flowId = (flows.SelectedItem as ToolWorkflow)?.Id;
        // "Ready" says whether a task can use the tool now (the manager starts its backend itself);
        // "Last check" is only the result of the last connection test.
        profiles.ItemsSource = store.Tools().Select(t => new { t.Id, t.Name, t.Connector, t.Enabled, Ready = readiness == null ? "" : readiness.Problem(t, null) is { } problem ? "No: " + problem : "Yes (starts automatically)", LastCheck = t.Status, t.Version, t.CheckedAt }).ToArray();
        profiles.SelectedItem = profiles.Items.Cast<object>().FirstOrDefault(v => v.GetType().GetProperty("Id")?.GetValue(v)?.ToString() == selectedToolId);
        runs.ItemsSource = store.ToolRuns();
        runs.SelectedItem = runs.Items.Cast<ToolRun>().FirstOrDefault(r => r.Id == runId);
        flows.ItemsSource = store.Workflows();
        flows.SelectedItem = flows.Items.Cast<ToolWorkflow>().FirstOrDefault(w => w.Id == flowId);
        refreshing = false;
    }

    private void ProfileSelected(object sender, SelectionChangedEventArgs e)
    {
        if (refreshing || profiles.SelectedItem == null)
            return;
        var id = profiles.SelectedItem.GetType().GetProperty("Id")?.GetValue(profiles.SelectedItem)?.ToString();
        var tool = store.Tools().Single(t => t.Id == id);
        selectedToolId = tool.Id;
        toolChoice.SelectedValue = tool.Id;
        name.Text = tool.Name;
        connector.SelectedItem = tool.Connector;
        source.Text = tool.SourcePath;
        workspace.Text = tool.WorkspaceRoot;
        executable.Text = tool.Executable;
        desktopExecutable.Text = tool.DesktopExecutable;
        endpoint.Text = tool.Endpoint;
        timeout.Text = tool.TimeoutSeconds.ToString();
        token.Clear();
        enabled.IsChecked = tool.Enabled;
        writes.IsChecked = tool.AllowWrites;
        models.IsChecked = tool.AllowModelCalls;
        publishing.IsChecked = tool.AllowPublishing;
        taskAutomation.IsChecked = tool.AllowTaskAutomation;
        autoStartBackend.IsChecked = tool.AutoStartBackend;
        backendExecutable.Text = tool.BackendExecutable;
        backendArguments.Text = JsonSerializer.Serialize(tool.BackendArguments);
        backendDirectory.Text = tool.BackendWorkingDirectory;
        backendData.Text = tool.BackendDataRoot;
        startupTimeout.Text = tool.BackendStartupTimeoutSeconds.ToString();
        operation.ItemsSource = ToolExecutionService.Capabilities(tool.Connector);
        operation.SelectedIndex = 0;
    }

    private void NewProfile(object sender, RoutedEventArgs e)
    {
        selectedToolId = null;
        name.Clear();
        source.Clear();
        workspace.Clear();
        executable.Clear();
        desktopExecutable.Clear();
        endpoint.Clear();
        token.Clear();
        backendExecutable.Clear();
        backendArguments.Text = "[]";
        backendDirectory.Clear();
        backendData.Clear();
        startupTimeout.Text = "90";
        autoStartBackend.IsChecked = true;
        connector.SelectedIndex = 0;
        enabled.IsChecked = writes.IsChecked = models.IsChecked = publishing.IsChecked = taskAutomation.IsChecked = false;
    }

    private async void SaveProfile(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        beforeMutation();
        var tool = store.Tools().FirstOrDefault(t => t.Id == selectedToolId) ?? new ConnectedTool();
        tool.Name = name.Text.Trim();
        tool.Connector = connector.SelectedItem as string ?? "";
        if (tool.Name.Length == 0 || ToolExecutionService.Capabilities(tool.Connector).Count == 0 || !int.TryParse(timeout.Text, out var seconds) || seconds is < 5 or > 7200)
            throw new InvalidOperationException("Enter a name, connector and a timeout of 5–7200 seconds.");
        if (!int.TryParse(startupTimeout.Text, out var startupSeconds) || startupSeconds is < 5 or > 600)
            throw new InvalidOperationException("Startup wait must be 5 to 600 seconds.");
        var arguments = JsonSerializer.Deserialize<string[]>(backendArguments.Text) ?? throw new InvalidOperationException("Backend arguments must be a JSON array, e.g. [\"server.mjs\"].");
        if (arguments.Any(a => a == null))
            throw new InvalidOperationException("Backend arguments must not contain null.");
        tool.AutoStartBackend = autoStartBackend.IsChecked == true;
        tool.BackendExecutable = backendExecutable.Text.Trim();
        tool.BackendArguments = arguments;
        tool.BackendWorkingDirectory = backendDirectory.Text.Trim();
        tool.BackendDataRoot = backendData.Text.Trim();
        tool.BackendStartupTimeoutSeconds = startupSeconds;
        tool.SourcePath = source.Text.Trim();
        tool.WorkspaceRoot = workspace.Text.Trim();
        tool.Executable = executable.Text.Trim();
        tool.DesktopExecutable = desktopExecutable.Text.Trim();
        tool.Endpoint = endpoint.Text.Trim();
        tool.TimeoutSeconds = seconds;
        tool.Enabled = enabled.IsChecked == true;
        tool.AllowWrites = writes.IsChecked == true;
        tool.AllowModelCalls = models.IsChecked == true;
        tool.AllowPublishing = publishing.IsChecked == true;
        tool.AllowTaskAutomation = taskAutomation.IsChecked == true;
        if (token.Password.Length > 0)
            tool.ProtectedToken = SecretProtection.Protect(token.Password);
        token.Clear();
        store.SaveTool(tool);
        selectedToolId = tool.Id;
        operation.ItemsSource = ToolExecutionService.Capabilities(tool.Connector);
        operation.SelectedIndex = 0;
        status.Text = "Saved. Older requests must be re-approved if the settings changed.";
        return Task.CompletedTask;
    });
    private async void LaunchDesktop(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        var tool = SelectedTool();
        var path = Path.GetFullPath(tool.DesktopExecutable);
        if (!File.Exists(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose the app's .exe file in the connector settings.");
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path)! });
        status.Text = "App opened as requested.";
        return Task.CompletedTask;
    });
    private ConnectedTool SelectedTool() => store.Tools().Single(t => t.Id == selectedToolId);
    private ToolRun SelectedRun() => runs.SelectedItem as ToolRun ?? throw new InvalidOperationException("Select a request in the Tools list.");
    private async void CheckTool(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        beforeMutation();
        await tools.CheckAsync(SelectedTool(), operationCancellation!.Token);
        status.Text = "Status/metadata read; no model was called.";
    });
    private async void CreateRequest(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        var tool = SelectedTool();
        var capability = operation.SelectedItem as ToolCapability ?? throw new InvalidOperationException("Select an operation.");
        var run = tools.Create(tool.Id, capability.Id, parameters.Text);
        Refresh();
        runs.SelectedItem = runs.Items.Cast<ToolRun>().First(r => r.Id == run.Id);
        status.Text = "Request prepared; approve the exact input before it writes data, calls AI or publishes.";
        return Task.CompletedTask;
    });
    private async void ApproveRequest(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        var run = SelectedRun();
        if (MessageBox.Show($"Approve operation {run.Operation}?\n\n{run.Input}", "Approve exact input", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            return Task.CompletedTask;
        tools.Approve(run.Id);
        return Task.CompletedTask;
    });
    private async void ExecuteRequest(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        await tools.ExecuteAsync(SelectedRun().Id, operationCancellation!.Token);
    });
    private async void ReviewRequest(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        tools.Review(SelectedRun().Id);
        return Task.CompletedTask;
    });
    private void NewWorkflow(object sender, RoutedEventArgs e)
    {
        var profile = store.Tools().FirstOrDefault(t => t.Connector == "video");
        var marketing = store.Tools().FirstOrDefault(t => t.Connector == "marketing");
        var project = store.Projects().FirstOrDefault();
        workflowEditor.Text = JsonSerializer.Serialize(new ToolWorkflow { Name = "Export video → prepare marketing", ProjectId = project?.Id ?? "CHOOSE_PROJECT_ID", Steps = [new() { Key = "video", ToolId = profile?.Id ?? "CHOOSE_TOOL_ID", Operation = "export", Input = new JsonObject { ["projectFolder"] = "D:/VideoProjects/MyTrailer" } }, new() { Key = "marketing", ToolId = marketing?.Id ?? "CHOOSE_MARKETING_TOOL_ID", Operation = "media.import", DependsOn = ["video"], Input = new JsonObject { ["project_id"] = "ENTER_PROJECT_ID_IN_MARKETING", ["path"] = new JsonObject { ["$from"] = "video.artifactPath" } } }] }, Json);
    }

    private async void ImportWorkflow(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Workflow JSON|*.json"
        };
        if (dialog.ShowDialog() == true)
        {
            if (new FileInfo(dialog.FileName).Length > 1_000_000)
                throw new InvalidOperationException("Workflow JSON is too large.");
            workflowEditor.Text = File.ReadAllText(dialog.FileName);
        }

        return Task.CompletedTask;
    });
    private async void SaveWorkflow(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        var workflow = JsonSerializer.Deserialize<ToolWorkflow>(workflowEditor.Text, Json) ?? throw new InvalidOperationException("Invalid workflow JSON.");
        workflows.Save(workflow);
        status.Text = "Plan saved; no operation was run.";
        return Task.CompletedTask;
    });
    private ToolWorkflow SelectedWorkflow() => flows.SelectedItem as ToolWorkflow ?? throw new InvalidOperationException("Select a workflow.");
    private async void RunWorkflow(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        await workflows.RunAsync(SelectedWorkflow().Id, operationCancellation!.Token);
    });
    private async void ConfirmWorkflow(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        workflows.Confirm(SelectedWorkflow().Id);
        return Task.CompletedTask;
    });
    private void StopClicked(object sender, RoutedEventArgs e)
    {
        operationCancellation?.Cancel();
        workflows.Stop();
        tools.StopAll();
    }

    public async Task StopAsync(bool waitForUiOperation = true)
    {
        operationCancellation?.Cancel();
        workflows.Stop();
        await tools.StopAndWaitAsync();
        while (workflows.IsBusy || (waitForUiOperation && busy))
            await Task.Delay(50);
    }

    public Task RunWorkflowTask(string workflowId) => Guard(async () =>
    {
        await workflows.RunAsync(workflowId, operationCancellation!.Token);
    });
    public void ConfirmWorkflowTask(string workflowId) => workflows.Confirm(workflowId);
    public void PauseAfterCurrent() => workflows.PauseAfterCurrent();
    private async void PrepareSelfUpdate(object sender, RoutedEventArgs e) => await PrepareUpdate(true);
    private async void PrepareToolUpdate(object sender, RoutedEventArgs e) => await PrepareUpdate(false);
    private Task PrepareUpdate(bool self) => Guard(async () =>
    {
        beforeMutation();
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose an existing build folder (nothing is built)"
        };
        if (dialog.ShowDialog() != true)
            return;
        var tool = self ? null : SelectedTool();
        var entry = self ? "AIEmployeeManager.exe" : Path.GetFileName(tool!.DesktopExecutable);
        if (!self && (!File.Exists(tool!.DesktopExecutable) || Path.GetFileNameWithoutExtension(tool.DesktopExecutable).StartsWith("python", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Choose the standalone Windows app's .exe; Python cannot be an update target.");
        if (!entry.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Tool update requires its installed .exe path, not the Python interpreter. Configure a separate desktop executable profile.");
        var target = self ? AppContext.BaseDirectory : Path.GetDirectoryName(Path.GetFullPath(tool!.DesktopExecutable))!;
        prepared = await Task.Run(() => UpdatePreparationService.Prepare(store, dataRoot, dialog.FolderName, target, self ? "AIEmployeeManager" : tool!.Id, entry, self ? new[] { dataRoot } : new[] { dataRoot, tool!.WorkspaceRoot }.Where(Directory.Exists).ToArray(), self));
        updatePreview.Text = File.ReadAllText(prepared.PlanFile);
        status.Text = $"Prepared {prepared.Files} files; nothing installed yet. Plan hash: {prepared.ApprovedHash}";
    });
    private async void ApplyUpdate(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        beforeMutation();
        var update = prepared ?? throw new InvalidOperationException("Prepare and review the manifest first.");
        using var frozen = freezeUpdate();
        if (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(update.PlanFile))) != update.ApprovedHash)
            throw new InvalidOperationException("The manifest changed; prepare again.");
        if (update.SelfUpdate)
        {
            var document = JsonNode.Parse(File.ReadAllText(update.PlanFile))!.AsObject();
            var fresh = Path.Combine(Path.GetDirectoryName(update.PlanFile)!, "manager-apply-" + Guid.NewGuid().ToString("N") + ".db");
            store.Backup(fresh);
            document["DatabaseBackup"] = fresh;
            var bytes = System.Text.Encoding.UTF8.GetBytes(document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllBytes(update.PlanFile, bytes);
            update = update with
            {
                ApprovedHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
            };
            prepared = update;
            updatePreview.Text = document.ToJsonString();
        }

        if (MessageBox.Show($"Apply {update.Files} files to:\n{update.Target}\n\nHash: {update.ApprovedHash}\nData is kept and the old version is backed up. The manager closes if it updates itself.", "Approve update", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        using var process = UpdatePreparationService.Launch(update, appRoot);
        if (update.SelfUpdate)
        {
            ClosingForUpdate = true;
            closeForUpdate();
            return;
        }

        await process.WaitForExitAsync();
        if (process.ExitCode == 0)
        {
            var id = JsonNode.Parse(File.ReadAllText(update.PlanFile))?["ApplicationId"]?.GetValue<string>();
            var tool = store.Tools().FirstOrDefault(t => t.Id == id);
            if (tool != null)
            {
                tool.Version = "Updated " + DateTimeOffset.UtcNow.ToString("O");
                tool.Status = "Updated; explicit connector check required";
                tool.CheckedAt = null;
                store.SaveTool(tool);
            }
        }

        status.Text = process.ExitCode == 0 ? "Files updated. Check the real connector/tool before use; backup: " + update.Backup : "Update failed; see the updater's backup/log.";
    });
    private void SeedTools()
    {
        foreach (var tool in new[]
        {
            new ConnectedTool
            {
                Id = "pixel-foundry",
                Name = "Pixel Art Tool",
                Connector = "pixel",
                SourcePath = @"D:\Projects\pixel-art-tool",
                Endpoint = "http://127.0.0.1:4317"
            },
            new ConnectedTool
            {
                Id = "trailerforge",
                Name = "TrailerForge",
                Connector = "video",
                SourcePath = @"D:\Projects\TrailerForge\outputs\TrailerForge",
                DesktopExecutable = @"D:\Projects\TrailerForge\TrailerForge\TrailerForge.exe",
                Executable = ProcessRunner.FindExecutable("python") ?? ""
            },
            new ConnectedTool
            {
                // Seeded disabled like the others; enable it and choose the allowed workspace in Tools.
                Id = "aivideostudio",
                Name = "AIVideoStudio",
                Connector = "aivideo",
                SourcePath = @"D:\Projects\AIVideoStudio\src",
                DesktopExecutable = @"D:\Projects\AIVideoStudio\AIVideoStudio\AIVideoStudio.exe",
                // PATH Python often lacks Pillow/imageio_ffmpeg/edge_tts; the TrailerForge venv has them (same engine).
                Executable = File.Exists(@"D:\Projects\TrailerForge\.venv\Scripts\python.exe") ? @"D:\Projects\TrailerForge\.venv\Scripts\python.exe" : ProcessRunner.FindExecutable("python") ?? ""
            },
            new ConnectedTool
            {
                // Seeded disabled like the others; enable it and choose the allowed workspace in Tools.
                Id = "zapbop",
                Name = "ZapBop Music Producer",
                Connector = "music",
                SourcePath = @"D:\Projects\MusicProducer",
                DesktopExecutable = @"D:\Projects\MusicProducer\build\ZapBop.exe",
                Executable = File.Exists(@"D:\Projects\MusicProducer\.venv\Scripts\python.exe") ? @"D:\Projects\MusicProducer\.venv\Scripts\python.exe" : ""
            },
            new ConnectedTool
            {
                Id = "rikerat",
                Name = "Rikerat Marketing",
                Connector = "marketing",
                SourcePath = @"D:\Projects\RikeratMarketingAgent",
                Endpoint = ""
            }
        }

        )
            if (store.Tools().All(t => t.Id != tool.Id))
                store.SaveTool(tool);
    }

    private static string Example(string operation) => operation switch
    {
        "generate" => "{\"action\":\"create\",\"prompt\":\"A small pixel-art fish\",\"outputWidth\":128,\"outputHeight\":128}",
        "export" => "{\"projectFolder\":\"D:/VideoProjects/MyTrailer\"}",
        "project.create" => "{\"name\":\"My Game\",\"description\":\"\",\"language\":\"vi\"}",
        "media.import" => "{\"project_id\":\"ID\",\"path\":\"D:/Videos/trailer.mp4\"}",
        "job.create" => "{\"project_id\":\"ID\",\"media_id\":\"ID\",\"platforms\":[\"youtube\"],\"export_mode\":\"reference\"}",
        "youtube.publish" => "{\"jobId\":\"ID\",\"body\":{\"confirmed\":true,\"privacy_status\":\"private\"}}",
        "content.set" => "{\"jobId\":\"ID\",\"body\":{\"content\":\"Draft caption\"}}",
        "review" => "{\"jobId\":\"ID\",\"body\":{\"gate\":\"content\",\"decision\":\"approve\",\"revision\":1}}",
        "job.status" or "job.run" or "schedule" => "{\"jobId\":\"ID\",\"body\":{}}",
        "inspect" => "{\"projectFolder\":\"D:/VideoProjects/MyTrailer\"}",
        _ => "{}"
    };
    public void Dispose()
    {
        disposed = true;
        tools.Dispose();
    }
}
