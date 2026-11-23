using System.Text.Json.Nodes;
using Employee.Core;
using System.Text.RegularExpressions;

namespace Employee.Infrastructure;

// Turns whatever a planner or preparation step wrote into a request the Pixel Art Tool accepts.
// The tool rejects a request for any missing or out-of-range field (see validateInput in the
// tool's prompts.mjs); here every field is completed with the tool's own default, a near valid
// value, or what the step's request implies, and each correction is reported. The image itself is
// chosen earlier by ToolTaskExecutor; a request that still has no usable image is the one case
// that cannot be completed.
internal static class PixelRequest
{
    private static readonly string[] ImageActions = ["create", "edit", "animation", "direction", "skin"];
    // Done by the tool without a model call; they need an image but no description.
    private static readonly string[] OfflineActions = ["recolor", "hue", "procedural"];
    private static readonly Dictionary<string, int> Words = new() { ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["sixteen"] = 16 };
    // "8 frames", "8-frame", "four-frame", "4 khung hinh" (normalized text: lower case, no accents).
    private static readonly Regex FrameCount = new(@"\b(?<n>[2-9]|[1-6][0-9]|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|sixteen)[ -](?:frames?\b|khung hinh)", RegexOptions.CultureInvariant);

    // The size of one frame named in a brief: "each frame is exactly 64x64", "64×64 per frame",
    // "8 frames of 32x32", "64x64 cells", "khung 64x64". A sheet size ("256x64 sheet") is not one.
    internal static (int Width, int Height)? FrameSize(string brief)
    {
        var text = TaskIntent.Normalize(brief.Replace('×', 'x'));
        var m = Regex.Match(text, @"\b(?:each|every|per|one|moi)\s+(?:frame|cell|khung(?: hinh)?)\s+(?:is\s+|la\s+)?(?:exactly\s+|dung\s+)?(?<w>[0-9]{1,4})\s*x\s*(?<h>[0-9]{1,4})\b");
        if (!m.Success)
            m = Regex.Match(text, @"\b(?<w>[0-9]{1,4})\s*x\s*(?<h>[0-9]{1,4})\s*(?:pixels?\s+|px\s+)?(?:per frame|each|cells?\b|frames?\b)");
        if (!m.Success)
            m = Regex.Match(text, @"\bframes? (?:of|at) (?<w>[0-9]{1,4})\s*x\s*(?<h>[0-9]{1,4})\b|\bkhung(?: hinh)? (?<w>[0-9]{1,4})\s*x\s*(?<h>[0-9]{1,4})\b");
        return m.Success ? (int.Parse(m.Groups["w"].Value), int.Parse(m.Groups["h"].Value)) : null;
    }

    public sealed record FrameSplit(JsonObject Base, IReadOnlyList<JsonObject> Motions);

    // A create whose prompt asks for frames and has no image to animate cannot be one call: the tool
    // returns one still. Split it into the base pose (this call) and one animation call per motion
    // named in the prompt ("four-frame idle … four-frame peck" → idle, peck), each animating the base.
    // Null when the request is not such a case.
    public static FrameSplit? SplitFrames(JsonObject input, string baseKey)
    {
        var action = Text(input["action"])?.Trim().ToLowerInvariant();
        if (action is not (null or "create" or "generate" or "new" or "draw") || input["image"] != null || input["styleGuide"] != null)
            return null;
        var prompt = Text(input["prompt"])?.Trim() ?? "";
        var text = TaskIntent.Normalize(prompt);
        if (!FrameCount.IsMatch(text) || Regex.IsMatch(text, @"\b(base|single|still|reference|static)\b|\bone pose\b"))
            return null;
        var cell = Regex.Match(text, @"(?<w>[0-9]{1,4})\s*x\s*(?<h>[0-9]{1,4})\s*(?:pixels?\s+|px\s+)?(?:cells?|frames?|khung)\b");
        void Size(JsonObject call)
        {
            if (!cell.Success) return;
            call["outputWidth"] = int.Parse(cell.Groups["w"].Value);
            call["outputHeight"] = int.Parse(cell.Groups["h"].Value);
        }
        var still = input.DeepClone().AsObject();
        still["action"] = "create";
        still.Remove("frames");
        still["prompt"] = "ONE still image only (no frames, no grid, no sprite sheet): the base pose of the subject below, centered with transparent padding, to be animated afterwards.\n" + prompt;
        Size(still);
        var motions = new List<JsonObject>();
        foreach (Match m in new Regex(FrameCount + @"\s+(?<what>(?:[\p{L}\p{N}]+\s+){0,3}[\p{L}\p{N}]+)").Matches(text))
        {
            if (motions.Count == 4) break;
            var what = m.Groups["what"].Value;
            var motion = new JsonObject
            {
                ["action"] = "animation",
                ["image"] = new JsonObject { ["$imageFrom"] = baseKey },
                ["frames"] = Words.TryGetValue(m.Groups["n"].Value, out var n) ? n : int.Parse(m.Groups["n"].Value),
                ["prompt"] = $"Animate ONLY this motion of the base pose: {m.Value}. Keep identity, camera, scale and baseline. Full brief for context:\n{prompt}"
            };
            foreach (var key in new[] { "fps", "direction", "artMode", "lockPalette", "preserveBackground" })
                if (input[key] != null) motion[key] = input[key]!.DeepClone();
            Size(motion);
            if (!motions.Any(other => other["prompt"]!.GetValue<string>().Contains(what, StringComparison.Ordinal) && other["frames"]!.GetValue<int>() == motion["frames"]!.GetValue<int>()))
                motions.Add(motion);
        }
        return motions.Count == 0 ? null : new(still, motions);
    }

