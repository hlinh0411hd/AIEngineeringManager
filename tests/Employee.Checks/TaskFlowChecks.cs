using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Employee.Core;
using Employee.Infrastructure;
using static Harness;

// Tasks, sessions, planning, steps, approvals and deadlines.
static class TaskFlowChecks
{
    public static async Task Run()
    {
        await Check("A tool step that names its preparation output field is mapped to the step key", () =>
        {
            var plan = new WorkPlan("p", [
                new("t1", "p", "Prepare tool input", WorkKind.Content, AccessLevel.ReadProject, []),
                new("t2", "p", "Generate animation", WorkKind.Content, AccessLevel.ReadProject, ["t1"], Tool: new ToolTaskSpec("pixel-foundry", "employee-pixel-art", "generate", null, "t1.toolInput")),
                new("t3", "p", "Generate again", WorkKind.Content, AccessLevel.ReadProject, [], Tool: new ToolTaskSpec("pixel-foundry", "employee-pixel-art", "generate", null, "t1.toolInput"))]);
            var normalized = PlanningService.NormalizeToolReferences(plan);
            var t2 = normalized.Tasks.Single(t => t.Key == "t2");
            var t3 = normalized.Tasks.Single(t => t.Key == "t3");
            Assert(t2.Tool!.InputFrom == "t1" && t2.DependsOn.SequenceEqual(["t1"]), "t1.toolInput maps to t1");
            // The second tool step reading t1 gets its own copy of the preparation step.
            Assert(t3.Tool!.InputFrom == "t1_t3" && t3.DependsOn.Contains("t1_t3") && normalized.Tasks.Any(t => t.Key == "t1_t3" && t.Tool == null), "The dependency is added when missing, on its own copy of the preparation");
            var unknown = PlanningService.NormalizeToolReferences(plan with { Tasks = [plan.Tasks[0], plan.Tasks[1] with { Tool = plan.Tasks[1].Tool! with { InputFrom = "x.toolInput" } }] });
            Assert(unknown.Tasks[1].Tool!.InputFrom == "x.toolInput", "An unknown step is left for validation to reject");
            return Task.CompletedTask;
        });
        await Check("After a rejected plan, the next planner is told none of its steps exist", async () =>
        {
            using var f = New();
            var session = SessionFor(f);
            session.Coordinator = new("codex", "a", "planner");
            session.RootWorkId = f.Work.Id;
            f.Store.SaveSession(session);
            var planning = new PlanningService(f.Store, f.Store, f.Manager);
            // First attempt: a plan the manager rejects.
            f.First.Result = new(FailureKind.None, JsonSerializer.Serialize(new WorkPlan("Prepare, generate and integrate the art.", []), AgentProtocol.Json));
            try
            {
                await planning.GenerateAsync(session, f.Settings, f.Health, CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
            }

            f.Store.Log(f.Work.Id, "PlanRejected", "InputFrom must name a declared AI preparation dependency in the same project.");
            // Second attempt: capture what the planner is shown.
            var context = "";
            f.First.Response = request =>
            {
                context = request.Context + "\n" + request.Work.Request;
                return new(FailureKind.None, JsonSerializer.Serialize(new WorkPlan("Fresh plan", [new("a", f.Project.Id, "Review", WorkKind.Review, AccessLevel.ReadProject, [])]), AgentProtocol.Json));
            };
            var result = await planning.GenerateAsync(session, f.Settings, f.Health, CancellationToken.None);
            Assert(result != null, "The second plan is accepted");
            Assert(context.Contains("REJECTED") && context.Contains("none of its steps exist") && context.Contains("InputFrom must name"), "The planner is told the previous plan was rejected and why");
            Assert(context.Contains("Steps that already exist in this task: "), "The planner is told how many steps exist");
        });
        await Check("A step is told not to regenerate art only when earlier steps produced images", async () =>
        {
            using var f = New();
            var earlier = new WorkItem { ProjectId = f.Project.Id, Kind = WorkKind.Review, Access = AccessLevel.ReadProject, Request = "Review the scene", State = WorkState.Completed };
            f.Store.SaveWork(earlier);
            var context = "";
            f.First.Response = request =>
            {
                context = request.Context;
                return new(FailureKind.None, "done");
            };
            f.Work.DependsOn = [earlier.Id];
            f.Store.SaveWork(f.Work);
            await f.Manager.RunAsync(f.Work, f.Settings, f.Health);
            Assert(f.First.Calls == 1 && !context.Contains("GENERATED PROJECT INPUTS") && !context.Contains("Do not regenerate them"), "No image inputs: no ban on generating art");
            f.Store.Log(earlier.Id, "ImageImported", "{\"projectAssetPath\":\"Assets/cat.png\"}");
            var next = new WorkItem { ProjectId = f.Project.Id, Kind = WorkKind.Coding, Access = AccessLevel.ReadProject, Request = "Review weekly quest code", DependsOn = [earlier.Id] };
            f.Store.SaveWork(next);
            await f.Manager.RunAsync(next, f.Settings, f.Health);
            Assert(context.Contains("GENERATED PROJECT INPUTS") && context.Contains("Assets/cat.png"), "Real image inputs are still passed on");
        });
        await Check("A JSON-only answer followed by a STEP STATUS line still parses", () =>
        {
            var answer = "{\"toolInput\":{\"action\":\"animation\",\"frames\":8}}  \nSTEP STATUS: COMPLETE";
            var stream = System.Text.Json.JsonSerializer.Serialize(new { type = "item.completed", item = new { type = "agent_message", text = answer } });
            var parsed = AgentProtocol.Parse<System.Text.Json.Nodes.JsonObject>(stream);
            Assert(parsed?["toolInput"]?["frames"]?.GetValue<int>() == 8, "The tool input survives the status line");
            return Task.CompletedTask;
        });
        await Check("Workers report a step status; an unfinished report is read even inside the Codex event stream", () =>
        {
            Assert(StepStatus.Remaining("Done the layout.\nSTEP STATUS: INCOMPLETE: walking frames, pathfinding") == "walking frames, pathfinding");
            Assert(StepStatus.Remaining("{\"type\":\"agent_message\",\"text\":\"Layout done.\\nSTEP STATUS: INCOMPLETE: isometric tilemap\"}") == "isometric tilemap");
            Assert(StepStatus.Remaining("STEP STATUS: INCOMPLETE: x\nfixed it\nSTEP STATUS: COMPLETE") == null, "The last status wins");
            Assert(StepStatus.Remaining("All done.") == null && StepStatus.Remaining("step status: incomplete: lower case prose") == null);
            return Task.CompletedTask;
        });
        await Check("A step that reports unfinished work runs again to finish it, then waits for review when attempts run out", async () =>
        {
            using var f = New();
            var contexts = new List<string>();
            f.First.Response = request =>
            {
                contexts.Add(request.Context);
                return new(FailureKind.None, contexts.Count == 1 ? "Scene done.\nSTEP STATUS: INCOMPLETE: walking frames" : "Frames done.\nSTEP STATUS: COMPLETE");
            };
            await f.Manager.RunAsync(f.Work, f.Settings, f.Health);
            Assert(f.First.Calls == 2 && f.Work.State == WorkState.NeedsReview, "The step ran again and finished, got " + f.Work.State);
            Assert(contexts[0].Contains("STEP STATUS:") && !contexts[0].Contains("CONTINUATION") && contexts[1].Contains("CONTINUATION") && contexts[1].Contains("walking frames"), "The second run is told what is left");
            Assert(!f.Store.Logs(f.Work.Id).Any(l => l.Event == StepStatus.GaveUp));

            using var h = New();
            h.Settings.MaxAttempts = 20;
            var run = 0;
            h.First.Response = _ => new(FailureKind.None, ++run switch
            {
                1 => "STEP STATUS: INCOMPLETE: tilemap, pathfinding, sprite frames",
                2 => "STEP STATUS: INCOMPLETE: pathfinding, sprite frames",
                _ => "STEP STATUS: INCOMPLETE: walking frames for five characters, zone animations, handoff events"
            });
            await h.Manager.RunAsync(h.Work, h.Settings, h.Health);
            // Runs 1-3 each report less or different missing work; runs 4 and 5 repeat run 3's gap.
            Assert(h.First.Calls == 3 + StepStatus.MaxStalls && h.Work.State == WorkState.NeedsReview && h.Work.Notes.Contains("no visible progress"), "Keeps going while it progresses, stops after repeated identical gaps, got " + h.First.Calls);

            using var same = New();
            same.Settings.MaxAttempts = 20;
            var turn = 0;
            same.First.Response = _ => new(FailureKind.None, ++turn == 1 ? "STEP STATUS: INCOMPLETE: verify scene, sprites and pathfinding in the app" : "STEP STATUS: INCOMPLETE: verify the scene, sprites and pathfinding in the running app");
            await same.Manager.RunAsync(same.Work, same.Settings, same.Health);
            Assert(same.First.Calls == 1 + StepStatus.MaxStalls && same.Work.Notes.Contains("no visible progress"), "The same gap repeated stops the extra runs, got " + same.First.Calls);

            // Why it is unfinished decides: a problem the model could not solve moves to a stronger
            // model; something outside the AI stops the step; more work keeps the same model going.
            Assert(StepStatus.WhyUnfinished("the unit tests still fail with a null reference error") == StepStatus.Why.Capability);
            Assert(StepStatus.WhyUnfinished("needs write permission to Assets/Arts") == StepStatus.Why.External);
            Assert(StepStatus.WhyUnfinished("missing file cat_back.png; the user must provide it") == StepStatus.Why.External);
            Assert(StepStatus.WhyUnfinished("remaining screens: shop and settings") == StepStatus.Why.MoreWork);
            Assert(StepStatus.WhyUnfinished("vẫn bị lỗi khi build") == StepStatus.Why.Capability && StepStatus.WhyUnfinished("chưa có quyền ghi vào project") == StepStatus.Why.External);
            using var hard = New(textOnly: true);
            hard.Settings.MaxAttempts = 20;
            hard.First.Response = _ => new(FailureKind.None, "STEP STATUS: INCOMPLETE: the pathfinding test still fails with an off-by-one error");
            hard.Second.Response = _ => new(FailureKind.None, "Fixed.\nSTEP STATUS: COMPLETE");
            await hard.Manager.RunAsync(hard.Work, hard.Settings, hard.Health);
            Assert(hard.First.Calls == 2 && hard.Second.Calls == 1 && hard.Work.Escalations == 1 && hard.Store.Logs(hard.Work.Id).Any(l => l.Event == "StepEscalated"), $"A stalled capability problem moves to another model: first={hard.First.Calls} second={hard.Second.Calls} escalations={hard.Work.Escalations}");
            using var outside = New(textOnly: true);
            outside.Settings.MaxAttempts = 20;
            outside.First.Response = _ => new(FailureKind.None, "STEP STATUS: INCOMPLETE: missing file cat_back.png, the user must provide it");
            await outside.Manager.RunAsync(outside.Work, outside.Settings, outside.Health);
            Assert(outside.First.Calls == 1 && outside.Second.Calls == 0 && outside.Work.Notes.Contains("outside the AI"), $"Something external stops at once without a stronger model: first={outside.First.Calls} second={outside.Second.Calls} notes={outside.Work.Notes}");

            using var endless = New();
            endless.Settings.MaxAttempts = 0;
            var part = 0;
            endless.First.Response = _ => new(FailureKind.None, ++part > 30 ? "STEP STATUS: COMPLETE" : $"STEP STATUS: INCOMPLETE: part{part + 1} part{part + 2} part{part + 3}");
            await endless.Manager.RunAsync(endless.Work, endless.Settings, endless.Health);
            Assert(endless.First.Calls == 31 && endless.Work.State == WorkState.NeedsReview && !endless.Store.Logs(endless.Work.Id).Any(l => l.Event == StepStatus.GaveUp), "Unlimited attempts keep a progressing step running to the end, got " + endless.First.Calls);
            Assert(endless.Settings.UnlimitedAttempts && !endless.Settings.AttemptsUsedUp(endless.Work), "Unlimited never runs out");
            Assert(StepStatus.Instruction.Contains("launching or visually checking the app"), "Workers are told app checks are not missing work");

            using var g = New();
            g.Settings.MaxAttempts = 2;
            g.First.Result = new(FailureKind.None, "Partial.\nSTEP STATUS: INCOMPLETE: pathfinding");
            await g.Manager.RunAsync(g.Work, g.Settings, g.Health);
            Assert(g.First.Calls == 2 && g.Work.State == WorkState.NeedsReview && g.Work.Notes.Contains("unfinished work") && g.Work.Notes.Contains("pathfinding"), "Stops at the attempt limit and says what is missing");
            Assert(StepStatus.Latest(g.Store.Logs(g.Work.Id), StepStatus.GaveUp) == "pathfinding", "The session sees the step as unfinished");
        });
        await Check("Tool steps that name another tool step as input, give both input forms, or none, are repaired", () =>
        {
            var tool = new ToolTaskSpec("pixel-foundry", "employee-pixel-art", "generate", new System.Text.Json.Nodes.JsonObject { ["action"] = "create" });
            var plan = new WorkPlan("p", [
                new PlannedTask("t1", "p", "inspect", WorkKind.Review, AccessLevel.ReadProject, []),
                new PlannedTask("t2", "p", "create", WorkKind.Content, AccessLevel.ReadProject, ["t1"], Tool: tool),
                new PlannedTask("t3", "p", "animate", WorkKind.Content, AccessLevel.ReadProject, ["t2"], Tool: tool with { Input = new System.Text.Json.Nodes.JsonObject { ["action"] = "animation" }, InputFrom = "t2" }),
                new PlannedTask("t4", "p", "animate again", WorkKind.Content, AccessLevel.ReadProject, [], Tool: tool with { Input = new System.Text.Json.Nodes.JsonObject { ["action"] = "edit" }, InputFrom = "t1" }),
                new PlannedTask("t5", "p", "anything", WorkKind.Content, AccessLevel.ReadProject, [], Tool: tool with { Input = null, InputFrom = null })]);
            var fixedPlan = PlanningService.NormalizeToolReferences(plan).Tasks;
            Assert(fixedPlan[2].Tool!.InputFrom == null && fixedPlan[2].Tool!.Input!["image"]?["$imageFrom"]?.GetValue<string>() == "t2", "A tool step named as input becomes the image source");
            Assert(fixedPlan[3].Tool!.InputFrom == null && fixedPlan[3].Tool!.Input!["action"]!.GetValue<string>() == "edit" && fixedPlan[3].DependsOn.Contains("t1"), "With both, the explicit input wins and the step stays a dependency");
            Assert(fixedPlan[4].Tool!.Input != null, "With neither, an empty request is completed later");
            Assert(fixedPlan.Where(t => t.Tool != null).All(t => t.SkillId == "employee-pixel-art"), "A tool step without a skill gets its tool's skill");
            return Task.CompletedTask;
        });
        await Check("A plan that is not valid JSON is asked for once more, with the error", async () =>
        {
            using var f = New();
            var session = SessionFor(f);
            session.Coordinator = new("codex", "a", "planner");
            session.RootWorkId = f.Work.Id;
            f.Store.SaveSession(session);
            var planning = new PlanningService(f.Store, f.Store, f.Manager);
            var good = JsonSerializer.Serialize(new WorkPlan("Fresh plan", [new("a", f.Project.Id, "Review", WorkKind.Review, AccessLevel.ReadProject, [])]), AgentProtocol.Json);
            var asked = new List<string>();
            AgentResult Answer(ExecutionRequest request)
            {
                asked.Add(request.Work.Request);
                return new(FailureKind.None, asked.Count == 1 ? good + "}" : good);
            }
            f.First.Response = Answer;
            f.Second.Response = Answer;
            var result = await planning.GenerateAsync(session, f.Settings, f.Health, CancellationToken.None);
            var coordinatorLogs = string.Join(" | ", f.Store.WorkItems().Where(w => w.IsCoordinator).SelectMany(w => f.Store.Logs(w.Id)).Select(l => l.Event + ":" + l.Message[..Math.Min(120, l.Message.Length)]));
            Assert(result != null && asked.Count == 2, "The plan is accepted on the second answer, got " + asked.Count + " calls; " + coordinatorLogs);
            Assert(asked[1].Contains("PREVIOUS ANSWER WAS NOT VALID JSON"), "The second call is told what was wrong");
            Assert(AgentProtocol.JsonError(good) == null && AgentProtocol.JsonError("{\"a\":}") != null, "Valid JSON has no error; broken JSON has one");
        });
        await Check("An empty plan is reported as such, with the planner's reason", async () =>
        {
            using var f = New();
            var session = SessionFor(f);
            session.Coordinator = new("codex", "a", "planner");
            f.Store.SaveSession(session);
            f.First.Result = new(FailureKind.None, JsonSerializer.Serialize(new WorkPlan("Pixel Art Tool is not running.", []), AgentProtocol.Json));
            var message = "";
            try
            {
                await new PlanningService(f.Store, f.Store, f.Manager).GenerateAsync(session, f.Settings, f.Health, CancellationToken.None);
            }
            catch (InvalidOperationException error)
            {
                message = error.Message;
            }

            Assert(message.Contains("returned no steps") && message.Contains("Pixel Art Tool is not running") && !message.Contains("task limit"), "Empty plan message was: " + message);
        });
        await Check("SemiAutomatic proposes without execution then accepts exact approval", async () =>
        {
            using var f = New();
            f.Settings.Mode = RoutingMode.SemiAutomatic;
            await f.Manager.RunAsync(f.Work, f.Settings, f.Health);
            Assert(f.Work.State == WorkState.AwaitingApproval && f.First.Calls == 0);
            await f.Manager.RunAsync(f.Work, f.Settings, f.Health, new("codex", "a", "approved"), true);
            Assert(f.First.Calls == 1 && f.Work.State == WorkState.NeedsReview);
        });
        await Check("Task review handles legacy/plain results and hides results from an earlier run", () =>
        {
            using var f = New();
            var now = DateTimeOffset.UtcNow;
            TaskLog[] logs = [new(1, f.Work.Id, now, "Execution", "provider metadata\nPlain response from AI")];
            Assert(TaskReviewReport.Create(f.Work, f.Project, logs, null).Result == "Plain response from AI");
            var newer = logs.Append(new TaskLog(2, f.Work.Id, now, "Route", "new run")).ToArray();
            Assert(!TaskReviewReport.Create(f.Work, f.Project, newer, null).Result.Contains("Plain response"));
            Assert(TaskReviewReport.Create(f.Work, f.Project, [], null).Changes.Contains("No checkpoint"));
            return Task.CompletedTask;
        });
        await Check("Implementation with approved write access reaches the permitted worker", async () =>
        {
            using var f = New();
            f.Work.Request = "Implement weekly quests";
            f.Work.Access = AccessLevel.WriteProject;
            f.Store.SaveWork(f.Work);
            f.Settings.Providers.Single(p => p.Id == "codex").AllowProjectWrite = true;
            await f.Manager.RunAsync(f.Work, f.Settings, f.Health);
            Assert(f.First.Calls == 1 && f.Work.State == WorkState.NeedsReview, "Valid write scope must still execute; WrongAccess cannot become an unconditional block.");
        });
        await Check("Unified task schema backs up V2 before root conversion and rejects future schemas", () =>
        {
            var path = Path.Combine(root, "migration-v2.db");
            string id;
            using (var s = new SqliteStore(path))
            {
                var session = new WorkSession
                {
                    Goal = "Existing session"
                };
                id = session.Id;
                s.SaveSession(session);
            }

            using (var c = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + path))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "PRAGMA user_version=2";
                cmd.ExecuteNonQuery();
            }

            using (var s = new SqliteStore(path))
            {
                Assert(s.Sessions().Single().Id == id);
            }

            var backup = Directory.GetFiles(root, "migration-v2.db.v2-*.bak").Single();
            using (var c = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + backup))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "PRAGMA user_version";
                Assert(Convert.ToInt32(cmd.ExecuteScalar()) == 2);
            }

