using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Employee.Core;

namespace Employee.Infrastructure;
public sealed class ToolTaskExecutor(IEmployeeStore store, IToolWorkflowStore registry, ToolExecutionService tools, ToolSkillRegistry skills, string dataRoot, Func<WorkItem, string[], IDisposable> reserve)
{
    private WorkItem Dependency(WorkItem work, string key) => FindDependency(work, key, store.WorkItems()) ?? throw new InvalidOperationException("Tool input needs a completed, declared dependency in the same project: " + key);

    // A step the tool step declares, or one declared by a step it declares (typically the input
    // preparation step, which names the image it prepared from). Planners often list only the
    // preparation step on the tool step; the image it refers to is still part of the same chain.
    // Also a completed tool step of the same task, by id (an image made in an earlier round of a send-back).
    internal static WorkItem? FindDependency(WorkItem work, string key, IReadOnlyList<WorkItem> items) =>
        Chain(work, items).SingleOrDefault(w => w.SessionId == work.SessionId && w.ProjectId == work.ProjectId && (w.Id == key || w.PlanKey == key) && w.State == WorkState.Completed)
        ?? items.SingleOrDefault(w => work.ParentTaskId != null && w.ParentTaskId == work.ParentTaskId && w.SessionId == work.SessionId && w.ProjectId == work.ProjectId && w.Id == key && w.ToolRunId != null && w.State == WorkState.Completed);

    // Which of a "create, then animate" pair a step's description asks for: 0 the still image,
    // 1 the animation, null when it does not say.
    internal static int? PresetRole(string request)
    {
        var text = request.ToLowerInvariant();
        string[] animation = ["animation", "animate", "spritesheet", "sprite sheet", "frames", "khung", "anim"];
        string[] still = ["still image", "single image", "one image", "starting pose", "ảnh tĩnh", "một ảnh", "tạo ảnh", "create image", "base image", "ảnh gốc"];
        var isAnimation = animation.Any(text.Contains);
        var isStill = still.Any(text.Contains);
        return isAnimation == isStill ? null : isAnimation ? 1 : 0;
    }

    // The steps this step depends on, directly or through them (a few levels), nearest first.
    internal static IReadOnlyList<WorkItem> Chain(WorkItem work, IReadOnlyList<WorkItem> items)
    {
        var byId = items.ToDictionary(w => w.Id);
        var seen = new HashSet<string>();
        var result = new List<WorkItem>();
        var level = work.DependsOn.ToList();
        for (var depth = 0; depth < 4 && level.Count > 0; depth++)
        {
            var next = new List<string>();
            foreach (var id in level)
                if (seen.Add(id) && byId.TryGetValue(id, out var step))
                {
                    result.Add(step);
                    next.AddRange(step.DependsOn);
                }
            level = next;
        }
        return result;
    }

    // Final Pixel request: values read from the request text (only when the user did not pick the
    // settings in the Tool task form), then completion to what the tool accepts, always.
    internal static IReadOnlyList<string> FinishPixelRequest(JsonObject input, PixelToolOptions options, string stepRequest, bool presetApplied, string? goalText)
    {
        if (!presetApplied)
        {
            PixelToolOptions.ApplyTextHints(input, stepRequest, overrideExisting: false);
            if (goalText != null)
                PixelToolOptions.ApplyTextHints(input, goalText, overrideExisting: true);
        }
        return PixelRequest.Complete(input, options, stepRequest);
    }

    // The settings the user chose in the Tool task form override what the planner wrote, on the tool
    // step for that tool: the only one, or the one doing the action the user chose.
    // With two presets (create, then animate) the step doing the matching action gets each; when the
    // planner left the action out, the first tool step gets the first preset. Returns what was applied.
    internal static JsonObject? ApplyPreset(WorkItem work, ToolTaskSpec spec, WorkItem? goal, JsonObject input, IReadOnlyList<WorkItem> items)
    {
        var presets = new[] { goal?.ToolPresetFirst, goal?.ToolPreset }.Where(p => p?.Input != null && p.ToolId == spec.ToolId && p.Operation == spec.Operation).Select(p => p!.Input!).ToArray();
        if (presets.Length == 0)
            return null;
        var calls = items.Where(w => w.SessionId == work.SessionId && w.ParentTaskId == work.ParentTaskId && w.Tool is { } t && t.ToolId == spec.ToolId && t.Operation == spec.Operation)
            .OrderBy(w => int.TryParse(w.PlanKey?.TrimStart('t', 'T'), out var n) ? n : int.MaxValue).ThenBy(w => w.PlanKey, StringComparer.Ordinal).ThenBy(w => w.CreatedAt).ToList(); // t9 before t10
        string? Action(JsonNode? node) => (node as JsonValue)?.ToString().Trim().ToLowerInvariant();
        var planned = Action(input["action"]);
        var chosen = presets.FirstOrDefault(p => planned != null && Action(p["action"]) == planned);
        if (chosen == null)
        {
            // One set of settings: every call of this tool that does not name another action gets it,
            // including the calls a send-back adds next to the finished ones.
            if (presets.Length == 1 && (calls.Count <= 1 || planned == null))
                chosen = presets[0];
            else if (presets.Length == 2 && planned == null)
                // What the step says it makes; else its place among the calls not yet done (calls of
                // earlier rounds are finished), the first being the still image.
                chosen = presets[PresetRole(work.Request) ?? Math.Clamp(calls.Where(w => w.State != WorkState.Completed || w.Id == work.Id).ToList().FindIndex(w => w.Id == work.Id), 0, 1)];
        }
        if (chosen == null)
            return null;
        foreach (var (key, value) in chosen)
            input[key] = value?.DeepClone();
        return chosen;
    }