    public static IReadOnlyList<string> Complete(JsonObject input, PixelToolOptions options, string stepRequest)
    {
        var notes = new List<string>();
        void Note(string text) => notes.Add(text);

        foreach (var key in input.Where(p => p.Value == null).Select(p => p.Key).ToArray())
            input.Remove(key);
        Rename(input, "size", "outputWidth", notes);
        foreach (var (alias, field) in new[] { ("width", "outputWidth"), ("height", "outputHeight"), ("frameWidth", "outputWidth"), ("frameHeight", "outputHeight"), ("frameCount", "frames"), ("frameRate", "fps"), ("mode", "artMode"), ("facing", "direction"), ("description", "prompt") })
            Rename(input, alias, field, notes);
        foreach (var alias in new[] { "referenceImage", "styleReference", "styleImage", "reference", "sample", "styleSample" })
            Rename(input, alias, "styleGuide", notes);
        if (input["outputWidth"] is JsonValue sizeText && sizeText.TryGetValue<string>(out var size) && Regex.Match(size, @"^\s*(\d{1,4})\s*[xX×]\s*(\d{1,4})\s*$") is { Success: true } wh)
        {
            input["outputWidth"] = int.Parse(wh.Groups[1].Value);
            input["outputHeight"] = int.Parse(wh.Groups[2].Value);
        }

        // Action: tool names, then common words, then what the request and inputs imply.
        var action = Text(input["action"])?.Trim().ToLowerInvariant();
        action = action switch
        {
            "animate" or "anim" or "animation" or "sprite_animation" => "animation",
            "generate" or "new" or "create" or "draw" => "create",
            "edit" or "redraw" or "restyle" or "modify" or "fix" => "edit",
            "reskin" or "skin" => "skin",
            "direction" or "turn" or "rotate" => "direction",
            "recolor" or "recolour" or "replace_color" or "replacecolor" => "recolor",
            "hue" or "hue_shift" or "hueshift" => "hue",
            "procedural" or "offline_motion" or "simple_motion" => "procedural",
            _ => action
        };
        if (action is "recolor" or "hue" or "procedural")
        {
            if (!(options.Actions ?? []).Contains(action))
                throw new InvalidOperationException($"This Pixel Art Tool version has no '{action}' action; update the tool.");
            return CompleteOffline(input, action, options, notes);
        }
        if (action is "detect" or "refine")
            throw new InvalidOperationException($"Pixel action '{action}' returns text, not an image; plan it as a normal AI step.");
        if (action == null || !ImageActions.Contains(action))
        {
            var text = TaskIntent.Normalize(stepRequest);
            var inferred = input["image"] == null && input["styleGuide"] == null ? "create"
                : input["frames"] != null || Regex.IsMatch(text, @"\b(anim|animation|frame|frames|khung hinh)\b") ? "animation"
                : Regex.IsMatch(text, @"\b(skin|reskin|doi mau|recolor)\b") ? "skin"
                : Regex.IsMatch(text, @"\b(direction|huong|xoay)\b") ? "direction"
                : "edit";
            Note($"action '{action ?? "missing"}' → {inferred}");
            action = inferred;
        }
        // create is a still image; a multi-frame brief needs the animation operation. With a source
        // image the step is turned into an animation of it. Without one it is refused: the tool would
        // return a single pose (as it did for "a four-frame idle" in a create prompt) and the step
        // would look done. A base pose "for the 8-frame walk" is a still on purpose and passes.
        var frameBrief = TaskIntent.Normalize((Text(input["prompt"]) ?? "") + " " + stepRequest);
        var briefText = TaskIntent.Normalize(Text(input["prompt"]) ?? "");
        var briefFrames = FrameCount.Match(frameBrief);
        var asksFrames = briefFrames.Success || Regex.IsMatch(frameBrief, @"\banimation frames?\b|khung hinh");
        if (action == "create" && asksFrames && input["image"] == null && input["styleGuide"] == null && FrameCount.IsMatch(briefText)
            && !Regex.IsMatch(briefText, @"\b(base|single|still|reference|static)\b|\bone pose\b"))
            throw new InvalidOperationException("Pixel 'create' makes one still image, but this request asks for animation frames. Plan a create step for the base pose, then an 'animation' step that uses it ({\"$imageFrom\":\"<create step key>\"}) with frames and fps; one animation step per motion.");
        if (action == "create" && asksFrames && input["image"] != null)
        {
            action = "animation";
            var cell = Regex.Match(frameBrief, @"(?<w>[0-9]{1,4})\s*[x×]\s*(?<h>[0-9]{1,4})\s*(?:pixels?\s+|px\s+)?(?:cells?|frames?|khung)\b");
            input["outputWidth"] = cell.Success ? int.Parse(cell.Groups["w"].Value) : options.Defaults.OutputWidth;
            input["outputHeight"] = cell.Success ? int.Parse(cell.Groups["h"].Value) : options.Defaults.OutputHeight;
            if (briefFrames.Success && input["frames"] == null) input["frames"] = Words.TryGetValue(briefFrames.Groups["n"].Value, out var n) ? n : int.Parse(briefFrames.Groups["n"].Value);
            Note("create animation brief corrected to animation; dimensions are per frame, not full sheet");
        }
        input["action"] = action;

        // Art mode and direction: the tool's own lists and defaults.
        var mode = Text(input["artMode"])?.Trim().ToLowerInvariant();
        if (mode == "auto" && options.AutoArtMode)
            mode = input["image"] == null ? options.Defaults.ArtMode : "auto";
        else if (mode == null || !options.ArtModes.ContainsKey(mode))
        {
            if (mode != null) Note($"artMode '{mode}' → {options.Defaults.ArtMode}");
            mode = options.Defaults.ArtMode;
        }
        input["artMode"] = mode;
        var direction = Compass(Text(input["direction"]));
        if (direction == null || !options.Directions.Contains(direction))
        {
            if (direction != null) Note($"direction '{direction}' → {options.Defaults.Direction}");
            direction = options.Defaults.Direction;
        }
        input["direction"] = direction;

        // Prompt: required for every image action; the step's own request is the brief.
        var prompt = Text(input["prompt"])?.Trim();
        if (string.IsNullOrEmpty(prompt))
        {
            prompt = stepRequest.Trim();
            Note("prompt taken from the step request");
        }
        input["prompt"] = prompt.Length > 4000 ? prompt[..4000] : prompt;

        // Frames are checked for every action; fps for animations.
        input["frames"] = Nearest(Number(input["frames"]) ?? options.Defaults.Frames, options.Frames, "frames", notes);
        input["fps"] = Clamp(Number(input["fps"]) ?? options.Defaults.Fps, options.Fps.Min, options.Fps.Max, "fps", notes);

        // A create request has no source image; a style sample is passed as its reference image.
        if (action == "create" && input["image"] == null && input["styleGuide"] != null)
        {
            input["image"] = input["styleGuide"]!.DeepClone();
            input.Remove("styleGuide");
            Note("style sample used as the create reference image");
        }
        if (action != "create" && input["image"] == null)
            throw new InvalidOperationException($"Pixel '{action}' needs a source PNG and none could be found: give the step an image, attach one PNG to the task, or make it depend on the step that generated one.");

        // For an animation the size is PER FRAME. Briefs often give the whole sheet ("each frame is
        // exactly 64x64; one horizontal 256x64 sheet" with outputWidth 256): the tool then made four
        // 256x64 frames. A frame size stated in the brief wins.
        if (action == "animation" && FrameSize(Text(input["prompt"]) + " " + stepRequest) is { } frame
            && (Number(input["outputWidth"]) != frame.Width || Number(input["outputHeight"]) != frame.Height))
        {
            Note($"output size {Number(input["outputWidth"])?.ToString() ?? "?"}x{Number(input["outputHeight"])?.ToString() ?? "?"} → {frame.Width}x{frame.Height}: animation sizes are per frame, as the brief states");
            input["outputWidth"] = frame.Width;
            input["outputHeight"] = frame.Height;
        }

        // Output size: required for create, and the frame size the tool finishes frames to.
        var canvasMax = options.ArtModes.TryGetValue(mode, out var limit) ? limit : options.ArtModes.Values.Max();
        var source = PixelToolOptions.PngSize(input["image"]);
        var defaultWidth = action == "create" || source == null ? options.Defaults.OutputWidth : Math.Clamp(source.Value.Width, options.CanvasMin, canvasMax);
        var defaultHeight = action == "create" || source == null ? options.Defaults.OutputHeight : Math.Clamp(source.Value.Height, options.CanvasMin, canvasMax);
        input["outputWidth"] = Clamp(Number(input["outputWidth"]) ?? defaultWidth, options.CanvasMin, canvasMax, "outputWidth", notes);
        input["outputHeight"] = Clamp(Number(input["outputHeight"]) ?? defaultHeight, options.CanvasMin, canvasMax, "outputHeight", notes);

        // Style switches: booleans the tool insists on; sensible defaults per action.
        var hasStyleGuide = input["styleGuide"] != null;
        var preserve = Bool(input["preserveStyle"]) ?? (action == "animation" || (hasStyleGuide && action != "create") || (action == "create" && input["image"] != null));
        if (action == "create" && input["image"] == null) preserve = false;
        input["preserveStyle"] = preserve;
        // Restyling after a sample takes the sample's colours; otherwise pixel art keeps its palette.
        input["lockPalette"] = Bool(input["lockPalette"]) ?? (mode == "pixel" && !(hasStyleGuide && action == "edit"));
        // Auto-fix: the tool checks its result and may generate once more to fix what it finds (on by default).
        input["autoRetry"] = Bool(input["autoRetry"]) ?? true;
        foreach (var flag in new[] { "lockScale", "keepSilhouette", "geminiRemoveBackground", "strictPalette" })
            if (input[flag] != null)
                input[flag] = Bool(input[flag]) ?? true;
        CompleteCommon(input, options, notes);
        if (input["preserveBackground"] != null)
            input["preserveBackground"] = Bool(input["preserveBackground"]) ?? false;
        // First frame keeps the source pixels unless asked otherwise (the editor's default).
        if (action == "animation")
            input["anchorFirstFrame"] = Bool(input["anchorFirstFrame"]) ?? true;
        else
            input.Remove("anchorFirstFrame");

        // Reference role and contexts are only legal for their own action.
        var role = Text(input["styleReferenceRole"]);
        if (role != null && !(role == "original" || (role == "skin" && action == "skin") || (role == "edit" && action == "edit")))
        {
            input.Remove("styleReferenceRole");
            Note($"styleReferenceRole '{role}' removed");
        }
        // Editing a whole animation sheet: the tool edits frame by frame and builds each frame's context.
        if (action == "edit" && input["animation"] is JsonObject animation)
        {
            input["animation"] = Grid(animation, notes, "animation");
            input["scope"] = Text(input["scope"]) == "current" ? "current" : "all";
            var frameCount = input["animation"]!["columns"]!.GetValue<int>() * input["animation"]!["rows"]!.GetValue<int>();
            input["frameIndex"] = Math.Clamp(Number(input["frameIndex"]) ?? 0, 0, frameCount - 1);
            if (input["region"] is JsonObject r && !(Number(r["x"]) >= 0 && Number(r["y"]) >= 0 && Number(r["width"]) >= 1 && Number(r["height"]) >= 1))
            {
                input.Remove("region");
                notes.Add("invalid edit region removed");
            }
            input.Remove("editContext");
        }
        else if (action == "edit")
        {
            var image = source ?? (options.Defaults.OutputWidth, options.Defaults.OutputHeight);
            var context = input["editContext"] as JsonObject;
            int Field(string name, int fallback) => Number(context?[name]) ?? fallback;
            var total = Math.Clamp(Field("total", 1), 1, 64);
            var repaired = new JsonObject
            {
                ["index"] = Math.Clamp(Field("index", 0), 0, total - 1),
                ["total"] = total,
                ["width"] = Math.Clamp(Field("width", image.Width), 1, 1024),
                ["height"] = Math.Clamp(Field("height", image.Height), 1, 1024),
                ["fps"] = Math.Clamp(Field("fps", input["fps"]!.GetValue<int>()), 1, 30)
            };
            if (context?["region"] is JsonObject region && Number(region["x"]) is int rx && Number(region["y"]) is int ry && Number(region["width"]) is int rw && Number(region["height"]) is int rh
                && rx >= 0 && ry >= 0 && rw >= 1 && rh >= 1 && rx + rw <= repaired["width"]!.GetValue<int>() && ry + rh <= repaired["height"]!.GetValue<int>())
                repaired["region"] = region.DeepClone();
            if (context == null) Note("editContext filled from the image");
            input["editContext"] = repaired;
        }
        else if (input.Remove("editContext"))
            Note("editContext removed (only for edit)");
        if (action == "skin" && input["sheet"] is JsonObject wholeSheet)
        {
            input["sheet"] = Grid(wholeSheet, notes, "sheet");
            input["keepSilhouette"] = Bool(input["keepSilhouette"]) ?? true;
        }
        else
            input.Remove("sheet");
        if (input["sheetContext"] is JsonObject sheet)
        {
            var valid = action == "skin" && new[] { "columns", "rows", "index", "width", "height" }.All(k => Number(sheet[k]) != null)
                && Number(sheet["columns"]) >= 1 && Number(sheet["rows"]) >= 1 && Number(sheet["columns"]) * Number(sheet["rows"]) <= 64
                && Number(sheet["index"]) >= 0 && Number(sheet["index"]) < Number(sheet["columns"]) * Number(sheet["rows"])
                && Number(sheet["width"]) is >= 1 and <= 512 && Number(sheet["height"]) is >= 1 and <= 512;
            if (!valid)
            {
                input.Remove("sheetContext");
                Note("invalid sheetContext removed");
            }
        }
        else
            input.Remove("sheetContext");
        // The tool derives the style profile from the image; a hand-written one is a likely error.
        if (input.Remove("styleProfile"))
            Note("styleProfile removed (the tool derives it)");
        foreach (var key in new[] { "image", "styleGuide" })
            if (input[key] is { } png && !(Text(png) is { } url && url.StartsWith("data:image/png;base64,", StringComparison.Ordinal)))
            {
                if (key == "image")
                    throw new InvalidOperationException("Pixel source image is not a PNG.");
                input.Remove(key);
                Note("styleGuide is not a PNG and was removed");
            }
            else if (Text(input[key])?.Length > 11 * 1024 * 1024)
                throw new InvalidOperationException($"Pixel {key} exceeds the tool's 8 MB limit.");

        // The tool finishes frames itself (grid, size, palette) exactly as its editor does.
        input["exactFrames"] = true;
        return notes;
    }

