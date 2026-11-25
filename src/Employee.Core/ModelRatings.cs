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
public sealed record RatingEntry(string Pattern, int Cost, Dictionary<string, int> Quality, string? Deprecation = null);
// Priority: optional per-work-type model order ("provider/model" or "provider/model@maxDifficulty"),
// work type names as keys. A work type that is not listed keeps the built-in order; an empty list
// turns the order off for that work type.
public sealed record RatingSet(string Source, DateTimeOffset Updated, RatingEntry[] Models, Dictionary<string, string[]>? Priority = null);
// One step of a work type's priority order. MaxDifficulty: the step is skipped for harder work.
public sealed record PriorityRung(string ProviderId, string ModelId, int MaxDifficulty = 100);
// Built-in starting ratings used when the user has not rated a model. Quality is 0-100 per
// capability, estimated from public benchmarks (DeepSWE / SWE-bench for coding and debugging,
// intelligence indexes for research/planning/architecture); CostRank is 0-100 relative usage
// (subscription limits track API price). As of 2026-11. Cheap models are rated close to strong
// ones where benchmarks show a small gap (tests, build, translation) so routing saves usage there.
// Measured runs and user ratings override these.
public static class ModelRatings
{
    private const string Benchmarks = "Built-in rating (public benchmarks/pricing, 2026-11, revised)";
    // Column order = WorkCapability order:
    //  Code Rev  Res  Cont Cls  Ext  Plan ImgG UI   Dbg  Refa Test Gfx  Perf Arch GDes Writ Tran ImgU Build   (0 = cannot)
    // Ordered most-specific first; matched against the lowercased model ID.
    private static readonly (string Pattern, int Cost, int[] Quality)[] Table =
    [
        ("gpt-6.1-astra",  95, [ 94, 94, 94, 90, 95, 95, 96, 94, 92, 94, 94, 93, 94, 93, 95, 91, 89, 92, 94, 92]),
        ("gpt-6.1-sol",  25, [ 91, 88, 86, 86, 91, 91, 87, 92, 88, 88, 90, 89, 87, 87, 87, 85, 83, 88, 90, 88]),
        ("gpt-6.1-terra",  12, [ 84, 82, 79, 80, 88, 88, 81, 88, 82, 80, 83, 86, 79, 79, 79, 79, 77, 86, 86, 85]),
        ("gpt-6.1-luna",   4, [ 78, 72, 66, 72, 86, 86, 68, 84, 74, 70, 76, 82, 66, 66, 64, 70, 70, 82, 80, 82]),
        ("gpt-6-astra",  95, [ 93, 93, 93, 89, 94, 94, 95, 93, 91, 93, 93, 92, 93, 92, 94, 90, 88, 91, 93, 91]),
        ("gpt-6-sol",  25, [ 90, 87, 85, 85, 90, 90, 86, 91, 87, 87, 89, 88, 86, 86, 86, 84, 82, 87, 89, 87]),
        ("gpt-6-terra",  12, [ 83, 81, 78, 79, 87, 87, 80, 87, 81, 79, 82, 85, 78, 78, 78, 78, 76, 85, 85, 84]),
        ("gpt-6-luna",   4, [ 77, 71, 65, 71, 85, 85, 67, 83, 73, 69, 75, 81, 65, 65, 63, 69, 69, 81, 79, 81]),
        ("gpt-5.6-sol",   45, [ 86, 85, 83, 82, 88, 88, 84, 85, 83, 84, 85, 86, 83, 83, 84, 82, 80, 86, 86, 86]),
        ("gpt-5.6-terra", 20, [ 81, 78, 75, 76, 85, 85, 76, 82, 78, 76, 79, 82, 75, 75, 75, 75, 74, 83, 82, 81]),
        ("gpt-5.6-luna",   6, [ 75, 70, 64, 68, 80, 80, 64, 78, 72, 67, 73, 78, 63, 63, 62, 66, 65, 78, 76, 76]),
        // Claude 5.5 generation: Opus/Sonnet 5.5 and Haiku 5.5 (Sept-Oct 2026 launch figures: SWE-bench Pro,
        // Terminal-Bench 4.0, CursorBench, prices). Older Claude generations (served through other CLIs) come first.
        ("fable",  95, [ 96, 96, 96, 95, 94, 94, 96,  0, 95, 96, 96, 94, 95, 94, 97, 95, 97, 94, 94, 93]),
        ("opus-4",  55, [ 89, 89, 87, 86, 89, 89, 89,  0, 88, 89, 89, 87, 87, 87, 89, 86, 87, 86, 85, 87]),
        ("opus",  50, [ 95, 95, 94, 92, 93, 93, 95,  0, 94, 95, 95, 93, 93, 93, 95, 92, 93, 92, 91, 93]),
        ("sonnet-4",  25, [ 85, 83, 81, 83, 85, 85, 81,  0, 86, 83, 84, 84, 81, 81, 81, 82, 83, 83, 81, 84]),
        ("sonnet",  25, [ 93, 91, 89, 90, 91, 91, 89,  0, 93, 91, 92, 92, 88, 88, 88, 89, 90, 90, 88, 92]),
        ("haiku-4",   6, [ 78, 74, 72, 78, 85, 85, 70,  0, 78, 72, 75, 80, 68, 68, 66, 72, 76, 82, 78, 78]),
        ("haiku",   4, [ 79, 75, 73, 80, 88, 88, 70,  0, 78, 75, 77, 84, 68, 68, 66, 72, 78, 86, 82, 82]),
        ("glm",            8, [ 70, 68, 66, 70, 78, 78, 64,  0, 68, 64, 66, 72, 60, 60, 60, 64, 68, 74,  0, 70]),
        ("gemini-4-carbon",   42, [ 90, 92, 91, 88, 91, 91, 93,  0, 88, 92, 89, 89, 92, 91, 93, 89, 87, 90,  0, 88]),
        ("gemini-4-neon",     35, [ 92, 90, 90, 88, 90, 90, 90,  0, 91, 91, 91, 90, 90, 90, 90, 89, 87, 89,  0, 90]),
        ("gemini-4-argon",    22, [ 85, 85, 86, 84, 88, 88, 85,  0, 84, 85, 85, 86, 84, 84, 85, 83, 82, 86,  0, 85]),
        ("gemini-4-hydrogen", 10, [ 81, 79, 79, 80, 86, 86, 78,  0, 80, 79, 80, 83, 77, 77, 77, 78, 78, 84,  0, 82]),
        ("nemotron-3-ultra",   2, [ 83, 82, 81, 79, 84, 84, 82,  0, 80, 82, 82, 82, 81, 80, 82, 79, 77, 81,  0, 81]),
        ("qwen4-max",          2, [ 82, 81, 80, 80, 84, 84, 80,  0, 81, 81, 81, 81, 79, 79, 80, 79, 78, 82,  0, 80]),
        ("kilo-auto/free",     2, [ 80, 78, 77, 77, 80, 80, 77,  0, 78, 78, 79, 79, 76, 76, 77, 76, 75, 78,  0, 78]),
        ("gemma-4-31b",        2, [ 78, 78, 76, 77, 80, 80, 75,  0, 77, 76, 77, 78, 74, 74, 75, 75, 75, 78,  0, 76]),
        ("swe-2-flash",        2, [ 77, 74, 72, 70, 74, 74, 72,  0, 74, 76, 76, 77, 72, 72, 72, 70, 68, 70,  0, 76]),
        ("flash",          8, [ 70, 68, 66, 70, 78, 78, 64,  0, 68, 64, 66, 72, 60, 60, 60, 64, 68, 74,  0, 70]),
        // Gemini models other than Flash (rough estimate: mid tier). Replace through the ratings file.
        ("gemini",        40, [ 88, 87, 88, 86, 90, 90, 87,  0, 87, 86, 87, 88, 86, 86, 86, 85, 84, 89, 88, 87]),
        ("mini",          10, [ 74, 70, 68, 72, 80, 80, 66, 70, 70, 68, 70, 76, 64, 64, 62, 66, 68, 78, 72, 74]),
        ("nano",           4, [ 62, 58, 55, 62, 72, 72, 52,  0, 58, 55, 58, 66, 50, 50, 48, 54, 58, 70,  0, 62])
    ];
// Ratings loaded from a ratings file or URL take precedence over the built-in table.
// Replaced as a whole, so readers on any thread see rows and source from the same load.
private sealed record LoadedRatings((string Pattern, int Cost, int[] Quality, string? Deprecation)[] Rows, string Source, Dictionary<WorkCapability, PriorityRung[]>? Priority = null);
private static LoadedRatings loaded = new([], "");