    // Picks the source image when the request has none: the image a Pixel step earlier in this
    // step's chain produced (most recent), else the only PNG the user attached. Also accepts a
    // plain project path where a reference object was expected.
    internal static IReadOnlyList<string> ChooseImages(WorkItem work, JsonObject input, IReadOnlyList<WorkItem> items, IReadOnlyList<WorkAttachment> attachments, Func<WorkItem, bool>? isStill = null)
    {
        var notes = new List<string>();
        foreach (var key in new[] { "image", "styleGuide", "referenceImage", "styleReference", "styleImage" })
            if (input[key] is JsonValue value && value.TryGetValue<string>(out var text) && !text.StartsWith("data:", StringComparison.Ordinal))
            {
                var name = text.Trim().Replace('\\', '/');
                if (!name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) continue;
                var attachment = attachments.FirstOrDefault(a => string.Equals(Path.GetFileName(a.Name), Path.GetFileName(name), StringComparison.OrdinalIgnoreCase));
                input[key] = attachment != null && !name.Contains('/') ? new JsonObject { ["$attachmentPng"] = attachment.Name } : new JsonObject { ["$projectPng"] = name };
                notes.Add($"{key} path '{name}' read as a {(attachment != null && !name.Contains('/') ? "attachment" : "project PNG")}");
            }
        var action = (input["action"] as JsonValue)?.ToString().ToLowerInvariant();
        if (input["image"] != null || action is "create" or "generate" or "new" or "draw")
            return notes;
        // An image this step works from: one made earlier in its chain, else the newest still image a
        // tool step of the same task made (a send-back redoing the animation keeps the pose already
        // made instead of going back to the style sample).
        var produced = Chain(work, items).FirstOrDefault(w => w.State == WorkState.Completed && w.ToolRunId != null && w.Tool is { Operation: "generate" })
            ?? (isStill == null || work.ParentTaskId == null ? null : items.Where(w => w.ParentTaskId == work.ParentTaskId && w.SessionId == work.SessionId && w.Id != work.Id && w.State == WorkState.Completed && w.ToolRunId != null && w.Tool is { Operation: "generate" } && isStill(w)).OrderByDescending(w => w.UpdatedAt ?? w.CreatedAt).FirstOrDefault());
        if (produced != null)
        {
            var inChain = Chain(work, items).Contains(produced);
            input["image"] = new JsonObject { ["$imageFrom"] = inChain ? produced.PlanKey ?? produced.Id : produced.Id };
            notes.Add((inChain ? "image taken from the earlier Pixel step " : "image taken from the still image this task already made in step ") + (produced.PlanKey ?? produced.Id));
            return notes;
        }
        var pngs = attachments.Where(ToolSkillRegistry.IsPngReference).ToArray();
        if (pngs.Length == 1)
        {
            input["image"] = new JsonObject { ["$attachmentPng"] = pngs[0].Name };
            notes.Add("image taken from the only attached PNG: " + pngs[0].Name);
        }
        return notes;
    }
    // A PNG named as an attachment: the user's (task, then their comments), exact name first, then the
    // same file name in any case; the newest wins when it was attached twice. Then the images the
    // manager itself posted on the task (tool results): planners see them in the task history and
    // name them as attachments (seen live: the goose image a repair plan animated).
    internal static WorkAttachment FindAttachment(WorkItem goal, string wanted)
    {
        var all = goal.Attachments.Concat(goal.Comments.Where(comment => !comment.IsAssistant).SelectMany(comment => comment.Attachments)).ToArray();
        var posted = goal.Comments.Where(comment => comment.IsAssistant).SelectMany(comment => comment.Attachments).ToArray();
        bool SameFile(WorkAttachment a) => string.Equals(Path.GetFileName(a.Name), Path.GetFileName(wanted), StringComparison.OrdinalIgnoreCase);
        return all.LastOrDefault(a => a.Name == wanted) ?? all.LastOrDefault(SameFile) ?? posted.LastOrDefault(SameFile)
            ?? throw new InvalidOperationException("Attached PNG not found or name is ambiguous: " + wanted);
    }

    // At most this many calls come out of one tool step; more is a planning problem, not a split.
    internal const int MaxSplit = 16;

    // One tool step that needs several calls: the step keeps the first call, every other call
    // becomes a tool step of its own (key "t3.2", "t3.3", …) with the same tool, skill and access.
    // A call that refers to this step's result or to another new call waits for it; the others run
    // after this step's own dependencies. Steps that waited for this step also wait for the new ones.
    internal IReadOnlyList<WorkItem> Split(WorkItem work, ToolTaskSpec spec, IReadOnlyList<JsonObject> calls, string why)
    {
        if (calls.Count == 0)
            return [];
        if (calls.Count > MaxSplit - 1)
        {
            // Never silently: the dropped calls are named on the step and on the task (seen live: 9 of 15 poses lost).
            var dropped = string.Join("; ", calls.Skip(MaxSplit - 1).Select(Describe));
            store.Log(work.Id, "ToolSplit", $"{calls.Count + 1} calls were asked for; only the first {MaxSplit} are made. NOT MADE: {dropped}");
            if (work.ParentTaskId != null)
                store.Log(work.ParentTaskId, "ToolSplitDropped", $"Step {work.PlanKey ?? work.Id}: {calls.Count + 1 - MaxSplit} tool call(s) over the limit of {MaxSplit} were not made: {dropped}");
            calls = calls.Take(MaxSplit - 1).ToList();
        }
        var items = store.WorkItems();
        var baseKey = work.PlanKey ?? work.Id;
        var keys = new List<(string Key, string Id)> { (baseKey, work.Id) };
        var created = new List<WorkItem>();
        for (var i = 0; i < calls.Count; i++)
        {
            var key = baseKey + "." + (i + 2);
            while (items.Any(w => w.SessionId == work.SessionId && w.PlanKey == key) || keys.Any(k => k.Key == key))
                key += "b";
            var waits = References(calls[i]).Select(reference => keys.Where(k => reference == k.Key || reference.StartsWith(k.Key + ".", StringComparison.Ordinal)).OrderByDescending(k => k.Key.Length).Select(k => k.Id).FirstOrDefault())
                .Where(id => id != null).Cast<string>().Distinct().ToList();
            var step = new WorkItem
            {
                ProjectId = work.ProjectId, SessionId = work.SessionId, ParentTaskId = work.ParentTaskId, PlanKey = key,
                Kind = work.Kind, Effort = work.Effort, Difficulty = work.Difficulty, CapabilityHint = work.CapabilityHint, Access = work.Access, Priority = work.Priority,
                SkillId = work.SkillId, SkillHash = work.SkillHash, DelegationAllowed = false, Milestone = work.Milestone,
                Tool = spec with { Input = calls[i], InputFrom = null },
                Request = $"{work.Request}\nCall {i + 2} of {calls.Count + 1} split from step {baseKey}: {Describe(calls[i])}",
                DependsOn = waits.Count > 0 ? waits : work.DependsOn.ToList(),
                State = WorkState.Queued
            };
            store.SaveWork(step);
            keys.Add((key, step.Id));
            created.Add(step);
        }
        var ids = created.Select(w => w.Id).ToList();
        foreach (var dependent in items.Where(w => w.SessionId == work.SessionId && w.Id != work.Id && w.DependsOn.Contains(work.Id)))
        {
            dependent.DependsOn = dependent.DependsOn.Concat(ids).Distinct().ToList();
            store.SaveWork(dependent);
        }
        TaskDependencies.Validate(store.WorkItems().Where(w => w.SessionId == work.SessionId).ToList());
        store.Log(work.Id, "ToolSplit", why + ". New steps: " + string.Join(", ", created.Select(w => w.PlanKey + " (" + Describe(w.Tool!.Input!) + ")")));
        return created;
    }

