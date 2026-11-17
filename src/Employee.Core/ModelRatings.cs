namespace Employee.Core;
// What a step actually needs from a model. The first seven mirror WorkKind (keep that order);
// the rest split game/app work where models differ a lot (debugging, graphics math, performance,
// architecture), where cheap models are enough (tests, build, translation), or where only some
// workers can do it at all (image generation, reading attached images).
public enum WorkCapability
{
    Coding,
    Review,
    Research,
    Content,
    Classification,
    Extraction,
    Planning,
    ImageGeneration,
    UiDesign,
    Debugging,
    Refactoring,
    Testing,
    GraphicsMath,
    Performance,
    Architecture,
    GameDesign,
    CreativeWriting,
    Translation,
    ImageUnderstanding,
    BuildDevOps
}

public static class WorkCapabilities
{
    private static readonly string[] ImageTerms = Vocabulary.ImageGeneration;
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp"];
    // Checked in order; the first match wins. Code capabilities apply to Coding/Review steps,
    // writing/design ones to Content/Research/Review steps.
    private static readonly (WorkCapability Capability, bool Code, bool Text, string[] Terms)[] Signals = [(WorkCapability.Architecture, true, true, ["kien truc", "architecture", "system design", "thiet ke he thong", "cau truc module", "module structure", "data model", "schema design", "thiet ke du lieu"]), (WorkCapability.GraphicsMath, true, false, ["shader", "hlsl", "glsl", "shader graph", "rendering", "render pipeline", "physics", "vat ly", "va cham", "collision", "raycast", "quaternion", "vector math", "toan hoc", "pathfinding", "tim duong", "procedural", "particle", "lighting"]), (WorkCapability.Performance, true, false, ["toi uu", "optimize", "optimization", "performance", "hieu nang", "fps", "lag", "giat", "memory leak", "ro ri bo nho", "garbage collection", "profiling", "profiler", "load time"]), (WorkCapability.Debugging, true, false, ["debug", "fix bug", "fix crash", "sua loi", "bi loi", "gap loi", "bao loi", "bug", "crash", "exception", "stack trace", "khong chay", "khong hoat dong", "null reference", "nullreferenceexception", "race condition"]), (WorkCapability.Testing, true, false, ["unit test", "unit tests", "viet test", "test case", "test cases", "integration test", "kiem thu", "playmode test", "editmode test", "automated test", "automated tests"]), (WorkCapability.Refactoring, true, false, ["refactor", "refactoring", "tai cau truc", "clean code", "don dep code", "tach module", "tach class", "rename across"]), (WorkCapability.BuildDevOps, true, false, ["build script", "ci", "github actions", "pipeline ci", "packaging", "dong goi", "deploy", "release build", "installer", "msbuild", "gradle", "addressables build", "build apk", "build ios"]), (WorkCapability.UiDesign, true, true, ["ui", "ux", "giao dien", "layout", "menu", "hud", "button", "nut bam", "man hinh", "screen", "xaml", "css", "html", "uxml", "ugui", "canvas", "responsive", "theme", "popup", "dialog box"]), (WorkCapability.GameDesign, false, true, ["game design", "gdd", "co che", "mechanic", "mechanics", "gameplay loop", "can bang", "balancing", "balance", "kinh te game", "economy", "level design", "thiet ke man", "progression", "loot", "drop rate"]), (WorkCapability.CreativeWriting, false, true, ["cot truyen", "story", "narrative", "loi thoai", "dialogue", "dialog", "lore", "cau chuyen", "backstory", "quest text", "kich ban", "cutscene"]), (WorkCapability.Translation, true, true, ["dich sang", "dich tieng", "dich van ban", "dich noi dung", "ban dich", "translate", "translation", "localization", "localize", "ban dia hoa", "da ngon ngu", "multilingual"])];
    public static bool GeneratesImage(WorkItem work)
    {
        if (work.Tool is { Operation: "generate" })
            return true;
        if (work.SkillId == "imagegen")
            return true;
        var text = " " + TaskIntent.Normalize(work.Request) + " ";
        return ImageTerms.Any(term => text.Contains(" " + term + " ", StringComparison.Ordinal));
    }