    // "SW", "south-west", "southwest", "tây nam", "down-left" → SW.
    private static string? Compass(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = TaskIntent.Normalize(text).Replace("-", " ").Replace("_", " ");
        var compact = t.Replace(" ", "").ToUpperInvariant();
        if (compact is "N" or "NE" or "E" or "SE" or "S" or "SW" or "W" or "NW") return compact;
        // Whole words only, except the English compass words, which also count inside "southwest".
        bool Has(params string[] words) => words.Any(w => Regex.IsMatch(t, $@"(^|\s){w}($|\s)") || (w is "north" or "south" or "east" or "west" && t.Replace(" ", "").Contains(w)));
        var north = Has("north", "bac", "up", "back", "len");
        var south = Has("south", "nam", "down", "front", "xuong");
        var east = Has("east", "dong", "right", "phai");
        var west = Has("west", "tay", "left", "trai");
        var vertical = north && !south ? "N" : south && !north ? "S" : "";
        var horizontal = east && !west ? "E" : west && !east ? "W" : "";
        var result = vertical + horizontal;
        return result.Length == 0 ? text.Trim().ToUpperInvariant() : result;
    }

    // Recolour, hue shift and simple motion: an image and their own few settings.
    private static IReadOnlyList<string> CompleteOffline(JsonObject input, string action, PixelToolOptions options, List<string> notes)
    {
        input["action"] = action;
        if (Text(input["image"]) is not { } image || !image.StartsWith("data:image/png;base64,", StringComparison.Ordinal))
            throw new InvalidOperationException($"Pixel '{action}' needs a source PNG and none could be found.");
        if (action == "recolor")
        {
            foreach (var key in new[] { "fromColor", "toColor" })
                if (Text(input[key]) is not { } colour || !Regex.IsMatch(colour, "^#[0-9a-fA-F]{6}$"))
                    throw new InvalidOperationException("Recolour needs fromColor and toColor as #rrggbb.");
        }
        else if (action == "hue")
            input["degrees"] = Clamp(Number(input["degrees"]) ?? 0, -180, 180, "degrees", notes);
        else
        {
            var motions = options.Procedural ?? ["idle"];
            var motion = Text(input["motion"]);
            if (motion == null || !motions.Contains(motion))
            {
                notes.Add($"motion '{motion ?? "missing"}' → {motions[0]}");
                input["motion"] = motions[0];
            }
            input["frames"] = Nearest(Number(input["frames"]) ?? options.Defaults.Frames, options.Frames, "frames", notes);
        }
        CompleteCommon(input, options, notes);
        input["exactFrames"] = true;
        return notes;
    }