    // Which models to try first for each work type, in the user's order. The router takes the first
    // one that is usable and good enough for the step's difficulty; models that are not in the
    // catalog, blocked, disabled or out of usage are skipped. Claude and Codex first; names:
    // haiku/sonnet/opus = Claude, astra/sol/terra/luna = Codex (GPT-6.1 first, then GPT-6).
    // "@69" = only while the step's difficulty is 69 or less.
    private static readonly Dictionary<WorkCapability, string[]> DefaultPriority = new()
    {
        [WorkCapability.Coding] = [
            "kilo/kilo/nvidia/nemotron-3-ultra-550b-a55b:free@35", "opencode/opencode/qwen4-max-free@35", "cline/nvidia/nemotron-3-ultra-550b-a55b:free@35", "kilo/kilo/kilo-auto/free@35", "opencode/opencode/deepseek-v4.1-flash-free@35", "devin/swe-2-flash-medium@35",
            ..Ladder("haiku", "sonnet", "sol", "opus", "astra"),
            "claude/claude-haiku-5-5@35",
            "antigravity/gemini-4-hydrogen-medium@35", "antigravity/gemini-4-neon-medium", "antigravity/claude-sonnet-5-5-medium", "antigravity/gemini-4-carbon-medium", "antigravity/claude-sonnet-4-6", "antigravity/gemini-3.8-flash-medium",
            "kilo/kilo/nvidia/nemotron-3-ultra-550b-a55b:free", "opencode/opencode/qwen4-max-free", "cline/nvidia/nemotron-3-ultra-550b-a55b:free", "kilo/kilo/kilo-auto/free", "opencode/opencode/deepseek-v4.1-flash-free", "cline/google/gemma-4-31b-it:free", "devin/swe-2-flash-medium", "qoder/GLM-5.3"],
        [WorkCapability.Debugging] = [..Ladder("sonnet@69", "opus", "astra", "sol"),
            "antigravity/gemini-4-neon-medium@69", "antigravity/gemini-4-carbon-medium", "antigravity/claude-opus-5-5-medium", "antigravity/claude-opus-4-6-thinking", "antigravity/gemini-3.8-flash-medium",
            "kilo/kilo/nvidia/nemotron-3-ultra-550b-a55b:free", "opencode/opencode/qwen4-max-free", "cline/nvidia/nemotron-3-ultra-550b-a55b:free", "devin/swe-2-flash-medium", "qoder/GLM-5.3"],
        [WorkCapability.Refactoring] = [
            "opencode/opencode/qwen4-max-free@35", "kilo/kilo/nvidia/nemotron-3-ultra-550b-a55b:free@35", "cline/recursive/flint-v1.0:free@35", "devin/swe-2-flash-medium@35",
            ..Ladder("haiku", "sonnet", "sol", "opus"),
            "claude/claude-haiku-5-5@35",
            "antigravity/gemini-4-hydrogen-medium@35", "antigravity/gemini-4-neon-medium", "antigravity/gemini-4-carbon-medium", "antigravity/claude-sonnet-5-5-medium", "antigravity/claude-sonnet-4-6", "antigravity/gemini-3.8-flash-medium",
            "opencode/opencode/qwen4-max-free", "kilo/kilo/nvidia/nemotron-3-ultra-550b-a55b:free", "cline/recursive/flint-v1.0:free", "devin/swe-2-flash-medium"],
        [WorkCapability.Testing] = [
            "opencode/opencode/deepseek-v4.1-flash-free@35", "kilo/kilo/kilo-auto/free@35", "cline/inclusionai/ling-3.2-flash:free@35", "devin/swe-2-flash-medium@35",
            ..Ladder("haiku", "terra", "sonnet", "sol", "opus"),
            "claude/claude-haiku-5-5@35",
            "antigravity/gemini-4-argon-medium", "antigravity/gemini-4-hydrogen-medium", "antigravity/gemini-3.8-flash-medium",
            "opencode/opencode/deepseek-v4.1-flash-free", "kilo/kilo/kilo-auto/free", "cline/inclusionai/ling-3.2-flash:free", "devin/swe-2-flash-medium"],
        [WorkCapability.BuildDevOps] = [
            "opencode/opencode/deepseek-v4.1-flash-free@35", "kilo/kilo/kilo-auto/free@35", "cline/inclusionai/ling-3.2-flash:free@35", "devin/swe-2-flash-medium@35",
            ..Ladder("haiku", "terra", "sonnet", "sol", "opus"),
            "claude/claude-haiku-5-5@35",
            "antigravity/gemini-4-argon-medium", "antigravity/gemini-4-hydrogen-medium", "antigravity/gemini-3.8-flash-medium",
            "opencode/opencode/deepseek-v4.1-flash-free", "kilo/kilo/kilo-auto/free", "cline/inclusionai/ling-3.2-flash:free", "devin/swe-2-flash-medium"],
        [WorkCapability.UiDesign] = [..Ladder("sonnet", "opus", "sol", "astra"),
            "antigravity/gemini-4-neon-medium", "antigravity/claude-sonnet-5-5-medium", "antigravity/claude-sonnet-4-6", "antigravity/gemini-3.8-flash-medium",
            "opencode/opencode/qwen4-max-free", "kilo/kilo/nvidia/nemotron-3-ultra-550b-a55b:free", "cline/google/gemma-4-31b-it:free"],
        [WorkCapability.GraphicsMath] = [..Ladder("sonnet@69", "opus", "astra", "sol"),
            "antigravity/gemini-4-neon-medium@69", "antigravity/gemini-4-carbon-medium", "antigravity/claude-opus-5-5-medium", "antigravity/claude-opus-4-6-thinking",
            "kilo/kilo/nvidia/nemotron-3-ultra-550b-a55b:free", "opencode/opencode/qwen4-max-free"],
        [WorkCapability.Performance] = [..Ladder("sonnet@69", "opus", "astra", "sol"),
            "antigravity/gemini-4-neon-medium@69", "antigravity/gemini-4-carbon-medium", "antigravity/claude-opus-5-5-medium", "antigravity/claude-opus-4-6-thinking",
            "kilo/kilo/nvidia/nemotron-3-ultra-550b-a55b:free", "opencode/opencode/qwen4-max-free"],
        [WorkCapability.Architecture] = [..Ladder("sonnet@69", "opus", "astra"),
            "antigravity/gemini-4-neon-medium@69", "antigravity/gemini-4-carbon-medium", "antigravity/claude-opus-5-5-medium", "antigravity/claude-opus-4-6-thinking", "antigravity/gemini-3.8-flash-medium",
            "kilo/kilo/nvidia/nemotron-3-ultra-550b-a55b:free", "opencode/opencode/qwen4-max-free", "cline/nvidia/nemotron-3-ultra-550b-a55b:free"],
        [WorkCapability.Planning] = [..Ladder("opus", "sonnet", "astra"),
            "antigravity/gemini-4-carbon-medium", "antigravity/gemini-4-neon-medium", "antigravity/claude-opus-5-5-medium", "antigravity/claude-opus-4-6-thinking", "antigravity/gemini-3.8-flash-medium",
            "responses-api/gpt-6.2-astra", "kilo/kilo/nvidia/nemotron-3-ultra-550b-a55b:free", "opencode/opencode/qwen4-max-free", "cline/nvidia/nemotron-3-ultra-550b-a55b:free"],
        [WorkCapability.Review] = [..Ladder("haiku", "terra", "sonnet", "sol", "opus", "astra"),
            "claude/claude-haiku-5-5@35",
            "antigravity/gemini-4-carbon-medium", "antigravity/claude-sonnet-5-5-medium", "antigravity/claude-sonnet-4-6", "antigravity/gemini-3.8-flash-medium",
            "kilo/kilo/nvidia/nemotron-3-ultra-550b-a55b:free", "opencode/opencode/qwen4-max-free", "cline/google/gemma-4-31b-it:free", "clixad/gpt-6-luna"],
        [WorkCapability.Research] = [..Ladder("sonnet", "opus", "sol"),
            "antigravity/gemini-4-carbon-medium", "antigravity/gemini-4-neon-medium", "antigravity/gemini-3.8-flash-medium",
            "kilo/kilo/nvidia/nemotron-3-ultra-550b-a55b:free", "opencode/opencode/qwen4-max-free", "clixad/gemini-3.8-flash"],
        [WorkCapability.Content] = [..Ladder("haiku", "sonnet", "sol", "opus"),
            "claude/claude-haiku-5-5@35",
            "antigravity/gemini-4-neon-medium", "antigravity/gemini-3.8-flash-medium",
            "responses-api/claude-sonnet-5", "qoder/GLM-5.3", "kilo/kilo/inclusionai/ling-3.2-flash:free", "opencode/opencode/deepseek-v4.1-flash-free"],
        [WorkCapability.CreativeWriting] = [..Ladder("sonnet", "opus", "sol"),
            "antigravity/gemini-4-neon-medium", "antigravity/claude-sonnet-5-5-medium",
            "responses-api/claude-sonnet-5", "qoder/GLM-5.3"],
        [WorkCapability.GameDesign] = [..Ladder("sonnet", "opus", "sol", "astra"),
            "antigravity/gemini-4-neon-medium", "antigravity/claude-sonnet-5-5-medium",
            "kilo/kilo/nvidia/nemotron-3-ultra-550b-a55b:free", "opencode/opencode/qwen4-max-free"],
        [WorkCapability.Translation] = [..Ladder("haiku", "terra", "sonnet", "opus"),
            "claude/claude-haiku-5-5",
            "antigravity/gemini-3.8-flash-medium",
            "responses-api/gpt-6-luna", "clixad/deepseek-v4.1-flash", "qoder/DeepSeek-Flash", "ollama/qwen3.5:4b"],
        [WorkCapability.Classification] = [..Ladder("haiku", "luna", "terra", "sonnet"),
            "claude/claude-haiku-5-5",
            "antigravity/gemini-3.8-flash-low",
            "ollama/qwen3.5:4b", "responses-api/gpt-6-luna", "clixad/gemini-3.5-flash-lite", "qoder/DeepSeek-Flash"],
        [WorkCapability.Extraction] = [..Ladder("haiku", "luna", "terra", "sonnet"),
            "claude/claude-haiku-5-5",
            "antigravity/gemini-3.8-flash-low",
            "ollama/qwen3.5:4b", "responses-api/gpt-6-luna", "clixad/gemini-3.5-flash-lite", "qoder/DeepSeek-Flash"],
        [WorkCapability.ImageGeneration] = Ladder("terra", "sol", "astra"),
        [WorkCapability.ImageUnderstanding] = Ladder("terra", "sol", "astra")
    };

