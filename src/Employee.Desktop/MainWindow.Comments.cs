using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Employee.Core;
using Employee.Infrastructure;

namespace Employee.Desktop;
public partial class MainWindow
{
    private AttachmentEditor taskAttachments = null!, commentAttachments = null!;
    private readonly TextBox commentInput = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        Height = 48,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto
    };
    private readonly StackPanel commentHistory = new();
    private string? commentGoalId;
    private ScrollViewer commentScroll = null!;
    private string? commentRevision;
    private void VerifyAttachmentSmoke(WorkItem goal)
    {
        var pixels = new byte[]
        {
            0,
            0,
            255,
            255
        };
        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(1, 1, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, 4);
        // Exercise the routed command, not just a synthetic Pasting event:
        // image-only data must enable Paste before TextBox will raise that event.
        var previousClipboard = Clipboard.GetDataObject();
        try
        {
            Clipboard.SetImage(bitmap);
            foreach (var(input, editor)in new[]
            {
                (TaskRequest, taskAttachments),
                (commentInput, commentAttachments)
            }

            )
            {
                if (!System.Windows.Input.ApplicationCommands.Paste.CanExecute(null, input))
                    throw new InvalidOperationException("Image-only clipboard did not enable Paste.");
                System.Windows.Input.ApplicationCommands.Paste.Execute(null, input);
                if (editor.Files.Count != 1 || editor.Files[0].Content.Length == 0)
                    throw new InvalidOperationException("Clipboard image paste failed.");
            }

            Clipboard.SetText("Plain text paste fixture");
            System.Windows.Input.ApplicationCommands.Paste.Execute(null, TaskRequest);
            if (!TaskRequest.Text.Contains("Plain text paste fixture") || taskAttachments.Files.Count != 1)
                throw new InvalidOperationException("Plain text paste regressed.");
            TaskRequest.Text = "Attachment smoke fixture";
            var created = CreateTaskRecord();
            if (store.WorkItems().Single(w => w.Id == created.Id).Attachments.Count != 1 || taskAttachments.Files.Count != 0)
                throw new InvalidOperationException("Task attachment save failed.");
        }
        finally
        {
            if (previousClipboard == null)
                Clipboard.Clear();
            else
                Clipboard.SetDataObject(previousClipboard, true);
        }

        commentAttachments.Clear();
        RefreshTasks(goal.Id);
    }

    private void InitializeAttachments()
    {
        taskAttachments = new(TaskRequest);
        TaskInputPanel.Children.Add(taskAttachments);
        var taskRefine = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        TaskInputPanel.Children.Add(taskRefine);
        AddRefineButton(taskRefine, TaskRequest, () => null);
        var composer = new StackPanel();
        DockPanel.SetDock(composer, Dock.Bottom);
        CommentPanel.Children.Add(composer);
        composer.Children.Add(commentInput);
        commentAttachments = new(commentInput);
        composer.Children.Add(commentAttachments);
        commentInput.ToolTip = "Enter an update, correction or change for the selected task.";
        composer.Children.Insert(0, new TextBlock { Text = "New execution request for selected task (continues its plan and steps)", Margin = new Thickness(0, 0, 0, 6) });
        var buttons = new WrapPanel();
        composer.Children.Add(buttons);
        AddRefineButton(buttons, commentInput, () => TaskGrid.SelectedItem as WorkItem);
        var send = new Button
        {
            Content = "Send comment"
        };
        buttons.Children.Add(send);
        var update = new Button
        {
            Content = "Send & continue updating"
        };
        buttons.Children.Add(update);
        send.Click += async (_, _) => await Guard(() => SendTaskComment(false));
        update.Click += async (_, _) => await Guard(() => SendTaskComment(true));
        commentScroll = new ScrollViewer
        {
            Content = commentHistory,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        CommentPanel.Children.Add(commentScroll);
    }

    // "Refine with AI": an AI rewrites the typed text into a clearer, more complete request in the
    // same language; the user checks it in the box and can undo before sending.
    private void AddRefineButton(Panel panel, TextBox box, Func<WorkItem?> task)
    {
        var refine = new Button { Content = "✨ Refine with AI", Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 2, 10, 2), ToolTip = "An AI rewrites what you typed into a clearer, more complete request (same language). Nothing is sent until you press Send; Undo brings your text back." };
        var undo = new Button { Content = "Undo refine", Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 2, 10, 2), Visibility = Visibility.Collapsed };
        string? before = null;
        refine.Click += async (_, _) =>
        {
            var text = box.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                Status.Text = "Type something first, then refine it.";
                return;
            }
            refine.IsEnabled = false;
            box.IsReadOnly = true;
            var label = refine.Content;
            refine.Content = "Refining…";
            try
            {
                var selected = task();
                var project = selected?.ProjectId is { Length: > 0 } id ? store.Projects().FirstOrDefault(p => p.Id == id) : TaskProject.SelectedItem as ProjectInfo;
                if (health.Count == 0)
                    health = await orchestrator.DetectAsync(settings);
                var (refined, provider) = await new RequestRefiner(store, providers).RefineAsync(text, selected, project, settings, health, CancellationToken.None);
                if (box.Text == text)
                {
                    before = text;
                    box.Text = refined;
                    box.CaretIndex = box.Text.Length;
                    undo.Visibility = Visibility.Visible;
                    Status.Text = "Refined by " + provider + ". Check it, edit if needed, then send. Undo brings your text back.";
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
            {
                Status.Text = ex.Message;
            }
            finally
            {
                refine.Content = label;
                refine.IsEnabled = true;
                box.IsReadOnly = false;
            }
        };
        undo.Click += (_, _) =>
        {
            if (before != null)
                box.Text = before;
            before = null;
            undo.Visibility = Visibility.Collapsed;
        };
        box.TextChanged += (_, _) => { if (box.Text.Length == 0) { before = null; undo.Visibility = Visibility.Collapsed; } };
        panel.Children.Add(refine);
        panel.Children.Add(undo);
    }

    private async Task SendTaskComment(bool continueUpdating)
    {
        var goal = unified.AddComment(SelectedWork(), commentInput.Text, commentAttachments.Files, continueUpdating);
        commentInput.Clear();
        commentAttachments.Clear();
        RefreshTasks(goal.Id);
        Status.Text = "Comment saved. It will be included on the next run or resume.";
        if (continueUpdating)
        {
            queueSuspended = false;
            Status.Text = "New execution request queued; the task plan and steps will be continued.";
        }

        await Task.CompletedTask;
    }

    private void OpenTaskDetails(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (source == null || ItemsControl.ContainerFromElement(TaskGrid, source)is not DataGridRow)
            return;
        for (var element = source; element != null && element is not DataGridRow; element = System.Windows.Media.VisualTreeHelper.GetParent(element))
            if (element is System.Windows.Controls.Primitives.ButtonBase)
                return;
        ShowTaskDetails(this, new());
    }

    private static TextBox CreateSelectableComment(string text)
    {
        var box = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            Foreground = System.Windows.Media.Brushes.Black,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            MinHeight = 0,
            VerticalContentAlignment = VerticalAlignment.Top
        };
        int? anchor = null;
        int CharacterAt(MouseEventArgs e) => box.GetCharacterIndexFromPoint(e.GetPosition(box), true);
        void UpdateSelection(MouseEventArgs e)
        {
            if (anchor is not int start)
                return;
            var end = CharacterAt(e);
            if (end >= 0)
                box.Select(Math.Min(start, end), Math.Abs(start - end));
        }

        box.PreviewMouseRightButtonDown += (_, e) =>
        {
            box.Focus();
            var start = CharacterAt(e);
            if (start < 0)
                return;
            box.Select(start, 0);
            if (box.CaptureMouse())
                anchor = start;
            e.Handled = true;
        };
        box.PreviewMouseMove += (_, e) =>
        {
            if (anchor == null || e.RightButton != MouseButtonState.Pressed)
                return;
            UpdateSelection(e);
            e.Handled = true;
        };
        box.PreviewMouseRightButtonUp += (_, e) =>
        {
            if (anchor == null)
                return;
            UpdateSelection(e);
            anchor = null;
            box.ReleaseMouseCapture();
            e.Handled = true;
        };
        box.LostMouseCapture += (_, _) => anchor = null;
        box.ContextMenuOpening += (_, e) =>
        {
            if (box.SelectionLength > 0)
                e.Handled = true;
        };
        return box;
    }

    private void EnableCommentImageCopy(FrameworkElement preview, WorkAttachment file)
    {
        if (preview is not Grid tile || tile.Children.Count == 0 || tile.Children[0] is not Button imageButton)
            return;
        imageButton.ToolTip = "Left click to preview. Right click to select, then Ctrl+C to copy.";
        imageButton.PreviewMouseRightButtonDown += (_, e) =>
        {
            imageButton.Focus();
            e.Handled = true;
        };
        imageButton.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key != Key.C || Keyboard.Modifiers != ModifierKeys.Control)
                return;
            e.Handled = true;
            await Guard(() =>
            {
                var bitmap = AttachmentPreview.Decode(file, 4096) ?? throw new InvalidOperationException("Cannot copy this image.");
                Clipboard.SetImage(bitmap);
                Status.Text = "Image copied. Paste it with Ctrl+V.";
                return Task.CompletedTask;
            });
        };
    }

    private void RefreshComments(WorkItem goal)
    {
        if (commentGoalId != goal.Id)
        {
            commentInput.Clear();
            commentAttachments.Clear();
            commentGoalId = goal.Id;
            commentRevision = null;
        }

        var revision = goal.Id + ":" + goal.Comments.Count + ":" + goal.UpdatedAt + ":" + goal.AccessResult;
        if (commentRevision == revision)
            return;
        var scrollToEnd = commentRevision == null || commentScroll.ScrollableHeight - commentScroll.VerticalOffset < 24;
        commentRevision = revision;
        commentHistory.Children.Clear();
        void Bubble(string text, IEnumerable<WorkAttachment> files, DateTimeOffset at, bool assistant)
        {
            var body = new StackPanel();
            System.Windows.Documents.TextElement.SetForeground(body, System.Windows.Media.Brushes.Black);
            body.Children.Add(new TextBlock { Text = (assistant ? "AI" : "You") + " · " + at.ToLocalTime().ToString("dd/MM HH:mm"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
            body.Children.Add(CreateSelectableComment(text));
            var attachments = new WrapPanel();
            body.Children.Add(attachments);
            foreach (var file in files)
            {
                var preview = AttachmentPreview.Create(file, out var isImage);
                attachments.Children.Add(preview);
                if (isImage)
                {
                    EnableCommentImageCopy(preview, file);
                    continue;
                }

                var save = new Button
                {
                    Content = "Save file: " + file.Label,
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                save.Click += async (_, _) => await Guard(() =>
                {
                    var dialog = new Microsoft.Win32.SaveFileDialog
                    {
                        FileName = Path.GetFileName(file.Name),
                        Filter = "All files|*.*"
                    };
                    if (dialog.ShowDialog(this) == true)
                        File.WriteAllBytes(dialog.FileName, file.Content);
                    return Task.CompletedTask;
                });
                attachments.Children.Add(save);
            }

            // Files and folders a tool produced: open them straight from the comment.
            var links = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            foreach (var line in text.Split('\n').Select(l => l.Trim()))
            {
                var isFile = line.StartsWith(ToolResultComment.FilePrefix, StringComparison.Ordinal);
                if (!isFile && !line.StartsWith(ToolResultComment.FolderPrefix, StringComparison.Ordinal))
                    continue;
                var path = line[(isFile ? ToolResultComment.FilePrefix.Length : ToolResultComment.FolderPrefix.Length)..];
                if (!Path.IsPathFullyQualified(path) || !(isFile ? File.Exists(path) : Directory.Exists(path)))
                    continue;
                var open = new Button { Content = isFile ? "Open " + Path.GetFileName(path) : "Open folder " + Path.GetFileName(Path.TrimEndingDirectorySeparator(path)), Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(10, 3, 10, 3), ToolTip = path };
                open.Click += (_, _) =>
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(isFile ? path : "explorer.exe", isFile ? "" : "\"" + path + "\"") { UseShellExecute = true });
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                    {
                        Status.Text = "Could not open " + path + ": " + ex.Message;
                    }
                };
                links.Children.Add(open);
                if (isFile)
                {
                    var reveal = new Button { Content = "Show in folder", Margin = new Thickness(0, 0, 12, 6), Padding = new Thickness(10, 3, 10, 3), ToolTip = path };
                    reveal.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
                    links.Children.Add(reveal);
                }
            }
            if (links.Children.Count > 0)
                body.Children.Add(links);

            var bubble = new Border
            {
                Child = body,
                Padding = new Thickness(14),
                CornerRadius = new CornerRadius(12),
                Margin = assistant ? new Thickness(0, 6, 48, 6) : new Thickness(48, 6, 0, 6),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = new System.Windows.Media.SolidColorBrush(assistant ? System.Windows.Media.Color.FromRgb(235, 240, 247) : System.Windows.Media.Color.FromRgb(216, 242, 232))
            };
            // The scroll viewer constrains the stretched history to its available width.
            // Opposite margins distinguish speakers without caching an initial zero viewport
            // width (the tab may be hidden), which used to leave messages only 160px wide.
            commentHistory.Children.Add(bubble);
        }

        Bubble(goal.Request, goal.Attachments, goal.CreatedAt, false);
        foreach (var comment in goal.Comments.OrderBy(c => c.At))
            Bubble(comment.Text, comment.Attachments, comment.At, comment.IsAssistant);
        if (!string.IsNullOrWhiteSpace(goal.AccessResult))
            Bubble("Current access: " + goal.Access + "\n" + TaskAccessProposal.DisplayResult(store, goal), [], goal.UpdatedAt ?? goal.CreatedAt, true);
        if (scrollToEnd)
            Dispatcher.BeginInvoke(new Action(() => commentScroll.ScrollToEnd()));
    }
}
