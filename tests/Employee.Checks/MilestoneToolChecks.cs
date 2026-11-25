using Employee.Core;
using Employee.Infrastructure;

// Milestone scope of a plan, and a tool named in a comment without its verb next to it.
static class MilestoneToolChecks
{
    public static int Run()
    {
        void Assert(bool condition, string message)
        {
            if (!condition)
                throw new Exception("Milestone/tool check failed: " + message);
        }

        // 1. A tool named with its use later in the same clause is a request for that tool.
        Assert(ToolSkillRegistry.StatesUse("tool pixel art có thể dùng antigravity trong setting có rồi mà", "tool pixel art"), "a named tool with 'dùng' later in the clause must count");
        Assert(ToolSkillRegistry.StatesUse("Pixel Art Tool: dùng antigravity để gen", "Pixel Art Tool"), "a tool name followed by a use clause must count");
        Assert(!ToolSkillRegistry.StatesUse("không dùng tool pixel art", "tool pixel art"), "a negated use must not count");
        Assert(!ToolSkillRegistry.StatesUse("tool pixel art là gì?", "tool pixel art"), "a question without a use verb must not count");
        Assert(!ToolSkillRegistry.StatesUse("ví dụ: dùng tool pixel art", "tool pixel art"), "an example must not count");

        // 2. Milestone requests are recognised in Vietnamese and English.
        Assert(PlanningService.AsksForMilestones("chia các milestone hoặc step nhỏ ra để mình biết còn làm gì"), "'milestone' in a comment must ask for a split");
        Assert(PlanningService.AsksForMilestones("split this into phases"), "'phases' must ask for a split");
        Assert(!PlanningService.AsksForMilestones("làm tiếp game này đi đủ các phần luôn"), "an ordinary continue request must not ask for a split");

        // 3. A plan keeps the open milestone and leaves later milestones out, with what depends on them.
        var session = new WorkSession
        {
            Id = "s",
            Goal = "game",
            Phases = [new PlanPhase("Research", "r"), new PlanPhase("Core", "c"), new PlanPhase("Content", "x"), new PlanPhase("Polish", "p")],
            AcceptedScopes = ["phase:0"]
        };
        var plan = new WorkPlan("s", [
            new PlannedTask("core1", "p", "core", WorkKind.Coding, AccessLevel.WriteProject, [], Phase: 2),
            new PlannedTask("core2", "p", "core 2", WorkKind.Coding, AccessLevel.WriteProject, ["core1"], Phase: 2),
            new PlannedTask("art", "p", "art", WorkKind.Coding, AccessLevel.WriteProject, [], Phase: 3),
            new PlannedTask("art_use", "p", "use art", WorkKind.Coding, AccessLevel.WriteProject, ["art"], Phase: 2),
            new PlannedTask("polish", "p", "polish", WorkKind.Coding, AccessLevel.WriteProject, ["core2"], Phase: 4),
            new PlannedTask("loose", "p", "no phase", WorkKind.Coding, AccessLevel.WriteProject, [])
        ]);
        var kept = MilestoneView.KeepOpenMilestone(session, plan).Tasks!.Select(t => t.Key).ToArray();
        Assert(kept.SequenceEqual(["core1", "core2", "loose"]), "open milestone 2 keeps its steps and steps without a phase; got " + string.Join(",", kept));

        // 4. Once the milestone is accepted, the next one is the open one and its steps are kept.
        session.AcceptedScopes.Add("phase:1");
        var next = MilestoneView.KeepOpenMilestone(session, plan).Tasks!.Select(t => t.Key).ToArray();
        Assert(next.SequenceEqual(["core1", "core2", "art", "art_use", "loose"]), "after milestone 2 is accepted, milestone 3 steps are kept and only milestone 4 waits; got " + string.Join(",", next));

        // 4b. A replan while the open milestone is being fixed after a failed test keeps all its steps.
        var fixing = new WorkSession { Id = "x", Goal = "game", Phases = session.Phases, AcceptedScopes = ["phase:0"], VerifyRounds = 2 };
        var fixPlan = new WorkPlan("fix", [
            new PlannedTask("sprite", "p", "generate sprites", WorkKind.Content, AccessLevel.WriteProject, [], Phase: 3),
            new PlannedTask("check", "p", "check", WorkKind.Coding, AccessLevel.WriteProject, ["sprite"], Phase: 2)
        ]);
        Assert(MilestoneView.KeepOpenMilestone(fixing, fixPlan).Tasks!.Length == 2, "a fix replan of the open milestone must keep every step");

        // 5. A plan with nothing for the open milestone is kept as it is.
        var onlyLater = new WorkPlan("s", [new PlannedTask("late", "p", "late", WorkKind.Coding, AccessLevel.WriteProject, [], Phase: 4)]);
        Assert(ReferenceEquals(MilestoneView.KeepOpenMilestone(session, onlyLater), onlyLater), "a plan with only later steps must not be emptied");

        // 6. Without phases nothing is filtered.
        var flat = new WorkSession { Id = "f", Goal = "g" };
        Assert(ReferenceEquals(MilestoneView.KeepOpenMilestone(flat, plan), plan), "a goal without phases must keep every step");

        // 7. A tool step written with the short names (id, skill) reads as the tool and skill it names,
        //    and the stored form reads back the same.
        var shortNames = System.Text.Json.JsonSerializer.Deserialize<ToolTaskSpec>("{\"id\":\"pixel-foundry\",\"skill\":\"employee-pixel-art\",\"operation\":\"generate\",\"input\":{\"action\":\"create\"}}")!;
        Assert(shortNames.ToolId == "pixel-foundry" && shortNames.SkillId == "employee-pixel-art" && shortNames.Operation == "generate" && shortNames.Input?["action"]?.ToString() == "create", "short tool names must be read");
        var stored = System.Text.Json.JsonSerializer.Deserialize<ToolTaskSpec>(System.Text.Json.JsonSerializer.Serialize(shortNames))!;
        Assert(stored.ToolId == shortNames.ToolId && stored.SkillId == shortNames.SkillId && stored.Input?["action"]?.ToString() == "create", "stored form must read back the same");
        var longNames = System.Text.Json.JsonSerializer.Deserialize<ToolTaskSpec>("{\"toolId\":\"pixel-foundry\",\"skillId\":\"employee-pixel-art\",\"operation\":\"generate\"}")!;
        Assert(longNames.ToolId == "pixel-foundry" && longNames.SkillId == "employee-pixel-art", "the planner's toolId/skillId names must still be read");

        // 8. A comment names the AI to use and, after "thay cho", the one to avoid.
        var ais = new[] { "codex", "claude", "antigravity", "ollama" };
        var asked = ProviderRequest.From("sử dụng antigravity thay cho codex đi", ais);
        Assert(asked.Prefer == "antigravity" && asked.Avoid == "codex", "'sử dụng X thay cho Y' prefers X and avoids Y; got " + asked);
        var typo = ProviderRequest.From("sử dụng antigrativy thay cho codex đi", ais);
        Assert(typo.Prefer == "antigravity", "a typo of the AI name still counts; got " + typo);
        Assert(ProviderRequest.From("không dùng codex", ais) == (null, "codex"), "'không dùng codex' avoids codex");
        Assert(ProviderRequest.From("dùng claude để viết lại", ais) == ("claude", null), "'dùng claude' prefers claude");
        Assert(ProviderRequest.From("codex là gì?", ais) == (null, null), "a question about an AI names no preference");
        Assert(ProviderRequest.From("làm tiếp game này đi", ais) == (null, null), "an ordinary comment names no AI");
        // The comment that failed in practice: the tool and the AI named after "bằng" / "qua".
        var practice = "làm tiếp bước tạo ảnh qua tool pixel art bằng antigravity nhé rồi tiếp tục làm";
        Assert(ProviderRequest.From(practice, ais).Prefer == "antigravity", "'bằng antigravity' must prefer antigravity");
        Assert(ToolSkillRegistry.StatesUse(practice, "tool pixel art"), "'qua tool pixel art' must name the pixel tool");

        // 9. The stored JSON of a tool step keeps the default shape: a saved step's binding hash depends on it.
        var plainJson = System.Text.Json.JsonSerializer.Serialize(new ToolTaskSpec("pixel-foundry", "employee-pixel-art", "generate"));
        Assert(plainJson == "{\"ToolId\":\"pixel-foundry\",\"SkillId\":\"employee-pixel-art\",\"Operation\":\"generate\",\"Input\":null,\"InputFrom\":null}", "tool step JSON changed shape: " + plainJson);

        // 10. A tool out of image quota is a wait for its reset, not a stop: the reset time is read from the report.
        var quotaReport = "BLOCKED: image generation tool capacity exhausted (ResourceExhausted HTTP 429, resets in ~3h15m)";
        Assert(ProviderCooldown.IsCapacityBlock(quotaReport), "a 429 / quota report must count as a capacity block");
        Assert(!ProviderCooldown.IsCapacityBlock("BLOCKED: the project folder is missing"), "a missing folder is not a capacity block");
        var now = new DateTimeOffset(2026, 11, 24, 13, 40, 0, TimeSpan.Zero);
        var wait = ProviderCooldown.RetryAt(FailureKind.RateLimit, quotaReport, now) - now;
        Assert(wait >= TimeSpan.FromHours(3.2) && wait <= TimeSpan.FromHours(3.3), "'resets in ~3h15m' must wait about 3h16m, got " + wait);

        Console.WriteLine("PASS tool-use wording, milestone requests, milestone scope of plans, tool step names and quota waits");
        return 0;
    }
}
