using System.Text.Json;
using Employee.Core;
using Employee.Infrastructure;

// Milestones, acceptance criteria and the verify → fix → verify loop, with a scripted AI: no real
// provider is called. Run alone with --acceptance.
static class AcceptanceChecks
{
    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private static string LastPlanPrompt = "";

    private sealed record Outcome(SqliteStore Store, UnifiedTaskService Flow, WorkItem Goal, FakeProvider Ai, List<string> Calls);

    // testerPasses: for each tester call in order, whether every criterion passes.
    private static async Task<Outcome> Play(string name, Func<int, bool> testerPasses, Action<ManagerSettings>? configure = null, string request = "Hãy tự làm 1 game nhỏ", string? fixOutput = null)
    {
        var folder = Path.Combine(Path.GetTempPath(), "EmployeeAcceptanceChecks", Guid.NewGuid().ToString("N"));
        var projectRoot = Path.Combine(folder, "game");
        Directory.CreateDirectory(projectRoot);
        var db = new SqliteStore(Path.Combine(folder, "checks.db"));
        db.SaveProject(new("p", "Fixture game", projectRoot));
        var calls = new List<string>();
        int testers = 0, goalChecks = 0;
        var ai = new FakeProvider("codex");
        ai.Response = request =>
        {
            var text = request.Work.Request;
            if (text.StartsWith("Create a bounded plan", StringComparison.Ordinal) && text.Contains("GOAL CHECK", StringComparison.Ordinal))
            {
                goalChecks++;
                calls.Add("goalcheck" + goalChecks);
                return new(FailureKind.None, goalChecks == 1
                    ? JsonSerializer.Serialize(new { summary = "Core is done; plan the content milestone.", done = false, phase = 2, tasks = new[] { new { key = "c1", projectId = "p", request = "Add enemy waves", kind = "Coding", access = "WriteProject", dependsOn = Array.Empty<string>() } } })
                    : JsonSerializer.Serialize(new { summary = "Everything is in place.", done = true, phase = 2, tasks = Array.Empty<object>(), acceptance = new[] { "moved goalposts" } }));
            }
            if (text.StartsWith("Create a bounded plan", StringComparison.Ordinal))
            {
                calls.Add("plan");
                LastPlanPrompt = text;
                Assert((text.Contains("ACCEPTANCE (what", StringComparison.Ordinal) || !db.LoadSettings().Automation.VerifyAcceptance) && text.Contains(AutoCheck.DeclaredFile, StringComparison.Ordinal) && text.Contains("Installed and on PATH", StringComparison.Ordinal), "The planner is asked for acceptance criteria, a self-check and told what is installed");
                return new(FailureKind.None, JsonSerializer.Serialize(new
                {
                    summary = "Two milestones.",
                    acceptance = new[] { "The smoke test exits 0", "A simulated run can be won and lost" },
                    phases = new object[]
                    {
                        new { name = "Core", goal = "A playable core", acceptance = new[] { "The player moves", "Enemies spawn" } },
                        new { name = "Content", goal = "Waves and upgrades" }
                    },
                    tasks = new[] { new { key = "t1", projectId = "p", request = "Build the core loop", kind = "Coding", access = "WriteProject", dependsOn = Array.Empty<string>(), acceptance = "node tests/smoke.mjs exits 0" } }
                }));
            }
            if (text.StartsWith("ACCEPTANCE VERIFICATION", StringComparison.Ordinal))
            {
                testers++;
                // Seen live: after a goal check the task showed Paused while it went on running.
                if (goalChecks > 0)
                    Assert(db.Sessions().Single().State == SessionState.Running, "A goal check left the running session " + db.Sessions().Single().State);
                var pass = testerPasses(testers);
                calls.Add("tester" + testers + (pass ? ":pass" : ":fail"));
                var count = text.Split('\n').Count(line => System.Text.RegularExpressions.Regex.IsMatch(line, @"^\d+\. "));
                var verdicts = Enumerable.Range(1, count).Select(id => new { id, pass = pass || id != count, blocking = true, evidence = pass || id != count ? "ran it" : "simulated run: nothing spawned" });
                return new(FailureKind.None, "Report.\n" + AcceptanceCheck.Marker + " " + JsonSerializer.Serialize(new { criteria = verdicts, summary = pass ? "all good" : "one failure" }) + "\nSTEP STATUS: COMPLETE");
            }
            calls.Add(text.StartsWith("ACCEPTANCE FIX", StringComparison.Ordinal) ? "fix" : "work:" + request.Work.PlanKey);
            if (request.Work.PlanKey == "t1")
                Assert(request.Context.Contains("ACCEPTANCE FOR THIS STEP", StringComparison.Ordinal) && request.Context.Contains("node tests/smoke.mjs exits 0", StringComparison.Ordinal), "A step is told how its own result is checked");
            if (fixOutput != null && text.StartsWith("ACCEPTANCE FIX", StringComparison.Ordinal))
                return new(FailureKind.None, fixOutput);
            return new(FailureKind.None, "Did the work.\nSTEP STATUS: COMPLETE");
        };
        var runner = new NullRunner();
        var flow = new UnifiedTaskService(db, db, new Orchestrator(db, [ai], runner), runner);
        var settings = new ManagerSettings();
        settings.Automation.SignOffMilestones = false; // these checks run straight through; sign-off has its own check
        settings.Providers[0].Enabled = true;
        settings.Providers[0].AllowProjectWrite = true;
        settings.Providers[0].Models.Add(new("", Enum.GetValues<WorkKind>()));
        configure?.Invoke(settings);
        db.SaveSettings(settings);
        var goal = new WorkItem { ProjectId = "p", IsGoal = true, State = WorkState.Saved, Request = request, AutoAssign = true, AutomaticIntent = true, Access = AccessLevel.WriteProject, GrantedAccess = AccessLevel.WriteProject };
        db.SaveWork(goal);
        await flow.StartAsync(goal, settings, [new("codex", true, true, true, "")]);
        Console.WriteLine($"  {name}: {string.Join(" → ", calls)}");
        return new(db, flow, db.WorkItems().Single(w => w.Id == goal.Id), ai, calls);
    }

