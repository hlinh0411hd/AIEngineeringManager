using System.Text.Json;
using System.Text.Json.Nodes;
using Employee.Core;

namespace Employee.Infrastructure;

// What the Pixel Art Tool offers, for forms: read from the tool's own tool-capabilities.json.
public sealed record PixelToolChoices(IReadOnlyDictionary<string, int> CanvasMax, IReadOnlyList<string> Directions, IReadOnlyList<int> Frames, int FpsMin, int FpsMax, int CanvasMin,
    string DefaultArtMode, string DefaultDirection, int DefaultFrames, int DefaultFps, int DefaultWidth, int DefaultHeight,
    IReadOnlyList<string> Actions, IReadOnlyList<string> Motions, IReadOnlyList<int> ExportScales, bool AutoArtMode, IReadOnlyDictionary<string, IReadOnlyList<string>> ApiImageModels, IReadOnlyList<string> Providers)
{
    public static PixelToolChoices For(ConnectedTool tool)
    {
        var o = PixelToolOptions.Read(tool);
        var api = o.Providers?.GetValueOrDefault("api")?.Vendors?.ToDictionary(v => v.Key, v => (IReadOnlyList<string>)(v.Value.ImageModels ?? [])) ?? new Dictionary<string, IReadOnlyList<string>>();
        return new(o.ArtModes, o.Directions, o.Frames, o.Fps.Min, o.Fps.Max, o.CanvasMin, o.Defaults.ArtMode, o.Defaults.Direction, o.Defaults.Frames, o.Defaults.Fps, o.Defaults.OutputWidth, o.Defaults.OutputHeight,
            o.Actions ?? ["animation", "create", "edit", "direction", "skin"], o.Procedural ?? [], o.ExportScales ?? [1], o.AutoArtMode, api, o.Providers?.Keys.ToArray() ?? ["codex"]);
    }
}