    // Settings every request may carry: export scale and the AI source.
    private static void CompleteCommon(JsonObject input, PixelToolOptions options, List<string> notes)
    {
        var scales = options.ExportScales ?? [1];
        if (input["exportScale"] != null)
            input["exportScale"] = Nearest(Number(input["exportScale"]) ?? 1, scales, "exportScale", notes);
        // Switching to the next AI when one has no usage left is on unless turned off; a paid API only when asked.
        input["autoSwitch"] = Bool(input["autoSwitch"]) ?? true;
        if (input["fallbackToApi"] != null) input["fallbackToApi"] = Bool(input["fallbackToApi"]) == true;
        var provider = Text(input["provider"]);
        if (provider == null) return;
        if (provider is not ("codex" or "claude" or "pixellab" or "api") || options.Provider(provider) == null)
        {
            input.Remove("provider");
            notes.Add($"provider '{provider}' not offered by this tool; Codex CLI is used");
            return;
        }
        if (provider == "api")
        {
            var vendors = options.Provider("api")?.Vendors ?? [];
            var vendor = Text(input["apiVendor"]);
            if (vendor == null || !vendors.ContainsKey(vendor))
            {
                vendor = vendors.Keys.FirstOrDefault() ?? "openai";
                input["apiVendor"] = vendor;
                notes.Add("apiVendor → " + vendor);
            }
            var models = vendors.GetValueOrDefault(vendor)?.ImageModels ?? [];
            if (Text(input["imageModel"]) is not { } model || (models.Length > 0 && !models.Contains(model)))
            {
                if (models.Length > 0) input["imageModel"] = models[0];
                else input.Remove("imageModel");
            }
        }
    }