    private static string[] Ladder(params string[] names) => [..names.SelectMany(entry =>
    {
        var parts = entry.Split('@');
        var cap = parts.Length > 1 ? "@" + parts[1] : "";
        return parts[0] switch
        {
            "haiku" or "sonnet" or "opus" => new[] { $"claude/{parts[0]}{cap}" },
            "astra" or "sol" or "terra" or "luna" => new[] { $"codex/gpt-6.1-{parts[0]}{cap}", $"codex/gpt-6-{parts[0]}{cap}" },
            _ => throw new ArgumentException("Unknown model name " + entry)};
    })];

    private static readonly Dictionary<WorkCapability, PriorityRung[]> DefaultRungs = DefaultPriority.ToDictionary(pair => pair.Key, pair => pair.Value.Select(text => ParseRung(text)!).ToArray());

    // "claude/sonnet", "claude/sonnet@69", "opencode/opencode/gpt-5" (the model keeps its own slashes).
    public static PriorityRung? ParseRung(string? text)
    {
        var value = (text ?? "").Trim();
        var maxDifficulty = 100;
        var at = value.LastIndexOf('@');
        if (at > 0)
        {
            if (!int.TryParse(value[(at + 1)..], out maxDifficulty) || maxDifficulty is < 0 or > 100)
                return null;
            value = value[..at];
        }

        var slash = value.IndexOf('/');
        if (slash < 1 || slash == value.Length - 1)
            return null;
        var provider = value[..slash].ToLowerInvariant();
        var model = value[(slash + 1)..].Trim();
        return System.Text.RegularExpressions.Regex.IsMatch(provider, @"^[a-z0-9-]{2,40}$") && model.Length is > 0 and <= 120 ? new(provider, model, maxDifficulty) : null;
    }

