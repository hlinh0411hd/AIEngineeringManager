using System.Text.Json.Nodes;
using Employee.Core;

namespace Employee.Infrastructure;

// Art made by a task's image tool steps is judged by looking at it next to the project's own art.
// Seen live (HonkAndBonk): idle/peck frames were animated from a poor earlier output, frame 1 was
// smaller than frames 2-4 and the two motions looked alike; the tester checked sizes and wiring only
// and passed them. The manager now builds one comparison picture and adds a criterion for it.
internal static class ArtReview
{
    public const string Criterion = "The new art matches the project's existing art in the ART REVIEW image: same scale, style, outline and palette as the reference, clean pixels without noise, every frame of an animation the same size and baseline, and each motion clearly different from the others.";

    private static readonly string[] SkippedFolders = ["library", "temp", "obj", "bin", "logs", "builds", "build", "node_modules", ".git", ".aiemployee", "aiemployeegenerated", "packages", "usersettings"];

    // The PNGs this task's image tool steps put into the project, newest last (at most 12).
    public static IReadOnlyList<PageSnapshot.ArtImage> Made(IEmployeeStore store, WorkSession session)
    {
        var images = new List<PageSnapshot.ArtImage>();
        foreach (var step in store.WorkItems().Where(w => w.SessionId == session.Id && w.Tool != null && w.State == WorkState.Completed).OrderBy(w => w.UpdatedAt))
        {
            var imported = store.Logs(step.Id).LastOrDefault(l => l.Event == "ImageImported");
            if (imported == null || Text(Parse(imported.Message)?["projectAssetPath"]) is not { } path || !File.Exists(path))
                continue;
            // What this call made: a split call's own line (not its parent's "make 15 poses").
            var request = System.Text.RegularExpressions.Regex.Replace(ManagedToolHandoff.OwnRequest(step.Request), @"^Call \d+ of \d+ split from step [^:]+:\s*", "").ReplaceLineEndings(" ");
            images.Add(new($"{step.PlanKey ?? step.Id[..8]}: {(request.Length > 90 ? request[..90] + "…" : request)}", path));
        }
        return images.TakeLast(12).ToArray();
    }

    // What the new art is compared with: the image each tool call was given as its source, then the
    // project's own art from before this task (the largest PNGs, art folders first).
    public static IReadOnlyList<PageSnapshot.ArtImage> Reference(IEmployeeStore store, IToolWorkflowStore? runs, WorkSession session, string root, DateTimeOffset since, string scratch)
    {
        var images = new List<PageSnapshot.ArtImage>();
        if (runs != null)
        {
            Directory.CreateDirectory(scratch);
            foreach (var step in store.WorkItems().Where(w => w.SessionId == session.Id && w.Tool != null && w.ToolRunId != null && w.State == WorkState.Completed).OrderBy(w => w.UpdatedAt).TakeLast(6))
            {
                var run = runs.ToolRuns().FirstOrDefault(r => r.Id == step.ToolRunId);
                if (Text(Parse(run?.Input)?["image"]) is not { } data || !data.StartsWith("data:image/png;base64,", StringComparison.Ordinal))
                    continue;
                var file = Path.Combine(scratch, "source-" + step.Id + ".png");
                try { File.WriteAllBytes(file, Convert.FromBase64String(data["data:image/png;base64,".Length..])); }
                catch (FormatException) { continue; }
                images.Add(new($"source image given to the tool for {step.PlanKey ?? step.Id[..8]}", file));
            }
        }
        var existing = Pngs(root).Where(file => File.GetLastWriteTimeUtc(file) < since.UtcDateTime)
            .OrderByDescending(file => System.Text.RegularExpressions.Regex.IsMatch(file.ToLowerInvariant(), @"[\\/](art|sprites?|textures?|characters?|graphics|images)[\\/]"))
            .ThenByDescending(file => new FileInfo(file).Length)
            .Take(3);
        images.AddRange(existing.Select(file => new PageSnapshot.ArtImage("existing art: " + Path.GetRelativePath(root, file), file)));
        return images;
    }

    private static IEnumerable<string> Pngs(string root)
    {
        var pending = new Stack<string>([root]);
        var seen = 0;
        while (pending.Count > 0 && seen < 5000)
        {
            var folder = pending.Pop();
            string[] files, folders;
            try
            {
                files = Directory.GetFiles(folder, "*.png");
                folders = Directory.GetDirectories(folder);
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var file in files)
            {
                seen++;
                yield return file;
            }
            foreach (var sub in folders.Where(f => !SkippedFolders.Contains(Path.GetFileName(f).ToLowerInvariant()) && (File.GetAttributes(f) & FileAttributes.ReparsePoint) == 0))
                pending.Push(sub);
        }
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static JsonNode? Parse(string? json)
    {
        try { return string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json); }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
