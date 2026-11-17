using System.Text.Json.Nodes;

namespace Employee.Infrastructure;

// The frame grid a Pixel Art Tool request asked for, and whether the returned image matches it.
// The tool itself cuts and fits the frames (requests carry exactFrames); the manager only checks.
internal static class PixelSheetLayout
{
    internal sealed record Layout(int Columns, int Rows, int FrameWidth, int FrameHeight);

    public static Layout? Requested(JsonNode output)
    {
        var settings = output["requestSettings"];
        var width = output["result"]?["frameWidth"]?.GetValue<int>() ?? settings?["outputWidth"]?.GetValue<int>() ?? 0;
        var height = output["result"]?["frameHeight"]?.GetValue<int>() ?? settings?["outputHeight"]?.GetValue<int>() ?? 0;
        if (width is < 8 or > 4096 || height is < 8 or > 4096)
            return null;
        var columns = output["result"]?["columns"]?.GetValue<int>() ?? 1;
        var rows = output["result"]?["rows"]?.GetValue<int>() ?? 1;
        return columns is < 1 or > 64 || rows is < 1 or > 64 ? null : new(columns, rows, width, height);
    }

    // Why the returned image cannot go into the project, or null when its size is the requested grid.
    public static string? Problem(JsonNode output, Layout layout)
    {
        var width = output["imageWidth"]?.GetValue<int>() ?? 0;
        var height = output["imageHeight"]?.GetValue<int>() ?? 0;
        int expectedWidth = layout.Columns * layout.FrameWidth, expectedHeight = layout.Rows * layout.FrameHeight;
        var settings = output["requestSettings"];
        if (settings?["action"]?.GetValue<string>() == "animation" && settings?["frames"] is JsonValue framesValue &&
            framesValue.TryGetValue<int>(out var requestedFrames) && requestedFrames > 1 && layout.Columns * layout.Rows != requestedFrames)
            return $"Pixel Art Tool returned {layout.Columns * layout.Rows} frames for an animation requiring {requestedFrames}. The output is retained for inspection; the Manager must plan a corrected tool job instead of counting a still pose as completed animation.";
        if (width == expectedWidth && height == expectedHeight)
            return null;
        return $"Pixel Art Tool returned a {width}x{height} image; the request was {layout.Columns}x{layout.Rows} frames of {layout.FrameWidth}x{layout.FrameHeight} ({expectedWidth}x{expectedHeight}). " +
            "The running tool does not finish frames for API requests yet: restart it from its source folder, or rebuild PixelFoundry.exe, then Run / resume. The image was not put into the project; the job is kept.";
    }
}