    // The model order for a work type: from the loaded ratings file when it lists the type, else built in.
    public static IReadOnlyList<PriorityRung> PriorityFor(WorkCapability capability)
    {
        if (Current.Priority is { } custom && custom.TryGetValue(capability, out var rungs))
            return rungs;
        return DefaultRungs.TryGetValue(capability, out var builtIn) ? builtIn : [];
    }

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

    private static (int Cost, int[] Quality, string Source, string? Deprecation)? Find(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return null;
        var id = modelId.ToLowerInvariant();
        var current = Current;
        foreach (var (pattern, cost, quality, deprecation) in current.Rows)
            if (Matches(id, pattern))
                return (cost, quality, current.Source, deprecation);
        foreach (var(pattern, cost, quality)in Table)
            if (Matches(id, pattern))
                return (cost, quality, Benchmarks, null);
        return null;
    }

    // Replaces the loaded ratings (null clears them). Each entry may rate only some capabilities;
    // the rest come from the built-in row for the same pattern, or the entry's own average.
    public static string Apply(RatingSet? set)
    {
        if (set == null || set.Models.Length == 0 && (set.Priority == null || set.Priority.Count == 0))
        {
            Volatile.Write(ref loaded, new([], ""));
            return Benchmarks;
        }

        var rows = new List<(string, int, int[], string?)>();
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
            rows.Add((pattern, entry.Cost, Enum.GetValues<WorkCapability>().Select(c => given.TryGetValue(c, out var v) ? v : builtIn?[(int)c] ?? fallback).ToArray(), entry.Deprecation));
        }

