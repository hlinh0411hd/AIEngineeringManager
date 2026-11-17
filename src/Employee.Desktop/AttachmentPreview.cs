using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Employee.Core;

namespace Employee.Desktop;
internal static class AttachmentPreview
{
    // Decode the bytes rather than trusting a filename extension. Keep decoded
    // previews bounded, and detach them from the stream before it is disposed.
    internal static BitmapSource? Decode(WorkAttachment file, int width)
    {
        try
        {
            using var stream = new MemoryStream(file.Content, writable: false);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            var landscape = frame.PixelWidth >= frame.PixelHeight;
            stream.Position = 0;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            if (landscape)
                bitmap.DecodePixelWidth = Math.Min(width, frame.PixelWidth);
            else
                bitmap.DecodePixelHeight = Math.Min(width, frame.PixelHeight);
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex)when (ex is NotSupportedException or IOException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    public static FrameworkElement Create(WorkAttachment file) => Create(file, out _);
    public static FrameworkElement Create(WorkAttachment file, out bool isImage, Action? remove = null)
    {
        var thumbnail = Decode(file, 160);
        isImage = thumbnail != null;
        if (thumbnail != null)
        {
            var open = new Button
            {
                Content = new Image
                {
                    Source = thumbnail,
                    Width = 128,
                    Height = 88,
                    Stretch = Stretch.Uniform
                },
                Width = 140,
                Height = 100,
                HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = "View image in app: " + file.Name,
                Cursor = Cursors.Hand,
                Padding = new Thickness(4),
                Margin = new Thickness(0)
            };
            System.Windows.Automation.AutomationProperties.SetName(open, "Open image: " + file.Name);
            open.Click += (_, _) => Show(file, open);
            var tile = new Grid
            {
                Width = 140,
                Height = 100,
                Margin = new Thickness(0, 4, 8, 4)
            };
            tile.Children.Add(open);
            if (remove != null)
            {
                var delete = new Button
                {
                    Content = "×",
                    Width = 24,
                    Height = 24,
                    MinHeight = 24,
                    Padding = new Thickness(0),
                    Margin = new Thickness(2),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    ToolTip = "Remove image"
                };
                System.Windows.Automation.AutomationProperties.SetName(delete, "Remove image: " + file.Name);
                delete.Click += (_, _) => remove();
                tile.Children.Add(delete);
            }

            return tile;
        }

        return new TextBlock
        {
            Text = file.Label,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 200,
            Margin = new Thickness(0, 4, 8, 4)
        };
    }

    private static void Show(WorkAttachment file, FrameworkElement source)
    {
        var bitmap = Decode(file, 2400);
        if (bitmap == null)
        {
            MessageBox.Show(Window.GetWindow(source), "Cannot preview this image.", "Image preview");
            return;
        }

        var content = new DockPanel();
        var close = new Button
        {
            Content = "Close preview (Esc)",
            IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        DockPanel.SetDock(close, Dock.Bottom);
        content.Children.Add(close);
        content.Children.Add(new Image { Source = bitmap, Stretch = Stretch.Uniform, Margin = new Thickness(12) });
        var viewer = new Window
        {
            Title = file.Name,
            Owner = Window.GetWindow(source),
            Width = Math.Min(960, SystemParameters.WorkArea.Width),
            Height = Math.Min(720, SystemParameters.WorkArea.Height),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = content
        };
        close.Click += (_, _) => viewer.Close();
        viewer.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                viewer.Close();
        };
        viewer.ShowDialog();
    }
}
