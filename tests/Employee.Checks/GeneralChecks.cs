using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Employee.Core;
using Employee.Infrastructure;
using static Harness;

// Testing, tools, updates and other behaviour.
static class GeneralChecks
{
    public static async Task Run()
    {
        await Check("A tool step can use an image its preparation step depends on, without declaring it again", () =>
        {
            var edit = new WorkItem { Id = "edit", PlanKey = "t3", SessionId = "s", ProjectId = "p", State = WorkState.Completed };
            var prepare = new WorkItem { Id = "prep", PlanKey = "t4", SessionId = "s", ProjectId = "p", State = WorkState.Completed, DependsOn = ["edit"] };
            var animate = new WorkItem { Id = "anim", PlanKey = "t5", SessionId = "s", ProjectId = "p", DependsOn = ["prep"] };
            var unrelated = new WorkItem { Id = "other", PlanKey = "t9", SessionId = "s", ProjectId = "p", State = WorkState.Completed };
            var oldRun = new WorkItem { Id = "old", PlanKey = "t3", SessionId = "s", ProjectId = "p", State = WorkState.Completed };
            IReadOnlyList<WorkItem> items = [edit, prepare, animate, unrelated, oldRun];
            Assert(ToolTaskExecutor.FindDependency(animate, "t3", items)?.Id == "edit", "The image from the preparation step's dependency is found");
            Assert(ToolTaskExecutor.FindDependency(animate, "t4", items)?.Id == "prep", "Direct dependencies still work");
            Assert(ToolTaskExecutor.FindDependency(animate, "t9", items) == null, "Steps outside the chain are not reachable");
            return Task.CompletedTask;
        });
        await Check("Any Pixel Art request is completed to what the tool accepts: defaults, near valid values, inferred action", () =>
        {
            var png = new byte[33];
            new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(png, 0);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16), 144);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20), 144);
            var img = "data:image/png;base64," + Convert.ToBase64String(png);
            var options = PixelToolOptions.Read(new ConnectedTool());
            System.Text.Json.Nodes.JsonObject Done(string json)
            {
                var input = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
                PixelRequest.Complete(input, options, "Cat lying on its back, paw twitch");
                return input;
            }

            var animation = Done($$$"""{"action":"animate","image":"{{{img}}}","frames":6,"fps":60,"direction":"south-west","prompt":""}""");
            Assert(animation["action"]!.GetValue<string>() == "animation" && animation["frames"]!.GetValue<int>() == 8 && animation["fps"]!.GetValue<int>() == 30 && animation["direction"]!.GetValue<string>() == "SW", "Out-of-range values move to the nearest valid one");
            Assert(animation["prompt"]!.GetValue<string>().Contains("paw twitch") && animation["preserveStyle"]!.GetValue<bool>() && animation["lockPalette"]!.GetValue<bool>() && animation["exactFrames"]!.GetValue<bool>(), "Missing prompt and switches get the step's brief and the tool's defaults");
            Assert(animation["outputWidth"]!.GetValue<int>() == 144, "Frames keep the source size when none was asked for");
            var create = Done("""{"action":"generate","outputWidth":4000,"outputHeight":"300","preserveStyle":"yes","prompt":"a cat"}""");
            Assert(create["action"]!.GetValue<string>() == "create" && create["outputWidth"]!.GetValue<int>() == 512 && create["outputHeight"]!.GetValue<int>() == 300 && !create["preserveStyle"]!.GetValue<bool>(), "Create sizes are clamped; no style without a reference");
            var restyle = Done($$$"""{"action":"redraw","image":"{{{img}}}","sample":"{{{img}}}","editContext":{"index":5,"total":2}}""");
            Assert(restyle["action"]!.GetValue<string>() == "edit" && restyle["styleGuide"] != null && !restyle["lockPalette"]!.GetValue<bool>() && restyle["editContext"]!["index"]!.GetValue<int>() == 1, "A restyle takes the sample's colours and gets a valid edit context");
            var guessed = Done($$$"""{"image":"{{{img}}}","frames":"8","sheetContext":{"columns":99},"editContext":{"index":0},"styleProfile":{"x":1}}""");
            Assert(guessed["action"]!.GetValue<string>() == "animation" && guessed["sheetContext"] == null && guessed["editContext"] == null && guessed["styleProfile"] == null, "Action is inferred and fields illegal for it are dropped");
            try
            {
                Done("""{"action":"animation"}""");
                Assert(false, "An animation without any image is refused clearly");
            }
            catch (InvalidOperationException ex)
            {
                Assert(ex.Message.Contains("needs a source PNG"), ex.Message);
            }

            // When the request names no image: the image an earlier Pixel step made, else the only attached PNG.
            var made = new WorkItem { Id = "made", PlanKey = "t3", State = WorkState.Completed, ToolRunId = "run", Tool = new ToolTaskSpec("pixel", "employee-pixel-art", "generate") };
            var prep = new WorkItem { Id = "prep", PlanKey = "t4", State = WorkState.Completed, DependsOn = ["made"] };
            var step = new WorkItem { Id = "anim", DependsOn = ["prep"] };
            var request = new System.Text.Json.Nodes.JsonObject { ["action"] = "animation" };
            ToolTaskExecutor.ChooseImages(step, request, [made, prep, step], []);
            Assert(request["image"]?["$imageFrom"]?.GetValue<string>() == "t3", "The image from the chain is used");
            var alone = new System.Text.Json.Nodes.JsonObject { ["action"] = "edit", ["styleGuide"] = "cat_new.png" };
            var attachment = new WorkAttachment("cat_new.png", png);
            ToolTaskExecutor.ChooseImages(new WorkItem(), alone, [], [attachment]);
            Assert(alone["image"]?["$attachmentPng"]?.GetValue<string>() == "cat_new.png" && alone["styleGuide"]?["$attachmentPng"] != null, "A file name becomes the attachment, and the only PNG is the source");
            return Task.CompletedTask;
        });
        await Check("Every Pixel Art option the tool publishes is offered and completed: offline edits, sheets, animation edits, export, AI source", () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "EmployeePixelCaps", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "tool-capabilities.json"), """
                {"version":1,"artModes":{"pixel":512,"illustration":1024},"directions":["N","NE","E","SE","S","SW","W","NW"],"frames":[4,8,12],"fps":{"min":1,"max":30},"canvasMin":16,
                 "defaults":{"artMode":"pixel","direction":"S","frames":8,"fps":8,"outputWidth":128,"outputHeight":128},
                 "actions":["animation","create","edit","direction","skin","recolor","hue","procedural"],"procedural":["idle","bounce","shake"],"exportScales":[1,2,4],"autoArtMode":true,
                 "providers":{"codex":{"label":"Codex CLI"},"api":{"label":"API","vendors":{"openai":{"imageModels":["gpt-image-1.5"]},"gemini":{"imageModels":["gemini-nano-banana-2.1"]}}}}}
                """);
            var tool = new ConnectedTool { SourcePath = folder };
            var choices = PixelToolChoices.For(tool);
            Assert(choices.Actions.Contains("procedural") && choices.Motions.SequenceEqual(["idle", "bounce", "shake"]) && choices.ExportScales.Contains(4) && choices.AutoArtMode && choices.ApiImageModels["gemini"].Single() == "gemini-nano-banana-2.1", "The form reads every option list from the tool");
            var header = new byte[33];
            new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(header, 0);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(16), 256);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(20), 128);
            var img = "data:image/png;base64," + Convert.ToBase64String(header);
            var options = PixelToolOptions.Read(tool);
            System.Text.Json.Nodes.JsonObject Done(string json)
            {
                var input = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
                PixelRequest.Complete(input, options, "make it");
                return input;
            }
            var motion = Done($$$"""{"action":"procedural","image":"{{{img}}}","motion":"wobble","frames":6,"exportScale":3}""");
            Assert(motion["motion"]!.GetValue<string>() == "idle" && motion["frames"]!.GetValue<int>() == 8 && motion["exportScale"]!.GetValue<int>() == 4 && motion["prompt"] == null, "Offline motion needs no description and gets valid values");
            var hue = Done($$$"""{"action":"hue_shift","image":"{{{img}}}","degrees":500}""");
            Assert(hue["action"]!.GetValue<string>() == "hue" && hue["degrees"]!.GetValue<int>() == 180, "Hue shift is clamped");
            try { Done($$$"""{"action":"recolor","image":"{{{img}}}","fromColor":"red"}"""); Assert(false, "A recolour without colours is refused"); } catch (InvalidOperationException) { }
            var sheet = Done($$$"""{"action":"reskin","image":"{{{img}}}","sheet":{"columns":99,"rows":2},"prompt":"ice"}""");
            Assert(sheet["sheet"]!["columns"]!.GetValue<int>() == 32 && sheet["keepSilhouette"]!.GetValue<bool>(), "A whole-sheet reskin keeps the outline by default and a valid grid");
            var edit = Done($$$"""{"action":"edit","image":"{{{img}}}","animation":{"columns":2,"rows":1},"scope":"current","frameIndex":7,"prompt":"hat"}""");
            Assert(edit["editContext"] == null && edit["frameIndex"]!.GetValue<int>() == 1 && edit["scope"]!.GetValue<string>() == "current", "An animation edit leaves per-frame context to the tool and keeps a valid frame");
            var api = Done($$$"""{"action":"animation","image":"{{{img}}}","provider":"api","apiVendor":"gemini","imageModel":"unknown","artMode":"auto"}""");
            Assert(api["imageModel"]!.GetValue<string>() == "gemini-nano-banana-2.1" && api["artMode"]!.GetValue<string>() == "auto" && api["anchorFirstFrame"]!.GetValue<bool>(), "API source picks a model the tool offers; auto art mode is left to the tool");
            var legacy = PixelToolChoices.For(new ConnectedTool());
            Assert(legacy.Actions.SequenceEqual(["animation", "create", "edit", "direction", "skin"]) && legacy.Motions.Count == 0, "Older tools without the lists still work");
            Directory.Delete(folder, true);
            return Task.CompletedTask;
        });
        await Check("Task problems come with what to do next and which button to press", () =>
        {
            using var f = New();
            var goal = new WorkItem { ProjectId = f.Project.Id, Request = "Make the shop", State = WorkState.NeedsReview };
            f.Store.SaveWork(goal);
            var step = new WorkItem { ProjectId = f.Project.Id, ParentTaskId = goal.Id, Request = "Generate the office tiles", State = WorkState.NeedsReview, Notes = "The AI cannot finish this step itself: missing file tiles.png" };
            f.Store.SaveWork(step);
            f.Store.Log(step.Id, StepStatus.GaveUp, "missing file tiles.png, the user must provide it");
            var blocked = NextStepGuide.For(goal, [step], [], f.Store)!;
            Assert(blocked.Contains("only you can give") && blocked.Contains("Send & continue updating") && blocked.Contains("Skip step"), blocked);
            var done = new WorkItem { ProjectId = f.Project.Id, ParentTaskId = goal.Id, Request = "Done step", State = WorkState.Completed };
            Assert(NextStepGuide.For(goal, [done], [], f.Store)!.Contains("press Complete"), "Finished tasks say how to close them");
            Assert(NextStepGuide.For(goal, [done], [new PendingDecision { Kind = "Plan" }], f.Store)!.Contains("✔ Approve"), "A waiting plan says Approve");
            goal.State = WorkState.WrongAccess;
            Assert(NextStepGuide.For(goal, [done], [], f.Store)!.Contains("Save project"), "Access problems say how to fix them");
            return Task.CompletedTask;
        });
        await Check("Plan draft asks for the full template and hands back a ready-to-send task request", () =>
        {
            var prompt = PlanDrafter.Prompt("làm màn shop", new ProjectInfo("p", "Cat Black Hole", "C:/x", Technology: "Unity"), "development", ["Assets/Arts/Cat/cat_idle.png"]);
            Assert(PlanDrafter.Sections.All(section => prompt.Contains(section)) && prompt.Contains("SAME language") && prompt.Contains("Unity") && prompt.Contains("cat_idle.png") && prompt.Contains(PlanDrafter.RequestStart), "All sections, language rule and project context are asked for");
            Assert(PlanDrafter.TaskRequest("# 1. Goal\n...\n" + PlanDrafter.RequestStart + "\nBuild the shop.\n" + PlanDrafter.RequestEnd) == "Build the shop." && PlanDrafter.TaskRequest("no markers") == null, "The ready-to-send part is cut out");
            return Task.CompletedTask;
        });
        await Check("Refine with AI asks for a clearer request in the user's language, with the task as context", () =>
        {
            var task = new WorkItem { Request = "Make the cat idle animation", ProjectId = "p" };
            task.Comments.Add(new WorkComment("the grey dots are dirty", [], DateTimeOffset.UtcNow));
            var prompt = RequestRefiner.Prompt("mèo vẫn bị cúi người, sửa lại", task, new ProjectInfo("p", "Cat Black Hole", "C:/x"));
            Assert(prompt.Contains("SAME language") && prompt.Contains("mèo vẫn bị cúi người") && prompt.Contains("Make the cat idle animation") && prompt.Contains("the grey dots are dirty") && prompt.Contains("COMMENT"), prompt);
            Assert(RequestRefiner.Prompt("a new game menu", null, null).Contains("NEW task"), "A new task is framed as one");
            return Task.CompletedTask;
        });
        await Check("A tool result becomes a readable comment with the image and Open lines for its files", () =>
        {
            var folder = Directory.CreateTempSubdirectory("tool-comment").FullName;
            var png = Path.Combine(folder, "cat.png");
            File.WriteAllBytes(png, [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 0]);
            var video = Path.Combine(folder, "clip.mp4");
            File.WriteAllBytes(video, [0, 0, 0, 24]);
            var result = new System.Text.Json.Nodes.JsonObject { ["projectAssetPath"] = png, ["status"] = "completed", ["sheetWidth"] = 128, ["sheetHeight"] = 128, ["columns"] = 1, ["rows"] = 1, ["frameWidth"] = 128, ["frameHeight"] = 128,
                ["result"] = new System.Text.Json.Nodes.JsonObject { ["output"] = new System.Text.Json.Nodes.JsonObject { ["video"] = video }, ["quality"] = new System.Text.Json.Nodes.JsonObject { ["provider"] = "claude", ["problems"] = new System.Text.Json.Nodes.JsonArray() } } };
            var made = ToolResultComment.Build(result.ToJsonString(), "Draw the cat")!.Value;
            Assert(made.Attachments.Count == 1 && made.Attachments[0].Name == "cat.png", "The image is attached");
            Assert(made.Text.Contains(ToolResultComment.FilePrefix + png) && made.Text.Contains(ToolResultComment.FilePrefix + video) && made.Text.Contains(ToolResultComment.FolderPrefix + folder) && made.Text.Contains("Made by Claude CLI") && !made.Text.Contains("{"), made.Text);
            Assert(ToolResultComment.Build("not json", "x") == null, "Plain text stays as it was");
            var option = Path.Combine(folder, "cat-option-2.png");
            File.WriteAllBytes(option, [137, 80, 78, 71, 13, 10, 26, 10, 1, 1, 1, 1]);
            result["optionPaths"] = new System.Text.Json.Nodes.JsonArray(option);
            var withOptions = ToolResultComment.Build(result.ToJsonString(), "Draw the cat")!.Value;
            Assert(withOptions.Attachments.Select(a => a.Name).SequenceEqual(["cat.png", "cat-option-2.png"]), "Other candidates show next to the main image: " + string.Join(",", withOptions.Attachments.Select(a => a.Name)));
            return Task.CompletedTask;
        });
        await Check("The tool's own quality check is summarised for the task log", () =>
        {
            var quality = System.Text.Json.Nodes.JsonNode.Parse("""{"fixed":["kept the main figure, removed extra copies"],"retried":true,"retryKept":true,"problems":[{"kind":"odd-frames","note":"frame 3 has a figure of very different size"}],"frames":[{"colourDrift":4.2,"detailKept":0.9},{"colourDrift":6.1,"detailKept":1}]}""");
            var summary = ToolTaskExecutor.QualitySummary(quality)!;
            Assert(summary.Contains("removed extra copies") && summary.Contains("new result was better") && summary.Contains("frame 3") && summary.Contains("6.1") && summary.Contains("90"), summary);
            Assert(ToolTaskExecutor.QualitySummary(null) == null, "No report, no summary");
            var switched = ToolTaskExecutor.QualitySummary(System.Text.Json.Nodes.JsonNode.Parse("""{"provider":"claude","switchedFrom":[{"from":"codex","reason":"usage limit"}],"problems":[]}"""))!;
            Assert(switched.Contains("Made by Claude CLI") && switched.Contains("Codex CLI had no usage left"), switched);
            var input = new System.Text.Json.Nodes.JsonObject { ["action"] = "create", ["prompt"] = "a cat" };
            PixelToolOptions.Read(new ConnectedTool()).Apply(input);
            Assert(input["autoRetry"]!.GetValue<bool>(), "Auto-fix is on unless turned off");
            Assert(input["autoSwitch"]!.GetValue<bool>() && input["fallbackToApi"] == null, "Switching AI on used-up usage is on by default; paid API only when asked");
            return Task.CompletedTask;
        });
        await Check("A Pixel Art edit request carries the frame context and style reference the tool requires", () =>
        {
            var png = new byte[33];
            new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(png, 0);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16), 144);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20), 144);
            var url = "data:image/png;base64," + Convert.ToBase64String(png);
            var input = new System.Text.Json.Nodes.JsonObject { ["action"] = "edit", ["image"] = url, ["referenceImage"] = url, ["width"] = 128, ["height"] = 128 };
            PixelToolOptions.Read(new ConnectedTool()).Apply(input);
            var context = input["editContext"];
            Assert(context?["index"]?.GetValue<int>() == 0 && context["total"]?.GetValue<int>() == 1 && context["width"]?.GetValue<int>() == 144 && context["height"]?.GetValue<int>() == 144 && context["fps"]?.GetValue<int>() == 8, "One frame of one at the image's size");
            Assert(input["styleGuide"]?.GetValue<string>() == url && input["referenceImage"] == null && input["preserveStyle"]?.GetValue<bool>() == true, "The style sample is sent as the tool's style reference");
            Assert(input["outputWidth"]?.GetValue<int>() == 128 && input["width"] == null, "width/height become the tool's output size");
            return Task.CompletedTask;
        });
        await Check("Pixel Art Tool requests ask the tool for finished frames, and a sheet of the wrong size is not imported", () =>
        {
            var input = new System.Text.Json.Nodes.JsonObject { ["action"] = "create", ["prompt"] = "a cat" };
            PixelToolOptions.Read(new ConnectedTool()).Apply(input);
            Assert(input["exactFrames"]?.GetValue<bool>() == true, "Image requests carry exactFrames");
            var detect = new System.Text.Json.Nodes.JsonObject { ["action"] = "detect" };
            try
            {
                PixelToolOptions.Read(new ConnectedTool()).Apply(detect);
                Assert(false, "Analysis actions are not image requests");
            }
            catch (InvalidOperationException)
            {
            }
            var output = System.Text.Json.Nodes.JsonNode.Parse("""{"result":{"columns":4,"rows":2},"requestSettings":{"outputWidth":128,"outputHeight":128},"imageWidth":1774,"imageHeight":887}""")!;
            var layout = PixelSheetLayout.Requested(output)!;
            Assert(PixelSheetLayout.Problem(output, layout) is { } problem && problem.Contains("1774x887") && problem.Contains("512x256"), "A raw sheet is reported, not imported");
            output["imageWidth"] = 512;
            output["imageHeight"] = 256;
            Assert(PixelSheetLayout.Problem(output, layout) == null, "A finished 4x2 sheet of 128x128 frames is accepted");
            return Task.CompletedTask;
        });
        await Check("Backends the manager started end when the manager is killed", async () =>
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            bool Listening()
            {
                try
                {
                    using var client = new System.Net.Sockets.TcpClient();
                    client.Connect(IPAddress.Loopback, port);
                    return true;
                }
                catch (System.Net.Sockets.SocketException)
                {
                    return false;
                }
            }

            using var host = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { ArgumentList = { "--launcher-host", port.ToString() }, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
            using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var line = await host.StandardOutput.ReadLineAsync(ready.Token);
            Assert(line == "READY" && Listening(), "The stand-in manager started a backend");
            // Killed outright, without its own cleanup and without killing its process tree.
            host.Kill(entireProcessTree: false);
            host.WaitForExit(10000);
            var ended = false;
            for (var i = 0; i < 50 && !ended; i++)
            {
                ended = !Listening();
                if (!ended)
                    await Task.Delay(100);
            }

            Assert(ended, "Windows ended the backend when the manager died");
        });
        await Check("A backend the manager started stops once idle; one the user started is left running", async () =>
        {
            static int FreePort()
            {
                var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                var port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                return port;
            }

            static bool Listening(int port)
            {
                try
                {
                    using var client = new System.Net.Sockets.TcpClient();
                    client.Connect(IPAddress.Loopback, port);
                    return true;
                }
                catch (System.Net.Sockets.SocketException)
                {
                    return false;
                }
            }

            async Task<bool> WaitFor(Func<bool> condition)
            {
                for (var i = 0; i < 50 && !condition(); i++)
                    await Task.Delay(100);
                return condition();
            }

            ConnectedTool Tool(int port) => new()
            {
                Id = "fake",
                Name = "Fake backend",
                Connector = "pixel",
                SourcePath = root,
                Endpoint = $"http://127.0.0.1:{port}",
                AutoStartBackend = true,
                BackendExecutable = Environment.ProcessPath!,
                BackendArguments = ["--listen", port.ToString()],
                BackendStartupTimeoutSeconds = 30
            };
            using var launcher = new ToolBackendLauncher { IdleShutdown = TimeSpan.FromMilliseconds(400) };
            var stopped = 0;
            launcher.Stopped += _ => Interlocked.Increment(ref stopped);
            // Started by the manager: stops after the idle grace.
            var port = FreePort();
            var lease = await launcher.EnsureAsync(Tool(port), _ => Task.CompletedTask, _ => { }, CancellationToken.None);
            Assert(Listening(port), "The backend was started");
            lease.Dispose();
            Assert(await WaitFor(() => !Listening(port)) && stopped == 1, "The idle backend the manager started was stopped");
            // Reused within the grace: kept running, then stopped after the last use.
            var reused = FreePort();
            var first = await launcher.EnsureAsync(Tool(reused), _ => Task.CompletedTask, _ => { }, CancellationToken.None);
            first.Dispose();
            var second = await launcher.EnsureAsync(Tool(reused), _ => Task.CompletedTask, _ => { }, CancellationToken.None);
            await Task.Delay(900);
            Assert(Listening(reused), "A backend reused within the grace keeps running");
            second.Dispose();
            Assert(await WaitFor(() => !Listening(reused)), "It stops after its last use");
            // Started by the user: never stopped.
            var external = FreePort();
            var userBackend = new System.Net.Sockets.TcpListener(IPAddress.Loopback, external);
            userBackend.Start();
            try
            {
                var borrowed = await launcher.EnsureAsync(Tool(external), _ => Task.CompletedTask, _ => { }, CancellationToken.None);
                borrowed.Dispose();
                await Task.Delay(900);
                Assert(Listening(external), "A backend the user started is left running");
            }
            finally
            {
                userBackend.Stop();
            }
        });
        await Check("Ambiguous project aliases never select a project", () =>
        {
            using var f = New();
            var dir = Path.Combine(root, "second");
            Directory.CreateDirectory(dir);
            new ProjectIndexer(f.Store).Register(dir, "Garden Two", "Garden");
            Assert(new ProjectIndexer(f.Store).Resolve("Garden") == null);
            return Task.CompletedTask;
        });
        await Check("Real process argv preserves spaces and shell metacharacters", async () =>
        {
            var text = "spaces & | > $() ` \" ti?ng Vi?t";
            var r = await new ProcessRunner().RunAsync(Environment.ProcessPath!, ["--echo", text], root, null, 10000, null, CancellationToken.None);
            Assert(r.ExitCode == 0 && r.Output == text, r.Output);
        });
        await Check("Real process cancellation terminates and output is bounded", async () =>
        {
            using var cts = new CancellationTokenSource(400);
            var watch = Stopwatch.StartNew();
            bool cancelled = false;
            try
            {
                await new ProcessRunner().RunAsync(Environment.ProcessPath!, ["--child"], root, null, 100, null, cts.Token);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }

            Assert(cancelled && watch.Elapsed < TimeSpan.FromSeconds(10));
            var r = await new ProcessRunner().RunAsync(Environment.ProcessPath!, ["--echo", new string ('x', 10000)], root, null, 100, null, CancellationToken.None);
            Assert(r.Output.Length == 100);
        });
        await Check("Explicit path and configured search paths take precedence over Desktop discovery", () =>
        {
            var folder = Path.Combine(root, "search-priority");
            Directory.CreateDirectory(folder);
            var executable = Path.Combine(folder, "codex.exe");
            File.WriteAllText(executable, "");
            Assert(ProcessRunner.ResolveCli("codex", "codex", new([folder], root))?.Executable == executable);
            Assert(ProcessRunner.ResolveCli(executable, "codex", new([], root))?.Executable == executable);
            return Task.CompletedTask;
        });
        await Check("Implementation with read access is WrongAccess without spending AI usage", async () =>
        {
            using var f = New();
            f.Work.Request = "Implement weekly quests";
            f.Store.SaveWork(f.Work);
            await f.Manager.RunAsync(f.Work, f.Settings, f.Health);
            Assert(f.Work.State == WorkState.WrongAccess && f.First.Calls == 0 && f.Second.Calls == 0 && f.Work.Attempts == 0, "Editing must not run through a read-only fixture.");
            Assert(f.Work.ProblemReason.Contains("WriteProject") && f.Store.Logs(f.Work.Id).All(l => l.Event != "Execution"));
        });
        await Check("Wrong project folder blocks inference and recovers after explicit correction", async () =>
        {
            using var f = New();
            f.Store.SaveProject(f.Project with { Root = Path.Combine(f.Project.Root, "missing-folder") });
            await f.Manager.RunAsync(f.Work, f.Settings, f.Health);
            Assert(f.Work.State == WorkState.WrongProject && f.First.Calls == 0 && f.Work.ProblemReason.Contains("missing-folder"));
            f.Store.SaveProject(f.Project);
            await f.Manager.RunAsync(f.Work, f.Settings, f.Health);
            Assert(f.First.Calls == 1 && f.Work.State == WorkState.NeedsReview);
        });
        await Check("Smart tests cannot run during a project execution and cancel cleanly", async () =>
        {
            using var f = New();
            var profile = new ProjectTestProfile
            {
                ProjectId = f.Project.Id,
                Name = "Cancellation check",
                Executable = Environment.ProcessPath!,
                Arguments = ["--child"],
                Enabled = true
            };
            f.Store.SaveTestProfile(profile);
            var service = new SmartTestingService(f.Store, f.Store, f.Manager, new ProcessRunner());
            var job = service.Enqueue(f.Work, ["Quest.cs"], "snapshot").Single();
            f.First.Block = true;
            var running = f.Manager.RunAsync(f.Work, f.Settings, f.Health);
            await f.First.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            bool locked = false;
            try
            {
                await service.RunAsync(job, CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                locked = true;
            }

            Assert(locked);
            f.Manager.Stop(f.Work.Id);
            await running;
            using var cts = new CancellationTokenSource(250);
            await service.RunAsync(job, cts.Token);
            Assert(job.State == TestJobState.Interrupted);
        });
        await Check("Smart test command profiles reject shells and release packaging", () =>
        {
            using var f = New();
            var profile = new ProjectTestProfile
            {
                ProjectId = f.Project.Id,
                Name = "profile",
                Executable = Environment.ProcessPath!,
                Arguments = ["--configuration=Release"]
            };
            bool rejected = false;
            try
            {
                SmartTestingService.ValidateProfile(profile);
            }
            catch (InvalidOperationException)
            {
                rejected = true;
            }

            Assert(rejected);
            Assert(new SmartTestingService(f.Store, f.Store, f.Manager, new NullRunner()).Enqueue(f.Work, ["code.cs"], "x").Count == 0);
            return Task.CompletedTask;
        });
        await Check("Smart testing cannot mark Verified while another configured check failed", async () =>
        {
            using var f = New();
            foreach (var flag in new[]
            {
                "pass",
                "fail"
            }

            )
                f.Store.SaveTestProfile(new() { ProjectId = f.Project.Id, Name = flag, Executable = Environment.ProcessPath!, Arguments = [flag], Enabled = true, FullValidation = true });
            var service = new SmartTestingService(f.Store, f.Store, f.Manager, new ValidationRunner());
            File.WriteAllText(Path.Combine(f.Project.Root, "Quest.cs"), "code");
            var jobs = service.Enqueue(f.Work, ["Quest.cs"], "snapshot");
            foreach (var job in jobs)
                await service.RunAsync(job, CancellationToken.None);
            Assert(f.Store.WorkItems().Single().Verification == VerificationState.PartiallyTested);
            service.Enqueue(f.Work, ["Quest.cs"], "snapshot");
            Assert(f.Store.WorkItems().Single().Verification != VerificationState.Verified);
        });
        await Check("Source edits during a test invalidate its verification snapshot", async () =>
        {
            using var f = New();
            var source = Path.Combine(f.Project.Root, "Quest.cs");
            File.WriteAllText(source, "before");
            f.Store.SaveTestProfile(new() { ProjectId = f.Project.Id, Name = "Source race", Executable = Environment.ProcessPath!, Arguments = ["pass"], Enabled = true, FullValidation = true });
            var service = new SmartTestingService(f.Store, f.Store, f.Manager, new ValidationRunner(() => File.WriteAllText(source, "after")));
            var job = service.Enqueue(f.Work, ["Quest.cs"], "snapshot").Single();
            await service.RunAsync(job, CancellationToken.None);
            Assert(job.State == TestJobState.Failed && f.Store.WorkItems().Single().Verification != VerificationState.Verified);
        });
    }
}