    // Work that is not a game and is done through connected tools (video, music, marketing): the
    // planner may choose a tool, never a publishing operation, and a failed criterion on a tool-made
    // result goes back to the coordinator, which can plan the tool again.
    private static async Task ToolWork()
    {
        var folder = Path.Combine(Path.GetTempPath(), "EmployeeAcceptanceChecks", Guid.NewGuid().ToString("N"));
        var projectRoot = Path.Combine(folder, "campaign");
        Directory.CreateDirectory(projectRoot);
        using var db = new SqliteStore(Path.Combine(folder, "checks.db"));
        db.SaveProject(new("p", "Campaign", projectRoot));
        // A video tool that is installed but switched off: the planner may still pick it, and it is switched on for the task.
        var videoSource = Path.Combine(folder, "trailerforge");
        Directory.CreateDirectory(videoSource);
        File.WriteAllText(Path.Combine(videoSource, "editor_core.py"), "");
        db.SaveTool(new ConnectedTool { Id = "trailerforge", Name = "TrailerForge", Connector = "video", SourcePath = videoSource, Executable = Environment.ProcessPath! });
        db.SaveTool(new ConnectedTool { Id = "rikerat", Name = "Rikerat", Connector = "marketing", Endpoint = "http://127.0.0.1:4399", BackendExecutable = Environment.ProcessPath!, Enabled = true, AllowWrites = true, AllowModelCalls = true, AllowPublishing = true, AllowTaskAutomation = true });
        db.SaveTool(new ConnectedTool { Id = "zapbop", Name = "ZapBop Music Producer", Connector = "music", Enabled = true, AllowWrites = true, AllowTaskAutomation = false });
        var skillsFolder = AppContext.BaseDirectory;
        while (skillsFolder != null && !Directory.Exists(Path.Combine(skillsFolder, ".agents", "skills", "employee-video")))
            skillsFolder = Path.GetDirectoryName(skillsFolder);
        if (skillsFolder == null && Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), ".agents", "skills", "employee-video")))
            skillsFolder = Directory.GetCurrentDirectory();
        Assert(skillsFolder != null, "The connector skills are in the repository");
        var calls = new List<string>();
        var testers = 0;
        var ai = new FakeProvider("codex");
        ai.Response = request =>
        {
            var text = request.Work.Request;
            if (text.StartsWith("ACCEPTANCE VERIFICATION", StringComparison.Ordinal))
            {
                testers++;
                calls.Add("tester" + testers);
                Assert(text.Contains("Files made by a tool", StringComparison.Ordinal) && text.Contains("Documents and content", StringComparison.Ordinal), "The tester is told how to check media and documents, not only software");
                return new(FailureKind.None, AcceptanceCheck.Marker + " " + JsonSerializer.Serialize(new { criteria = new[] { new { id = 1, pass = testers > 1, blocking = true, evidence = testers > 1 ? "ffprobe: 30.0 s" : "ffprobe: trailer.mp4 is 12.4 s" } }, summary = "" }) + "\nSTEP STATUS: COMPLETE");
            }
            calls.Add("work:" + request.Work.PlanKey);
            return new(FailureKind.None, "Done.\nSTEP STATUS: COMPLETE");
        };
        var runner = new NullRunner();
        var orchestrator = new Orchestrator(db, [ai], runner);
        var registry = new ToolSkillRegistry(db, Path.Combine(skillsFolder!, ".agents", "skills"));
        orchestrator.Skills = registry;
        using var toolService = new ToolExecutionService(db, db, runner, folder, folder, _ => new NoopLease());
        orchestrator.ToolTasks = new ToolTaskExecutor(db, db, toolService, registry, folder, (_, _) => new NoopLease());
        var settings = new ManagerSettings();
        settings.Automation.SignOffMilestones = false; // these checks run straight through; sign-off has its own check
        settings.Providers[0].Enabled = true;
        settings.Providers[0].AllowProjectWrite = true;
        settings.Providers[0].Models.Add(new("", Enum.GetValues<WorkKind>()));
        db.SaveSettings(settings);

        var goal = new WorkItem { ProjectId = "p", IsGoal = true, State = WorkState.Paused, Request = "Làm trailer 30 giây cho chiến dịch ra mắt và soạn 3 bài đăng", Access = AccessLevel.WriteProject, GrantedAccess = AccessLevel.WriteProject };
        var session = new WorkSession { RootWorkId = goal.Id, Goal = goal.Request, ProjectIds = ["p"], AccessCeiling = AccessLevel.WriteProject, AutoContinueSteps = true, Away = true, VerifyAcceptance = true, Acceptance = ["exports/trailer.mp4 exists and is 25-35 seconds long"] };
        goal.SessionId = session.Id;
        db.SaveSession(session);
        db.SaveWork(goal);

        // The planner picks tools the user did not name.
        var planning = new PlanningService(db, db, orchestrator);
        PlannedTask Tool(string key, string toolId, string skill, string operation) => new(key, "p", "Use the tool", WorkKind.Content, AccessLevel.ReadProject, [], SkillId: skill, Tool: new(toolId, skill, operation, new System.Text.Json.Nodes.JsonObject()));
        bool Accepted(PlannedTask step)
        {
            try { planning.Import(session, new("plan", [step])); return true; }
            catch (InvalidOperationException) { return false; }
        }
        Assert(Accepted(Tool("v1", "trailerforge", "employee-video", "export")) && Accepted(Tool("m1", "rikerat", "employee-marketing", "content.set")), "The planner may choose enabled tools with task automation, for any kind of work");
        Assert(!Accepted(Tool("m2", "rikerat", "employee-marketing", "youtube.publish")), "The planner may never choose a publishing operation by itself");
        var prepared = PlanningService.NormalizeToolReferences(new("p", [new("prep", "p", "prepare", WorkKind.Research, AccessLevel.ReadProject, []), new("call", "p", "call", WorkKind.Content, AccessLevel.ReadProject, [], SkillId: "employee-marketing", Tool: new("rikerat", "employee-marketing", "content.create", null, "prep"))]));
        Assert(prepared.Tasks.Single(t => t.Key == "call").DependsOn.SequenceEqual(["prep"]), "A tool step reading a preparation step depends on it even when the planner forgot to say so");
        // The planner wrote the skill id as toolId and left skillId empty (seen with a real model): resolved, not rejected.
        Assert(Accepted(Tool("m5", "employee-marketing", "", "project.create")), "A tool step naming the skill instead of the tool is resolved to the configured tool");
        Assert(registry.ToolGuides(null).Contains("project.create", StringComparison.Ordinal) && registry.ToolGuides(null).Contains("skillId employee-marketing", StringComparison.Ordinal), "The planner sees the input guide of usable tools");
        Assert(!Accepted(Tool("m3", "rikerat", "employee-marketing", "target.publish")) && Accepted(Tool("m4", "rikerat", "employee-marketing", "content.create")), "Publishing one post is never the planner's choice; creating draft content is");
        Assert(db.Tools().Single(t => t.Id == "trailerforge") is { Enabled: true, AllowTaskAutomation: true, AllowWrites: true, AllowPublishing: false }, "A tool the planner picked is switched on for the task, without publishing rights");
        Assert(registry.Problem(db.Tools().Single(t => t.Id == "zapbop"), null) != null && registry.Catalog("make a trailer").Contains("\"Usable\":true", StringComparison.Ordinal), "The planner sees whether a tool can run, not a stale connection check");
        Assert(!Accepted(Tool("z1", "zapbop", "employee-music", "export")), "A tool that is switched off and cannot run here (no source) is not the planner's to choose");
        settings.Automation.PlannerChoosesTools = false;
        db.SaveSettings(settings);
        Assert(!Accepted(Tool("v2", "trailerforge", "employee-video", "export")), "With the setting off a tool must be named by the user");
        settings.Automation.PlannerChoosesTools = true;
        db.SaveSettings(settings);

        // A finished tool step whose output misses a criterion: the coordinator plans the redo.
        var export = new WorkItem { ProjectId = "p", SessionId = session.Id, ParentTaskId = goal.Id, PlanKey = "v1", Kind = WorkKind.Content, Access = AccessLevel.ReadProject, State = WorkState.Completed, SkillId = "employee-video", Tool = new("trailerforge", "employee-video", "export", new System.Text.Json.Nodes.JsonObject()), Request = "Export the trailer" };
        db.SaveWork(export);
        var engine = new SessionEngine(db, db, orchestrator, new SmartTestingService(db, db, orchestrator, runner));
        string? replanned = null;
        engine.AcceptanceReplan = (s, failures, _) =>
        {
            replanned = failures;
            calls.Add("replan");
            db.SaveWork(new WorkItem { ProjectId = "p", SessionId = s.Id, ParentTaskId = goal.Id, PlanKey = "redo", Kind = WorkKind.Content, Access = AccessLevel.WriteProject, Request = "Re-cut the trailer project to 30 seconds" });
            return Task.FromResult(true);
        };
        // An ordinary task (no milestones, not a new build) gets no tester, even with criteria.
        await engine.RunAsync(session.Id, settings, [new("codex", true, true, true, "")]);
        Assert(calls.Count == 0 && db.Sessions().Single(s => s.Id == session.Id).AcceptedScopes.Count == 0, "An ordinary task ran a tester: " + string.Join(",", calls));
        // The same work as part of a milestone plan is verified.
        var withPhases = db.Sessions().Single(s => s.Id == session.Id);
        withPhases.Phases = [new("Launch kit", "trailer and posts")];
        withPhases.GoalVerified = true;
        db.SaveSession(withPhases);
        await engine.RunAsync(session.Id, settings, [new("codex", true, true, true, "")]);
        Console.WriteLine("  tool work: " + string.Join(" → ", calls));
        var after = db.Sessions().Single(s => s.Id == session.Id);
        var steps = db.WorkItems().Where(w => w.SessionId == session.Id && !w.IsGoal).ToArray();
        // The milestone (never accepted) is tested before the goal: tester1 fails it, the redo makes tester2 pass it, tester3 checks the goal.
        Assert(string.Join(",", calls) == "tester1,replan,work:redo,tester2,tester3", "Unexpected order: " + string.Join(",", calls) + " / " + after.Notes);
        Assert(replanned != null && replanned.Contains("12.4 s", StringComparison.Ordinal) && !steps.Any(s => s.PlanKey?.StartsWith("accfix", StringComparison.Ordinal) == true), "The failure goes to the coordinator with the tester's evidence, not to a plain AI fix step");
        Assert(steps.Where(s => s.VerifyScope != null).All(s => s.DependsOn.Contains(export.Id)), "The tester receives the tool step's result to open and check");
        Assert(after.State == SessionState.Completed && after.AcceptedScopes.SequenceEqual(["phase:0", "goal"]) && after.AcceptanceStalled == null, "The tool-made result is accepted after the redo: " + after.Notes);

        // A change request after acceptance: the result is tested again.
        var flow = new UnifiedTaskService(db, db, orchestrator, runner);
        goal = db.WorkItems().Single(w => w.Id == goal.Id);
        goal.State = WorkState.NeedsReview;
        db.SaveWork(goal);
        flow.AddComment(goal, "Thêm phụ đề cho trailer", [], continueUpdating: true);
        after = db.Sessions().Single(s => s.Id == session.Id);
        Assert(!after.AcceptedScopes.Contains("goal") && after.AcceptanceStalled == null, "An update after acceptance must be verified again");
    }

    // A PNG of the given size filled with one colour (for the art review checks).
    private static byte[] Png(int width, int height, (byte R, byte G, byte B, byte A) colour)
    {
        static void Chunk(Stream output, string type, byte[] data)
        {
            var length = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
            output.Write(length);
            var body = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            output.Write(body);
            var crc = new byte[4];
            var value = 0xFFFFFFFFu;
            foreach (var b in body)
            {
                value ^= b;
                for (var k = 0; k < 8; k++)
                    value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            }
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, value ^ 0xFFFFFFFFu);
            output.Write(crc);
        }
        var header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; header[9] = 6;
        var raw = new MemoryStream();
        for (var y = 0; y < height; y++)
        {
            raw.WriteByte(0);
            for (var x = 0; x < width; x++)
                raw.Write([colour.R, colour.G, colour.B, colour.A]);
        }
        var packed = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(packed, System.IO.Compression.CompressionLevel.Fastest, true))
            z.Write(raw.ToArray());
        var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", packed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    // Seen live (HonkAndBonk): "research the hot games and change the game after them" ended with a
    // design document and a few readability tweaks; one character was animated from a poor earlier
    // output and passed a tester who checked only sizes; the tool's own "isolated pixels" warning was ignored.
    private static async Task ResearchAndArt()
    {
        const string live = "nghiên cứu rồi chỉnh sửa game dựa theo các game incremental hot hiện này về cả lối chơi và UI UX. sử dụng tool pixel art để vẽ nhân vật, gui và anim cho chúng.";
        Assert(TaskIntent.IsResearchDriven(live) && TaskIntent.IsResearchDriven("Research top idle games and improve our upgrade screen"), "Research meant to change the project is recognised");
        foreach (var text in new[] { "nghiên cứu cơ chế combat của các game roguelike", "sửa lỗi nhảy trong game", "research the market for cat toys" })
            Assert(!TaskIntent.IsResearchDriven(text), "Not research-driven change: " + text);
        var run = await Play("research-driven", _ => true, request: live);
        var session = run.Flow.SessionFor(run.Goal)!;
        Assert(LastPlanPrompt.Contains("RESEARCH-DRIVEN CHANGES", StringComparison.Ordinal) && LastPlanPrompt.Contains("ADOPT", StringComparison.Ordinal), "The planner is told the research must become changes to build");
        Assert(LastPlanPrompt.Contains("MILESTONE SIZE", StringComparison.Ordinal) && LastPlanPrompt.Contains("never put making an asset and the work that depends on it", StringComparison.Ordinal), "The planner is told to keep milestones small, with an asset checked before work that builds on it");
        Assert(session.Acceptance.Contains(PlanningService.AdoptCriterion), "The whole goal is done only when the adopted changes are in the project: " + string.Join(" | ", session.Acceptance));
        var plain = await Play("plain build", _ => true);
        Assert(!plain.Flow.SessionFor(plain.Goal)!.Acceptance.Contains(PlanningService.AdoptCriterion) && !LastPlanPrompt.Contains("RESEARCH-DRIVEN", StringComparison.Ordinal), "An ordinary build gets no ADOPT criterion");

        // Seen live: with no planner chosen in Settings, gpt-6-luna (the cheapest) planned this goal as
        // an ordinary coding step of difficulty 50. Planning is routed as planning, hard for large goals.
        {
            var routing = new ManagerSettings();
            var codex = routing.Providers.First(p => p.Id == "codex");
            codex.Enabled = true;
            codex.AllowProjectWrite = true;
            codex.Models.Clear();
            foreach (var model in new[] { "gpt-6-luna", "gpt-6.1-terra", "gpt-6-sol" })
                codex.Models.Add(new(model, Enum.GetValues<WorkKind>()));
            var health = new[] { new ProviderHealth("codex", true, true, true, "") };
            var router = new ModelRouter();
            var large = router.Choose(UnifiedTaskService.PlannerSample(live, AccessLevel.ReadProject), routing, health, []);
            var small = router.Choose(UnifiedTaskService.PlannerSample("đổi chữ nút Mua thành Nâng cấp", AccessLevel.ReadProject), routing, health, []);
            Console.WriteLine($"  planner: large goal → {large?.ModelId}, small request → {small?.ModelId}");
            // The tester (it decides whether a milestone is done, and judges art by eye) is not the cheapest model either.
            WorkItem Tester(bool art) => new() { Kind = WorkKind.Coding, Access = AccessLevel.WriteProject, CapabilityHint = SessionEngine.TesterCapability(art), Difficulty = SessionEngine.TesterDifficulty(art), VerifyScope = "phase:0", Request = "ACCEPTANCE VERIFICATION" };
            var tester = router.Choose(Tester(false), routing, health, []);
            var artTester = router.Choose(Tester(true), routing, health, []);
            Console.WriteLine($"  tester: {tester?.ModelId}, with art review: {artTester?.ModelId}");
            Assert(tester != null && tester.ModelId != "gpt-6-luna" && artTester != null && artTester.ModelId != "gpt-6-luna", "The tester is not the cheapest model: " + tester?.Reason);
            Assert(large is { ModelId: "gpt-6-sol" }, "A large research-driven goal is planned by the strong model: " + large?.Reason);
            Assert(small != null && small.ModelId != "gpt-6-luna", "Even a small plan is not given to the cheapest model: " + small?.Reason);
        }

        // Seen live: the fix for "compare monster-fighting incremental games without offline progress"
        // found none and stopped the task. Such a criterion goes to the user as a known issue; the run goes on.
        var unreachable = await Play("unreachable criterion", tester => tester != 1, fixOutput: "Added matrix B1b.\nSTEP STATUS: BLOCKED: chưa có nguồn xác nhận game incremental đánh quái nào không có offline; Steam chặn truy vấn review, the user must decide.");
        var waived = unreachable.Flow.SessionFor(unreachable.Goal)!;
        Assert(waived.State == SessionState.Completed && waived.AcceptedScopes.Contains("phase:0") && waived.AcceptanceStalled == null, "The run goes on past a criterion only the user can settle: " + waived.Notes);
        Assert(waived.KnownIssues.Any(k => k.Contains("NOT MET, needs your decision", StringComparison.Ordinal)) && unreachable.Store.Logs(unreachable.Goal.Id).Any(l => l.Event == "AcceptanceWaived"), "The criterion is reported to the user as not met");
        // A fix that failed for lack of skill is not waived (the "never passes" run below still stalls).

        // The tool's own remaining problems are read from its result.
        Assert(ToolTaskExecutor.QualityProblems("""{"result":{"quality":{"problems":[{"note":"frames 2, 3, 4 still have many isolated pixels"}]}}}""").Single().Contains("isolated pixels", StringComparison.Ordinal)
            && ToolTaskExecutor.QualityProblems("""{"result":{"quality":{"problems":[]}}}""").Count == 0 && ToolTaskExecutor.QualityProblems("not json").Count == 0, "Remaining quality problems are read from the tool result");

        // Art review: the new PNGs of the task next to the project's art from before it, in one picture.
        var folder = Path.Combine(Path.GetTempPath(), "EmployeeAcceptanceChecks", Guid.NewGuid().ToString("N"));
        var root = Path.Combine(folder, "game");
        Directory.CreateDirectory(Path.Combine(root, "Assets", "Art"));
        Directory.CreateDirectory(Path.Combine(root, "Assets", "AIEmployeeGenerated"));
        Directory.CreateDirectory(Path.Combine(root, "Library"));
        var original = Path.Combine(root, "Assets", "Art", "goose.png");
        File.WriteAllBytes(original, Png(96, 64, (240, 240, 230, 255)));
        File.SetLastWriteTimeUtc(original, DateTime.UtcNow.AddDays(-3));
        File.WriteAllBytes(Path.Combine(root, "Library", "cache.png"), Png(512, 512, (0, 0, 0, 255)));
        File.SetLastWriteTimeUtc(Path.Combine(root, "Library", "cache.png"), DateTime.UtcNow.AddDays(-3));
        var made = Path.Combine(root, "Assets", "AIEmployeeGenerated", "idle.png");
        File.WriteAllBytes(made, Png(256, 64, (250, 160, 40, 255)));
        using var db = new SqliteStore(Path.Combine(folder, "checks.db"));
        db.SaveProject(new("p", "Goose", root));
        var artSession = new WorkSession { Goal = live, ProjectIds = ["p"] };
        var step = new WorkItem { ProjectId = "p", SessionId = artSession.Id, PlanKey = "t2", State = WorkState.Completed, Request = "Animate the goose idle", Tool = new ToolTaskSpec("pixel-foundry", "employee-pixel-art", "generate") };
        db.SaveWork(step);
        db.Log(step.Id, "ImageImported", JsonSerializer.Serialize(new { projectAssetPath = made }));
        var newArt = ArtReview.Made(db, artSession);
        var reference = ArtReview.Reference(db, db, artSession, root, DateTimeOffset.UtcNow.AddDays(-1), Path.Combine(folder, "sources"));
        Assert(newArt.Single().Path == made && newArt[0].Label.StartsWith("t2:", StringComparison.Ordinal), "The task's new PNG is shown, labelled with its step");
        Assert(reference.Single().Path == original, "The reference is the project's own art from before the task, not caches or generated files: " + string.Join(", ", reference.Select(r => r.Path)));
        var sheet = await PageSnapshot.ArtSheetAsync(reference, newArt, Path.Combine(root, ".aiemployee", "screenshots", "art-review-1.png"), Path.Combine(folder, "browser"), CancellationToken.None);
        if (PageSnapshot.Browser() == null)
            Console.WriteLine("  art review: no Edge/Chrome here, image not checked");
        else
        {
            var size = sheet == null ? default : PageSnapshot.PngSize(sheet) ?? default;
            Assert(size.Width == 1400 && size.Height >= 400, "The art review image is made with a real browser");
            var bytes = File.ReadAllBytes(sheet!);
            // The new art is drawn 4x (64 px tall → 256), so its orange fills a large area of the picture.
            var orange = Enumerable.Range(0, 40).Select(i => PngPixel(bytes, 30 + i * 20, 100 + i * 12)).Count(p => p.R > 230 && p.G is > 130 and < 190 && p.B < 80);
            Console.WriteLine($"  art review: {size.Width}x{size.Height}, {orange} of 40 probes on the new art");
            Assert(orange > 0, "The new art appears in the review image");
        }
    }

    // Seen live (HonkAndBonk): the coordinator kept answering "phase 1" while it planned later work,
    // then said done. Milestone 1 was tested four times and milestones 2 and 4 never. The manager now
    // moves the milestone itself: each milestone is tested once, in order, then the whole goal.
    private static async Task MilestoneOrder(bool signOff = false)
    {
        var folder = Path.Combine(Path.GetTempPath(), "EmployeeAcceptanceChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "game"));
        using var db = new SqliteStore(Path.Combine(folder, "checks.db"));
        db.SaveProject(new("p", "Fixture game", Path.Combine(folder, "game")));
        var tested = new List<string>();
        var goalChecks = 0;
        var ai = new FakeProvider("codex");
        ai.Response = request =>
        {
            var text = request.Work.Request;
            if (text.StartsWith("Create a bounded plan", StringComparison.Ordinal) && text.Contains("GOAL CHECK", StringComparison.Ordinal))
            {
                goalChecks++;
                // Always "phase 1"; after two rounds of work, "done".
                return new(FailureKind.None, goalChecks <= 2
                    ? JsonSerializer.Serialize(new { summary = "More work " + goalChecks + ": " + Guid.NewGuid(), done = false, phase = 1, tasks = new[] { new { key = "g" + goalChecks, projectId = "p", request = "Build part " + goalChecks, kind = "Coding", access = "WriteProject", dependsOn = Array.Empty<string>() } } })
                    : JsonSerializer.Serialize(new { summary = "All done.", done = true, phase = 1, tasks = Array.Empty<object>() }));
            }
            if (text.StartsWith("Create a bounded plan", StringComparison.Ordinal))
                return new(FailureKind.None, JsonSerializer.Serialize(new
                {
                    summary = "Four milestones.",
                    acceptance = new[] { "The game runs" },
                    phases = new object[]
                    {
                        new { name = "Design", goal = "A design document", acceptance = new[] { "DESIGN.md exists" } },
                        new { name = "Gameplay", goal = "The loop works", acceptance = new[] { "The loop runs" } },
                        new { name = "Art", goal = "Art in place", acceptance = new[] { "Sprites are used" } },
                        new { name = "Review", goal = "Polish and review" }
                    },
                    tasks = new[] { new { key = "t1", projectId = "p", request = "Write the design", kind = "Coding", access = "WriteProject", dependsOn = Array.Empty<string>() } }
                }));
            if (text.StartsWith("ACCEPTANCE VERIFICATION of ", StringComparison.Ordinal))
            {
                var scope = text["ACCEPTANCE VERIFICATION of ".Length..text.IndexOf(" (round", StringComparison.Ordinal)];
                tested.Add(scope.StartsWith("milestone", StringComparison.Ordinal) ? scope.Split(' ')[1] : "goal");
                var count = text.Split('\n').Count(line => System.Text.RegularExpressions.Regex.IsMatch(line, @"^\d+\. "));
                return new(FailureKind.None, AcceptanceCheck.Marker + " " + JsonSerializer.Serialize(new { criteria = Enumerable.Range(1, count).Select(id => new { id, pass = true, blocking = true, evidence = "ran it" }), summary = "ok" }) + "\nSTEP STATUS: COMPLETE");
            }
            return new(FailureKind.None, "Did the work.\nSTEP STATUS: COMPLETE");
        };
        var runner = new NullRunner();
        var flow = new UnifiedTaskService(db, db, new Orchestrator(db, [ai], runner), runner);
        var settings = new ManagerSettings();
        settings.Providers[0].Enabled = true;
        settings.Providers[0].AllowProjectWrite = true;
        settings.Providers[0].Models.Add(new("", Enum.GetValues<WorkKind>()));
        settings.Automation.SignOffMilestones = signOff;
        db.SaveSettings(settings);
        var goal = new WorkItem { ProjectId = "p", IsGoal = true, State = WorkState.Saved, Request = "Hãy tự làm 1 game nhỏ", AutoAssign = true, AutomaticIntent = true, Access = AccessLevel.WriteProject, GrantedAccess = AccessLevel.WriteProject };
        db.SaveWork(goal);
        await flow.StartAsync(goal, settings, [new("codex", true, true, true, "")]);
        WorkItem Fresh() => db.WorkItems().Single(w => w.Id == goal.Id);
        if (signOff)
        {
            // Seen asked: in large tasks a person accepts each milestone before the next starts, and can still change it.
            var waiting = flow.SessionFor(db.WorkItems().Single(w => w.Id == goal.Id))!;
            var signOffs = flow.Decisions(Fresh()).Where(d => d.Kind == "MilestoneSignOff" && d.State == DecisionState.Pending).ToArray();
            Assert(string.Join(",", tested) == "1/4" && signOffs.Length == 1 && waiting.AcceptedScopes.SequenceEqual(["phase:0"]) && waiting.State == SessionState.WaitingForUser,
                "After milestone 1 passes its tester the task waits for sign-off: tested " + string.Join(",", tested) + ", state " + waiting.State + ", decisions " + signOffs.Length + ", accepted " + string.Join("|", waiting.AcceptedScopes));
            // "ok" as a comment approves the milestone; nothing is replanned from it.
            flow.AddComment(Fresh(), "ok", [], continueUpdating: true);
            Assert(flow.SessionFor(Fresh())!.SignedOffScopes.SequenceEqual(["phase:0"]) && Fresh().State == WorkState.Queued && !db.Logs(goal.Id).Any(l => l.Event == "TaskUpdateRequested"),
                "An \"ok\" comment signs off the milestone and queues the task to go on");
            await flow.StartAsync(db.WorkItems().Single(w => w.Id == goal.Id), settings, [new("codex", true, true, true, "")]);
            var second = flow.SessionFor(db.WorkItems().Single(w => w.Id == goal.Id))!;
            Assert(string.Join(",", tested) == "1/4,2/4" && second.SignedOffScopes.SequenceEqual(["phase:0"]) && flow.Decisions(Fresh()).Count(d => d.Kind == "MilestoneSignOff" && d.State == DecisionState.Pending) == 1,
                "Approving starts milestone 2, which then waits for its own sign-off: tested " + string.Join(",", tested) + $"; session {second.State} {second.Notes}; goal {Fresh().State} {Fresh().Notes}; signed {string.Join('|', second.SignedOffScopes)}; checks {goalChecks}");
            foreach (var yes in new[] { "ok", "được rồi", "Duyệt", "làm tiếp đi", "looks good" })
                Assert(TaskIntent.IsApproval(yes), "An approving comment: " + yes);
            foreach (var change in new[] { "ok nhưng sửa màu nút", "chưa ổn", "không được, vẽ lại", "Thêm màn hướng dẫn chơi vào gameplay" })
                Assert(!TaskIntent.IsApproval(change), "A change is not an approval: " + change);
            flow.AddComment(db.WorkItems().Single(w => w.Id == goal.Id), "Thêm màn hướng dẫn chơi vào gameplay", [], continueUpdating: true);
            var reopened = flow.SessionFor(db.WorkItems().Single(w => w.Id == goal.Id))!;
            Assert(!reopened.AcceptedScopes.Contains("phase:1") && reopened.AcceptedScopes.Contains("phase:0") && AcceptanceCheck.OpenPhase(reopened) == 1
                && flow.Decisions(Fresh()).All(d => d.Kind != "MilestoneSignOff" || d.State != DecisionState.Pending), "A comment instead of sign-off reopens that milestone, to be reworked and tested again");
            Assert(SessionEngine.SignOffDue(new WorkSession { RootWorkId = "g", Phases = [new("A", "a"), new("B", "b")], AcceptedScopes = ["phase:0", "phase:1", "goal"], SignedOffScopes = ["phase:0"] }) == null, "The last milestone is the task's own final review, not a sign-off");
            return;
        }
        Console.WriteLine("  milestone order: tested " + string.Join(" → ", tested));
        var session = flow.SessionFor(db.WorkItems().Single(w => w.Id == goal.Id))!;
        Assert(string.Join(",", tested) == "1/4,2/4,3/4,4/4,goal", "Each milestone is tested once, in order, then the goal: " + string.Join(",", tested));
        Assert(session.AcceptedScopes.OrderBy(x => x).SequenceEqual(["goal", "phase:0", "phase:1", "phase:2", "phase:3"]), "Every milestone and the goal are accepted");
    }

    // Seen live (HonkAndBonk): a create prompt asked for "a four-frame idle" sheet, the tool returned
    // one pose, and the integration step stopped with "the animation frames and GUI assets are
    // unavailable". The request is now corrected before the call, and a step that reports missing
    // tool output makes the manager plan a corrected tool job instead of waiting for the user.
    private static async Task ToolOutputRepair()
    {
        var png = new byte[33];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(png, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16), 64);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20), 64);
        var img = "data:image/png;base64," + Convert.ToBase64String(png);
        var options = PixelToolOptions.Read(new ConnectedTool());
        System.Text.Json.Nodes.JsonObject Done(System.Text.Json.Nodes.JsonObject input)
        {
            PixelRequest.Complete(input, options, "Generate the character, GUI, and animation assets");
            return input;
        }
        const string livePrompt = "Arrange an exact 4-column by 2-row grid of eight equal 64x64 cells (256x128 total): first row is a four-frame looping idle for the Common goose; second row is a four-frame one-shot peck action.";
        var live = Done(new() { ["action"] = "create", ["image"] = img, ["prompt"] = livePrompt, ["outputWidth"] = 256, ["outputHeight"] = 128 });
        Assert(live["action"]!.GetValue<string>() == "animation" && live["frames"]!.GetValue<int>() == 4 && live["outputWidth"]!.GetValue<int>() == 64 && live["outputHeight"]!.GetValue<int>() == 64, "The live create brief with frames written as words becomes an animation of 64x64 frames: " + live.ToJsonString());
        try
        {
            Done(new() { ["action"] = "create", ["prompt"] = "A four-frame walk cycle of a goose" });
            Assert(false, "A create without a source that asks for frames must be refused");
        }
        catch (InvalidOperationException ex) { Assert(ex.Message.Contains("one still image", StringComparison.Ordinal), ex.Message); }
        Assert(Done(new() { ["action"] = "create", ["prompt"] = "Base pose of the goose for the 8-frame walk" })["action"]!.GetValue<string>() == "create", "A base pose for a later animation stays a still image");
        Assert(Done(new() { ["action"] = "create", ["prompt"] = "A coin icon", ["outputWidth"] = 32, ["outputHeight"] = 32 })["action"]!.GetValue<string>() == "create", "A plain still is untouched");
        var oneFrame = System.Text.Json.Nodes.JsonNode.Parse("""{"imageWidth":64,"imageHeight":64,"result":{"columns":1,"rows":1,"frameWidth":64,"frameHeight":64},"requestSettings":{"action":"animation","frames":8,"outputWidth":64,"outputHeight":64}}""")!;
        Assert(PixelSheetLayout.Problem(oneFrame, PixelSheetLayout.Requested(oneFrame)!) is { } problem && problem.Contains("1 frames", StringComparison.Ordinal), "One pose returned for an 8-frame animation is a failed tool step, not a finished one");

        // Seen live: "Each frame is exactly 64x64 pixels; output one horizontal 256x64 PNG sheet" with outputWidth 256 → four 256x64 frames.
        var sheetSized = Done(new() { ["action"] = "animation", ["image"] = img, ["frames"] = 4, ["outputWidth"] = 256, ["outputHeight"] = 64, ["prompt"] = "Create only a 4-frame one-shot peck animation for the existing Common goose. Each frame is exactly 64x64 pixels; output one horizontal 256x64 PNG sheet." });
        Assert(sheetSized["outputWidth"]!.GetValue<int>() == 64 && sheetSized["outputHeight"]!.GetValue<int>() == 64, "An animation's size is per frame, as the brief states: " + sheetSized.ToJsonString());
        Assert(PixelRequest.FrameSize("one horizontal 256x64 PNG sheet") == null && PixelRequest.FrameSize("8 frames of 32×32") == (32, 32) && PixelRequest.FrameSize("mỗi khung 48x48") == (48, 48), "A sheet size is not a frame size");

        // Seen live: a repair plan animated "the attached bfb0….png", an image the manager had posted on the task.
        var posted = new WorkAttachment("bfb0-goose.png", png);
        var owner = new WorkItem { Comments = [new WorkComment("Generated", [posted], DateTimeOffset.UtcNow) { IsAssistant = true }] };
        Assert(ToolTaskExecutor.FindAttachment(owner, "bfb0-goose.png") == posted, "An image the manager posted on the task can be used as a source");
        owner.Attachments = [new WorkAttachment("bfb0-goose.png", png.Concat(new byte[] { 1 }).ToArray())];
        Assert(ToolTaskExecutor.FindAttachment(owner, "bfb0-goose.png") != posted, "The user's own attachment of the same name wins");

        // No source image: the manager splits the call itself, base pose then one animation per motion.
        var split = PixelRequest.SplitFrames(new() { ["action"] = "create", ["prompt"] = livePrompt, ["fps"] = 8 }, "t3")!;
        Assert(split != null && split.Base["action"]!.GetValue<string>() == "create" && split.Base["frames"] == null && split.Base["outputWidth"]!.GetValue<int>() == 64, "The base pose is one 64x64 still");
        Assert(split!.Motions.Count == 2 && split.Motions.All(m => m["action"]!.GetValue<string>() == "animation" && m["frames"]!.GetValue<int>() == 4 && m["fps"]!.GetValue<int>() == 8 && m["image"]!["$imageFrom"]!.GetValue<string>() == "t3")
            && split.Motions[0]["prompt"]!.GetValue<string>().Contains("idle", StringComparison.Ordinal) && split.Motions[1]["prompt"]!.GetValue<string>().Contains("peck", StringComparison.Ordinal), "Idle and peck each get their own animation of the base: " + string.Join(" | ", split.Motions.Select(m => m.ToJsonString())));
        Assert(PixelRequest.SplitFrames(new() { ["action"] = "create", ["prompt"] = "A coin icon" }, "t3") == null && PixelRequest.SplitFrames(new() { ["action"] = "create", ["prompt"] = "Base pose for the 8-frame walk" }, "t3") == null, "Stills and base poses are not split");

        // Splitting a step: new tool steps with their own keys, waits where they use an earlier result, and the integration waits for all.
        {
            var splitFolder = Path.Combine(Path.GetTempPath(), "EmployeeAcceptanceChecks", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(splitFolder, "game"));
            using var splitDb = new SqliteStore(Path.Combine(splitFolder, "checks.db"));
            splitDb.SaveProject(new("p", "Goose game", Path.Combine(splitFolder, "game")));
            var splitRunner = new NullRunner();
            using var splitTools = new ToolExecutionService(splitDb, splitDb, splitRunner, splitFolder, splitFolder, _ => new NoopLease());
            var executor = new ToolTaskExecutor(splitDb, splitDb, splitTools, new ToolSkillRegistry(splitDb, splitFolder), splitFolder, (_, _) => new NoopLease());
            var prep = new WorkItem { ProjectId = "p", SessionId = "s", PlanKey = "t2", State = WorkState.Completed };
            var spec = new ToolTaskSpec("pixel-foundry", "employee-pixel-art", "generate", null, "t2");
            var tool = new WorkItem { ProjectId = "p", SessionId = "s", PlanKey = "t3", Access = AccessLevel.ReadProject, SkillId = "employee-pixel-art", SkillHash = "H", Tool = spec, DependsOn = [prep.Id], Request = "Generate the art" };
            var use = new WorkItem { ProjectId = "p", SessionId = "s", PlanKey = "t4", DependsOn = [tool.Id], Request = "Integrate" };
            foreach (var w in new[] { prep, tool, use }) splitDb.SaveWork(w);
            var made = executor.Split(tool, spec, [
                new() { ["action"] = "animation", ["image"] = new System.Text.Json.Nodes.JsonObject { ["$imageFrom"] = "t3" }, ["prompt"] = "idle" },
                new() { ["action"] = "create", ["prompt"] = "a pause icon" },
                new() { ["action"] = "animation", ["image"] = new System.Text.Json.Nodes.JsonObject { ["$imageFrom"] = "t3.3" }, ["prompt"] = "icon pulse" }], "test");
            var byKey = splitDb.WorkItems().ToDictionary(w => w.PlanKey!);
            Assert(made.Select(w => w.PlanKey).SequenceEqual(["t3.2", "t3.3", "t3.4"]) && made.All(w => w.Tool is { ToolId: "pixel-foundry", InputFrom: null } && w.Tool.Input != null && w.SkillHash == "H" && w.Access == AccessLevel.ReadProject), "Each extra call is a tool step of its own with the same tool and access");
            Assert(byKey["t3.2"].DependsOn.SequenceEqual([tool.Id]) && byKey["t3.3"].DependsOn.SequenceEqual([prep.Id]) && byKey["t3.4"].DependsOn.SequenceEqual([byKey["t3.3"].Id]), "A call waits only for the result it uses");
            Assert(byKey["t4"].DependsOn.Count == 4 && made.All(w => byKey["t4"].DependsOn.Contains(w.Id)), "The integration waits for every call");
            Assert(executor.AlreadySplit(tool) && !executor.AlreadySplit(use), "A step whose calls already have steps is known as split, so a remake does not split it again (seen live: five duplicate poses)");
            Assert(ManagedToolHandoff.OwnRequest(byKey["t3.3"].Request).StartsWith("Call 3 of 4 split from step t3: create: a pause icon", StringComparison.Ordinal), "A split call is described by its own line");

            // Seen live: 15 poses asked, only 6 made, the rest dropped silently. 15 now fit; more are named on the task.
            var many = new WorkItem { ProjectId = "p", SessionId = "s", ParentTaskId = "goal", PlanKey = "p1", SkillId = "employee-pixel-art", SkillHash = "H", Tool = spec, DependsOn = [prep.Id], Request = "Make the poses" };
            splitDb.SaveWork(many);
            var fifteen = executor.Split(many, spec, Enumerable.Range(2, 14).Select(i => new System.Text.Json.Nodes.JsonObject { ["action"] = "create", ["prompt"] = "pose " + i }).ToList(), "test");
            Assert(fifteen.Count == 14, "All 15 calls of a 15-pose step are made");
            var huge = new WorkItem { ProjectId = "p", SessionId = "s", ParentTaskId = "goal", PlanKey = "p2", SkillId = "employee-pixel-art", SkillHash = "H", Tool = spec, DependsOn = [prep.Id], Request = "Make the poses" };
            splitDb.SaveWork(huge);
            executor.Split(huge, spec, Enumerable.Range(2, 19).Select(i => new System.Text.Json.Nodes.JsonObject { ["action"] = "create", ["prompt"] = "pose " + i }).ToList(), "test");
            Assert(splitDb.Logs("goal").Any(l => l.Event == "ToolSplitDropped" && l.Message.Contains("pose 20", StringComparison.Ordinal)), "Calls over the limit are named on the task, not dropped silently");

            // Seen live: "make 15 poses" whose preparation answer could not be read became one image.
            var proseOnly = new WorkItem { ProjectId = "p", SessionId = "s", PlanKey = "t25", State = WorkState.Completed };
            splitDb.SaveWork(proseOnly);
            splitDb.Log(proseOnly.Id, "Execution", "codex\nI will write the poses below as a list.");
            Assert(executor.MeantSeveral(proseOnly, new WorkItem { Request = "Tạo 15 pose cơ sở riêng cho 15 sinh vật" }) && !executor.MeantSeveral(proseOnly, new WorkItem { Request = "Create the coin icon" }), "A step asking for several images is not reduced to one request");

            // Seen live: preparation copied the full projectAssetPath of an earlier result into $projectPng and all 27 calls were refused.
            Directory.CreateDirectory(Path.Combine(splitFolder, "game", "Assets"));
            var pose = Path.Combine(splitFolder, "game", "Assets", "pose.png");
            File.WriteAllBytes(pose, Png(8, 8, (10, 20, 30, 255)));
            File.WriteAllBytes(Path.Combine(splitFolder, "outside.png"), Png(8, 8, (10, 20, 30, 255)));
            var reader = new WorkItem { ProjectId = "p", SessionId = "s", Access = AccessLevel.ReadProject };
            Assert(executor.Resolve(reader, new System.Text.Json.Nodes.JsonObject { ["$projectPng"] = pose })!.GetValue<string>().StartsWith("data:image/png;base64,", StringComparison.Ordinal)
                && executor.Resolve(reader, new System.Text.Json.Nodes.JsonObject { ["$projectPng"] = "Assets/pose.png" })!.GetValue<string>().StartsWith("data:image/png;base64,", StringComparison.Ordinal), "A full or relative path inside the project is read");
            try
            {
                executor.Resolve(reader, new System.Text.Json.Nodes.JsonObject { ["$projectPng"] = Path.Combine(splitFolder, "outside.png") });
                throw new Exception("A full path outside the project was read");
            }
            catch (InvalidOperationException)
            {
            }
        }

        // Seen live: the Pixel Art tool failed to save its PNG (EPERM), then its server died mid-job and
        // the task stopped. Such passing errors get one automatic retry; usage limits do not.
        foreach (var notes in new[] { "V5 Pixel Art Tool / generate: Failed. Generated the PNG, but saving `result.png` failed with an `EPERM` permission error.", "V5 Pixel Art Tool / generate: Interrupted. Lost connection while waiting for image job 8d56; job kept for resume, not regenerated.", "V5 tool step: 404 Không tìm thấy tác vụ. Server có thể đã khởi động lại.", "V5 tool step: Tool/workflow conflicts with a running project or no resource slot remains.", "V5 tool step: Use a relative project PNG path." })
            Assert(SessionEngine.TransientToolFailure(notes), "A passing tool error is retried: " + notes);
        Assert(!SessionEngine.TransientToolFailure("V5 Pixel Art Tool / generate: Failed. You've hit your 5-hour limit · resets 1pm") && !SessionEngine.TransientToolFailure("Tool step access exceeds the current session grant"), "Usage limits and access problems are not retried blindly");
        // Seen live: an acceptance replan gave "Tạo 27 clip theo toolInputs t5" the pixel-art skill but no tool,
        // the AI said it had no pixel tool and three rounds failed on the same missing art.
        {
            ToolTaskSpec? PixelFor(string skill) => skill == "employee-pixel-art" ? new ToolTaskSpec("pixel-foundry", skill, "generate") : null;
            var replan = PlanningService.AttachMissingTools(new("p", [
                new("t1", "p", "Đọc HonkMonsterArt.cs và chuẩn bị 9 toolInputs tạo pose", WorkKind.Research, AccessLevel.WriteProject, [], SkillId: "employee-pixel-art"),
                new("t2", "p", "Tạo 9 pose cơ sở theo toolInputs của t1; một PNG 96×96 cho mỗi lệnh.", WorkKind.Content, AccessLevel.WriteProject, ["t1"], SkillId: "employee-pixel-art"),
                new("t3", "p", "Dựa trên 9 pose mới từ t2, chuẩn bị 27 toolInputs animation.", WorkKind.Content, AccessLevel.WriteProject, ["t2"], SkillId: "employee-pixel-art"),
                new("t4", "p", "Tạo 27 clip theo toolInputs t3, mỗi output một motion.", WorkKind.Content, AccessLevel.WriteProject, ["t3"], SkillId: "employee-pixel-art"),
                new("t5", "p", "Nếu có pose bị loại, tạo lại đúng các pose đó bằng Pixel Art Tool.", WorkKind.Content, AccessLevel.WriteProject, ["t4"], SkillId: "employee-pixel-art"),
                new("t6", "p", "Tích hợp các pose và clip mới của t2/t4 vào scene.", WorkKind.Coding, AccessLevel.WriteProject, ["t4"], SkillId: "employee-pixel-art"),
                new("t7", "p", "Tạo HUD mới cho màn chơi", WorkKind.Coding, AccessLevel.WriteProject, [], SkillId: "game-feature-delivery")]), PixelFor);
            var step = replan.Tasks.ToDictionary(t => t.Key);
            Assert(step["t2"].Tool is { ToolId: "pixel-foundry", InputFrom: "t1" } && step["t4"].Tool is { InputFrom: "t3" } && step["t5"].Tool is { Input: not null, InputFrom: null }, "Art steps with the tool skill become tool steps reading the preparation they name: " + string.Join(" | ", replan.Tasks.Select(t => t.Key + "=" + (t.Tool?.InputFrom ?? (t.Tool == null ? "-" : "own")))));
            Assert(step["t1"].Tool == null && step["t3"].Tool == null && step["t6"].Tool == null && step["t7"].Tool == null, "Preparation, integration and non-tool steps stay as they are");
            Assert(step["t1"].Access == AccessLevel.ReadProject && step["t3"].Access == AccessLevel.ReadProject && step["t6"].Access == AccessLevel.WriteProject, "A preparation only reads; integration still writes");
        }

        // Seen live: a step made its code changes, Codex's sandbox refused the compiler, and the task stopped for review.
        Assert(StepStatus.OnlyVerificationMissing("The managed command policy rejected the requested C# compiler invocation, so compilation remains unverified.")
            && StepStatus.OnlyVerificationMissing("Không chạy được Unity để build kiểm tra vì không có license; code đã sửa xong."), "A check that could not run here is not missing work");
        Assert(!StepStatus.OnlyVerificationMissing("Thiếu file project ZapBop đã lưu; chưa có đường dẫn workspace ZapBop khác được cung cấp.")
            && !StepStatus.OnlyVerificationMissing("The build failed with 3 errors in PlatformBridge.cs that I could not fix.")
            && !StepStatus.OnlyVerificationMissing("Sandbox policy rejected the edit, so the fix is not implemented and no files were changed."), "Missing input, a failing build or missing work still stop the step");

        // Seen live: the comment "lai di" became one step that handled only those two words and planned nothing.
        foreach (var redo in new[] { "lai di", "lại đi", "Làm lại đi", "chạy lại", "tiếp tục", "again", "try again please" })
            Assert(TaskIntent.IsRedo(redo), "A short redo comment means the original task again: " + redo);
        foreach (var change in new[] { "đổi màu nút lại đi cho đẹp hơn và thêm hiệu ứng khi bấm vào nút mua hàng", "the coin sound is too loud", "thêm nhạc nền" })
            Assert(!TaskIntent.IsRedo(change), "A real change is not a redo: " + change);
        Assert(PlanningService.RedoObjective("Add ZapBop audio to the app", "lai di").Contains("ORIGINAL TASK", StringComparison.Ordinal) && PlanningService.RedoObjective("Add ZapBop audio to the app", "lai di").EndsWith("Add ZapBop audio to the app", StringComparison.Ordinal), "A redo plans the original task with the tools as they are now");

        // TrailerForge's AI video generator runs through the bridge: an AI call, and only a real MP4 in the run folder counts.
        {
            Assert(ToolExecutionService.Capabilities("video").Single(c => c.Id == "generate") is { Writes: true, UsesAI: true, Publishes: false }, "Making a video counts as an AI call");
            var videoRun = Path.Combine(Path.GetTempPath(), "EmployeeAcceptanceChecks", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(videoRun, "Trailer abc", "export"));
            var mp4 = new byte[2048];
            "ftypisom"u8.CopyTo(mp4.AsSpan(4));
            var good = Path.Combine(videoRun, "Trailer abc", "export", "trailer.mp4");
            File.WriteAllBytes(good, mp4);
            var made = new System.Text.Json.Nodes.JsonObject { ["artifactPath"] = good };
            await ToolExecutionService.CheckVideo(made, videoRun, default);
            Assert(made["artifactSha256"]?.GetValue<string>().Length == 64 && made["artifactBytes"]?.GetValue<long>() == 2048, "A real MP4 in the run folder is accepted with its hash");
            var outside = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".mp4");
            File.WriteAllBytes(outside, mp4);
            File.WriteAllBytes(Path.Combine(videoRun, "broken.mp4"), new byte[2048]);
            foreach (var bad in new[] { outside, Path.Combine(videoRun, "broken.mp4") })
                try { await ToolExecutionService.CheckVideo(new System.Text.Json.Nodes.JsonObject { ["artifactPath"] = bad }, videoRun, default); throw new Exception("A bad video was accepted: " + bad); }
                catch (InvalidOperationException) { }
        }

        // Asked for: a task can end other than "complete": not completed, does not meet the requirements, cancelled.
        {
            var closeFolder = Path.Combine(Path.GetTempPath(), "EmployeeAcceptanceChecks", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(closeFolder, "game"));
            using var closeDb = new SqliteStore(Path.Combine(closeFolder, "checks.db"));
            closeDb.SaveProject(new("p", "Game", Path.Combine(closeFolder, "game")));
            var closeFlow = new UnifiedTaskService(closeDb, closeDb, new Orchestrator(closeDb, [], new NullRunner()), new NullRunner());
            WorkItem Goal(WorkState state)
            {
                var session = new WorkSession { Goal = "x", ProjectIds = ["p"], State = SessionState.WaitingForUser };
                closeDb.SaveSession(session);
                var goal = new WorkItem { IsGoal = true, ProjectId = "p", Request = "Make it", SessionId = session.Id, State = state };
                closeDb.SaveWork(goal);
                var made = new WorkItem { ProjectId = "p", SessionId = session.Id, ParentTaskId = goal.Id, State = WorkState.NeedsReview, ProviderId = "codex", ModelId = "m", Attempts = 1, Request = "made" };
                var waiting = new WorkItem { ProjectId = "p", SessionId = session.Id, ParentTaskId = goal.Id, State = WorkState.Queued, Request = "later" };
                closeDb.SaveWork(made);
                closeDb.SaveWork(waiting);
                closeDb.SaveDecision(new PendingDecision { SessionId = session.Id, WorkId = made.Id, Kind = "Review", Message = "review" });
                return goal;
            }
            var missed = Goal(WorkState.NeedsReview);
            closeFlow.Close(missed, WorkState.NotMet, "Wrong art style");
            var closedGoal = closeDb.WorkItems().Single(w => w.Id == missed.Id);
            Assert(closedGoal.State == WorkState.NotMet && closedGoal.Notes.Contains("Wrong art style", StringComparison.Ordinal)
                && closeFlow.Steps(closedGoal).All(s => s.State is WorkState.Skipped) && closeDb.Decisions(closedGoal.SessionId!).All(d => d.State != DecisionState.Pending)
                && closeDb.Sessions().Single(s => s.Id == closedGoal.SessionId).State == SessionState.Stopped, "Closing ends the steps, decisions and session, with the reason");
            try { closeFlow.Close(closedGoal, WorkState.Cancelled, ""); throw new Exception("A closed task was closed again"); } catch (InvalidOperationException) { }
            var cancelled = Goal(WorkState.Paused);
            closeFlow.Close(cancelled, WorkState.Cancelled, "");
            var pool = new TaskExecutionPool(closeDb, closeDb, new Orchestrator(closeDb, [], new NullRunner()), new NullRunner());
            try { await pool.StartAsync(closeDb.WorkItems().Single(w => w.Id == cancelled.Id), new ManagerSettings(), []); throw new Exception("A cancelled task ran again"); }
            catch (InvalidOperationException error) { Assert(error.Message.Contains("finished", StringComparison.Ordinal), error.Message); }
            try { closeFlow.Close(Goal(WorkState.Running), WorkState.Incomplete, ""); throw new Exception("A running task was closed"); } catch (InvalidOperationException) { }
            // Asked for: the result is already good though some steps did not finish; Complete still works.
            var goodEnough = Goal(WorkState.Paused);
            Assert(closeFlow.OpenWork(goodEnough) == (2, 1), "The open steps and decisions are counted for the question: " + closeFlow.OpenWork(goodEnough));
            var completedAnyway = closeFlow.CompleteAnyway(goodEnough);
            Assert(completedAnyway.State == WorkState.Completed && closeFlow.Steps(completedAnyway).All(s => s.State == WorkState.Skipped)
                && closeDb.Decisions(completedAnyway.SessionId!).All(d => d.State != DecisionState.Pending) && closeDb.Sessions().Single(s => s.Id == completedAnyway.SessionId).State == SessionState.Completed
                && completedAnyway.Notes.Contains("2 unfinished step(s)", StringComparison.Ordinal), "Complete anyway closes the open work and completes the task: " + completedAnyway.Notes);
        }

        // Asked for: the steps of a large task shown by milestone, older steps (made before steps recorded
        // their milestone) placed by when each milestone passed.
        {
            var t0 = DateTimeOffset.UtcNow.AddHours(-3);
            var phased = new WorkSession { RootWorkId = "g", Phases = [new("Design", "A design"), new("Build", "The loop"), new("Art", "Sprites")], AcceptedScopes = ["phase:0"], SignOffMilestones = true };
            var old1 = new WorkItem { CreatedAt = t0 };
            var old2 = new WorkItem { CreatedAt = t0.AddHours(2) };
            var tester = new WorkItem { CreatedAt = t0.AddHours(2), VerifyScope = "phase:1" };
            var recorded = new WorkItem { CreatedAt = t0, Milestone = 2 };
            var logs = new[] { new TaskLog(1, "g", t0.AddHours(1), "AcceptancePassed", "milestone 1/3 \"Design\", round 1: 1/1 criteria passed.") };
            var placed = MilestoneView.Assign(phased, [old1, old2, tester, recorded], logs);
            Assert(placed[old1.Id] == 0 && placed[old2.Id] == 1 && placed[tester.Id] == 1 && placed[recorded.Id] == 2, $"Steps are placed in their milestone: {placed[old1.Id]},{placed[old2.Id]},{placed[tester.Id]},{placed[recorded.Id]}");
            var groups = MilestoneView.Groups(phased, [old1, old2, tester, recorded], placed);
            Assert(groups.Count == 3 && groups[0].Status.Contains("waiting for your sign-off", StringComparison.Ordinal) && groups[1].Status == "in progress" && groups[1].Total == 2 && groups[0].Title == "Milestone 1/3 · Design",
                "Each milestone shows its status and steps: " + string.Join(" | ", groups.Select(g => g.Title + " " + g.Status + " " + g.Total)));
            Assert(MilestoneView.Current(phased) == 1, "New steps belong to the first milestone not accepted yet");
        }

        // Seen live: after "lại đi" the redone audio was in place, but dead steps from earlier attempts and
        // their decisions kept the task open.
        {
            var redoFolder = Path.Combine(Path.GetTempPath(), "EmployeeAcceptanceChecks", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(redoFolder);
            using var redoDb = new SqliteStore(Path.Combine(redoFolder, "checks.db"));
            var redoSession = new WorkSession { Goal = "audio", ProjectIds = ["p"] };
            redoDb.SaveSession(redoSession);
            var redoGoal = new WorkItem { IsGoal = true, ProjectId = "p", Request = "audio", SessionId = redoSession.Id };
            WorkItem RedoStep(string key, WorkState state, params string[] after) { var w = new WorkItem { ProjectId = "p", SessionId = redoSession.Id, ParentTaskId = redoGoal.Id, PlanKey = key, State = state, Request = key, DependsOn = [..after] }; redoDb.SaveWork(w); return w; }
            redoDb.SaveWork(redoGoal);
            var dead = RedoStep("t4", WorkState.Failed);
            var kept = RedoStep("t2", WorkState.Queued);
            var done = RedoStep("t1", WorkState.Completed);
            redoDb.SaveDecision(new PendingDecision { SessionId = redoSession.Id, WorkId = dead.Id, Kind = "Approval", Message = "old" });
            var earlier = new HashSet<string>([dead.Id, kept.Id, done.Id]);
            RedoStep("z1", WorkState.Queued, kept.Id);
            var redoFlow = new UnifiedTaskService(redoDb, redoDb, new Orchestrator(redoDb, [], new NullRunner()), new NullRunner());
            Assert(redoFlow.SupersedeEarlierSteps(redoDb.WorkItems().Single(w => w.Id == redoGoal.Id), redoSession, earlier) == 1
                && redoDb.WorkItems().Single(w => w.Id == dead.Id).State == WorkState.Skipped && redoDb.WorkItems().Single(w => w.Id == kept.Id).State == WorkState.Queued
                && redoDb.WorkItems().Single(w => w.Id == done.Id).State == WorkState.Completed && redoDb.Decisions(redoSession.Id).All(d => d.State != DecisionState.Pending),
                "A redo closes the old unfinished steps it does not build on, with their decisions");
        }

        // Seen live: the ZapBop preparation found no saved project and answered {"toolInputs":[]}; the step was
        // retried, then skipped with "STAND-IN NEEDED" although the plan said not to invent audio.
        Assert(!SessionEngine.TransientToolFailure("V5 tool step: " + ToolTaskExecutor.NothingToRun), "Nothing to run is not retried as a passing error");
        {
            var emptyFolder = Path.Combine(Path.GetTempPath(), "EmployeeAcceptanceChecks", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(emptyFolder, "game"));
            using var emptyDb = new SqliteStore(Path.Combine(emptyFolder, "checks.db"));
            emptyDb.SaveProject(new("p", "Audio game", Path.Combine(emptyFolder, "game")));
            using var emptyTools = new ToolExecutionService(emptyDb, emptyDb, new NullRunner(), emptyFolder, emptyFolder, _ => new NoopLease());
            var emptyExecutor = new ToolTaskExecutor(emptyDb, emptyDb, emptyTools, new ToolSkillRegistry(emptyDb, emptyFolder), emptyFolder, (_, _) => new NoopLease());
            var found = new WorkItem { ProjectId = "p", SessionId = "s", PlanKey = "t2", State = WorkState.Completed };
            emptyDb.SaveWork(found);
            emptyDb.Log(found.Id, "Execution", "codex\n{\"toolInputs\":[]}\n\nPROJECT FILE MISSING: no saved ZapBop project found.");
            Assert(emptyExecutor.EmptyPreparation(found) is { } said && said.StartsWith(ToolTaskExecutor.NothingToRun, StringComparison.Ordinal) && said.Contains("PROJECT FILE MISSING: no saved ZapBop project found", StringComparison.Ordinal), "An empty list is reported as nothing to run, with the preparation's reason");
        }

        // Seen live: free RAM fell under the reserve, the slot count went to 0 and a finished image
        // job of a running task was refused at import. A running task's own steps keep its slot.
        Assert(TaskExecutionPool.NoSlotLeft(1, 0, false, 0) && !TaskExecutionPool.NoSlotLeft(1, 0, true, 0) && TaskExecutionPool.NoSlotLeft(1, 0, false, 1) && !TaskExecutionPool.NoSlotLeft(1, 0, false, 2), "Low memory never refuses a step of a task that already runs");
        {
            // The backend's own output is kept, and its exit is explained in the tool error.
            var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            probe.Start();
            var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var logRoot = Path.Combine(Path.GetTempPath(), "EmployeeAcceptanceChecks", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(logRoot);
            using var launcher = new ToolBackendLauncher { LogFolder = logRoot, IdleShutdown = TimeSpan.FromHours(1) };
            var crashing = new ConnectedTool { Id = "pixel-foundry", Name = "Pixel Art Tool", Connector = "pixel", SourcePath = logRoot, Endpoint = $"http://127.0.0.1:{port}", AutoStartBackend = true, BackendExecutable = Environment.ProcessPath!, BackendArguments = ["--listen-then-crash", port.ToString()], BackendStartupTimeoutSeconds = 30 };
            using (await launcher.EnsureAsync(crashing, _ => Task.CompletedTask, _ => { }, CancellationToken.None))
            {
                var note = "";
                for (var i = 0; i < 60 && note.Length == 0; i++)
                {
                    await Task.Delay(100);
                    note = launcher.ExitNote(crashing);
                }
                await Task.Delay(300);
                note = launcher.ExitNote(crashing);
                Console.WriteLine("  backend crash note:" + note);
                Assert(note.Contains("exit code 3", StringComparison.Ordinal) && note.Contains("EPERM", StringComparison.Ordinal), "A backend that dies is explained with its exit code and last output: " + note);
                var log = File.ReadAllText(Path.Combine(logRoot, "pixel-foundry", "backend.log"));
                Assert(log.Contains("pixel server listening", StringComparison.Ordinal) && log.Contains("NOT stopped by the manager", StringComparison.Ordinal), "The backend log keeps its output and says who stopped it: " + log);
            }
        }

        // Seen live: 15 toolInputs inside a long explanation were not read.
        {
            var answer = "Đã đọc renderer và config. Dưới đây là **đúng 15 `toolInputs`**.\n\n**Nguồn**\n- {not json} monster-lineup\n\n```json\n{\"toolInputs\":[{\"action\":\"create\",\"prompt\":\"ONE pose of VINE CRAWLER {green}\"},{\"action\":\"create\",\"prompt\":\"ONE pose of ROT BULWARK\"}]}\n```\n\nPowerShell từ chối nạp System.Drawing, nên phép đo không hoàn thành.\nSTEP STATUS: COMPLETE";
            var codexStream = "{\"type\":\"thread.started\"}\n" + JsonSerializer.Serialize(new { type = "item.completed", item = new { id = "item_9", type = "agent_message", text = answer } });
            foreach (var output in new[] { answer, codexStream })
                Assert(AgentProtocol.FindObject(output, "toolInput", "toolInputs", "blocked")?["toolInputs"] is System.Text.Json.Nodes.JsonArray { Count: 2 }, "toolInputs are found inside prose and fences: " + output[..40]);
            Assert(AgentProtocol.FindObject("no json here {at all", "toolInputs") == null, "Prose without the object gives nothing");
            var receipt = ManagedToolHandoff.ShortReceipt(JsonSerializer.Serialize(new { projectAssetPath = @"D:\g\Assets\AIEmployeeGenerated\a.png", sheetWidth = 768, sheetHeight = 96, columns = 8, rows = 1, frameWidth = 96, frameHeight = 96, quality = new string('x', 3000), integrationStatus = new string('y', 2000) }))!;
            Assert(receipt.Length < 300 && receipt.Contains("a.png", StringComparison.Ordinal) && receipt.Contains("\"columns\":8", StringComparison.Ordinal), "Later steps get short receipts that keep path, size and grid: " + receipt.Length);
        }

        // The integration step reports the missing output: a corrected job is planned, the step is replaced.
        var folder = Path.Combine(Path.GetTempPath(), "EmployeeAcceptanceChecks", Guid.NewGuid().ToString("N"));
        var projectRoot = Path.Combine(folder, "game");
        Directory.CreateDirectory(projectRoot);
        using var db = new SqliteStore(Path.Combine(folder, "checks.db"));
        db.SaveProject(new("p", "Goose game", projectRoot));
        var calls = new List<string>();
        var ai = new FakeProvider("codex");
        ai.Response = request =>
        {
            calls.Add("work:" + request.Work.PlanKey);
            return new(FailureKind.None, request.Work.PlanKey == "t4"
                ? "Wired the one image.\nSTEP STATUS: BLOCKED: cần Manager trả hai strip đúng `256×64` trước khi tích hợp."
                : "Done.\nSTEP STATUS: COMPLETE");
        };
        var runner = new NullRunner();
        var orchestrator = new Orchestrator(db, [ai], runner);
        var settings = new ManagerSettings();
        settings.Automation.SignOffMilestones = false; // these checks run straight through; sign-off has its own check
        settings.Providers[0].Enabled = true;
        settings.Providers[0].AllowProjectWrite = true;
        settings.Providers[0].Models.Add(new("", Enum.GetValues<WorkKind>()));
        db.SaveSettings(settings);
        var goal = new WorkItem { ProjectId = "p", IsGoal = true, State = WorkState.Paused, Request = "nghiên cứu rồi chỉnh sửa game, dùng tool pixel art vẽ nhân vật, gui và anim", Access = AccessLevel.WriteProject, GrantedAccess = AccessLevel.WriteProject };
        var session = new WorkSession { RootWorkId = goal.Id, Goal = goal.Request, ProjectIds = ["p"], AccessCeiling = AccessLevel.WriteProject, AutoContinueSteps = true, Away = true };
        goal.SessionId = session.Id;
        db.SaveSession(session);
        db.SaveWork(goal);
        var art = new WorkItem { ProjectId = "p", SessionId = session.Id, ParentTaskId = goal.Id, PlanKey = "t3", Kind = WorkKind.Content, Access = AccessLevel.WriteProject, State = WorkState.Completed, Tool = new("pixel-foundry", "employee-pixel-art", "generate"), Request = "Generate the character, GUI and animation assets" };
        db.SaveWork(art);
        var integrate = new WorkItem { ProjectId = "p", SessionId = session.Id, ParentTaskId = goal.Id, PlanKey = "t4", Kind = WorkKind.Coding, Access = AccessLevel.WriteProject, DependsOn = [art.Id], Request = "Integrate the generated assets" };
        db.SaveWork(integrate);
        var review = new WorkItem { ProjectId = "p", SessionId = session.Id, ParentTaskId = goal.Id, PlanKey = "t5", Kind = WorkKind.Review, Access = AccessLevel.WriteProject, DependsOn = [integrate.Id], Request = "Review everything" };
        db.SaveWork(review);
        var engine = new SessionEngine(db, db, orchestrator, new SmartTestingService(db, db, orchestrator, runner));
        string? asked = null;
        engine.ToolOutputReplan = (s, failure, _) =>
        {
            asked = failure;
            calls.Add("toolrepair");
            db.SaveWork(new WorkItem { ProjectId = "p", SessionId = s.Id, ParentTaskId = goal.Id, PlanKey = "r1", Kind = WorkKind.Coding, Access = AccessLevel.WriteProject, Request = "Animate the goose idle and integrate it" });
            return Task.FromResult(true);
        };
        await engine.RunAsync(session.Id, settings, [new("codex", true, true, true, "")]);
        Console.WriteLine("  missing tool output: " + string.Join(" → ", calls));
        var stepsAfter = db.WorkItems().Where(w => w.SessionId == session.Id && !w.IsGoal).ToDictionary(w => w.PlanKey ?? w.Id);
        Assert(string.Join(",", calls) == "work:t4,toolrepair,work:r1,work:t5", "Unexpected order: " + string.Join(",", calls) + " / " + db.Sessions().Single(x => x.Id == session.Id).Notes);
        Assert(asked != null && asked.Contains("256×64", StringComparison.Ordinal) && stepsAfter["t4"].State == WorkState.Skipped && stepsAfter["t5"].DependsOn.Contains(stepsAfter["r1"].Id), "The worker's report goes to the coordinator, the blocked step is replaced and the review waits for the new work");

        // Seen live: the repair plan waited for the finished image step "t3" and was rejected
        // ("A dependency task does not exist in this session"). Existing finished steps may be waited for;
        // a skipped one is dropped.
        var planning = new PlanningService(db, db, orchestrator);
        var current = db.Sessions().Single(x => x.Id == session.Id);
        var linked = planning.Import(current, new("repair", [new PlannedTask("n1", "p", "Integrate the new frames", WorkKind.Coding, AccessLevel.WriteProject, ["t3", "t4"])]));
        planning.Approve(current, linked.Id);
        var n1 = db.WorkItems().Single(w => w.SessionId == session.Id && w.PlanKey == "n1");
        Assert(n1.DependsOn.SequenceEqual([art.Id]), "A repair step waits for the finished image step and not for the skipped one: " + string.Join(",", n1.DependsOn));
        // Seen live: two tool steps read one preparation step ("use a separate input preparation step per tool request").
        var shared = PlanningService.NormalizeToolReferences(new("p", [
            new PlannedTask("prep", "p", "Prepare inputs", WorkKind.Review, AccessLevel.ReadProject, []),
            new PlannedTask("a1", "p", "Animate idle", WorkKind.Content, AccessLevel.ReadProject, ["prep"], SkillId: "employee-pixel-art", Tool: new("pixel-foundry", "employee-pixel-art", "generate", null, "prep")),
            new PlannedTask("a2", "p", "Draw the pause icon", WorkKind.Content, AccessLevel.ReadProject, ["prep"], SkillId: "employee-pixel-art", Tool: new("pixel-foundry", "employee-pixel-art", "generate", null, "prep"))]));
        var copy = shared.Tasks.SingleOrDefault(t => t.Key == "prep_a2");
        Assert(copy != null && copy.Tool == null && copy.Request.Contains("Draw the pause icon", StringComparison.Ordinal) && shared.Tasks.Single(t => t.Key == "a2") is { Tool.InputFrom: "prep_a2" } a2 && a2.DependsOn.SequenceEqual(["prep_a2"]) && shared.Tasks.Single(t => t.Key == "a1").Tool!.InputFrom == "prep",
            "A shared preparation step is copied for the second tool call instead of the plan being rejected");
    }

    // The manager's own browser screenshot of a web project (real headless Edge/Chrome when installed).
    private static async Task Screenshots()
    {
        var root = Path.Combine(Path.GetTempPath(), "EmployeeAcceptanceChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        Assert(PageSnapshot.EntryPage(root) == null, "No page, no screenshot");
        Assert((await PageSnapshot.TakeAsync(root, "none", root, CancellationToken.None)).Count == 0, "A project without a web page gets no screenshot");
        File.WriteAllText(Path.Combine(root, "index.html"), "<!doctype html><html><body style='margin:0;background:#123'><main style='width:min-content'><p>SCORE 0</p><canvas id='c' width='240' height='320' style='max-width:100%;background:#fff'></canvas></main><script type='module' src='./src/main.js'></script></body></html>");
        File.WriteAllText(Path.Combine(root, "src", "main.js"), "document.body.dataset.loaded = 'yes';");
        Assert(PageSnapshot.EntryPage(root) == "index.html", "The root index.html is the page");
        if (PageSnapshot.Browser() == null)
        {
            Console.WriteLine("  screenshots: no Edge/Chrome installed, real capture not exercised");
            return;
        }
        var shots = await PageSnapshot.TakeAsync(root, "check", Path.Combine(root, "scratch"), CancellationToken.None);
        Assert(shots.Count == 2 && shots.All(s => File.Exists(s.Path) && new FileInfo(s.Path).Length > 500 && s.Path.Contains(Path.Combine(".aiemployee", "screenshots"))), "Desktop and phone screenshots are saved inside the project");
        var png = File.ReadAllBytes(shots[0].Path);
        Assert(png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) && System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)) == 1280, "The desktop shot is a 1280-wide PNG");
        Console.WriteLine("  screenshots: " + string.Join(", ", shots.Select(s => s.Name)));
        // The phone shot must lay the page out at a real 390px width (headless windows have a ~500px minimum):
        // a page that paints green below 400px and red above it must show green in the phone frame.
        File.WriteAllText(Path.Combine(root, "index.html"), "<!doctype html><html><head><meta name='viewport' content='width=device-width'><style>body{margin:0;height:100vh;background:#f00}@media (max-width:400px){body{background:#0f0}}</style></head><body></body></html>");
        var phone = (await PageSnapshot.TakeAsync(root, "width", Path.Combine(root, "scratch"), CancellationToken.None)).Single(s => s.Name.StartsWith("phone", StringComparison.Ordinal));
        var center = PngPixel(File.ReadAllBytes(phone.Path), 195, 400);
        Assert(center.G > 200 && center.R < 60, $"The phone frame is laid out at 390px (pixel {center})");
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    // One RGB pixel of a PNG written by a browser (8-bit RGB/RGBA, any filter), for the width check above.
    private static (int R, int G, int B) PngPixel(byte[] png, int x, int y)
    {
        int width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
        var channels = png[25] == 6 ? 4 : 3;
        using var data = new MemoryStream();
        for (var at = 8; at < png.Length;)
        {
            var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at, 4));
            if (System.Text.Encoding.ASCII.GetString(png, at + 4, 4) == "IDAT")
                data.Write(png, at + 8, length);
            at += 12 + length;
        }
        data.Position = 2;
        using var inflate = new System.IO.Compression.DeflateStream(data, System.IO.Compression.CompressionMode.Decompress);
        using var raw = new MemoryStream();
        inflate.CopyTo(raw);
        var bytes = raw.ToArray();
        var stride = width * channels;
        var previous = new byte[stride];
        var row = new byte[stride];
        for (var line = 0; line <= y; line++)
        {
            var filter = bytes[line * (stride + 1)];
            Array.Copy(bytes, line * (stride + 1) + 1, row, 0, stride);
            for (var i = 0; i < stride; i++)
            {
                int left = i >= channels ? row[i - channels] : 0, up = previous[i], corner = i >= channels ? previous[i - channels] : 0;
                row[i] = (byte)(row[i] + filter switch
                {
                    1 => left,
                    2 => up,
                    3 => (left + up) / 2,
                    4 => Paeth(left, up, corner),
                    _ => 0
                });
            }
            (previous, row) = (row, previous);
        }
        return (previous[x * channels], previous[x * channels + 1], previous[x * channels + 2]);
    }

    public static async Task<int> Run()
    {
        await ResearchAndArt();
        await MilestoneOrder();
        await MilestoneOrder(signOff: true);
        await ToolOutputRepair();
        await Screenshots();
        // Parsing the tester's verdict.
        var parsed = AcceptanceCheck.Parse("notes\nACCEPTANCE: {\"criteria\":[{\"id\":1,\"pass\":true,\"evidence\":\"ok {brace}\"},{\"id\":2,\"pass\":false,\"blocking\":false,\"evidence\":\"font is small\"}],\"summary\":\"s\"}\nSTEP STATUS: COMPLETE", 3);
        Assert(parsed is { Criteria.Count: 3 } && parsed.Criteria[0].Pass && parsed.MinorIssues.Single().Id == 2 && parsed.BlockingFailures.Single().Id == 3, "Verdicts are read; a criterion without a verdict is a blocking failure");
        Assert(AcceptanceCheck.Parse("ACCEPTANCE: all fine", 2) == null && AcceptanceCheck.Parse("no marker {\"criteria\":[]}", 2) == null && AcceptanceCheck.Parse("ACCEPTANCE: {\"criteria\":[{\"id\":9,\"pass\":true}]}", 2) == null, "Prose, missing marker and unknown criteria give no verdict");
        Assert(AcceptanceCheck.Parse("ACCEPTANCE: {\"criteria\":[{\"id\":1,\"pass\":false}]}\nlater\nACCEPTANCE:\n```json\n{\"criteria\":[{\"id\":1,\"pass\":true}]}\n```", 1) is { BlockingFailures.Count: 0 }, "The last verdict in the answer counts, fenced or not");

        // The declared self-check file.
        var checkRoot = Path.Combine(Path.GetTempPath(), "EmployeeAcceptanceChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(checkRoot, ".aiemployee"));
        AutoCheckCommand? Declared(string json)
        {
            File.WriteAllText(Path.Combine(checkRoot, ".aiemployee", "check.json"), json);
            return AutoCheck.Detect(new("x", "x", checkRoot), []);
        }
        Assert(Declared("{\"name\":\"Smoke\",\"executable\":\"node\",\"arguments\":[\"tests/smoke.mjs\"],\"timeoutSeconds\":5}") is { Executable: "node", TimeoutSeconds: 30 } ok && ok.Arguments.Single() == "tests/smoke.mjs", "A declared node self-check is used, with a bounded timeout");
        Assert(Declared("{\"version\":1,\"command\":\"node\",\"args\":[\"tools/selfcheck.mjs\"],\"timeoutSeconds\":60}") is { Executable: "node" } aliased && aliased.Arguments.Single() == "tools/selfcheck.mjs", "command/args are read like executable/arguments");
        Assert(Declared("{\"command\":\"node tests/smoke.mjs\"}") is { Executable: "node" } line && line.Arguments.Single() == "tests/smoke.mjs", "A whole command line is split");
        var listed = AgentProtocol.Parse<WorkPlan>("{\"summary\":\"s\",\"tasks\":[{\"key\":\"t1\",\"projectId\":\"p\",\"request\":\"r\",\"kind\":\"Content\",\"access\":\"WriteProject\",\"dependsOn\":[],\"acceptance\":[\"two drafts\",\"no posting\"]}]}");
        Assert(listed?.Tasks.Single().Acceptance == "two drafts; no posting", "A step's acceptance written as a list is read, not a broken plan");
        Assert(AcceptanceCheck.Parse("ACCEPTANCE: {\"criteria\":[{\"id\":1,\"pass\":false,\"unverifiable\":true,\"evidence\":\"needs a real browser\"},{\"id\":2,\"pass\":false,\"unverifiable\":true,\"blocking\":true}]}", 2) is { BlockingFailures.Count: 0, MinorIssues.Count: 2 } unverifiable && unverifiable.MinorIssues.All(c => c.Unverifiable), "Unverifiable on this machine goes to the person, not to the fix loop");
        Assert(Declared("{\"executable\":\"dotnet\",\"arguments\":[\"test\",\"-c\",\"Debug\"]}") != null, "dotnet -c Debug is a configuration, not inline code");
        foreach (var bad in new[]
        {
            "{\"executable\":\"cmd\",\"arguments\":[\"/c\",\"del\"]}", "{\"executable\":\"powershell\",\"arguments\":[\"x.ps1\"]}", "{\"executable\":\"C:\\\\Windows\\\\node.exe\",\"arguments\":[\"a.js\"]}",
            "{\"executable\":\"node\",\"arguments\":[\"-e\",\"process.exit(0)\"]}", "{\"executable\":\"python\",\"arguments\":[\"-c\",\"print(1)\"]}", "{\"executable\":\"node\",\"arguments\":[\"../outside.js\"]}",
            "{\"executable\":\"node\",\"arguments\":[\"C:\\\\x.js\"]}", "{\"executable\":\"dotnet\",\"arguments\":[\"publish\"]}", "{\"executable\":\"node\",\"arguments\":[]}", "not json"
        })
            Assert(Declared(bad) == null, "Rejected self-check: " + bad);

        // Recognising "make a game" as a build to plan, in both languages, without catching edits.
        foreach (var text in new[] { "Hãy nghiên cứu và tự làm 1 game thể loại roguelike", "tạo một game xếp hình", "Make a small game about cats", "build me a simple app for notes", "Viết cho mình 1 tool đổi tên file" })
            Assert(TaskIntent.IsNewBuild(text) && UnifiedTaskService.NeedsPlan(text, WorkKind.Coding, PlanningPolicy.Auto) && TaskIntent.TryResolve(text, out var kind, out var access) && kind == WorkKind.Coding && access == AccessLevel.WriteProject, "New build not recognised: " + text);
        foreach (var text in new[] { "sửa lỗi nhảy trong game", "tạo sprite cho game", "review the game code", "nghiên cứu cơ chế làm cho game hay" })
            Assert(!TaskIntent.IsNewBuild(text), "Not a new build: " + text);

        // 1. A failure in milestone 1 is fixed and tested again; later scopes pass first time.
        var fixedRun = await Play("fix then pass", tester => tester != 1);
        var session = fixedRun.Flow.SessionFor(fixedRun.Goal)!;
        var logs = fixedRun.Store.Logs(fixedRun.Goal.Id).Select(l => l.Event).ToArray();
        Assert(string.Join(",", fixedRun.Calls) == "plan,work:t1,tester1:fail,fix,tester2:pass,goalcheck1,work:c1,tester3:pass,goalcheck2,tester4:pass", "Unexpected order: " + string.Join(",", fixedRun.Calls));
        Assert(session.State == SessionState.Completed && session.GoalVerified && session.AcceptanceStalled == null && session.AcceptedScopes.SequenceEqual(["phase:0", "phase:1", "goal"]), "Every milestone and the goal are accepted");
        Assert(session.Acceptance.SequenceEqual(["The smoke test exits 0", "A simulated run can be won and lost"]) && session.Phases[0].Acceptance!.Length == 2, "A goal check cannot rewrite the acceptance criteria");
        Assert(logs.Count(e => e == "AcceptanceFailed") == 1 && logs.Count(e => e == "AcceptancePassed") == 3 && logs.Contains("AcceptanceCriteria") && logs.Contains("AcceptanceReport"), "The acceptance history is logged on the task");
        Assert(fixedRun.Goal.State == WorkState.NeedsReview && fixedRun.Goal.Notes.Contains("Acceptance verified by an independent tester", StringComparison.Ordinal), "The task says what was verified: " + fixedRun.Goal.Notes);
        var steps = fixedRun.Flow.Steps(fixedRun.Goal);
        Assert(steps.Count(s => s.VerifyScope != null) == 4 && steps.Single(s => s.PlanKey == "accfix1").Request.Contains("nothing spawned", StringComparison.Ordinal), "The fix step carries the tester's evidence");
        // The phase without its own criteria is judged by its goal sentence.
        Assert(steps.Single(s => s.PlanKey == "verify3").Request.Contains("1. Waves and upgrades", StringComparison.Ordinal), "A milestone without criteria is tested against its goal");

        // 2. A scope that never passes stops after its rounds, with the failures reported; nothing is planned after it.
        var stalledRun = await Play("never passes", _ => false);
        session = stalledRun.Flow.SessionFor(stalledRun.Goal)!;
        Assert(string.Join(",", stalledRun.Calls) == "plan,work:t1,tester1:fail,fix,tester2:fail,fix,tester3:fail", "Unexpected order: " + string.Join(",", stalledRun.Calls));
        Assert(session.State == SessionState.Completed && session.AcceptanceStalled != null && session.AcceptedScopes.Count == 0 && !session.GoalVerified, "The run stops unaccepted");
        Assert(stalledRun.Goal.Notes.Contains("NOT ACCEPTED", StringComparison.Ordinal) && stalledRun.Goal.Notes.Contains("nothing spawned", StringComparison.Ordinal), "The task says what still fails: " + stalledRun.Goal.Notes);
        Assert(NextStepGuide.For(stalledRun.Goal, stalledRun.Flow.Steps(stalledRun.Goal), stalledRun.Flow.Decisions(stalledRun.Goal), stalledRun.Store)?.Contains("acceptance criteria", StringComparison.Ordinal) == true, "The user is told what to do next");
        var second = stalledRun.Flow.Steps(stalledRun.Goal).Where(s => s.PlanKey!.StartsWith("accfix", StringComparison.Ordinal)).OrderBy(s => s.CreatedAt).Last();
        Assert(second.Reworks == 1 && second.RejectedRoutes.Count == 1, "The second fix is routed away from the model whose fix did not hold");

        // 3. Switched off: no tester steps, the plan runs as before.
        var offRun = await Play("verification off", _ => true, s => s.Automation.VerifyAcceptance = false);
        Assert(string.Join(",", offRun.Calls) == "plan,work:t1,goalcheck1,work:c1,goalcheck2" && offRun.Flow.SessionFor(offRun.Goal)!.AcceptedScopes.Count == 0, "Unexpected order: " + string.Join(",", offRun.Calls));

        foreach (var run in new[] { fixedRun, stalledRun, offRun })
            run.Store.Dispose();
        await ToolWork();
        Console.WriteLine("PASS acceptance verdict parsing, declared self-check validation, new-build recognition, milestone verify/fix/re-verify loop, stall stop and the off switch; scripted AI only");
        return 0;
    }
}