    private static JsonObject Grid(JsonObject grid, List<string> notes, string name)
    {
        // At most 64 cells: keep the rows and reduce the columns.
        var rows = Math.Clamp(Number(grid["rows"]) ?? 1, 1, 64);
        var columns = Math.Clamp(Number(grid["columns"]) ?? 1, 1, Math.Max(1, 64 / rows));
        if (columns != Number(grid["columns"]) || rows != Number(grid["rows"])) notes.Add($"{name} grid → {columns}x{rows}");
        return new JsonObject { ["columns"] = columns, ["rows"] = rows };
    }

    private static void Rename(JsonObject input, string alias, string field, List<string> notes)
    {
        if (input[alias] is not { } value) return;
        input.Remove(alias);
        if (input[field] != null) return;
        input[field] = value;
        notes.Add($"{alias} → {field}");
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static int? Number(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<int>(out var number)) return number;
        if (value.TryGetValue<double>(out var real)) return (int)Math.Round(real);
        return value.TryGetValue<string>(out var text) && int.TryParse(Regex.Match(text, @"-?\d+").Value, out var parsed) ? parsed : null;
    }

    private static bool? Bool(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<bool>(out var flag)) return flag;
        if (value.TryGetValue<int>(out var number)) return number != 0;
        return Text(node)?.Trim().ToLowerInvariant() switch { "true" or "yes" or "on" or "1" => true, "false" or "no" or "off" or "0" => false, _ => null };
    }

    private static int Clamp(int value, int min, int max, string name, List<string> notes)
    {
        var result = Math.Clamp(value, min, max);
        if (result != value) notes.Add($"{name} {value} → {result}");
        return result;
    }

    private static int Nearest(int value, int[] allowed, string name, List<string> notes)
    {
        var result = allowed.OrderBy(a => Math.Abs(a - value)).ThenByDescending(a => a).First();
        if (result != value) notes.Add($"{name} {value} → {result}");
        return result;
    }
}