    private static bool HasImageAttachment(WorkItem work) => work.Attachments.Concat(work.Comments.SelectMany(c => c.Attachments)).Any(file => ImageExtensions.Contains(Path.GetExtension(file.Name).ToLowerInvariant()));
    public static WorkCapability For(WorkItem work)
    {
        var kind = (WorkCapability)(int)work.Kind;
        if (work.Tool != null || work.IsCoordinator)
            return kind;
        // The planner's per-step label wins; then hard requirements; then wording.
        if (work.CapabilityHint is { } hint && Enum.TryParse<WorkCapability>(hint, true, out var planned) && Enum.IsDefined(planned))
            return planned;
        if (GeneratesImage(work))
            return WorkCapability.ImageGeneration;
        if (HasImageAttachment(work))
            return WorkCapability.ImageUnderstanding;
        var code = work.Kind is WorkKind.Coding or WorkKind.Review;
        var writing = work.Kind is WorkKind.Content or WorkKind.Research or WorkKind.Review;
        if (!code && !writing)
            return kind;
        var text = " " + TaskIntent.Normalize(work.Request) + " ";
        foreach (var(capability, forCode, forText, terms)in Signals)
            if ((code && forCode || writing && forText) && terms.Any(term => text.Contains(" " + term + " ", StringComparison.Ordinal)))
                return capability;
        return kind;
    }

    // Only Codex workers run with a built-in image tool and receive attached files; Claude and
    // Copilot workers in this app are tool-free and never see attachments.
    public static bool ProviderSupports(string providerId, WorkCapability capability) => capability is not (WorkCapability.ImageGeneration or WorkCapability.ImageUnderstanding) || providerId == "codex";
    public static string Display(WorkCapability capability) => capability switch
    {
        WorkCapability.Coding => "Code",
        WorkCapability.Review => "Review",
        WorkCapability.Research => "Research",
        WorkCapability.Content => "Content",
        WorkCapability.Classification => "Classification",
        WorkCapability.Extraction => "Extraction",
        WorkCapability.Planning => "Planning",
        WorkCapability.ImageGeneration => "Image generation",
        WorkCapability.UiDesign => "UI/UX",
        WorkCapability.Debugging => "Debug",
        WorkCapability.Refactoring => "Refactor",
        WorkCapability.Testing => "Testing",
        WorkCapability.GraphicsMath => "Graphics/Physics/Math",
        WorkCapability.Performance => "Performance",
        WorkCapability.Architecture => "Architecture",
        WorkCapability.GameDesign => "Game design",
        WorkCapability.CreativeWriting => "Story/Dialogue",
        WorkCapability.Translation => "Translation/Localization",
        WorkCapability.ImageUnderstanding => "Image/screenshot reading",
        WorkCapability.BuildDevOps => "Build/CI",
        _ => capability.ToString()};
}

