using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Employee.Core;
using Employee.Infrastructure;

Console.OutputEncoding = new UTF8Encoding(false);
if (args.FirstOrDefault() == "--testing-workspace")
{
    var folder = Path.Combine(Path.GetTempPath(), "TestingWorkspaceChecks", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(folder);
    using var db = new SqliteStore(Path.Combine(folder, "checks.db"));
    db.SaveProject(new("p", "Project", folder));
    db.SaveProject(new("other", "Other", folder));
    var work = new WorkItem
    {
        ProjectId = "p",
        Request = "Changed source"
    };
    db.SaveWork(work);
    var runner = new NullRunner();
    var testing = new SmartTestingService(db, db, new Orchestrator(db, [], runner), runner);
    var scoped = new ProjectTestProfile
    {
        ProjectId = "p",
        Name = "Scoped",
        Executable = Environment.ProcessPath!,
        Enabled = true,
        SourcePaths = ["src/Core"]
    };
    var general = new ProjectTestProfile
    {
        ProjectId = "p",
        Name = "General",
        Executable = Environment.ProcessPath!,
        Enabled = true
    };
    var foreign = new ProjectTestProfile
    {
        ProjectId = "other",
        Name = "Foreign",
        Executable = Environment.ProcessPath!,
        Enabled = true
    };
    var disabled = new ProjectTestProfile
    {
        ProjectId = "p",
        Name = "Disabled",
        Executable = Environment.ProcessPath!
    };
    foreach (var profile in new[]
    {
        scoped,
        general,
        foreign,
        disabled
    }

    )
        db.SaveTestProfile(profile);
    if (!SmartTestingService.CoversChanges(scoped, ["src\\Core\\File.cs"]) || SmartTestingService.CoversChanges(scoped, ["src/CoreExtra/File.cs"]))
        throw new Exception("Path boundary mismatch");
    var updated = testing.Enqueue(work, ["src/UI/View.cs"], "update", force: true, updatedOnly: true);
    if (updated.Count != 1 || updated[0].ProfileId != general.Id)
        throw new Exception("Updated scope selected unrelated suite");
    var all = testing.Enqueue(work, ["project"], "full", force: true);
    if (all.Count != 2 || all.Any(j => j.ProjectId != "p"))
        throw new Exception("Full scope crossed project/disabled boundary");
    var selected = testing.Enqueue(work, ["project"], "selected", scoped.Id, true);
    if (selected.Count != 1 || selected[0].ProfileId != scoped.Id)
        throw new Exception("Selected suite scope mismatch");
    var workspacePassed = selected[0];
    workspacePassed.State = TestJobState.Passed;
    db.SaveTestJob(workspacePassed);
    if (testing.Enqueue(work, ["project"], "selected", scoped.Id, true).Single().Id == workspacePassed.Id)
        throw new Exception("Manual run reused cached pass");
    scoped.Arguments = ["changed"];
    db.SaveTestProfile(scoped);
    if (testing.Enqueue(work, ["project"], "selected", scoped.Id, true).Single().ProfileSignature == workspacePassed.ProfileSignature)
        throw new Exception("Changed command retained stale signature");
    if (db.TestProfiles().Single(p => p.Id == scoped.Id).SourcePaths.Single() != "src/Core")
        throw new Exception("Source scope did not persist");
    Console.WriteLine("PASS testing scopes, path boundaries, project isolation, disabled suites, manual rerun, command signatures and scope persistence; no test processes or AI invoked");
    return 0;
}

if (args.FirstOrDefault() == "--acceptance")
    return await AcceptanceChecks.Run();
if (args.FirstOrDefault() == "--snapshot")
{
    foreach (var shot in await PageSnapshot.TakeAsync(Path.GetFullPath(args[1]), args.Length > 2 ? args[2] : "manual", Path.Combine(Path.GetTempPath(), "EmployeeManager", "browser"), CancellationToken.None))
        Console.WriteLine(shot.Name + ": " + shot.Path);
    return 0;
}
if (args.FirstOrDefault() == "--tool-check")
    return await LiveToolCheck.Run(args);
if (args.FirstOrDefault() == "--live-goal")
    return await LiveGoalRun.Run(args);
if (args.FirstOrDefault() == "--tool-recovery")
    return ToolRecoveryChecks.Run();
if (args.FirstOrDefault() == "--virtual-office")
    return VirtualOfficeChecks.Run();
if (args.FirstOrDefault() == "--access-proposal")
    return AccessProposalChecks.Run();
if (args.FirstOrDefault() == "--task-rerun")
    return TaskRerunChecks.Run();
if (args.FirstOrDefault() == "--telegram")
    return TelegramChecks.Run();
if (args.FirstOrDefault() == "--git-push")
    return await GitAutoPushChecks.Run();
if (args.FirstOrDefault() == "--task-access")
    return TaskAccessChecks.Run();
if (args.FirstOrDefault() == "--task-lifecycle")
{
    TaskLifecycleEdgeChecks.Run();
    return TaskLifecycleChecks.Run();
}

if (args.FirstOrDefault()is "--assignment" or "--project-assignment")
{
    var folder = Path.Combine(Path.GetTempPath(), "AssignmentChecks", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(folder);
    using var db = new SqliteStore(Path.Combine(folder, "checks.db"));
    db.SaveProject(new("p", "fixture", folder));
    var indexer = new ProjectIndexer(db);
    db.SaveProject(new("other", "Other App", Path.Combine(folder, "other"), "second,shared"));
    db.SaveProject(new("third", "Third App", Path.Combine(folder, "third"), "shared"));
    foreach (var request in new[]
    {
        "Review FIXTURE",
        "Fix " + folder
    }

    )
        if (indexer.Resolve(request)?.Id != "p")
            throw new Exception("Named/path project was not resolved");
    if (indexer.Resolve("Update second")?.Id != "other")
        throw new Exception("Project alias was not resolved");
    foreach (var request in new[]
    {
        "fix fixtureExtra",
        "fix unknown",
        "fix shared",
        "fixture and second"
    }

    )
        if (indexer.Resolve(request) != null)
            throw new Exception("Unknown/ambiguous/substring project must require user selection");
    if (indexer.Resolve("fixture", db.Projects().Single(p => p.Id == "other"))?.Id != "other")
        throw new Exception("Explicit manual selection was replaced");
    if (args.FirstOrDefault() == "--project-assignment")
    {
        Console.WriteLine("PASS project names, aliases, paths, unknown/ambiguous matches, substring rejection and explicit selection; no AI invoked");
        return 0;
    }

    db.DeleteProject("other");
    db.DeleteProject("third");
    foreach (var json in new[]
    {
        "{\"kind\":\"Review\",\"access\":\"ReadProject\"}",
        "{\"kind\":\"Planning\",\"access\":\"WriteProject\"}",
        "{}"
    }

    )
    {
        var fake = new FakeProvider("codex")
        {
            Result = new(FailureKind.None, json)
        };
        var runner = new NullRunner();
        var flow = new UnifiedTaskService(db, db, new Orchestrator(db, [fake], runner), runner);
        var settings = new ManagerSettings
        {
            Mode = RoutingMode.SemiAutomatic
        };
        settings.Providers[0].Enabled = true;
        settings.Providers[0].Models.Add(new("", [WorkKind.Coding, WorkKind.Review]));
        var goal = new WorkItem
        {
            ProjectId = "p",
            Request = "Review files",
            AutoAssign = true,
            Access = AccessLevel.WriteProject,
            State = WorkState.Saved
        };
        db.SaveWork(goal);
        await flow.StartAsync(goal, settings, [new("codex", true, true, false, "")]);
        var saved = db.WorkItems().Single(w => w.Id == goal.Id);
        if (fake.Calls != 1 || flow.SessionFor(saved)!.CallsUsed != 1)
            throw new Exception("Classification call budget failed");
        if (json.Contains("Review"))
        {
            if (saved.AutoAssign || saved.Kind != WorkKind.Review || saved.Access != AccessLevel.ReadProject || saved.State != WorkState.AwaitingApproval)
                throw new Exception("Assignment or approval gate failed");
        }
        else if (!saved.AutoAssign || flow.Steps(saved).Count != 0)
            throw new Exception("Invalid assignment executed");
    }

    // Classification must also work when only the eventual task kind is configured.
    {
        var fake = new FakeProvider("codex")
        {
            Result = new(FailureKind.None, "{\"kind\":\"Review\",\"access\":\"ReadProject\"}")
        };
        var runner = new NullRunner();
        var flow = new UnifiedTaskService(db, db, new Orchestrator(db, [fake], runner), runner);
        var settings = new ManagerSettings
        {
            Mode = RoutingMode.SemiAutomatic
        };
        settings.Providers[0].Enabled = true;
        settings.Providers[0].Models.Add(new("", [WorkKind.Review]));
        var goal = new WorkItem
        {
            ProjectId = "p",
            Request = "Inspect the project",
            AutoAssign = true,
            Access = AccessLevel.WriteProject,
            State = WorkState.Saved
        };
        db.SaveWork(goal);
        await flow.StartAsync(goal, settings, [new("codex", true, true, false, "")]);
        var saved = db.WorkItems().Single(w => w.Id == goal.Id);
        if (fake.Calls != 1 || saved.AutoAssign || saved.Kind != WorkKind.Review || saved.Access != AccessLevel.ReadProject)
            throw new Exception("Review-only classification route failed");
    }

    foreach (var access in Enum.GetValues<AccessLevel>())
    {
        var fake = new FakeProvider("codex");
        var runner = new NullRunner();
        var flow = new UnifiedTaskService(db, db, new Orchestrator(db, [fake], runner), runner);
        var settings = new ManagerSettings
        {
            Mode = RoutingMode.SemiAutomatic
        };
        settings.Providers[0].Enabled = true;
        settings.Providers[0].AllowProjectWrite = true;
        settings.Providers[0].Models.Add(new("", [WorkKind.Research]));
        var goal = new WorkItem
        {
            ProjectId = "p",
            Request = "Manual task",
            AutoAssign = false,
            Kind = WorkKind.Research,
            Access = access,
            Priority = 73,
            State = WorkState.Saved
        };
        db.SaveWork(goal);
        await flow.StartAsync(goal, settings, [new("codex", true, true, true, "")]);
        var saved = db.WorkItems().Single(w => w.Id == goal.Id);
        if (fake.Calls != 0 || saved.AutoAssign || saved.Kind != WorkKind.Research || saved.Access != access || saved.Priority != 73 || flow.SessionFor(saved)!.AccessCeiling != access)
            throw new Exception("Manual settings were replaced or classification ran");
    }

    Console.WriteLine("PASS automatic assignment, review-only route, manual settings, budget, approval and invalid JSON gates; fake AI only");
    return 0;
}

if (args.FirstOrDefault() == "--step-skip")
    return await StepSkipChecks.Run();
if (args.FirstOrDefault() == "--task-comments")
    return TaskCommentChecks.Run();
if (args.FirstOrDefault() == "--task-queue")
    return await TaskQueueChecks.Run();
if (args.FirstOrDefault() == "--project-scan")
    return ProjectScanChecks.Run();
if (args.FirstOrDefault() == "--attachments")
{
    var folder = Path.Combine(Path.GetTempPath(), "EmployeeAttachmentChecks", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(folder);
    try
    {
        using (var db = new SqliteStore(Path.Combine(folder, "checks.db")))
        {
            var work = new WorkItem
            {
                Request = "fixture",
                Attachments = [new("note.txt", Encoding.UTF8.GetBytes("attachment evidence"))],
                Comments = [new("extra context", [new("image.png", [1, 2, 3])], DateTimeOffset.UtcNow)]
            };
            db.SaveWork(work);
        }

        using (var db = new SqliteStore(Path.Combine(folder, "checks.db")))
        {
            var saved = db.WorkItems().Single();
            if (!saved.Comments.Single().Attachments.Single().Content.SequenceEqual(new byte[] { 1, 2, 3 }) || !TaskAttachments.Context(saved).Contains("attachment evidence") || !TaskAttachments.Context(saved).Contains("extra context"))
                throw new Exception("Attachment/comment round trip failed");
            if (JsonSerializer.Deserialize<WorkItem>("{\"Request\":\"legacy\"}")!.Attachments.Count != 0)
                throw new Exception("Legacy compatibility failed");
            try
            {
                TaskAttachments.Validate([new("large", new byte[TaskAttachments.MaxFileBytes + 1])]);
                throw new Exception("Limit not enforced");
            }
            catch (InvalidOperationException)
            {
            }
        }

        Console.WriteLine("PASS attachment persistence, comments, context, legacy compatibility and size limit");
        return 0;
    }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(folder, true);
    }
}

if (args.FirstOrDefault() == "--child")
{
    await Task.Delay(TimeSpan.FromMinutes(2));
    return 0;
}

if (args.FirstOrDefault() == "--launcher-host")
{
    // Stands in for the manager: starts a backend through the launcher, then waits to be killed.
    var hostFolder = Path.Combine(Path.GetTempPath(), "EmployeeLauncherHost", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(hostFolder);
    var launcher = new ToolBackendLauncher { IdleShutdown = TimeSpan.FromHours(1) };
    var backend = new ConnectedTool
    {
        Id = "fake",
        Name = "Fake backend",
        Connector = "pixel",
        SourcePath = hostFolder,
        Endpoint = $"http://127.0.0.1:{args[1]}",
        AutoStartBackend = true,
        BackendExecutable = Environment.ProcessPath!,
        BackendArguments = ["--listen", args[1]],
        BackendStartupTimeoutSeconds = 30
    };
    _ = await launcher.EnsureAsync(backend, _ => Task.CompletedTask, _ => { }, CancellationToken.None);
    Console.WriteLine("READY");
    await Task.Delay(Timeout.Infinite);
    return 0;
}

if (args.FirstOrDefault() == "--listen-then-crash")
{
    // A backend that starts, says something, then dies on its own (like the Pixel Art server did).
    var crashing = new System.Net.Sockets.TcpListener(IPAddress.Loopback, int.Parse(args[1]));
    crashing.Start();
    Console.WriteLine("pixel server listening on " + args[1]);
    _ = Task.Run(async () =>
    {
        while (true)
        {
            using var client = await crashing.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            _ = await stream.ReadAsync(new byte[4096]);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
        }
    });
    await Task.Delay(1500);
    Console.Error.WriteLine("Error: EPERM: operation not permitted, open 'result.png'");
    return 3;
}

if (args.FirstOrDefault() == "--listen")
{
    // Minimal local HTTP backend for launcher checks: answers 200 until it is stopped.
    var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, int.Parse(args[1]));
    listener.Start();
    while (true)
    {
        using var client = await listener.AcceptTcpClientAsync();
        using var stream = client.GetStream();
        var buffer = new byte[4096];
        _ = await stream.ReadAsync(buffer);
        var reply = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(reply);
    }
}

if (args.FirstOrDefault() == "--echo")
{
    Console.Write(args[1]);
    return 0;
}

if (args.FirstOrDefault() == "--codex-write-check")
{
    var executable = ProcessRunner.ResolveCli("codex", "codex")?.Executable ?? throw new Exception("Codex missing");
    var mode = CodexWindowsSettings.ReadCurrentMode() ?? throw new Exception("Windows sandbox mode missing");
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var result = await new ProcessRunner().RunAsync(executable, CodexWindowsSettings.WriteProbeArguments(Path.GetFullPath(args[1]), mode), Path.GetFullPath(args[1]), null, 6000, null, deadline.Token);
    Console.WriteLine(result.Output);
    return result.ExitCode == 0 && result.Output.Contains("EMPLOYEE_PROJECT_WRITE_OK") ? 0 : 1;
}

if (args.FirstOrDefault() == "--detect-codex-without-path")
{
    Environment.SetEnvironmentVariable("PATH", Path.Combine(Path.GetTempPath(), "employee-empty-path"));
    var provider = new CliProvider("codex", new ProcessRunner());
    var result = await provider.DetectAsync(new() { Id = "codex" }, CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    return result.Installed && result.CanExecute ? 0 : 1;
}

if (args.FirstOrDefault() == "--local-evaluate")
{
    using var s = new SqliteStore(Path.Combine(args[1], "local-evaluation.db"));
    using var local = new OllamaProvider();
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
    var models = await local.ListModelsAsync(timeout.Token);
    if (models.Count == 0)
    {
        Console.WriteLine("No local models installed.");
        return 1;
    }

    var model = args.Length > 2 ? args[2] : models[0].Id;
    if (!models.Any(m => m.Id == model))
        throw new Exception("Requested local model is not installed.");
    Console.WriteLine("Evaluating installed model: " + model);
    var results = await new LocalEvaluator(local, s).EvaluateAsync(model, timeout.Token);
    Console.WriteLine(JsonSerializer.Serialize(new { Model = model, Results = results, QualificationScope = "Classification and Extraction only; not Coding", MemoryTelemetry = "Not measured" }, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

if (args.FirstOrDefault() == "--detect")
{
    using var s = new SqliteStore(Path.Combine(args[1], "diagnostics.db"));
    var runner = new ProcessRunner();
    using var local = new OllamaProvider();
    IAIProvider[] adapters = [..new[]
    {
        "codex",
        "claude",
        "copilot",
        "gemini",
        "opencode",
        "freebuff",
        "antigravity"
    }.Select(id => (IAIProvider)new CliProvider(id, runner)), local];
    var health = await new Orchestrator(s, adapters, runner).DetectAsync(s.LoadSettings());
    Console.WriteLine(JsonSerializer.Serialize(health, new JsonSerializerOptions { WriteIndented = true }));
    var models = await local.ListModelsAsync(new CancellationTokenSource(5000).Token);
    Console.WriteLine(JsonSerializer.Serialize(models));
    return 0;
}

Harness.Start(args);
if (args.FirstOrDefault() == "--general")
{
    await GeneralChecks.Run();
    return Harness.Failed == 0 ? 0 : 1;
}
await DataChecks.Run();
await RoutingChecks.Run();
await TaskFlowChecks.Run();
await GeneralChecks.Run();
// The self-contained check groups also run here, so none of them can quietly fall behind the code.
// Each can still be run alone with its flag.
async Task Group(string name, Func<Task<int>> run) => await Harness.Check("Check group " + name, async () => Harness.Assert(await run() == 0, name + " returned a failure code"));
await Group("--tool-recovery", () => Task.FromResult(ToolRecoveryChecks.Run()));
await Group("--virtual-office", () => Task.FromResult(VirtualOfficeChecks.Run()));
await Group("--access-proposal", () => Task.FromResult(AccessProposalChecks.Run()));
await Group("--task-rerun", () => Task.FromResult(TaskRerunChecks.Run()));
await Group("--telegram", () => Task.FromResult(TelegramChecks.Run()));
await Group("--git-push", GitAutoPushChecks.Run);
await Group("--task-access", () => Task.FromResult(TaskAccessChecks.Run()));
await Group("--task-lifecycle", () => { TaskLifecycleEdgeChecks.Run(); return Task.FromResult(TaskLifecycleChecks.Run()); });
await Group("--step-skip", StepSkipChecks.Run);
await Group("--task-comments", () => Task.FromResult(TaskCommentChecks.Run()));
await Group("--task-queue", TaskQueueChecks.Run);
await Group("--project-scan", () => Task.FromResult(ProjectScanChecks.Run()));
await Group("--acceptance", AcceptanceChecks.Run);
Console.WriteLine($"RESULT: {Harness.Passed} passed, {Harness.Failed} failed. Test data: {Harness.root}");
return Harness.Failed == 0 ? 0 : 1;
