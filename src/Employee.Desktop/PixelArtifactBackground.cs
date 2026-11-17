using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Employee.Desktop;

internal static class PixelArtifactBackground
{
    public static byte[] RemoveWhiteMatte(byte[] png)
    {
        using var stream = new MemoryStream(png, writable: false);
        var frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var width = frame.PixelWidth;
        var height = frame.PixelHeight;
        if (width < 3 || height < 3 || (long)width * height > 4_194_304) return png;

        var bitmap = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);

        bool NearWhite(int index)
        {
            var offset = index * 4;
            return pixels[offset + 3] == 255 && pixels[offset] >= 247 &&
                pixels[offset + 1] >= 247 && pixels[offset + 2] >= 247;
        }

        var count = width * height;
        if (!NearWhite(0) || !NearWhite(width - 1) ||
            !NearWhite(count - width) || !NearWhite(count - 1)) return png;

        // A matte should cover every border. Leave images with foreground at the edge alone.
        for (var x = 0; x < width; x++)
            if (!NearWhite(x) || !NearWhite(count - width + x)) return png;
        for (var y = 0; y < height; y++)
            if (!NearWhite(y * width) || !NearWhite(y * width + width - 1)) return png;

        var queue = new int[count];
        var end = 0;
        void Visit(int index)
        {
            if (!NearWhite(index)) return;
            pixels[index * 4 + 3] = 0;
            queue[end++] = index;
        }
        Visit(0);
        for (var i = 0; i < end; i++)
        {
            var index = queue[i];
            var x = index % width;
            if (x > 0) Visit(index - 1);
            if (x < width - 1) Visit(index + 1);
            if (index >= width) Visit(index - width);
            if (index < count - width) Visit(index + width);
        }
        if (end == count || end < count / 20) return png;

        var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(result));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }
}