public sealed record ModelRating(int Quality, int CostRank, string Source);
// Ratings-file format (model-ratings.json, local or from a URL). Quality keys are WorkCapability names.
public sealed record RatingEntry(string Pattern, int Cost, Dictionary<string, int> Quality);
public sealed record RatingSet(string Source, DateTimeOffset Updated, RatingEntry[] Models);
// Built-in starting ratings used when the user has not rated a model. Quality is 0-100 per
// capability, estimated from public benchmarks (DeepSWE / SWE-bench for coding and debugging,
// intelligence indexes for research/planning/architecture); CostRank is 0-100 relative usage
// (subscription limits track API price). As of 2026-11. Cheap models are rated close to strong
// ones where benchmarks show a small gap (tests, build, translation) so routing saves usage there.
// Measured runs and user ratings override these.
public static class ModelRatings
{
    private const string Benchmarks = "Built-in rating (public benchmarks/pricing, 2026-11)";
    // Column order = WorkCapability order:
    //  Code Rev  Res  Cont Cls  Ext  Plan ImgG UI   Dbg  Refa Test Gfx  Perf Arch GDes Writ Tran ImgU Build   (0 = cannot)
    // Ordered most-specific first; matched against the lowercased model ID.
    private static readonly (string Pattern, int Cost, int[] Quality)[] Table =
    [
        ("gpt-6.1-astra", 95, [ 96, 96, 96, 91, 96, 96, 97, 96, 93, 96, 95, 94, 96, 95, 97, 93, 90, 93, 95, 94]),
        ("gpt-6.1-sol",   45, [ 91, 89, 87, 86, 91, 91, 88, 93, 88, 89, 90, 90, 88, 88, 88, 86, 84, 89, 91, 90]),
        ("gpt-6.1-terra", 20, [ 88, 85, 81, 81, 89, 89, 83, 91, 84, 83, 86, 88, 82, 82, 82, 81, 79, 87, 88, 87]),
        ("gpt-6.1-luna",   5, [ 86, 79, 71, 75, 86, 86, 73, 89, 80, 76, 82, 86, 72, 72, 72, 74, 72, 84, 84, 85]),
        ("gpt-6-astra",   95, [ 95, 95, 95, 90, 95, 95, 96, 95, 92, 95, 94, 93, 95, 94, 96, 92, 89, 92, 94, 93]),
        ("gpt-6-sol",     45, [ 90, 88, 86, 85, 90, 90, 87, 92, 87, 88, 89, 89, 87, 87, 87, 85, 83, 88, 90, 89]),
        ("gpt-6-terra",   20, [ 87, 84, 80, 80, 88, 88, 82, 90, 83, 82, 85, 87, 81, 81, 81, 80, 78, 86, 87, 86]),
        ("gpt-6-luna",     5, [ 85, 78, 70, 74, 85, 85, 72, 88, 79, 75, 81, 85, 71, 71, 71, 73, 71, 83, 83, 84]),
        ("gpt-5.6-sol",   45, [ 86, 85, 83, 82, 88, 88, 84, 85, 83, 84, 85, 86, 83, 83, 84, 82, 80, 86, 86, 86]),
        ("gpt-5.6-terra", 20, [ 81, 78, 75, 76, 85, 85, 76, 82, 78, 76, 79, 82, 75, 75, 75, 75, 74, 83, 82, 81]),
        ("gpt-5.6-luna",   6, [ 75, 70, 64, 68, 80, 80, 64, 78, 72, 67, 73, 78, 63, 63, 62, 66, 65, 78, 76, 76]),
        ("fable",         95, [ 91, 92, 93, 93, 92, 92, 92,  0, 92, 91, 91, 90, 91, 90, 93, 93, 95, 92, 90, 89]),
        ("opus",          60, [ 94, 94, 92, 91, 93, 93, 94,  0, 93, 94, 94, 92, 92, 92, 94, 91, 92, 91, 90, 92]),
        ("sonnet",        45, [ 92, 90, 88, 90, 90, 90, 88,  0, 93, 90, 91, 91, 88, 88, 88, 88, 90, 90, 88, 90]),
        ("haiku",         15, [ 78, 74, 72, 78, 85, 85, 70,  0, 78, 72, 75, 80, 68, 68, 66, 72, 76, 82, 78, 78]),
        ("glm",            8, [ 70, 68, 66, 70, 78, 78, 64,  0, 68, 64, 66, 72, 60, 60, 60, 64, 68, 74,  0, 70]),
        ("flash",          8, [ 70, 68, 66, 70, 78, 78, 64,  0, 68, 64, 66, 72, 60, 60, 60, 64, 68, 74,  0, 70]),
        // Gemini models other than Flash (rough estimate: mid tier). Replace through the ratings file.
        ("gemini",        40, [ 88, 87, 88, 86, 90, 90, 87,  0, 87, 86, 87, 88, 86, 86, 86, 85, 84, 89, 88, 87]),
        ("mini",          10, [ 74, 70, 68, 72, 80, 80, 66, 70, 70, 68, 70, 76, 64, 64, 62, 66, 68, 78, 72, 74]),
        ("nano",           4, [ 62, 58, 55, 62, 72, 72, 52,  0, 58, 55, 58, 66, 50, 50, 48, 54, 58, 70,  0, 62])
    ];
    // Ratings loaded from a ratings file or URL take precedence over the built-in table.
    // Replaced as a whole, so readers on any thread see rows and source from the same load.
    private sealed record LoadedRatings((string Pattern, int Cost, int[] Quality)[] Rows, string Source);
    private static LoadedRatings loaded = new([], "");
    private static LoadedRatings Current => Volatile.Read(ref loaded);
    public static string ActiveSource => Current is { Rows.Length: > 0 } current ? $"{current.Source} ({current.Rows.Length} models) + built-in" : Benchmarks;

    // A pattern matches where it is not part of a longer word: "mini" matches "gpt-5-mini" and
    // "o4mini2" but not "gemini" or "minimax". Digits and punctuation count as boundaries.
    internal static bool Matches(string id, string pattern)
    {
        for (var at = id.IndexOf(pattern, StringComparison.Ordinal); at >= 0; at = id.IndexOf(pattern, at + 1, StringComparison.Ordinal))
        {
            var end = at + pattern.Length;
            var before = at > 0 && char.IsLetter(id[at - 1]) && char.IsLetter(pattern[0]);
            var after = end < id.Length && char.IsLetter(id[end]) && char.IsLetter(pattern[^1]);
            if (!before && !after)
                return true;
        }

        return false;
    }