            using (var c = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + path))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "PRAGMA user_version";
                Assert(Convert.ToInt32(cmd.ExecuteScalar()) == 3);
                cmd.CommandText = "PRAGMA user_version=4";
                cmd.ExecuteNonQuery();
            }

            bool rejected = false;
            try
            {
                using var s = new SqliteStore(path);
            }
            catch (InvalidOperationException)
            {
                rejected = true;
            }

            Assert(rejected);
            return Task.CompletedTask;
        });
        await Check("Plans reject scope escalation and dependency cycles before creating tasks", () =>
        {
            using var f = New();
            var session = SessionFor(f);
            var planner = new PlanningService(f.Store, f.Store, f.Manager);
            foreach (var plan in new[]
            {
                new WorkPlan("bad project", [new("a", "other", "work", WorkKind.Coding, AccessLevel.ReadProject, [])]),
                new WorkPlan("bad access", [new("a", f.Project.Id, "work", WorkKind.Coding, AccessLevel.WriteProject, [])]),
                new WorkPlan("cycle", [new("a", f.Project.Id, "a", WorkKind.Coding, AccessLevel.ReadProject, ["b"]), new("b", f.Project.Id, "b", WorkKind.Coding, AccessLevel.ReadProject, ["a"])])
            }

            )
            {
                bool rejected = false;
                try
                {
                    planner.Import(session, plan);
                }
                catch (InvalidOperationException)
                {
                    rejected = true;
                }

                Assert(rejected);
            }

            Assert(f.Store.WorkItems().Count == 1);
            return Task.CompletedTask;
        });
        await Check("Plan approval creates dependency IDs atomically and cannot duplicate tasks", () =>
        {
            using var f = New();
            var session = SessionFor(f);
            var planner = new PlanningService(f.Store, f.Store, f.Manager);
            var record = planner.Import(session, new("Two steps", [new("a", f.Project.Id, "First", WorkKind.Coding, AccessLevel.ReadProject, []), new("b", f.Project.Id, "Second", WorkKind.Coding, AccessLevel.ReadProject, ["a"])]));
            planner.Approve(session, record.Id);
            var children = f.Store.WorkItems().Where(w => w.Id != f.Work.Id).ToArray();
            Assert(children.Length == 2);
            Assert(children.Single(w => w.Request == "Second").DependsOn.Single() == children.Single(w => w.Request == "First").Id);
            bool duplicate = false;
            try
            {
                planner.Approve(session, record.Id);
            }
            catch (InvalidOperationException)
            {
                duplicate = true;
            }

            Assert(duplicate && f.Store.WorkItems().Count == 3);
            return Task.CompletedTask;
        });
        await Check("On-demand coordinator uses read access, consumes one call and only proposes a plan", async () =>
        {
            using var f = New();
            var session = SessionFor(f);
            session.AccessCeiling = AccessLevel.WriteProject;
            session.Coordinator = new("codex", "a", "coordinator");
            f.Store.SaveSession(session);
            f.First.Before = () => Assert(f.Store.WorkItems().Single(w => w.IsCoordinator).Access == AccessLevel.ReadProject);
            f.First.Result = new(FailureKind.None, JsonSerializer.Serialize(new WorkPlan("Plan", [new("a", f.Project.Id, "Review", WorkKind.Review, AccessLevel.ReadProject, [])]), AgentProtocol.Json));
            var record = await new PlanningService(f.Store, f.Store, f.Manager).GenerateAsync(session, f.Settings, f.Health, CancellationToken.None);
            Assert(record is { State: DecisionState.Pending } && f.First.Calls == 1 && session.CallsUsed == 1);
            Assert(f.Store.WorkItems().Count(w => !w.IsCoordinator) == 1);
        });
        await Check("Away runs independent tasks, leaves reviews and blocks unmet dependencies", async () =>
        {
            using var f = New();
            var session = SessionFor(f);
            ExtraWork(f, session);
            var dependent = ExtraWork(f, session, "Dependent", [f.Work.Id]);
            await EngineFor(f).RunAsync(session.Id, f.Settings, f.Health);
            Assert(f.First.Calls == 2);
            Assert(f.Store.WorkItems().Single(w => w.Id == dependent.Id).State == WorkState.Queued);
            Assert(f.Store.Decisions(session.Id).Count(d => d.Kind == "Review") == 2);
            Assert(f.Store.Sessions().Single().State == SessionState.WaitingForUser);
        });
        await Check("Attended sessions yield after a result and do not silently approve it", async () =>
        {
            using var f = New();
            var session = SessionFor(f, away: false);
            ExtraWork(f, session);
            await EngineFor(f).RunAsync(session.Id, f.Settings, f.Health);
            Assert(f.First.Calls == 1 && f.Store.Sessions().Single().State == SessionState.WaitingForUser);
        });
        await Check("Session call and time budgets prevent additional AI invocations", async () =>
        {
            using var f = New();
            var session = SessionFor(f);
            session.MaxCalls = 1;
            f.Store.SaveSession(session);
            ExtraWork(f, session);
            await EngineFor(f).RunAsync(session.Id, f.Settings, f.Health);
            Assert(f.First.Calls == 1 && f.Store.Sessions().Single().State == SessionState.BudgetReached);
            using var expired = New();
            var old = SessionFor(expired);
            old.Deadline = DateTimeOffset.UtcNow.AddMinutes(-1);
            expired.Store.SaveSession(old);
            await EngineFor(expired).RunAsync(old.Id, expired.Settings, expired.Health);
            Assert(expired.First.Calls == 0 && expired.Store.Sessions().Single().State == SessionState.BudgetReached);
        });
        await Check("Session Stop awaits cancellation and saves checkpoint; pause stops intake", async () =>
        {
            using var f = New();
            var session = SessionFor(f);
            f.First.Block = true;
            var engine = EngineFor(f);
            var running = engine.RunAsync(session.Id, f.Settings, f.Health);
            await f.First.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            engine.Stop();
            await running;
            Assert(f.First.Exited && f.Store.Sessions().Single().State == SessionState.Stopped && f.Store.LatestHandoff(f.Work.Id) != null);
            using var paused = New();
            var p = SessionFor(paused);
            ExtraWork(paused, p);
            var scheduler = EngineFor(paused);
            paused.First.Before = scheduler.PauseAfterCurrent;
            await scheduler.RunAsync(p.Id, paused.Settings, paused.Health);
            Assert(paused.First.Calls == 1 && paused.Store.Sessions().Single().State == SessionState.Paused);
        });
        await Check("Review approval unblocks dependencies but does not bypass test verification", async () =>
        {
            using var f = New();
            var session = SessionFor(f);
            var dependent = ExtraWork(f, session, "Dependent", [f.Work.Id]);
            var engine = EngineFor(f);
            await engine.RunAsync(session.Id, f.Settings, f.Health);
            engine.ResolveDecision(session.Id, f.Store.Decisions(session.Id).Single(d => d.Kind == "Review").Id, true, f.Settings, f.Health);
            await engine.RunAsync(session.Id, f.Settings, f.Health);
            Assert(f.First.Calls == 2);
            Assert(f.Store.WorkItems().Single(w => w.Id == dependent.Id).State == WorkState.NeedsReview);
            Assert(f.Store.WorkItems().Single(w => w.Id == f.Work.Id).Verification != VerificationState.Verified);
        });
        await Check("Delegation preserves scope/handoff and only invokes permitted target", async () =>
        {
            using var f = New(textOnly: true);
            var session = SessionFor(f);
            session.AllowDelegation = true;
            f.Store.SaveSession(session);
            SeedDelegationEvidence(f);
            f.First.Result = new(FailureKind.None, "{\"delegation\":{\"providerId\":\"gemini\",\"modelId\":\"b\",\"reason\":\"Research specialist\"}}");
            await EngineFor(f).RunAsync(session.Id, f.Settings, f.Health);
            var child = f.Store.WorkItems().Single(w => w.ParentTaskId == f.Work.Id);
            Assert(child.Access == f.Work.Access && child.ProjectId == f.Project.Id && child.DelegationDepth == 1);
            Assert(f.Second.Calls == 1 && f.Store.Sessions().Single().SwitchesUsed == 1);
            Assert(f.Store.LatestHandoff(child.Id) != null);
            Assert(f.Store.WorkItems().Single(w => w.Id == f.Work.Id).State == WorkState.Delegated);
        });
        await Check("Delegation cycle and depth gates stop AI-to-AI churn", async () =>
        {
            using var f = New(textOnly: true);
            var session = SessionFor(f);
            session.AllowDelegation = true;
            f.Store.SaveSession(session);
            f.First.Result = new(FailureKind.None, "{\"delegation\":{\"providerId\":\"gemini\",\"modelId\":\"b\",\"reason\":\"Next\"}}");
            f.Second.Result = new(FailureKind.None, "{\"delegation\":{\"providerId\":\"claude\",\"modelId\":\"a\",\"reason\":\"Back\"}}");
            SeedDelegationEvidence(f);
            await EngineFor(f).RunAsync(session.Id, f.Settings, f.Health);
            Assert(f.First.Calls == 1 && f.Second.Calls == 1);
            Assert(f.Store.WorkItems().Count == 2);
            Assert(f.Store.Decisions(session.Id).Any(d => d.Kind == "Delegation" && d.Message.Contains("routing cycle")));
            using var depth = New(textOnly: true);
            var s = SessionFor(depth);
            s.AllowDelegation = true;
            s.MaxDelegationDepth = 0;
            depth.Store.SaveSession(s);
            depth.First.Result = f.First.Result;
            await EngineFor(depth).RunAsync(s.Id, depth.Settings, depth.Health);
            Assert(depth.Second.Calls == 0 && depth.Store.WorkItems().Count == 1);
        });
        await Check("Delegation without measured benefit waits for approval and never invokes the target", async () =>
        {
            using var f = New(textOnly: true);
            var session = SessionFor(f);
            session.AllowDelegation = true;
            f.Store.SaveSession(session);
            f.First.Result = new(FailureKind.None, "{\"delegation\":{\"providerId\":\"gemini\",\"modelId\":\"b\",\"reason\":\"Suggested specialist\"}}");
            await EngineFor(f).RunAsync(session.Id, f.Settings, f.Health);
            Assert(f.First.Calls == 1 && f.Second.Calls == 0 && f.Store.WorkItems().Count == 1);
            Assert(f.Store.Decisions(session.Id).Any(d => d.Kind == "Delegation" && d.State == DecisionState.Pending));
        });
        await Check("Session permission ceiling also rejects direct tampered work records", async () =>
        {
            using var f = New();
            var session = SessionFor(f);
            f.Work.Access = AccessLevel.WriteProject;
            f.Store.SaveWork(f.Work);
            await EngineFor(f).RunAsync(session.Id, f.Settings, f.Health);
            Assert(f.First.Calls == 0 && f.Store.WorkItems().Single(w => w.Id == f.Work.Id).State == WorkState.WrongAccess && f.Store.Sessions().Single().State == SessionState.WaitingForUser);
            Assert(f.Store.Decisions(session.Id).Any(d => d.Kind == "WrongAccess" && d.State == DecisionState.Pending));
        });
        await Check("V2 restart recovery pauses sessions and interrupts tests without auto-resume", () =>
        {
            using var f = New();
            var s = SessionFor(f);
            s.State = SessionState.Running;
            f.Store.SaveSession(s);
            f.Store.SaveTestJob(new() { ProjectId = f.Project.Id, State = TestJobState.Running });
            f.Store.RecoverInterruptedWork();
            Assert(f.Store.Sessions().Single().State == SessionState.Paused && f.Store.TestJobs().Single().State == TestJobState.Interrupted);
            return Task.CompletedTask;
        });
        await Check("Smart tests invalidate cached results when an approved command changes", async () =>
        {
            using var f = New();
            var profile = new ProjectTestProfile
            {
                ProjectId = f.Project.Id,
                Name = "Versioned check",
                Executable = Environment.ProcessPath!,
                Arguments = ["--echo", "first"],
                Enabled = true
            };
            f.Store.SaveTestProfile(profile);
            var service = new SmartTestingService(f.Store, f.Store, f.Manager, new ProcessRunner());
            File.WriteAllText(Path.Combine(f.Project.Root, "Quest.cs"), "code");
            var first = service.Enqueue(f.Work, ["Quest.cs"], "same").Single();
            await service.RunAsync(first, CancellationToken.None);
            profile.Arguments = ["--echo", "second"];
            f.Store.SaveTestProfile(profile);
            var second = service.Enqueue(f.Work, ["Quest.cs"], "same").Single();
            Assert(second.Id != first.Id);
            profile.Arguments = ["--echo", "third"];
            f.Store.SaveTestProfile(profile);
            bool rejected = false;
            try
            {
                await service.RunAsync(second, CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                rejected = true;
            }

            Assert(rejected && second.State == TestJobState.Pending);
        });
        await Check("A plain task automatically uses bounded orchestration without a separate session setup", async () =>
        {
            using var f = New();
            var flow = UnifiedFor(f);
            await flow.StartAsync(f.Work, f.Settings, f.Health);
            var goal = f.Store.WorkItems().Single(w => w.Id == f.Work.Id);
            Assert(goal.IsGoal && goal.SessionId != null && goal.State == WorkState.NeedsReview);
            Assert(f.First.Calls == 1 && flow.Steps(goal).Single().State == WorkState.Completed);
            Assert(flow.CanComplete(goal));
            Assert(f.Store.Sessions().Single().RootWorkId == goal.Id);
            Assert(goal.Verification != VerificationState.Verified);
        });
        await Check("Complex tasks plan automatically using the permitted worker kind and run dependencies", async () =>
        {
            using var f = New();
            f.Settings.Automation.Planning = PlanningPolicy.Always;
            f.Settings.Automation.MaxTasks = 2;
            f.Settings.Providers.Single(p => p.Id == "codex").Models = [new("a", [WorkKind.Coding])];
            f.Settings.Providers.Single(p => p.Id == "gemini").Enabled = false;
            f.First.Response = r => r.Work.IsCoordinator ? new(FailureKind.None, JsonSerializer.Serialize(new WorkPlan("Two bounded steps", [new("a", f.Project.Id, "Inspect quest code", WorkKind.Coding, AccessLevel.ReadProject, []), new("b", f.Project.Id, "Check code", WorkKind.Review, AccessLevel.ReadProject, ["a"])]), AgentProtocol.Json)) : new(FailureKind.None, "Step output");
            var flow = UnifiedFor(f);
            await flow.StartAsync(f.Work, f.Settings, f.Health);
            var goal = f.Store.WorkItems().Single(w => w.Id == f.Work.Id);
            Assert(f.First.Calls == 3 && goal.State == WorkState.NeedsReview && goal.Attempts == 3);
            Assert(flow.Steps(goal).Count == 2 && flow.Steps(goal).All(w => w.State == WorkState.Completed && w.ParentTaskId == goal.Id));
            // Planning is routed as planning (hard work), carried out by the permitted Coding model.
            Assert(f.Store.WorkItems().Single(w => w.IsCoordinator) is { Kind: WorkKind.Planning, ProviderId: "codex", ModelId: "a" });
            Assert(f.Settings.Providers.Single(p => p.Id == "codex").Models.Single().Kinds.SequenceEqual([WorkKind.Coding]));
        });
        await Check("Unified SemiAutomatic asks in the task before calling any AI", async () =>
        {
            using var f = New();
            f.Settings.Mode = RoutingMode.SemiAutomatic;
            var flow = UnifiedFor(f);
            await flow.StartAsync(f.Work, f.Settings, f.Health);
            var goal = f.Store.WorkItems().Single(w => w.Id == f.Work.Id);
            Assert(goal.State == WorkState.AwaitingApproval && f.First.Calls == 0);
            await flow.StartAsync(goal, f.Settings, f.Health, approved: true);
            goal = f.Store.WorkItems().Single(w => w.Id == goal.Id);
            Assert(f.First.Calls == 1 && !flow.CanComplete(goal));
            var decision = flow.Decisions(goal).Single(d => d.Kind == "Review");
            flow.Resolve(goal, decision.Id, true, f.Settings, f.Health);
            await flow.StartAsync(goal, f.Settings, f.Health);
            Assert(flow.CanComplete(f.Store.WorkItems().Single(w => w.Id == goal.Id)) && f.First.Calls == 1);
        });
        await Check("Unified Manual does not silently choose a planner or worker", async () =>
        {
            using var f = New();
            f.Settings.Mode = RoutingMode.Manual;
            f.Settings.Automation.Planning = PlanningPolicy.Always;
            var flow = UnifiedFor(f);
            await flow.StartAsync(f.Work, f.Settings, f.Health);
            Assert(f.First.Calls == 0 && f.Store.WorkItems().Single(w => w.Id == f.Work.Id).State == WorkState.WaitingForProvider);
        });
        await Check("Plans outside the registered project fall back to the original project task", async () =>
        {
            using var f = New();
            f.Settings.Automation.Planning = PlanningPolicy.Always;
            f.First.Response = r => r.Work.IsCoordinator ? new(FailureKind.None, JsonSerializer.Serialize(new WorkPlan("invalid", [new("x", "unregistered-project", "Write outside project", WorkKind.Coding, AccessLevel.WriteProject, [])]), AgentProtocol.Json)) : new(FailureKind.None, "Read-only analysis done");
            var flow = UnifiedFor(f);
            await flow.StartAsync(f.Work, f.Settings, f.Health);
            var goal = f.Store.WorkItems().Single(w => w.Id == f.Work.Id);
            var step = flow.Steps(goal).Single();
            Assert(step.ProjectId == f.Project.Id && step.Access == AccessLevel.WriteProject && goal.State == WorkState.NeedsReview);
            Assert(f.Store.Logs(goal.Id).Any(l => l.Event == "PlanRejected"));
        });
        await Check("A one-call task budget skips planning and never overspends", async () =>
        {
            using var f = New();
            f.Settings.Automation.Planning = PlanningPolicy.Always;
            f.Settings.Automation.MaxCalls = 1;
            var flow = UnifiedFor(f);
            await flow.StartAsync(f.Work, f.Settings, f.Health);
            Assert(f.First.Calls == 1 && f.Store.WorkItems().All(w => !w.IsCoordinator));
            var goal = f.Store.WorkItems().Single(w => w.Id == f.Work.Id);
            Assert(goal.State == WorkState.NeedsReview && goal.Attempts == 1);
        });
        await Check("Unapplied edit proposals still wait for the user within the main task", async () =>
        {
            using var f = New(textOnly: true);
            f.Work.Access = AccessLevel.WriteProject;
            f.Store.SaveWork(f.Work);
            foreach (var p in f.Settings.Providers.Where(p => p.Id is "claude" or "gemini")) p.AllowProjectWrite = true;
            var health = f.Health.Select(h => h with { CanWrite = true }).ToArray();
            // Claude now edits files directly; a proposal-only CLI (here Gemini) still returns proposals.
            Assert(Orchestrator.EditsDirectly("claude") && Orchestrator.EditsDirectly("codex") && !Orchestrator.EditsDirectly("gemini"));
            f.First.Result = new(FailureKind.RateLimit, "usage limit");
            f.Second.Result = new(FailureKind.None, JsonSerializer.Serialize(new EditProposal("proposal", [new("Quest.cs", "NEW", "code")])));
            var flow = UnifiedFor(f);
            await flow.StartAsync(f.Work, f.Settings, health);
            var goal = f.Store.WorkItems().Single(w => w.Id == f.Work.Id);
            Assert(goal.State == WorkState.NeedsReview && !flow.CanComplete(goal) && flow.Decisions(goal).Any(d => d.Kind == "Review"));
            Assert(!File.Exists(Path.Combine(f.Project.Root, "Quest.cs")));
        });
        await Check("Legacy sessions become task roots without deleting work or resetting limits", () =>
        {
            using var f = New();
            var session = SessionFor(f);
            session.CallsUsed = 3;
            session.MaxCalls = 4;
            session.State = SessionState.Paused;
            f.Store.SaveSession(session);
            var flow = UnifiedFor(f);
            flow.WrapLegacySessions();
            flow.WrapLegacySessions();
            var rootGoal = f.Store.WorkItems().Single(w => w.IsGoal);
            Assert(f.Store.WorkItems().Count == 2 && f.Store.WorkItems().Single(w => w.Id == f.Work.Id).ParentTaskId == rootGoal.Id);
            Assert(flow.SessionFor(rootGoal)?.CallsUsed == 3 && flow.SessionFor(rootGoal)?.MaxCalls == 4);
            Assert(f.First.Calls == 0);
            return Task.CompletedTask;
        });
        await Check("Unified Stop awaits the worker and preserves task checkpoint", async () =>
        {
            using var f = New();
            f.First.Block = true;
            var flow = UnifiedFor(f);
            var running = flow.StartAsync(f.Work, f.Settings, f.Health);
            await f.First.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            flow.Stop();
            await running;
            var goal = f.Store.WorkItems().Single(w => w.Id == f.Work.Id);
            Assert(!flow.IsBusy && f.First.Exited && goal.State == WorkState.Paused && f.Store.LatestHandoff(flow.Steps(goal).Single().Id) != null);
        });
        await Check("SemiAutomatic plans are reviewed and approved inline before steps exist", async () =>
        {
            using var f = New();
            f.Settings.Mode = RoutingMode.SemiAutomatic;
            f.Settings.Automation.Planning = PlanningPolicy.Always;
            f.First.Response = r => r.Work.IsCoordinator ? new(FailureKind.None, JsonSerializer.Serialize(new WorkPlan("Inline plan", [new("a", f.Project.Id, "Work", WorkKind.Coding, AccessLevel.ReadProject, [])]), AgentProtocol.Json)) : new(FailureKind.None, "done");
            var flow = UnifiedFor(f);
            await flow.StartAsync(f.Work, f.Settings, f.Health);
            var goal = f.Store.WorkItems().Single(w => w.Id == f.Work.Id);
            await flow.StartAsync(goal, f.Settings, f.Health, approved: true);
            goal = f.Store.WorkItems().Single(w => w.Id == goal.Id);
            Assert(f.First.Calls == 1 && flow.Steps(goal).Count == 0 && goal.State == WorkState.AwaitingApproval);
            var decision = flow.Decisions(goal).Single(d => d.Kind == "Plan");
            flow.Resolve(goal, decision.Id, true, f.Settings, f.Health);
            Assert(flow.Steps(goal).Count == 1);
            await flow.StartAsync(goal, f.Settings, f.Health, approved: true);
            Assert(f.First.Calls == 2);
        });
        await Check("Cancelling automatic planning never starts a worker afterward", async () =>
        {
            using var f = New();
            f.Settings.Automation.Planning = PlanningPolicy.Always;
            f.First.Block = true;
            var flow = UnifiedFor(f);
            var running = flow.StartAsync(f.Work, f.Settings, f.Health);
            await f.First.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            flow.Stop();
            await running;
            var goal = f.Store.WorkItems().Single(w => w.Id == f.Work.Id);
            Assert(f.First.Calls == 1 && flow.Steps(goal).Count == 0 && goal.State == WorkState.Paused && goal.Attempts == 1);
        });
        await Check("Stored execution attempts are reconciled before resuming a task", async () =>
        {
            using var f = New();
            f.Work.IsGoal = true;
            f.Store.SaveWork(f.Work);
            var s = new WorkSession
            {
                Goal = f.Work.Request,
                RootWorkId = f.Work.Id,
                ProjectIds = [f.Project.Id],
                MaxCalls = 1,
                State = SessionState.Paused
            };
            f.Store.SaveSession(s);
            f.Work.SessionId = s.Id;
            f.Store.SaveWork(f.Work);
            f.Store.SaveWork(new() { SessionId = s.Id, ParentTaskId = f.Work.Id, ProjectId = f.Project.Id, Request = "Interrupted worker", Access = AccessLevel.ReadProject, Attempts = 1, State = WorkState.Paused });
            await UnifiedFor(f).StartAsync(f.Work, f.Settings, f.Health);
            Assert(f.First.Calls == 0 && f.Store.Sessions().Single().CallsUsed == 1 && f.Store.Sessions().Single().State == SessionState.BudgetReached);
        });
        await Check("Resuming a finished task does not spend usage or remove its final review state", async () =>
        {
            using var f = New();
            f.Settings.Automation.MaxCalls = 1;
            var flow = UnifiedFor(f);
            await flow.StartAsync(f.Work, f.Settings, f.Health);
            var goal = f.Store.WorkItems().Single(w => w.Id == f.Work.Id);
            await flow.StartAsync(goal, f.Settings, f.Health);
            Assert(f.First.Calls == 1 && f.Store.WorkItems().Single(w => w.Id == goal.Id).State == WorkState.NeedsReview);
        });
        await Check("Unlimited sessions ignore old deadlines and keep cancellation available", async () =>
        {
            using var f = New();
            var session = SessionFor(f);
            session.DurationMinutes = 0;
            session.Deadline = DateTimeOffset.UtcNow.AddHours(-1);
            f.Store.SaveSession(session);
            await EngineFor(f).RunAsync(session.Id, f.Settings, f.Health);
            Assert(f.First.Calls == 1 && f.Store.Sessions().Single().Deadline == null);
            using var p = New();
            var s = SessionFor(p);
            s.DurationMinutes = 0;
            s.Coordinator = new("codex", "a", "planner");
            p.First.Result = new(FailureKind.None, JsonSerializer.Serialize(new WorkPlan("Unlimited plan", [new("a", p.Project.Id, "Work", WorkKind.Coding, AccessLevel.ReadProject, [])]), AgentProtocol.Json));
            p.Store.SaveSession(s);
            Assert(await new PlanningService(p.Store, p.Store, p.Manager).GenerateAsync(s, p.Settings, p.Health, CancellationToken.None) != null);
        });
        await Check("After code steps the project is built and tested; a failure becomes a fix step, the same failure again stops", async () =>
        {
            var unity = Directory.CreateTempSubdirectory("unity-check").FullName;
            Directory.CreateDirectory(Path.Combine(unity, "Assets")); Directory.CreateDirectory(Path.Combine(unity, "ProjectSettings"));
            File.WriteAllText(Path.Combine(unity, "Assembly-CSharp.csproj"), "<Project/>");
            var command = AutoCheck.Detect(new ProjectInfo("u", "Game", unity), []);
            Assert(command is { Executable: "dotnet" } && command.Arguments.Contains("Assembly-CSharp.csproj"), "Unity projects compile their scripts");
            Assert(AutoCheck.Detect(new ProjectInfo("u", "Game", unity), [new ProjectTestProfile { ProjectId = "u", Enabled = true, Name = "Mine", Executable = "dotnet", Arguments = ["test"] }])!.Name == "Mine", "The user's own test command wins");
            Assert(AutoCheck.Digest("Build started\nFoo.cs(3,1): error CS1002: ; expected\nBuild FAILED.\n").Contains("error CS1002"), "The error lines are picked out");

            using var f = New();
            File.WriteAllText(Path.Combine(f.Project.Root, "broken.py"), "def oops(:\n    pass\n");
            var session = SessionFor(f);
            session.RootWorkId = f.Work.Id;
            session.AutoContinueSteps = true;
            session.AccessCeiling = AccessLevel.WriteProject;
            f.Store.SaveSession(session);
            foreach (var p in f.Settings.Providers.Where(p => p.Id == f.First.Id)) p.AllowProjectWrite = true;
            var code = ExtraWork(f, session, "Implement the feature");
            code.Access = AccessLevel.WriteProject;
            f.Store.SaveWork(code);
            await EngineFor(f).RunAsync(session.Id, f.Settings, f.Health);
            var logs = f.Store.Logs(f.Work.Id);
            var fixes = f.Store.WorkItems().Where(w => w.SessionId == session.Id && w.PlanKey == "fix1").ToArray();
            Assert(logs.Any(l => l.Event == "AutoCheckFailed") && fixes.Length == 1 && fixes[0].Request.Contains("broken.py") && fixes[0].State is WorkState.Completed or WorkState.NeedsReview, "A failing check adds a fix step that runs: " + string.Join(", ", logs.Select(l => l.Event)));
            Assert(logs.Any(l => l.Event == "AutoCheckStalled") && f.Store.Sessions().Single().CheckFixes == 1, "The same failure after the fix stops instead of looping");
        });
        await Check("A large goal goes on phase by phase: when steps finish, a goal check adds the next ones until the goal is done", async () =>
        {
            using var f = New();
            var session = SessionFor(f);
            session.RootWorkId = f.Work.Id;
            session.AutoContinueSteps = true;
            session.Phases = [new("Core", "core feature"), new("Polish", "polish and content")];
            f.Store.SaveSession(session);
            var first = ExtraWork(f, session, "Phase 1 work");
            var ran = new List<string>();
            f.First.Response = r => { ran.Add(r.Work.Request.Split('\n')[0]); return new(FailureKind.None, "done"); };
            var engine = EngineFor(f);
            var checks = 0;
            engine.GoalCheck = (s, _) =>
            {
                checks++;
                var current = f.Store.Sessions().Single(x => x.Id == s.Id);
                if (checks == 1)
                {
                    current.PhaseIndex = 1;
                    f.Store.SaveSession(current);
                    ExtraWork(f, current, "Phase 2 work");
                    return Task.FromResult(true);
                }
                current.GoalVerified = true;
                f.Store.SaveSession(current);
                return Task.FromResult(false);
            };
            await engine.RunAsync(session.Id, f.Settings, f.Health);
            var after = f.Store.Sessions().Single();
            Assert(checks == 2 && ran.IndexOf("Phase 1 work") >= 0 && ran.IndexOf("Phase 2 work") > ran.IndexOf("Phase 1 work") && after.State == SessionState.Completed && after.PhaseIndex == 1, $"checks={checks} calls={f.First.Calls} ran={string.Join(" | ", ran)} state={after.State} phase={after.PhaseIndex}");
            Assert(f.Store.WorkItems().Where(w => w.SessionId == session.Id).All(w => w.State is WorkState.NeedsReview or WorkState.Completed), "Both phases' steps ran");
            var answer = AgentProtocol.Parse<WorkPlan>("{\"summary\":\"all done\",\"tasks\":[],\"phases\":[{\"name\":\"Core\",\"goal\":\"core\"},{\"name\":\"Polish\",\"goal\":\"polish\"}],\"done\":true,\"phase\":2}");
            Assert(answer is { Done: true, Phase: 2, Phases.Length: 2 } && answer.Phases![1].Name == "Polish", "The coordinator's phases and goal-check answer are read");
        });
        await Check("Time renewal resumes unfinished steps while preserving completed work and AI budgets", async () =>
        {
            using var f = New();
            var goal = f.Work;
            var started = DateTimeOffset.UtcNow.AddHours(-9);
            var session = new WorkSession
            {
                RootWorkId = goal.Id,
                Goal = goal.Request,
                ProjectIds = [goal.ProjectId],
                AccessCeiling = goal.Access,
                StartedAt = started,
                Deadline = DateTimeOffset.UtcNow.AddMinutes(-1),
                DurationMinutes = 480,
                CallsUsed = 1,
                MaxCalls = 4,
                SwitchesUsed = 1,
                BaseSwitchesUsed = 1,
                AutoContinueSteps = true,
                Away = true
            };
            f.Store.SaveSession(session);
            goal.IsGoal = true;
            goal.SessionId = session.Id;
            f.Store.SaveWork(goal);
            var done = ExtraWork(f, session, "Completed step");
            done.State = WorkState.Completed;
            done.Attempts = 1;
            done.ParentTaskId = goal.Id;
            f.Store.SaveWork(done);
            var pending = ExtraWork(f, session, "Continue this", [done.Id]);
            pending.State = WorkState.Paused;
            pending.ParentTaskId = goal.Id;
            f.Store.SaveWork(pending);
            var flow = UnifiedFor(f);
            await flow.StartAsync(goal, f.Settings, f.Health);
            Assert(f.First.Calls == 0 && f.Store.Sessions().Single().State == SessionState.BudgetReached);
            flow.RenewTime(goal, 60);
            var renewed = f.Store.Sessions().Single();
            Assert(renewed.StartedAt == started && renewed.CallsUsed == 1 && renewed.MaxCalls == 4 && renewed.SwitchesUsed == 1 && renewed.Deadline > DateTimeOffset.UtcNow.AddMinutes(59));
            await flow.StartAsync(f.Store.WorkItems().Single(w => w.Id == goal.Id), f.Settings, f.Health);
            Assert(f.First.Calls == 1 && f.Store.WorkItems().Single(w => w.Id == done.Id).Attempts == 1 && f.Store.WorkItems().Single(w => w.Id == goal.Id).State == WorkState.NeedsReview);
        });
        await Check("Deadline cancellation checkpoints running work and renewal never replenishes AI usage", async () =>
        {
            using var f = New();
            var session = SessionFor(f);
            session.Deadline = DateTimeOffset.UtcNow.AddMilliseconds(1500);
            f.Store.SaveSession(session);
            f.First.Block = true;
            await EngineFor(f).RunAsync(session.Id, f.Settings, f.Health);
            Assert(f.First.Exited, "The running worker must be stopped at the deadline");
            Assert(f.Store.LatestHandoff(f.Work.Id) != null, "A checkpoint must be saved");
            Assert(f.Store.WorkItems().Single().State == WorkState.Paused, "The step must be paused, was " + f.Store.WorkItems().Single().State);
            Assert(f.Store.Sessions().Single().State == SessionState.BudgetReached, "The session must stop at its deadline, was " + f.Store.Sessions().Single().State);
            var flow = UnifiedFor(f);
            flow.WrapLegacySessions();
            var goal = f.Store.WorkItems().Single(w => w.IsGoal);
            flow.RenewTime(goal, 0);
            var renewed = f.Store.Sessions().Single();
            Assert(renewed.Deadline == null && renewed.DurationMinutes == 0, "Renewal grants unlimited time");
            Assert(renewed.CallsUsed == 1, "Renewal keeps AI usage, CallsUsed=" + renewed.CallsUsed);
        });
        await Check("Coordinator deadline saves progress and never starts a worker after expiry", async () =>
        {
            using var f = New();
            var goal = f.Work;
            var session = new WorkSession
            {
                RootWorkId = goal.Id,
                Goal = goal.Request,
                ProjectIds = [goal.ProjectId],
                AccessCeiling = goal.Access,
                StartedAt = DateTimeOffset.UtcNow,
                Deadline = DateTimeOffset.UtcNow.AddMilliseconds(1500),
                DurationMinutes = 480
            };
            f.Store.SaveSession(session);
            goal.SessionId = session.Id;
            goal.IsGoal = true;
            f.Store.SaveWork(goal);
            f.Settings.Automation.Planning = PlanningPolicy.Always;
            f.First.Block = true;
            await UnifiedFor(f).StartAsync(goal, f.Settings, f.Health);
            Assert(f.First.Calls == 1 && f.First.Exited && f.Store.Sessions().Single().State == SessionState.BudgetReached && f.Store.WorkItems().Count(w => !w.IsGoal && !w.IsCoordinator) == 0 && f.Store.WorkItems().Where(w => w.IsCoordinator).All(w => f.Store.LatestHandoff(w.Id) != null));
        });
    }
}
