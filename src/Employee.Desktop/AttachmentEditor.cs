using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Employee.Core;
using Employee.Infrastructure;
using Microsoft.Win32;

namespace Employee.Desktop;
public sealed class AttachmentEditor : StackPanel
{
    public List<WorkAttachment> Files { get; } = [];

    private readonly ListBox list = new()
    {
        MaxHeight = 180,
        Margin = new Thickness(0, 4, 0, 8)
    };
    public AttachmentEditor(TextBox input)
    {
        list.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(WrapPanel)));
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        ScrollViewer.SetCanContentScroll(list, false);
        var buttons = new WrapPanel();
        Children.Add(buttons);
        var add = new Button
        {
            Content = "Attach images / files..."
        };
        buttons.Children.Add(add);
        add.Click += (_, _) => Try(() =>
        {
            var dialog = new OpenFileDialog
            {
                Multiselect = true,
                Title = "Choose images or files"
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true)
                return;
            var pending = new List<WorkAttachment>();
            foreach (var path in dialog.FileNames)
            {
                if (new FileInfo(path).Length > TaskAttachments.MaxFileBytes)
                    throw new InvalidOperationException("File exceeds 10 MB.");
                pending.Add(new(Path.GetFileName(path), File.ReadAllBytes(path)));
            }

            TaskAttachments.Validate(Files.Concat(pending));
            Files.AddRange(pending);
            Refresh();
        });
        var remove = new Button
        {
            Content = "Remove selected file"
        };
        buttons.Children.Add(remove);
        remove.Click += (_, _) =>
        {
            if (list.SelectedItem is ListBoxItem { Tag: WorkAttachment file })
            {
                Files.Remove(file);
                Refresh();
            }
        };
        Children.Add(new TextBlock { Text = "Ctrl+V to paste an image · maximum 10 MB/file", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        Children.Add(list);
        // TextBox's built-in Paste command only enables text formats. Intercept
        // command routing before its class handlers so image-only clipboard data
        // also works with Ctrl+V, Shift+Insert and the Paste context menu.
        CommandManager.AddPreviewCanExecuteHandler(input, (_, e) =>
        {
            if (e.Command != ApplicationCommands.Paste || input.IsReadOnly || !HasClipboardImage())
                return;
            e.CanExecute = true;
            e.Handled = true;
        });
        CommandManager.AddPreviewExecutedHandler(input, (_, e) =>
        {
            if (e.Command != ApplicationCommands.Paste || input.IsReadOnly || !HasClipboardImage())
                return;
            e.Handled = true;
            Try(() =>
            {
                var bitmap = Clipboard.GetImage();
                if (bitmap == null)
                    throw new InvalidOperationException("Cannot read an image from the clipboard. Copy an image and try again.");
                AddImage(bitmap);
            });
        });
        DataObject.AddPastingHandler(input, (_, e) =>
        {
            if (input.IsReadOnly || !e.DataObject.GetDataPresent(DataFormats.Bitmap))
                return;
            e.CancelCommand();
            Try(() =>
            {
                if (e.DataObject.GetData(DataFormats.Bitmap)is BitmapSource bitmap)
                    AddImage(bitmap);
            });
        });
    }

    private static bool HasClipboardImage()
    {
        try
        {
            return Clipboard.ContainsImage();
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return false;
        }
    }

    private void AddImage(BitmapSource bitmap)
    {
        if ((long)bitmap.PixelWidth * bitmap.PixelHeight > 25_000_000)
            throw new InvalidOperationException("Image is too large (maximum 25 million pixels).");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        var file = new WorkAttachment("paste-" + DateTime.Now.ToString("yyyyMMdd-HHmmssfff") + ".png", stream.ToArray());
        TaskAttachments.Validate(Files.Append(file));
        Files.Add(file);
        Refresh();
    }

    public void Clear()
    {
        Files.Clear();
        Refresh();
    }

    private void Refresh()
    {
        list.Items.Clear();
        foreach (var file in Files)
        {
            var preview = AttachmentPreview.Create(file, out _, () =>
            {
                Files.Remove(file);
                Refresh();
            });
            list.Items.Add(new ListBoxItem { Tag = file, Content = preview, Padding = new Thickness(0), Margin = new Thickness(0), BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Left });
        }
    }

    private void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Attachments");
        }
    }
}