    private static (int Cost, int[] Quality, string Source)? Find(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return null;
        var id = modelId.ToLowerInvariant();
        var current = Current;
        foreach (var (pattern, cost, quality) in current.Rows)
            if (Matches(id, pattern))
                return (cost, quality, current.Source);
        foreach (var(pattern, cost, quality)in Table)
            if (Matches(id, pattern))
                return (cost, quality, Benchmarks);
        return null;
    }

    // Replaces the loaded ratings (null clears them). Each entry may rate only some capabilities;
    // the rest come from the built-in row for the same pattern, or the entry's own average.
    public static string Apply(RatingSet? set)
    {
        if (set == null || set.Models.Length == 0)
        {
            Volatile.Write(ref loaded, new([], ""));
            return Benchmarks;
        }

        var rows = new List<(string, int, int[])>();
        foreach (var entry in set.Models)
        {
            var pattern = (entry.Pattern ?? "").Trim().ToLowerInvariant();
            if (!System.Text.RegularExpressions.Regex.IsMatch(pattern, @"^[a-z0-9][a-z0-9._:/-]{1,80}$"))
                throw new InvalidOperationException($"Invalid model pattern '{entry.Pattern}'.");
            if (entry.Cost is < 0 or > 100)
                throw new InvalidOperationException($"Usage rank for '{pattern}' must be 0-100.");
            var given = new Dictionary<WorkCapability, int>();
            foreach (var(name, value)in entry.Quality ?? [])
            {
                if (!Enum.TryParse<WorkCapability>(name, true, out var capability) || !Enum.IsDefined(capability))
                    throw new InvalidOperationException($"Unknown work type '{name}' for '{pattern}'.");
                if (value is < 0 or > 100)
                    throw new InvalidOperationException($"Quality for '{pattern}' / {name} must be 0-100.");
                given[capability] = value;
            }

            if (given.Count == 0)
                throw new InvalidOperationException($"'{pattern}' has no quality values.");
            var builtIn = Table.FirstOrDefault(row => Matches(pattern, row.Pattern)).Quality;
            var fallback = (int)Math.Round(given.Values.Average());
            rows.Add((pattern, entry.Cost, Enum.GetValues<WorkCapability>().Select(c => given.TryGetValue(c, out var v) ? v : builtIn?[(int)c] ?? fallback).ToArray()));
        }

        Volatile.Write(ref loaded, new([..rows.OrderByDescending(row => row.Item1.Length)], $"Ratings from {(string.IsNullOrWhiteSpace(set.Source) ? "file" : set.Source)}, {set.Updated:yyyy-MM-dd}"));
        return ActiveSource;
    }

    // The built-in table in the ratings-file format, as a starting point for editing.
    public static RatingSet BuiltIn() => new("built-in", DateTimeOffset.UtcNow, [..Table.Select(row => new RatingEntry(row.Pattern, row.Cost, Enum.GetValues<WorkCapability>().ToDictionary(c => c.ToString(), c => row.Quality[(int)c])))]);
    // Overall rating shown in the model list: average over the seven base text capabilities.
    public static ModelRating Effective(ModelInfo model)
    {
        if (model.RatedByUser)
            return new(model.Quality, model.CostRank, "User rating");
        if (Find(model.Id)is { } hit)
            return new((int)Math.Round(hit.Quality.Take(7).Average()), hit.Cost, hit.Source);
        return new(model.Quality, model.CostRank, string.IsNullOrWhiteSpace(model.Id) ? "CLI default; exact model unknown" : "Unrated (catalog default)");
    }

    // Quality for one capability. A user rating applies to every capability the provider can do.
    public static int QualityFor(string providerId, ModelInfo model, WorkCapability capability)
    {
        if (!WorkCapabilities.ProviderSupports(providerId, capability))
            return 0;
        if (model.RatedByUser || Find(model.Id)is not { } hit)
            return model.Quality;
        return hit.Quality[(int)capability];
    }

    // Minimum quality a step needs, from its 0-100 difficulty. The router takes the
    // least-usage model at or above this bar: difficulty 20 → 59, 50 → 72, 80 → 86, 100 → 95.
    public static int RequiredQuality(int difficulty, RoutingPreference preference)
    {
        var bar = 50 + difficulty * 0.45;
        bar += preference switch
        {
            RoutingPreference.Economy => -6,
            RoutingPreference.Performance => 6,
            _ => 0
        };
        return (int)Math.Clamp(Math.Round(bar), 40, 99);
    }
}