internal sealed record PixelToolOptions(
    int Version, Dictionary<string, int> ArtModes, string[] Directions, int[] Frames,
    PixelToolOptions.Range Fps, int CanvasMin, PixelToolOptions.Values Defaults)
{
    // Optional lists newer tools publish; older capability files simply lack them.
    public string[]? Actions { get; init; }
    public string[]? OfflineActions { get; init; }
    public string[]? Procedural { get; init; }
    public int[]? ExportScales { get; init; }
    public bool AutoArtMode { get; init; }
    public Dictionary<string, ProviderInfo>? Providers { get; init; }
    internal sealed record ProviderInfo(string? Label, Dictionary<string, VendorInfo>? Vendors);
    internal sealed record VendorInfo(string[]? ImageModels);
    internal sealed record Range(int Min, int Max);
    internal sealed record Values(string ArtMode, string Direction, int Frames, int Fps, int OutputWidth, int OutputHeight);

    private static readonly PixelToolOptions Legacy = new(1,
        new() { ["pixel"] = 512, ["illustration"] = 512 },
        ["N", "NE", "E", "SE", "S", "SW", "W", "NW"], [4, 8, 12],
        new(1, 30), 16, new("pixel", "S", 8, 8, 128, 128));

    public static PixelToolOptions Read(ConnectedTool tool)
    {
        var root = string.IsNullOrWhiteSpace(tool.BackendWorkingDirectory)
            ? tool.SourcePath : tool.BackendWorkingDirectory;
        if (!Path.IsPathFullyQualified(root)) return Legacy;
        var file = new FileInfo(Path.Combine(root, "tool-capabilities.json"));
        if (!file.Exists) return Legacy;
        if (file.Length > 16_384 || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Pixel Art capability file is oversized or redirected.");

        var options = JsonSerializer.Deserialize<PixelToolOptions>(File.ReadAllText(file.FullName), JsonSerializerOptions.Web);
        if (options == null || options.Version != 1 || options.CanvasMin is < 1 or > 1024 ||
            options.ArtModes == null || options.ArtModes.Count == 0 || options.ArtModes.Any(mode => mode.Value < options.CanvasMin || mode.Value > 4096) ||
            options.Directions == null || options.Frames == null || options.Fps == null || options.Defaults == null ||
            !options.ArtModes.ContainsKey(options.Defaults.ArtMode) || !options.Directions.Contains(options.Defaults.Direction) ||
            !options.Frames.Contains(options.Defaults.Frames) || options.Frames.Any(frames => frames < 4 || frames > 64 || frames % 4 != 0) ||
            options.Fps.Min < 1 || options.Fps.Max < options.Fps.Min || options.Fps.Max > 120 ||
            options.Defaults.Fps < options.Fps.Min || options.Defaults.Fps > options.Fps.Max)
            throw new InvalidOperationException("Pixel Art published an invalid or unsupported capability file.");
        return options;
    }

    public static void ApplyTextHints(JsonObject input, string request, bool overrideExisting)
    {
        var text = TaskIntent.Normalize(request);
        var action = input["action"]?.GetValue<string>();
        void Set(string key, JsonNode value)
        {
            if (overrideExisting || input[key] == null) input[key] = value;
        }

        int? Number(string pattern)
        {
            var values = System.Text.RegularExpressions.Regex.Matches(text, pattern)
                .Select(match => int.Parse(match.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture))
                .Distinct().Take(2).ToArray();
            return values.Length == 1 ? values[0] : null;
        }

        bool Has(params string[] phrases) =>
            phrases.Any(phrase => (" " + text + " ").Contains(" " + phrase + " ", StringComparison.Ordinal));

        if (action == "animation")
        {
            if (Number(@"\b(?<n>[0-9]{1,4}) (?:frames?|khung hinh)\b|\b(?:so frame|frames|so khung hinh) (?<n>[0-9]{1,4})\b") is int frames)
                Set("frames", JsonValue.Create(frames)!);
            if (Number(@"\b(?<n>[0-9]{1,4}) fps\b|\bfps (?<n>[0-9]{1,4})\b") is int fps)
                Set("fps", JsonValue.Create(fps)!);
        }

        if (action == "create")
        {
            var sizes = System.Text.RegularExpressions.Regex.Matches(request, @"(?<![0-9])(?<w>[0-9]{1,4})\s*[xX×]\s*(?<h>[0-9]{1,4})(?![0-9])")
                .Select(match => (Width: int.Parse(match.Groups["w"].Value, System.Globalization.CultureInfo.InvariantCulture),
                    Height: int.Parse(match.Groups["h"].Value, System.Globalization.CultureInfo.InvariantCulture)))
                .Distinct().Take(2).ToArray();
            if (sizes.Length == 1)
            {
                Set("outputWidth", JsonValue.Create(sizes[0].Width)!);
                Set("outputHeight", JsonValue.Create(sizes[0].Height)!);
            }
        }

        var illustration = Has("net ve 2d", "minh hoa 2d", "ve tay", "hand drawn", "art mode illustration", "artmode illustration");
        var pixel = Has("net pixel", "phong cach pixel art", "dang pixel art", "art mode pixel", "artmode pixel");
        if (illustration != pixel)
        {
            Set("artMode", JsonValue.Create(illustration ? "illustration" : "pixel")!);
            if (illustration) Set("lockPalette", JsonValue.Create(false)!);
        }

        if (Has("nen trong suot", "khong nen", "xoa nen trang", "khong nen trang", "transparent background", "remove white background"))
            Set("preserveBackground", JsonValue.Create(false)!);
        else if (Has("giu nen", "dung nen trang", "tao nen trang", "dat nen trang", "use a white background", "keep the white background", "use an opaque background", "keep background"))
            Set("preserveBackground", JsonValue.Create(true)!);
        if (Has("giu style goc", "giu phong cach goc", "giu nguyen style", "preserve original style"))
            Set("preserveStyle", JsonValue.Create(true)!);

        var directions = System.Text.RegularExpressions.Regex.Matches(text, @"\b(?:huong|facing|direction) (n|ne|e|se|s|sw|w|nw)\b")
            .Select(match => match.Groups[1].Value.ToUpperInvariant()).Distinct().Take(2).ToArray();
        if (directions.Length == 1) Set("direction", JsonValue.Create(directions[0])!);
    }

    // Width and height of a PNG data URL, read from its header.
    internal static (int Width, int Height)? PngSize(JsonNode? image)
    {
        const string prefix = "data:image/png;base64,";
        if (image is not JsonValue value || !value.TryGetValue<string>(out var url) || !url.StartsWith(prefix, StringComparison.Ordinal) || url.Length < prefix.Length + 44)
            return null;
        try
        {
            var header = Convert.FromBase64String(url.Substring(prefix.Length, 44));
            if (!header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                return null;
            return (System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16)), System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20)));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    // Completes a request with the tool's defaults; see PixelRequest.
    public IReadOnlyList<string> Apply(JsonObject input, string stepRequest = "") => PixelRequest.Complete(input, this, stepRequest);
}