        var priority = new Dictionary<WorkCapability, PriorityRung[]>();
        foreach (var (name, entries) in set.Priority ?? [])
        {
            if (!Enum.TryParse<WorkCapability>(name, true, out var capability) || !Enum.IsDefined(capability))
                throw new InvalidOperationException($"Unknown work type '{name}' in the priority order.");
            priority[capability] = [..(entries ?? []).Select(text => ParseRung(text) ?? throw new InvalidOperationException($"Invalid priority entry '{text}' for {name}; use provider/model or provider/model@maxDifficulty."))];
        }

        Volatile.Write(ref loaded, new([..rows.OrderByDescending(row => row.Item1.Length)], $"Ratings from {(string.IsNullOrWhiteSpace(set.Source) ? "file" : set.Source)}, {set.Updated:yyyy-MM-dd}", priority));
        return ActiveSource;
    }

    // The built-in table and priority order in the ratings-file format, as a starting point for editing.
    public static RatingSet BuiltIn() => new("built-in", DateTimeOffset.UtcNow, [..Table.Select(row => new RatingEntry(row.Pattern, row.Cost, Enum.GetValues<WorkCapability>().ToDictionary(c => c.ToString(), c => row.Quality[(int)c]), null))], DefaultPriority.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value));
    // Overall rating shown in the model list: average over the seven base text capabilities.
    public static ModelRating Effective(ModelInfo model)
    {
        if (model.RatedByUser)
            return new(model.Quality, model.CostRank, "User rating");
        if (Find(model.Id)is { } hit)
        {
            var source = hit.Source;
            if (!string.IsNullOrWhiteSpace(hit.Deprecation))
                source += " · " + hit.Deprecation;
            return new((int)Math.Round(hit.Quality.Take(7).Average()), hit.Cost, source);
        }
        return new(model.Quality, model.CostRank, string.IsNullOrWhiteSpace(model.Id) ? "CLI default; exact model unknown" : "Unrated (catalog default)");
    }

    public static string? DeprecationFor(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return null;
        var hit = Find(modelId);
        return hit?.Deprecation;
    }

    public static string DescribeCapabilities(string? providerId, ModelInfo model)
    {
        var caps = Enum.GetValues<WorkCapability>();
        var lines = new List<string>();
        foreach (var cap in caps)
        {
            var q = QualityFor(providerId ?? "", model, cap);
            lines.Add($"{WorkCapabilities.Display(cap)} {q}");
        }
        var rating = Effective(model);
        lines.Add($"Usage rank: {rating.CostRank}");
        lines.Add($"RatingSource: {rating.Source}");
        var deprecation = DeprecationFor(model.Id);
        if (!string.IsNullOrWhiteSpace(deprecation))
            lines.Add($"Deprecation: {deprecation}");
        return string.Join(" · ", lines);
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
