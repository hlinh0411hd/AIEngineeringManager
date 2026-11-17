using System.Text;
using System.Text.Json;
using Employee.Core;
using Employee.Infrastructure;

// Runs one goal through the real services (planner, session engine, workers, checks) without the
// desktop window, in an isolated data folder. THIS CALLS REAL AI PROVIDERS and spends their usage;
// it is never part of the normal check run.
//
//   Employee.Checks --live-goal <dataDir> <projectDir> <requestFile>
//       [--settings-from <manager.db>] [--plan-only] [--minutes N] [--providers a,b]
//
// --settings-from copies provider/automation settings (read from a private copy of that database).
static class LiveGoalRun
{
    public static async Task<int> Run(string[] args)
    {
        if (args.Length < 4)
        {
            Console.WriteLine("Usage: --live-goal <dataDir> <projectDir> <requestFile> [--settings-from <manager.db>] [--plan-only] [--minutes N] [--providers a,b]");
            return 2;
        }
        string? Option(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        var dataDir = Path.GetFullPath(args[1]);
        var projectDir = Path.GetFullPath(args[2]);
        var request = File.ReadAllText(args[3]).Trim();
        var planOnly = args.Contains("--plan-only");
        var minutes = int.TryParse(Option("--minutes"), out var m) ? m : 120;
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(projectDir);
        var transcript = Path.Combine(dataDir, "live-goal.log");
        void Say(string text)
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {text}";
            Console.WriteLine(line);
            File.AppendAllText(transcript, line + Environment.NewLine);
        }

        var first = !File.Exists(Path.Combine(dataDir, "manager.db"));
        using var store = new SqliteStore(Path.Combine(dataDir, "manager.db"));
        if (first && Option("--settings-from") is { } source)
        {
            var copy = Path.Combine(dataDir, "settings-source");
            Directory.CreateDirectory(copy);
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(source + suffix))
                    File.Copy(source + suffix, Path.Combine(copy, "manager.db" + suffix), true);
            using (var original = new SqliteStore(Path.Combine(copy, "manager.db")))
            {
                store.SaveSettings(original.LoadSettings());
                foreach (var tool in original.Tools())
                    store.SaveTool(tool);
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }

        // Only this isolated copy: the user's own database keeps its cooldowns and tool data.
        if (args.Contains("--clear-cooldowns"))
        {
            var reset = store.LoadSettings();
            foreach (var provider in reset.Providers)
                provider.CooldownUntil = null;
            store.SaveSettings(reset);
        }
        foreach (var tool in store.Tools().Where(t => t.Connector == "marketing"))
        {
            tool.BackendDataRoot = Path.Combine(dataDir, "tools", "rikerat");
            tool.Endpoint = "http://127.0.0.1:4339";
            store.SaveTool(tool);
        }
        var settings = store.LoadSettings();
        if (Option("--providers") is { } only)
            foreach (var provider in settings.Providers)
                provider.Enabled &= only.Split(',').Contains(provider.Id);
        store.SaveSettings(settings);

        var runner = new ProcessRunner();
        using var ollama = new OllamaProvider();
        IAIProvider[] providers = [.. ManagerSettings.DefaultProviderIds.Where(id => id != "ollama").Select(id => (IAIProvider)new CliProvider(id, runner)), ollama];
        var orchestrator = new Orchestrator(store, providers, runner);
        var pool = new TaskExecutionPool(store, store, orchestrator, runner);
        var appRoot = FindAppRoot();
        var skills = new ToolSkillRegistry(store, Path.Combine(appRoot, ".agents", "skills"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agents", "skills")) { AutomationDataRoot = dataDir };
        orchestrator.Skills = skills;
        using var tools = new ToolExecutionService(store, store, runner, appRoot, dataDir, paths => pool.ReserveExternal(paths, settings));
        orchestrator.ToolTasks = new ToolTaskExecutor(store, store, tools, skills, dataDir, (work, paths) => pool.ReserveExternal(paths, settings, work.ParentTaskId ?? throw new InvalidOperationException("Tool task has no parent goal.")));
        Say(skills.Status);

        var project = store.Projects().FirstOrDefault(p => string.Equals(p.Root, Path.TrimEndingDirectorySeparator(projectDir), StringComparison.OrdinalIgnoreCase))
            ?? new ProjectIndexer(store).Register(projectDir, Path.GetFileName(Path.TrimEndingDirectorySeparator(projectDir)), "");
        var health = await orchestrator.DetectAsync(settings);
        foreach (var h in health.Where(h => settings.Providers.Any(p => p.Id == h.Id && p.Enabled)))
            Say($"provider {h.Id}: canExecute={h.CanExecute} canWrite={h.CanWrite} cooldown={h.CooldownUntil?.ToLocalTime()}");

        var goal = store.WorkItems().FirstOrDefault(w => w.IsGoal && w.ProjectId == project.Id && w.Request == request && w.State != WorkState.Completed);
        if (goal == null)
        {
            goal = new WorkItem { State = WorkState.Saved, IsGoal = true, ProjectId = project.Id, Request = request, AutoAssign = true, AutomaticIntent = true, Kind = WorkKind.Coding, Access = AccessLevel.WriteProject, GrantedAccess = AccessLevel.WriteProject };
            store.SaveWork(goal);
            store.Log(goal.Id, "Created", "Task created by the live goal run. Access: " + goal.Access);
        }
        Say($"goal {goal.Id} in {project.Root} ({project.Technology})");
        // Like the user pressing "Skip step" on steps that stopped on an access problem.
        if (args.Contains("--skip-blocked"))
            foreach (var blocked in pool.Steps(store.WorkItems().Single(w => w.Id == goal.Id)).Where(s => s.State is WorkState.WrongAccess or WorkState.WaitingForPermission || s.State == WorkState.NeedsReview && StepStatus.Latest(store.Logs(s.Id), StepStatus.GaveUp) != null))
            {
                pool.SkipStep(goal, blocked.Id);
                Say("skipped blocked step " + (blocked.PlanKey ?? blocked.Id));
            }
        // Like the user writing a change request and pressing "Send & continue updating".
        if (Option("--comment") is { } comment)
        {
            goal = pool.AddComment(store.WorkItems().Single(w => w.Id == goal.Id), comment, [], continueUpdating: true);
            Say("comment sent: " + comment);
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(minutes));
        long seen = 0;
        var stopPrinting = false;
        void Drain()
        {
            var current = store.WorkItems().Single(w => w.Id == goal.Id);
            var ids = store.WorkItems().Where(w => w.Id == current.Id || (current.SessionId != null && w.SessionId == current.SessionId)).ToDictionary(w => w.Id);
            foreach (var log in ids.Keys.SelectMany(store.Logs).Where(l => l.Sequence > seen).OrderBy(l => l.Sequence))
            {
                seen = Math.Max(seen, log.Sequence);
                var work = ids[log.WorkId];
                var who = work.IsGoal ? "GOAL" : work.IsCoordinator ? "COORD" : work.PlanKey ?? work.Id[..6];
                var limit = log.Event is "Execution" or "ToolResult" ? 2500 : 700;
                var message = log.Event == "Execution" ? TaskReviewReport.ReadableOutput(log.Message) : log.Message;
                message = message.Length > limit ? message[..(limit / 4)] + "\n   […]\n" + message[^(limit * 3 / 4)..] : message;
                Say($"{who} {log.Event}: {message.Replace("\r", "")}");
            }
        }
        var printer = Task.Run(async () =>
        {
            while (!stopPrinting)
            {
                try { Drain(); } catch (Exception ex) { Say("log reader: " + ex.Message); }
                await Task.Delay(3000);
            }
        });
        if (planOnly)
            pool.Changed += () =>
            {
                var current = store.WorkItems().Single(w => w.Id == goal.Id);
                if (current.SessionId != null && store.WorkItems().Any(w => w.SessionId == current.SessionId && !w.IsGoal && !w.IsCoordinator))
                    cts.Cancel();
            };

        // Like the user pressing Run / resume while the task can still move by itself.
        for (var round = 1; round <= 6 && !cts.IsCancellationRequested; round++)
        {
            try
            {
                await pool.StartAsync(store.WorkItems().Single(w => w.Id == goal.Id), settings, health, token: cts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (InvalidOperationException ex)
            {
                Say("start refused: " + ex.Message);
                break;
            }
            var now = store.WorkItems().Single(w => w.Id == goal.Id);
            var session = pool.SessionFor(now);
            Say($"round {round} ended: goal={now.State} session={session?.State} — {session?.Notes}");
            if (planOnly || now.State is not (WorkState.Paused or WorkState.Queued) || session?.State is SessionState.BudgetReached or SessionState.Stopped)
                break;
        }

        stopPrinting = true;
        await printer;
        Drain();
        var final = store.WorkItems().Single(w => w.Id == goal.Id);
        var finalSession = pool.SessionFor(final);
        var summary = new StringBuilder();
        summary.AppendLine($"FINAL goal={final.State} verification={final.Verification} session={finalSession?.State} calls={finalSession?.CallBudgetDisplay} goalChecks={finalSession?.GoalChecks} verified={finalSession?.GoalVerified}");
        summary.AppendLine("notes: " + final.Notes);
        if (finalSession != null)
        {
            summary.AppendLine("phases: " + (finalSession.Phases.Count == 0 ? "(none)" : string.Join(" → ", finalSession.Phases.Select((p, i) => (i == finalSession.PhaseIndex ? "*" : "") + p.Name))));
            foreach (var plan in pool.Plans(final))
                summary.AppendLine($"plan {plan.State} ({plan.Plan.Tasks.Length} steps): {plan.Plan.Summary}");
        }
        foreach (var step in pool.Steps(final).OrderBy(w => w.CreatedAt))
            summary.AppendLine($"  [{step.State}] {step.PlanKey ?? step.Id[..6]} {step.Kind}/{step.Access} {step.ExecutorDisplay} {step.ProviderId}/{step.ModelId} attempts={step.Attempts} :: {step.RequestPreview[..Math.Min(220, step.RequestPreview.Length)]}");
        foreach (var decision in pool.Decisions(final))
            summary.AppendLine($"  pending {decision.Kind}: {decision.Message[..Math.Min(300, decision.Message.Length)]}");
        Say(summary.ToString());
        File.WriteAllText(Path.Combine(dataDir, "live-goal-plans.json"), JsonSerializer.Serialize(pool.Plans(final), new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static string FindAppRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder != null; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, "AIEmployeeManager.slnx")))
                return folder.FullName;
        return AppContext.BaseDirectory;
    }
}

// Starts each configured tool the way a task would (backend auto-start included) and calls its
// read-only operations, to prove the manager's routes match the real tools. No generation, no
// publishing, no AI usage.
//   Employee.Checks --tool-check <dataDir> --settings-from <manager.db>
static class LiveToolCheck
{
    public static async Task<int> Run(string[] args)
    {
        var dataDir = Path.GetFullPath(args[1]);
        var source = args[Array.IndexOf(args, "--settings-from") + 1];
        Directory.CreateDirectory(dataDir);
        var copy = Path.Combine(dataDir, "settings-source");
        Directory.CreateDirectory(copy);
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(source + suffix))
                File.Copy(source + suffix, Path.Combine(copy, "manager.db" + suffix), true);
        if (File.Exists(Path.Combine(dataDir, "manager.db")))
            File.Delete(Path.Combine(dataDir, "manager.db"));
        using var store = new SqliteStore(Path.Combine(dataDir, "manager.db"));
        using (var original = new SqliteStore(Path.Combine(copy, "manager.db")))
        {
            store.SaveSettings(original.LoadSettings());
            foreach (var tool in original.Tools())
                store.SaveTool(tool);
        }
        var appRoot = Directory.GetCurrentDirectory();
        var runner = new ProcessRunner();
        var skills = new ToolSkillRegistry(store, Path.Combine(appRoot, ".agents", "skills")) { AutomationDataRoot = dataDir };
        Console.WriteLine(skills.Status);
        var project = new ProjectInfo("check", "Tool check", dataDir);
        using var tools = new ToolExecutionService(store, store, runner, appRoot, dataDir, _ => new NoopLease());
        var failures = 0;
        foreach (var tool in store.Tools())
        {
            Console.WriteLine($"\n=== {tool.Name} ({tool.Connector}) source={tool.SourcePath} endpoint={tool.Endpoint}");
            try
            {
                skills.PrepareRequestedTools([tool], project);
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine("  PREPARE FAILED: " + ex.Message);
                continue;
            }
            foreach (var operation in ToolExecutionService.Capabilities(tool.Connector).Where(c => !c.Writes && !c.UsesAI && !c.Publishes && c.Id is not ("inspect" or "job.status" or "task.status")))
            {
                var run = tools.Create(tool.Id, operation.Id, "{}");
                using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                await tools.ExecuteAsync(run.Id, limit.Token);
                run = store.ToolRuns().Single(r => r.Id == run.Id);
                var output = run.Output ?? "";
                Console.WriteLine($"  {operation.Id}: {run.State} {(run.Error.Length > 0 ? "ERROR " + run.Error : output[..Math.Min(400, output.Length)])}");
                if (run.ConnectionLog.Length > 0)
                    Console.WriteLine("    " + run.ConnectionLog.Trim().Replace("\n", "\n    "));
                if (run.State != ToolRunState.Completed)
                    failures++;
            }
        }
        // Marketing routes that write, against a throwaway data folder and port (never the user's data).
        if (store.Tools().FirstOrDefault(t => t.Connector == "marketing") is { } marketing)
        {
            Console.WriteLine("\n=== Marketing write routes (isolated data folder)");
            await tools.StopAndWaitAsync();
            marketing.BackendDataRoot = Path.Combine(dataDir, "rikerat-data");
            marketing.Endpoint = "http://127.0.0.1:4329";
            marketing.AllowPublishing = true;
            store.SaveTool(marketing);
            async Task<(ToolRun Run, System.Text.Json.Nodes.JsonNode? Output)> Call(string operation, string input)
            {
                input = input.Replace('~', '"');
                var run = tools.Create(marketing.Id, operation, input);
                if (run.State == ToolRunState.WaitingApproval)
                    tools.Approve(run.Id);
                using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                await tools.ExecuteAsync(run.Id, limit.Token);
                run = store.ToolRuns().Single(r => r.Id == run.Id);
                Console.WriteLine($"  {operation}: {run.State} {(run.Error.Length > 0 ? run.Error[..Math.Min(260, run.Error.Length)] : (run.Output ?? "")[..Math.Min(260, (run.Output ?? "").Length)])}");
                return (run, run.Error.Length > 0 || string.IsNullOrEmpty(run.Output) ? null : System.Text.Json.Nodes.JsonNode.Parse(run.Output));
            }
            void Expect(bool ok, string what)
            {
                if (!ok) { failures++; Console.WriteLine("    MISMATCH: " + what); }
            }
            var created = await Call("project.create", "{~name~:~Tool check~,~description~:~route check~,~language~:~vi~}");
            Expect(created.Output?["id"] != null, "project.create returns the new project's id");
            Expect((await Call("projects", "{}")).Output is System.Text.Json.Nodes.JsonArray { Count: 1 }, "projects lists it");
            Expect((await Call("platforms", "{}")).Run.State == ToolRunState.Completed, "platforms");
            Expect((await Call("queue", "{}")).Run.State == ToolRunState.Completed, "queue");
            // Unknown ids must reach the route and get the backend's own NOT_FOUND, not "no such route" or 405.
            foreach (var (operation, input) in new[]
            {
                ("job.status", "{~jobId~:~nope~}"), ("job.run", "{~jobId~:~nope~}"), ("content.set", "{~jobId~:~nope~,~body~:{~content~:~x~}}"),
                ("review", "{~jobId~:~nope~,~body~:{~gate~:~content~,~decision~:~approve~,~revision~:1}}"),
                ("schedule", "{~jobId~:~nope~,~body~:{~scheduled_at~:null}}"), ("export", "{~jobId~:~nope~}"), ("task.status", "{~taskId~:~nope~}"),
                ("target.content", "{~targetId~:~nope~,~body~:{~fields~:{~caption~:~x~}}}"), ("target.schedule", "{~targetId~:~nope~,~body~:{~scheduled_at~:null}}"),
                ("target.publish", "{~targetId~:~nope~,~body~:{~confirmed~:true}}")
            })
                Expect((await Call(operation, input)).Run.Error.Contains("NOT_FOUND", StringComparison.Ordinal), operation + " reaches its route (expects the backend's NOT_FOUND)");
            var content = await Call("content.create", "{~project_id~:~nope~,~content_mode~:~create~,~prompt~:~route check only~,~platforms~:[~facebook~]}");
            Expect(content.Run.Error.Contains("NOT_FOUND", StringComparison.Ordinal), "content.create reaches its route and its body is accepted");
            var media = await Call("media.import", "{~project_id~:~nope~,~path~:~C:/missing.mp4~}");
            Expect(media.Run.Error.Contains("Tool HTTP 4", StringComparison.Ordinal) && !media.Run.Error.Contains("405", StringComparison.Ordinal) && !media.Run.Error.Contains("422", StringComparison.Ordinal), "media.import reaches its route with an accepted body shape");
            var job = await Call("job.create", "{~project_id~:~nope~,~media_id~:~nope~,~platforms~:[~youtube~]}");
            Expect(job.Run.Error.Contains("Tool HTTP 4", StringComparison.Ordinal) && !job.Run.Error.Contains("405", StringComparison.Ordinal) && !job.Run.Error.Contains("422", StringComparison.Ordinal), "job.create reaches its route with an accepted body shape");
        }
        Console.WriteLine($"\nRESULT: {failures} failure(s)");
        return failures == 0 ? 0 : 1;
    }
}
