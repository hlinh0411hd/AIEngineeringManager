using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Employee.Desktop;

// One looping clip cut from a sprite sheet: FrameCount cells of a Columns x Rows grid, read
// row-major, played at FramesPerSecond. SheetFile is relative to Assets/VirtualOffice.
public sealed record SpriteClip(string SheetFile, int Columns, int Rows, int FrameCount, double FramesPerSecond);

// Plays one SpriteClip as a looping animation, drawing the current frame itself. It runs its own
// timer while the element is loaded and visible; a missing sheet renders nothing.
public sealed class SpriteAnimation : FrameworkElement
{
    private SpriteClip? clip;
    private BitmapSource?[] frames = [];
    private int frame;
    private readonly DispatcherTimer timer = new();
    private bool playing = true;

    public SpriteAnimation()
    {
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        timer.Tick += (_, _) => Advance();
        IsVisibleChanged += (_, _) => UpdateTimer();
        Loaded += (_, _) => UpdateTimer();
        Unloaded += (_, _) => UpdateTimer();
    }

    public SpriteClip? Source
    {
        get => clip;
        set
        {
            if (clip == value) return;
            clip = value;
            frame = 0;
            frames = CutFrames(value);
            InvalidateMeasure();
            InvalidateVisual();
            UpdateTimer();
        }
    }

    public bool IsPlaying { get => playing; set { playing = value; UpdateTimer(); } }

    // Frames are cut once through OfficeAssets so every frame of a sheet shares the same visible
    // bounds and the sprite does not jitter between padded cells.
    private static BitmapSource?[] CutFrames(SpriteClip? clip)
    {
        if (clip is not { Columns: > 0, Rows: > 0, FrameCount: > 0, FramesPerSecond: > 0 }) return [];
        var sheet = OfficeAssets.Load(clip.SheetFile);
        if (sheet == null) return [];
        var count = Math.Min(clip.FrameCount, clip.Columns * clip.Rows);
        var cut = new BitmapSource?[count];
        for (var i = 0; i < count; i++)
            cut[i] = OfficeAssets.Frame(sheet, i / clip.Columns, i % clip.Columns, clip.Rows, clip.Columns);
        return cut;
    }

    // The timer only runs while there is something to animate and the element can be seen.
    private void UpdateTimer()
    {
        timer.Stop();
        if (!playing || !IsLoaded || !IsVisible || frames.Length < 2 || clip is not { FramesPerSecond: > 0 }) return;
        timer.Interval = TimeSpan.FromSeconds(1 / clip.FramesPerSecond);
        timer.Start();
    }

    private void Advance()
    {
        frame = (frame + 1) % frames.Length;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var native = frames.Length > 0 && frames[0] is { } first ? new Size(first.PixelWidth, first.PixelHeight) : default;
        return new Size(double.IsNaN(Width) ? native.Width : Width, double.IsNaN(Height) ? native.Height : Height);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (frame >= frames.Length || frames[frame] is not { } image) return;
        var width = double.IsNaN(Width) ? image.PixelWidth : Width;
        var height = double.IsNaN(Height) ? image.PixelHeight : Height;
        // Uniform fit, like the static sprites: frames are cropped to visible pixels, so filling
        // the whole box would stretch the character.
        var scale = Math.Min(width / image.PixelWidth, height / image.PixelHeight);
        var size = new Size(image.PixelWidth * scale, image.PixelHeight * scale);
        drawingContext.DrawImage(image, new Rect((width - size.Width) / 2, (height - size.Height) / 2, size.Width, size.Height));
    }
}

// The clips every character with v2 sprite sheets (1-12, 14, 16) can play, looked up by character
// id; 13, 15 and 67 have no sheets and resolve to nothing. All sheets share one layout: 3x3
// frames of 128 px, read row-major.
public static class CharacterAnimations
{
    private const string Art = "v2/";
    public const int Columns = 3, Rows = 3, FrameCount = 9;
    // 7.6 fps: the office timer ticks every 33 ms and advances a frame every 4th tick.
    public const double FramesPerSecond = 7.6;

    private static readonly string[] SheetCharacters = ["1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "11", "12", "14", "16"];
    // The office roster: only characters with animation sheets may be shown.
    public static IReadOnlyList<string> Ids { get; } = SheetCharacters;
    // Clip names match the office poses; the suffix is how the sheet files are named on disk.
    private static readonly (string Name, string Suffix)[] ClipFiles =
    [
        ("idle", "-idle.png"), ("working", "-working.png"), ("reporting", "-reporting.png"),
        ("walking", "-walking.png"), ("walkingBack", "-walking_2.png")
    ];

    private static readonly Dictionary<string, Dictionary<string, SpriteClip>> ByCharacter = Register();

    private static Dictionary<string, Dictionary<string, SpriteClip>> Register()
    {
        var clips = new Dictionary<string, Dictionary<string, SpriteClip>>(StringComparer.Ordinal);
        foreach (var character in SheetCharacters)
            clips[character] = ClipFiles.ToDictionary(file => file.Name, file => new SpriteClip(Art + character + file.Suffix, Columns, Rows, FrameCount, FramesPerSecond), StringComparer.Ordinal);
        return clips;
    }

    public static IReadOnlyDictionary<string, SpriteClip>? ClipsFor(string characterId) => ByCharacter.GetValueOrDefault(characterId);
    public static SpriteClip? Clip(string characterId, string clipName) => ByCharacter.GetValueOrDefault(characterId)?.GetValueOrDefault(clipName);
    public static SpriteClip? Clip(int characterId, string clipName) => Clip(characterId.ToString(), clipName);
}