    // The preparation wrote about toolInput(s) without valid JSON, or the step asks for several images.
    internal bool MeantSeveral(WorkItem preparation, WorkItem work)
    {
        var answer = store.Logs(preparation.Id).LastOrDefault(l => l.Event == "Execution")?.Message ?? "";
        if (answer.Contains("toolInput", StringComparison.Ordinal))
            return true;
        var request = TaskIntent.Normalize(work.Request);
        return System.Text.RegularExpressions.Regex.IsMatch(request, @"\b([2-9]|[1-9][0-9])\s+(pose|poses|clip|clips|animation|animations|asset|assets|icon|icons|sprite|sprites|anh|nhan vat|sinh vat|quai|frame sheets?)\b")
            || System.Text.RegularExpressions.Regex.IsMatch(request, @"\b(moi nhan vat|moi sinh vat|tung nhan vat|one per|each character|each monster|for every)\b");
    }

    internal bool AlreadySplit(WorkItem work) => store.WorkItems().Any(w => w.SessionId == work.SessionId && w.Id != work.Id && w.Tool != null && w.Request.Contains("split from step " + (work.PlanKey ?? work.Id) + ":", StringComparison.Ordinal));

    private static IEnumerable<string> References(JsonNode? node) => node switch
    {
        JsonObject obj => obj.SelectMany(p => (p.Key is "$imageFrom" or "$from") && p.Value is JsonValue v && v.TryGetValue<string>(out var text) ? [text] : References(p.Value)),
        JsonArray array => array.SelectMany(References),
        _ => []
    };

    private static string Describe(JsonObject call)
    {
        var action = call["action"] is JsonValue a && a.TryGetValue<string>(out var act) ? act + ": " : "";
        var prompt = call["prompt"] is JsonValue p && p.TryGetValue<string>(out var text) ? text : call.ToJsonString();
        return action + (prompt.Length > 160 ? prompt[..160] + "…" : prompt);
    }

    // A completed Pixel step whose result is one still frame (the "create" half of create-then-animate).
    private bool StillImage(WorkItem step)
    {
        try
        {
            var result = Output(step)["result"];
            return (result?["columns"]?.GetValue<int>() ?? 1) * (result?["rows"]?.GetValue<int>() ?? 1) == 1;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or FormatException)
        {
            return false;
        }
    }

    internal const string NothingToRun = "Nothing to run: the preparation step returned an empty toolInputs list.";

    // The preparation's own words for why the list is empty (e.g. "PROJECT FILE MISSING: no saved ZapBop project found").
    private string NothingToRunReason(WorkItem preparation)
    {
        var said = store.Logs(preparation.Id).LastOrDefault(l => l.Event == "Execution")?.Message ?? "";
        var match = System.Text.RegularExpressions.Regex.Matches(said, @"[A-Z][A-Z ]{3,40}(MISSING|NOT FOUND|NONE FOUND)[^\\""\n]{0,200}").LastOrDefault();
        return match == null ? "" : " It said: " + match.Value.Trim();
    }

    // The failure message for a preparation that answered with an empty list; null when it did not.
    internal string? EmptyPreparation(WorkItem preparation)
    {
        try
        {
            var output = Output(preparation);
            return output["toolInput"] == null && output["toolInputs"] is JsonArray { Count: 0 } ? NothingToRun + NothingToRunReason(preparation) : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private JsonObject Output(WorkItem dependency)
    {
        if (dependency.ToolRunId != null)
        {
            var run = registry.ToolRuns().Single(r => r.Id == dependency.ToolRunId);
            if (run.State != ToolRunState.Completed)
                throw new InvalidOperationException("Tool result must be reviewed before reuse.");
            var text = run.Output;
            if (run.OutputFile != null)
            {
                var expected = Path.Combine(dataRoot, "artifacts", "tool-runs", run.Id, "output.json");
                if (Path.GetFullPath(run.OutputFile) != Path.GetFullPath(expected))
                    throw new InvalidOperationException("Unexpected tool output location.");
                var bytes = File.ReadAllBytes(expected);
                if (bytes.Length > 16_000_000 || Convert.ToHexString(SHA256.HashData(bytes)) != run.OutputSha256)
                    throw new InvalidOperationException("Tool output artifact changed.");
                text = Encoding.UTF8.GetString(bytes);
            }

            return JsonNode.Parse(text) as JsonObject ?? throw new InvalidOperationException("Tool output is not an object.");
        }

        var execution = store.Logs(dependency.Id).LastOrDefault(l => l.Event == "Execution") ?? throw new InvalidOperationException("Dependency has no AI response.");
        var answer = execution.Message[(execution.Message.IndexOf('\n') + 1)..];
        var parsed = AgentProtocol.FindObject(answer, "toolInput", "toolInputs", "blocked") ?? AgentProtocol.Parse<JsonObject>(answer)
            ?? throw new InvalidOperationException("Input preparation did not return JSON. Review its response; no tool was called.");
        if (parsed["blocked"] != null)
            throw new InvalidOperationException("Input preparation blocked: " + parsed["blocked"]!.ToString());
        return parsed;
    }

    // A tool step planned as text-only still runs inside its task: the task's own approved access covers reading its project.
    private bool GoalAllowsRead(WorkItem work) =>
        work.ParentTaskId != null && store.WorkItems().FirstOrDefault(w => w.Id == work.ParentTaskId && w.SessionId == work.SessionId && w.ProjectId == work.ProjectId) is { } goal && goal.Access >= AccessLevel.ReadProject;

    internal JsonNode? Resolve(WorkItem work, JsonNode? node, int depth = 0)
    {
        if (depth > 30)
            throw new InvalidOperationException("Tool input references are too deeply nested.");
        if (node is JsonObject obj)
        {
            if (obj.Count == 1 && obj["$attachmentPng"] is JsonValue attachmentName)
            {
                var goal = store.WorkItems().Single(w => w.Id == work.ParentTaskId && w.SessionId == work.SessionId);
                var image = FindAttachment(goal, attachmentName.GetValue<string>());
                var bytes = image.Content;
                if (bytes.Length > 8 * 1024 * 1024 || bytes.Length < 8 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                    throw new InvalidOperationException("Attachment must be a PNG up to 8 MB.");
                return JsonValue.Create("data:image/png;base64," + Convert.ToBase64String(bytes));
            }

            if (obj.Count == 1 && obj["$projectPng"] is JsonValue projectImage)
            {
                if (work.Access < AccessLevel.ReadProject && !GoalAllowsRead(work))
                    throw new InvalidOperationException("Project PNG references require approved ReadProject access.");
                var project = store.Projects().Single(p => p.Id == work.ProjectId);
                var relative = projectImage.GetValue<string>();
                // Earlier tool results report full projectAssetPath values and preparation steps copy them:
                // a full path inside the project is accepted, anything outside it is still refused below.
                if (Path.IsPathFullyQualified(relative) && !Path.GetFullPath(relative).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(project.Root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Project PNG path is outside the project; use a path relative to the project root.");
                var path = Path.GetFullPath(Path.Combine(project.Root, relative));
                RequireWithin(path, project.Root);
                var info = new FileInfo(path);
                if (info.Length > 8 * 1024 * 1024)
                    throw new InvalidOperationException("Project PNG exceeds 8 MB.");
                var bytes = File.ReadAllBytes(path);
                if (bytes.Length < 8 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                    throw new InvalidOperationException("Project image is not PNG.");
                return JsonValue.Create("data:image/png;base64," + Convert.ToBase64String(bytes));
            }

            if (obj.Count == 1 && obj["$imageFrom"] is JsonValue imageReference)
            {
                var dependency = Dependency(work, imageReference.GetValue<string>());
                var output = Output(dependency);
                if (dependency.ToolRunId == null)
                    throw new InvalidOperationException("PNG references must come from a completed tool artifact.");
                var expected = Path.GetFullPath(Path.Combine(dataRoot, "artifacts", "tool-runs", dependency.ToolRunId, "image.png"));
                if (Path.GetFullPath(output["artifactPath"]?.GetValue<string>() ?? "") != expected)
                    throw new InvalidOperationException("Image is not the dependency's managed PNG artifact.");
                var info = new FileInfo(expected);
                if (info.Length > 8 * 1024 * 1024 || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("PNG is oversized or redirected.");
                var bytes = File.ReadAllBytes(expected);
                if (Convert.ToHexString(SHA256.HashData(bytes)) != output["artifactSha256"]?.GetValue<string>())
                    throw new InvalidOperationException("PNG artifact changed since generation.");
                if (bytes.Length < 8 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                    throw new InvalidOperationException("Invalid PNG artifact.");
                return JsonValue.Create("data:image/png;base64," + Convert.ToBase64String(bytes));
            }

            if (obj.Count == 1 && obj["$from"] is JsonValue reference)
            {
                var parts = reference.GetValue<string>().Split('.');
                if (parts.Length < 2 || parts.Length > 12)
                    throw new InvalidOperationException("Expected dependencyKey.outputField.");
                JsonNode? value = Output(Dependency(work, parts[0]));
                foreach (var part in parts.Skip(1))
                    value = value is JsonObject fields ? fields[part] : null;
                return value?.DeepClone() ?? throw new InvalidOperationException("Dependency output field missing: " + reference);
            }

            var result = new JsonObject();
            foreach (var pair in obj)
                result[pair.Key] = Resolve(work, pair.Value, depth + 1);
            return result;
        }

        if (node is JsonArray array)
        {
            var result = new JsonArray();
            foreach (var item in array)
                result.Add(Resolve(work, item, depth + 1));
            return result;
        }

        return node?.DeepClone();
    }

    private static void RequireWithin(string file, string folder)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var path = Path.GetFullPath(file);
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("File is outside the approved workspace.");
        for (var current = path; current.Length >= root.Length; current = Path.GetDirectoryName(current) ?? "")
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Workspace references cannot follow redirected paths.");
    }

    private void ValidateMediaPath(WorkItem work, ConnectedTool tool, JsonObject input)
    {
        var path = input["path"]?.GetValue<string>() ?? throw new InvalidOperationException("media.import requires a path.");
        var project = store.Projects().Single(p => p.Id == work.ProjectId);
        foreach (var root in new[]
        {
            project.Root,
            tool.WorkspaceRoot
        }.Where(Directory.Exists))
        {
            var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            if (Path.GetFullPath(path).StartsWith(normalized + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                RequireWithin(path, root);
                return;
            }
        }

        foreach (var dependency in store.WorkItems().Where(w => work.DependsOn.Contains(w.Id) && w.SessionId == work.SessionId && w.ProjectId == work.ProjectId && w.ToolRunId != null && w.State == WorkState.Completed))
        {
            var artifact = Output(dependency)["artifactPath"]?.GetValue<string>();
            if (artifact != null && Path.GetFullPath(artifact).Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                return;
        }

        throw new InvalidOperationException("Marketing media must be inside the selected project/allowed tool workspace or a declared tool output.");
    }

    private string Binding(WorkItem work) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { work.ProjectId, Root = Path.GetFullPath(store.Projects().Single(p => p.Id == work.ProjectId).Root), work.SessionId, work.Tool, work.DependsOn, work.SkillHash }))));
    public void Approve(WorkItem work, bool result)
    {
        var run = BoundRun(work);
        if (result)
            tools.Review(run.Id);
        else
            tools.Approve(run.Id);
        work.State = WorkState.Queued;
        work.Notes = result ? "Tool result reviewed; resume dependent steps." : "Exact tool request approved; resume to execute.";
        store.SaveWork(work);
    }

    public bool CanContinueImage(WorkItem work, WorkSession session)
    {
        if (work.ToolRunId == null || !ImageDeliveryPolicy.Continues(work, session, store.WorkItems(), store.LoadSettings().Automation))
            return false;
        var run = BoundRun(work);
        var profile = registry.Tools().Single(t => t.Id == run.ToolId);
        return profile.Enabled && profile.AllowWrites && profile.AllowModelCalls && profile.Connector == "pixel" && run.Operation == "generate" && (run.State is ToolRunState.WaitingApproval or ToolRunState.Draft or ToolRunState.NeedsReview or ToolRunState.Completed || run.State == ToolRunState.Interrupted && ToolExecutionService.IsRemoteJobId(run.RemoteId));
    }

    public bool NeedsImageImport(WorkItem work, WorkSession session) => CanContinueImage(work, session) && BoundRun(work)is { State: ToolRunState.Completed, ProjectAssetPath: null };
    private void ImportImage(WorkItem work, WorkSession session, ToolRun run, CancellationToken token)
    {
        if (!ImageDeliveryPolicy.Continues(work, session, store.WorkItems(), store.LoadSettings().Automation))
            return;
        var profile = registry.Tools().Single(t => t.Id == run.ToolId);
        if (profile.Connector != "pixel" || run.Operation != "generate" || run.State is not (ToolRunState.NeedsReview or ToolRunState.Completed))
            return;
        if (!profile.Enabled || !profile.AllowWrites)
            throw new InvalidOperationException("Pixel connector write permission is required for image delivery.");
        token.ThrowIfCancellationRequested();
        var output = tools.ReadOutput(run);
        var path = output["artifactPath"]?.GetValue<string>() ?? throw new InvalidOperationException("Image job has no PNG artifact; existing job is retained.");
        if (!Guid.TryParse(run.Id, out _))
            throw new InvalidOperationException("Unexpected image request identifier.");
        var source = Path.GetFullPath(Path.Combine(dataRoot, "artifacts", "tool-runs", run.Id, "image.png"));
        if (!source.Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unexpected managed image location.");
        RequireWithin(source, dataRoot);
        if (new FileInfo(source).Length > 12_000_000)
            throw new InvalidOperationException("Managed PNG exceeds artifact limit.");
        var bytes = File.ReadAllBytes(source);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (hash != output["artifactSha256"]?.GetValue<string>() || bytes.Length < 24 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new InvalidOperationException("Generated PNG changed or has an invalid header; keep the original job for inspection.");
        // The tool returns finished frames; a sheet of another size is not put into the project.
        var layout = PixelSheetLayout.Requested(output);
        if (layout != null && PixelSheetLayout.Problem(output, layout) is { } wrongSize)
            throw new InvalidOperationException(wrongSize);
        var project = store.Projects().Single(p => p.Id == work.ProjectId);
        var folder = Directory.Exists(Path.Combine(project.Root, "Assets")) ? Path.Combine("Assets", "AIEmployeeGenerated") : "AIEmployeeGenerated";
        var destination = Path.GetFullPath(Path.Combine(project.Root, folder, run.Id + "-" + hash[..12] + ".png"));
        using var lease = reserve(work, [project.Root]);
        // Validate existing ancestors before creating directories; never follow a project junction.
        var projectRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(project.Root));
        if (!destination.StartsWith(projectRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Image destination escapes project.");
        for (var current = destination; current.Length >= projectRoot.Length; current = Path.GetDirectoryName(current) ?? "")
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Image delivery cannot follow redirected project paths.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination))
        {
            if (new FileInfo(destination).Length != bytes.Length || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(destination))) != hash)
                throw new InvalidOperationException("Project image was edited after import. It is preserved; no regeneration or overwrite was performed.");
        }
        else
        {
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    stream.Write(bytes);
                File.Move(temporary, destination, false);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }

        run.ProjectAssetPath = destination;
        run.ProjectAssetSha256 = hash;
        registry.SaveToolRun(run);
        var receipt = new JsonObject
        {
            ["projectAssetPath"] = destination,
            ["projectAssetSha256"] = hash,
            ["remoteArtifactPath"] = path
        };
        if (layout != null)
        {
            receipt["sheetWidth"] = layout.Columns * layout.FrameWidth;
            receipt["sheetHeight"] = layout.Rows * layout.FrameHeight;
            receipt["columns"] = layout.Columns;
            receipt["rows"] = layout.Rows;
            receipt["frameWidth"] = layout.FrameWidth;
            receipt["frameHeight"] = layout.FrameHeight;
        }
        receipt["integrationStatus"] = (layout != null
            ? $"PNG ({layout.Columns}x{layout.Rows} grid of {layout.FrameWidth}x{layout.FrameHeight} frames, as returned by the tool) copied into the selected project"
            : "PNG copied into the selected project")
            + "; downstream worker must wire importer/scene/UI consumer. Not verified by running the project.";
        // Other candidates (PixelLab): copied next to the main image as <name>-option-N.png to pick from.
        if (output["candidatePaths"] is JsonArray candidatePaths)
        {
            var options = new JsonArray();
            var runFolder = Path.GetFullPath(Path.Combine(dataRoot, "artifacts", "tool-runs", run.Id)) + Path.DirectorySeparatorChar;
            var number = 1;
            foreach (var candidate in candidatePaths.Select(c => c?.ToString()).OfType<string>())
            {
                var candidateFile = Path.GetFullPath(candidate);
                if (!candidateFile.StartsWith(runFolder, StringComparison.OrdinalIgnoreCase) || !File.Exists(candidateFile))
                    continue;
                var option = Path.Combine(Path.GetDirectoryName(destination)!, Path.GetFileNameWithoutExtension(destination) + $"-option-{++number}.png");
                File.Copy(candidateFile, option, true);
                options.Add(option);
            }
            if (options.Count > 0)
            {
                receipt["optionPaths"] = options;
                receipt["integrationStatus"] = receipt["integrationStatus"] + $" {options.Count} other option(s) saved next to it (-option-N.png); the main image is option 1.";
            }
        }
        if (QualitySummary(output["result"]?["quality"]) is { } quality)
        {
            receipt["quality"] = quality;
            store.Log(work.Id, "ToolQuality", quality);
        }
        store.Log(work.Id, "ImageImported", receipt.ToJsonString());
    }

    // What the tool's own check still found wrong in a finished image (empty when nothing).
    internal static IReadOnlyList<string> QualityProblems(string? output)
    {
        try
        {
            return string.IsNullOrWhiteSpace(output) || JsonNode.Parse(output)?["result"]?["quality"]?["problems"] is not JsonArray problems ? []
                : problems.Select(p => p?["note"]?.ToString()).Where(note => !string.IsNullOrWhiteSpace(note)).Cast<string>().ToArray();
        }
        catch (JsonException) { return []; }
    }

    // The tool's own check of what it produced: what it fixed, what it retried, what is still wrong.
    internal static string? QualitySummary(JsonNode? quality)
    {
        if (quality is not JsonObject q)
            return null;
        var parts = new List<string>();
        static string Name(string? p) => p switch { "codex" => "Codex CLI", "claude" => "Claude CLI", "pixellab" => "PixelLab", "api" => "API", _ => p ?? "?" };
        if (q["provider"]?.ToString() is { } made)
            parts.Add($"Made by {Name(made)}" + (q["switchedFrom"] is JsonArray from && from.Count > 0 ? $" ({string.Join(", ", from.Select(f => Name(f?["from"]?.ToString())))} had no usage left or was not signed in)." : "."));
        if (q["fixed"] is JsonArray fixedItems && fixedItems.Count > 0)
            parts.Add("Fixed: " + string.Join("; ", fixedItems.Select(f => f?.ToString())));
        if (q["retried"]?.GetValue<bool>() == true)
            parts.Add(q["retryKept"]?.GetValue<bool>() == true ? "Generated once more to fix problems; the new result was better and was kept." : q["retryError"] != null ? "Tried to generate once more but it failed: " + q["retryError"] : "Generated once more; the first result was better and was kept.");
        if (q["problems"] is JsonArray problems && problems.Count > 0)
            parts.Add("Still to check: " + string.Join("; ", problems.Select(p => p?["note"]?.ToString())));
        else
            parts.Add("No remaining problems detected.");
        if (q["frames"] is JsonArray frames && frames.Count > 0)
        {
            var drift = frames.Select(f => f?["colourDrift"]?.GetValue<double>() ?? 0).Max();
            var detail = frames.Select(f => f?["detailKept"]?.GetValue<double>() ?? 1).Min();
            parts.Add($"Worst frame: colour drift {drift:0.#}, dark details kept {detail:P0}.");
        }
        return string.Join(" ", parts);
    }

    private string Result(ToolRun run)
    {
        if (run.ProjectAssetPath == null)
            return run.Output;
        var output = new JsonObject
        {
            ["projectAssetPath"] = run.ProjectAssetPath,
            ["projectAssetSha256"] = run.ProjectAssetSha256
        };
        // The other candidates copied next to the image in the project, for the result note.
        var folder = Path.GetDirectoryName(run.ProjectAssetPath);
        if (folder != null && Directory.Exists(folder))
        {
            var options = Directory.GetFiles(folder, Path.GetFileNameWithoutExtension(run.ProjectAssetPath) + "-option-*.png").Order(StringComparer.OrdinalIgnoreCase).ToArray();
            if (options.Length > 0)
                output["optionPaths"] = new JsonArray(options.Select(o => (JsonNode)o).ToArray());
        }
        foreach (var field in tools.ReadOutput(run).AsObject())
            if (!output.ContainsKey(field.Key))
                output[field.Key] = field.Value?.DeepClone();
        return output.ToJsonString();
    }

    public bool WasReviewedExternally(WorkItem work)
    {
        if (work.ToolRunId == null)
            return false;
        var run = BoundRun(work);
        return (work.State == WorkState.AwaitingApproval && (run.State is ToolRunState.Draft or ToolRunState.Completed)) || (work.State == WorkState.NeedsReview && run.State == ToolRunState.Completed);
    }

    private ToolRun BoundRun(WorkItem work)
    {
        var run = registry.ToolRuns().Single(r => r.Id == work.ToolRunId);
        if (work.Tool == null || run.ToolId != work.Tool.ToolId || run.Operation != work.Tool.Operation || work.ToolBindingHash != Binding(work))
            throw new InvalidOperationException("Tool task binding changed. Review a fresh plan; do not replay an old write.");
        return run;
    }

    public async Task RunAsync(WorkItem work, WorkSession session, CancellationToken token)
    {
        try
        {
            if (work.SessionId != session.Id || !session.ProjectIds.Contains(work.ProjectId))
                throw new InvalidOperationException("Tool task is outside its approved session/project.");
            if (work.Access > session.AccessCeiling)
            {
                TaskProblems.Mark(work, WorkState.WrongAccess, "Tool step access exceeds the current session grant; correct scope before resuming.");
                return;
            }

            if (work.ToolRunId != null && BoundRun(work)is { State: ToolRunState.Completed } completed)
            {
                ImportImage(work, session, completed, token);
                work.State = WorkState.Completed;
                work.Notes = "Reused completed tool request " + completed.Id + "; no replay or additional model call.";
                var reused = Result(completed);
                store.Log(work.Id, "ToolResult", reused[..Math.Min(16000, reused.Length)]);
                return;
            }

            if (!store.LoadSettings().Automation.UseToolSkills)
                throw new InvalidOperationException("V5 tool skills are disabled in Settings > Tools.");
            var spec = work.Tool ?? throw new InvalidOperationException("Missing tool step.");
            if (work.SkillId != spec.SkillId || work.SkillHash == null)
                throw new InvalidOperationException("Tool step has no approved skill binding.");
            skills.Validate(spec);
            // The step runs under the skill as it is now; the operation itself was validated just above.
            if (skills.Hash(spec.SkillId) is var current && current != work.SkillHash)
            {
                work.SkillHash = current;
                store.SaveWork(work);
                store.Log(work.Id, "SkillRebound", "The tool step uses the current version of skill " + spec.SkillId + ".");
            }
            _ = skills.Read(spec.SkillId, work.SkillHash);
            var profile = registry.Tools().Single(t => t.Id == spec.ToolId);
            var capability = ToolExecutionService.Capabilities(profile.Connector).Single(c => c.Id == spec.Operation);
            ToolRun run;
            if (work.ToolRunId == null)
            {
                var pixel = profile.Connector == "pixel" && spec.Operation == "generate";
                JsonObject input;
                if (spec.Input != null)
                    input = spec.Input.DeepClone().AsObject();
                else
                {
                    var preparation = Dependency(work, spec.InputFrom!);
                    JsonObject? prepared = null;
                    var emptyList = false;
                    try
                    {
                        var output = Output(preparation);
                        prepared = output["toolInput"]?.DeepClone() as JsonObject;
                        emptyList = prepared == null && output["toolInputs"] is JsonArray { Count: 0 };
                        // Several calls prepared for one step (several assets, a base then its animations):
                        // this step makes the first, the rest become steps of their own.
                        if (prepared == null && output["toolInputs"] is JsonArray many && many.OfType<JsonObject>().Any())
                        {
                            var calls = many.OfType<JsonObject>().Select(call => call.DeepClone().AsObject()).ToList();
                            prepared = calls[0];
                            // A remake of this step (quality redo, retry) makes only its own call again:
                            // the other calls already have their steps (seen live: five duplicate poses).
                            if (!AlreadySplit(work))
                                Split(work, spec, calls.Skip(1).ToList(), $"the preparation step asked for {calls.Count} calls");
                        }
                    }
                    // A preparation that meant to answer with JSON, or a step that asks for several images, is
                    // not reduced to one request built from the step's description: the step fails and the
                    // preparation runs again. Seen live: "make 15 poses" became one image.
                    catch (InvalidOperationException ex) when (pixel && ex.Message.StartsWith("Input preparation did not return JSON", StringComparison.Ordinal) && !MeantSeveral(preparation, work))
                    {
                        store.Log(work.Id, "PixelRequestCompleted", "The preparation step returned no usable JSON; the request is built from this step's description.");
                    }
                    // An empty list is an answer, not a broken one: the preparation found nothing for the
                    // tool to do (seen live: no saved ZapBop project existed, so nothing to inspect).
                    if (prepared == null && emptyList)
                        throw new InvalidOperationException(EmptyPreparation(preparation) ?? NothingToRun);
                    input = prepared ?? (pixel ? new JsonObject() : throw new InvalidOperationException("Input preparation must return {toolInput:{...}}."));
                }
                var goalTask = store.WorkItems().FirstOrDefault(item => item.Id == work.ParentTaskId) ?? store.WorkItems().FirstOrDefault(item => item.Id == session.RootWorkId);
                var appliedPreset = ApplyPreset(work, spec, goalTask, input, store.WorkItems());
                var presetApplied = appliedPreset != null;
                if (appliedPreset != null)
                    store.Log(work.Id, "ToolPresetApplied", "Settings chosen in the Tool task form: " + appliedPreset.ToJsonString());
                if (pixel)
                {
                    var attached = (goalTask?.Attachments ?? []).Concat(goalTask?.Comments.Where(c => !c.IsAssistant).SelectMany(c => c.Attachments) ?? []).ToArray();
                    var chosen = ChooseImages(work, input, store.WorkItems(), attached, StillImage);
                    if (chosen.Count > 0)
                        store.Log(work.Id, "PixelReferenceSelected", string.Join("; ", chosen));
                }
                // Frames asked of a still image with nothing to animate: draw the base pose here,
                // then one animation call per motion, each its own step after this one.
                if (pixel && !presetApplied && PixelRequest.SplitFrames(input, work.PlanKey ?? work.Id) is { } frames)
                {
                    input = frames.Base;
                    if (!AlreadySplit(work))
                        Split(work, spec, frames.Motions, "create cannot return frames: base pose first, then " + frames.Motions.Count + " animation call(s)");
                }
                input = Resolve(work, input)!.AsObject();
                if (pixel)
                {
                    var otherImageSteps = store.WorkItems().Any(step => step.SessionId == session.Id &&
                        step.ProjectId == work.ProjectId && step.Id != work.Id &&
                        step.Tool is { Operation: "generate" } other && other.ToolId == spec.ToolId &&
                        step.State is not (WorkState.Completed or WorkState.Skipped));
                    var goal = session.RootWorkId == null ? null : store.WorkItems().FirstOrDefault(item => item.Id == session.RootWorkId);
                    var update = goal?.Comments.LastOrDefault(comment => !comment.IsAssistant);
                    var corrections = FinishPixelRequest(input, PixelToolOptions.Read(profile), work.Request, presetApplied, otherImageSteps ? null : update?.Text ?? goal?.Request ?? session.Goal);
                    if (corrections.Count > 0)
                        store.Log(work.Id, "PixelRequestCompleted", string.Join("; ", corrections));
                    // A remake after the tool's own check found problems: say what to avoid.
                    if (store.Logs(work.Id).LastOrDefault(l => l.Event == "ToolQualityRedo") is { } redo && input["prompt"] is JsonValue promptValue && promptValue.TryGetValue<string>(out var prompt))
                    {
                        var avoid = "\nThe previous attempt was rejected by the quality check: " + redo.Message[..Math.Min(600, redo.Message.Length)] + " Use flat colour areas and a clean outline; keep every frame the same size and baseline.";
                        input["prompt"] = (prompt.Length + avoid.Length > 4000 ? prompt[..(4000 - avoid.Length)] : prompt) + avoid;
                    }
                }
                if (profile.Connector == "marketing" && spec.Operation == "media.import")
                    ValidateMediaPath(work, profile, input);
                run = tools.Create(spec.ToolId, spec.Operation, input.ToJsonString());
                work.ToolRunId = run.Id;
                work.ToolBindingHash = Binding(work);
                store.SaveWork(work);
                store.Log(work.Id, "ToolPrepared", JsonSerializer.Serialize(new { spec.ToolId, spec.SkillId, spec.Operation, RunId = run.Id, work.SkillHash }));
            }
            else
                run = BoundRun(work);
            if (run.ModelCallReserved && work.Attempts == 0)
            {
                work.Attempts = 1;
                store.SaveWork(work);
            }

            var imageDelivery = ImageDeliveryPolicy.Continues(work, session, store.WorkItems(), store.LoadSettings().Automation) && profile.Connector == "pixel" && spec.Operation == "generate";
            var automatic = !capability.Publishes && (session.Mode == RoutingMode.Automatic && profile.AllowTaskAutomation || imageDelivery);
            if (run.State == ToolRunState.WaitingApproval && automatic)
            {
                tools.Approve(run.Id);
                run = BoundRun(work);
                store.Log(work.Id, "ToolPolicyApproval", "Exact request approved under the configured task automation policy: " + run.Id);
            }

            var resumeImage = run.State == ToolRunState.Interrupted && profile.Connector == "pixel" && spec.Operation == "generate" && ToolExecutionService.IsRemoteJobId(run.RemoteId);
            if (run.State == ToolRunState.Draft || resumeImage)
            {
                if (capability.UsesAI && !resumeImage && session.RemainingCalls == 0)
                    throw new InvalidOperationException("Session call budget reached; tool AI was not called.");
                using var lease = reserve(work, new[] { profile.SourcePath, profile.WorkspaceRoot }.Where(Directory.Exists).ToArray());
                work.State = WorkState.Running;
                store.SaveWork(work);
                await tools.ExecuteAsync(run.Id, token, scopeReserved: true, onModelCallReserved: () =>
                {
                    work.Attempts = Math.Max(1, work.Attempts);
                    store.SaveWork(work);
                });
                run = BoundRun(work);
            }

            if (run.State == ToolRunState.NeedsReview && profile.Connector == "pixel" && spec.Operation == "generate" && (JsonNode.Parse(run.Output)?["artifactPath"] == null))
            {
                run.State = ToolRunState.Failed;
                run.Error = "Pixel job completed without a PNG artifact. Inspect the backend result before a fresh request.";
                registry.SaveToolRun(run);
            }

            ImportImage(work, session, run, token);
            // The tool's own check still finds problems (noise, stray pixels, drift): the image is made
            // once more, told what to avoid, instead of being passed on as done. Seen live: "frames 2, 3, 4
            // still have many isolated pixels" went into the game.
            if (run.State is ToolRunState.NeedsReview or ToolRunState.Completed && profile.Connector == "pixel" && spec.Operation == "generate"
                && QualityProblems(run.Output) is { Count: > 0 } problems && !store.Logs(work.Id).Any(l => l.Event == "ToolQualityRedo") && session.RemainingCalls > 0)
            {
                store.Log(work.Id, "ToolQualityRedo", "The tool still reported: " + string.Join("; ", problems) + ". Making the image once more and asking it to avoid that.");
                work.ToolRunId = null;
                work.ToolBindingHash = null;
                work.State = WorkState.Queued;
                work.Notes = "Making the image once more: the tool still reported quality problems (" + string.Join("; ", problems) + ").";
                return;
            }
            if (run.State == ToolRunState.NeedsReview && automatic && (session.AutoContinueSteps || imageDelivery))
            {
                tools.Review(run.Id);
                run = BoundRun(work);
            }

            work.State = run.State switch
            {
                ToolRunState.Completed => WorkState.Completed,
                ToolRunState.WaitingApproval => WorkState.AwaitingApproval,
                ToolRunState.NeedsReview => WorkState.NeedsReview,
                ToolRunState.Failed => WorkState.Failed,
                ToolRunState.Cancelled or ToolRunState.Interrupted => WorkState.Paused,
                _ => WorkState.Paused
            };
            work.ProviderId = "tool:" + profile.Connector;
            work.ModelId = null;
            work.Notes = $"V5 {profile.Name} / {run.Operation}: {run.State}. Request {run.Id}. " + run.Error;
            if (run.State == ToolRunState.WaitingApproval)
                work.Notes += "\nInput needing approval (" + profile.Name + ", " + spec.Operation + "):\n" + run.Input[..Math.Min(6000, run.Input.Length)] + (run.PayloadFile == null ? "" : "\nFull input: " + run.PayloadFile + "; SHA256: " + run.PayloadSha256);
            var delivered = Result(run);
            store.Log(work.Id, "ToolResult", delivered[..Math.Min(16000, delivered.Length)]);
            var project = store.Projects().Single(p => p.Id == work.ProjectId);
            store.SaveHandoff(new(work.Id, DateTimeOffset.UtcNow, "V5 tool checkpoint", work.Request, project.Root, "", work.Notes, "Continue dependent tasks only after this result is completed.", "Not collected", "Not collected", "Not tested", run.Output[..Math.Min(16000, run.Output.Length)]));
        }
        catch (OperationCanceledException)when (token.IsCancellationRequested)
        {
            work.State = WorkState.Paused;
            work.Notes = "Tool stopped; existing run retained. Inspect tool state before retrying.";
            throw;
        }
        catch (Exception ex)
        {
            work.State = work.ToolRunId != null && registry.ToolRuns().Any(r => r.Id == work.ToolRunId && r.State == ToolRunState.Draft) ? WorkState.Paused : WorkState.Failed;
            work.Notes = "V5 tool step: " + ex.Message;
            store.Log(work.Id, "ToolError", work.Notes);
        }
        finally
        {
            store.SaveWork(work);
        }
    }


}
