using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Employee.Core;
using Employee.Infrastructure;
using Microsoft.Win32;

namespace Employee.Desktop;

// Create a task that uses a chosen tool. The task is planned like any task in the selected project;
// the form only fixes which tool runs and with which settings. The settings shown depend on the tool,
// each with the tool's own default selected.
public sealed class ToolTaskFormView : UserControl
{
    public enum SubmitMode { Run, Queue, Save }
    public event Func<ToolTaskForm, SubmitMode, Task>? Submitted;


    private readonly Func<IReadOnlyList<ProjectInfo>> projects;
    private readonly Func<IReadOnlyList<ConnectedTool>> tools;
    private readonly ComboBox project = new() { DisplayMemberPath = nameof(ProjectInfo.Name), MinWidth = 260 };
    private readonly ComboBox tool = new() { DisplayMemberPath = nameof(ConnectedTool.Name), MinWidth = 260 };
    private readonly TextBox request = new() { Height = 90, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top, ToolTip = "What the task should do in this project, for example: replace the CatIdleBellyPawTwitch animation." };
    private readonly ContentControl settingsHost = new();
    private PixelSettings? pixel;
    private MusicSettings? music;

    public ToolTaskFormView(Func<IReadOnlyList<ProjectInfo>> projects, Func<IReadOnlyList<ConnectedTool>> tools)
    {
        this.projects = projects;
        this.tools = tools;
        var form = new StackPanel { Margin = new Thickness(12), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
        form.Children.Add(Row(Field("Project", project), Field("Tool", tool)));
        form.Children.Add(Field("Request", request, stretch: true));
        form.Children.Add(settingsHost);
        var buttons = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(Button("Create & run", SubmitMode.Run, primary: true));
        buttons.Children.Add(Button("Add to queue", SubmitMode.Queue));
        buttons.Children.Add(Button("Save for later", SubmitMode.Save));
        form.Children.Add(buttons);
        Content = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        project.SelectionChanged += (_, _) => { if (pixel != null) pixel.ProjectRoot = (project.SelectedItem as ProjectInfo)?.Root; };
        tool.SelectionChanged += (_, _) => ShowSettings();
        IsVisibleChanged += (_, _) => { if (IsVisible) Refresh(); };
    }

    // Reloads projects and tools, keeping the current selection where it still exists.
    public void Refresh(string? projectId = null)
    {
        var keepProject = projectId ?? (project.SelectedItem as ProjectInfo)?.Id;
        var keepTool = (tool.SelectedItem as ConnectedTool)?.Id;
        var projectList = projects();
        project.ItemsSource = projectList;
        project.SelectedItem = projectList.FirstOrDefault(p => p.Id == keepProject) ?? projectList.FirstOrDefault();
        var toolList = tools().Where(t => t.Enabled).OrderByDescending(t => t.Connector == "pixel").ToArray();
        // Same tools as before: keep the settings being filled in.
        if (tool.ItemsSource is ConnectedTool[] shown && shown.Select(t => t.Id).SequenceEqual(toolList.Select(t => t.Id)))
            return;
        tool.ItemsSource = toolList;
        tool.SelectedItem = toolList.FirstOrDefault(t => t.Id == keepTool) ?? toolList.FirstOrDefault();
        if (toolList.Length == 0)
            settingsHost.Content = Note("No connected tool is enabled. Enable one in Tools.");
    }

    private void ShowSettings()
    {
        pixel = null;
        music = null;
        if (tool.SelectedItem is not ConnectedTool selected)
            return;
        if (selected.Connector == "music")
        {
            music = new MusicSettings(selected);
            settingsHost.Content = music;
            return;
        }
        if (selected.Connector != "pixel")
        {
            settingsHost.Content = Note(selected.Name + " has no settings form yet. Use an AI task that names the tool.");
            return;
        }
        try
        {
            pixel = new PixelSettings(PixelToolChoices.For(selected)) { ProjectRoot = (project.SelectedItem as ProjectInfo)?.Root };
            settingsHost.Content = pixel;
        }
        catch (InvalidOperationException ex)
        {
            settingsHost.Content = Note(ex.Message);
        }
    }

    private async Task Submit(SubmitMode mode)
    {
        if (project.SelectedItem is not ProjectInfo p)
            throw new InvalidOperationException("Choose a project.");
        if (tool.SelectedItem is not ConnectedTool t || pixel == null && music == null)
            throw new InvalidOperationException("Choose a tool that has a settings form.");
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new InvalidOperationException("Describe what the task should do.");
        if (music != null)
        {
            var (operation, musicInput) = music.Build();
            var musicForm = new ToolTaskForm(p.Id, t.Id, operation, musicInput, [], request.Text.Trim());
            if (Submitted != null)
                await Submitted(musicForm, mode);
            return;
        }
        var (input, first, attachments) = pixel!.Build(request.Text.Trim());
        var form = new ToolTaskForm(p.Id, t.Id, "generate", input, attachments, request.Text.Trim(), first);
        if (Submitted != null)
            await Submitted(form, mode);
    }

    // Empties the form after a task is created, like the AI task form; the project stays selected.
    public void Clear()
    {
        request.Clear();
        pixel?.ClearImages();
        music?.Clear();
        ShowSettings();
    }

    private Button Button(string text, SubmitMode mode, bool primary = false)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(14, 4, 14, 4) };
        if (primary)
        {
            button.Background = new SolidColorBrush(Color.FromRgb(0x75, 0x45, 0xD6));
            button.Foreground = Brushes.White;
        }
        button.Click += async (_, _) =>
        {
            try
            {
                await Submit(mode);
            }
            catch (InvalidOperationException ex)
            {
                MessageBox.Show(Window.GetWindow(this), ex.Message, "Tool task", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        };
        return button;
    }

    private static TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Opacity = 0.8 };
    private static TextBlock Label(string text) => new() { Text = text, Margin = new Thickness(0, 10, 0, 4), FontWeight = FontWeights.SemiBold };
    private static StackPanel Field(string label, FrameworkElement control, bool stretch = false)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, stretch ? 0 : 16, 0) };
        panel.Children.Add(Label(label));
        panel.Children.Add(control);
        return panel;
    }
    private static WrapPanel Row(params UIElement[] items)
    {
        var row = new WrapPanel();
        foreach (var item in items)
        {
            if (item is CheckBox box) box.Margin = new Thickness(0, 12, 18, 0);
            row.Children.Add(item);
        }
        return row;
    }

    // ZapBop settings: only what the connector really supports (inspect or WAV export of a saved
    // project). ZapBop has no API for composing; that stays in the ZapBop app.
    private sealed class MusicSettings : StackPanel
    {
        private readonly ConnectedTool profile;
        private readonly ComboBox operation = new() { MinWidth = 220 };
        private readonly TextBox file = new() { MinWidth = 420, ToolTip = "A project saved from ZapBop's Professional tab (.json), inside the tool's allowed workspace." };

        public MusicSettings(ConnectedTool profile)
        {
            this.profile = profile;
            operation.ItemsSource = new[] { new KeyValuePair<string, string>("export", "Export to WAV"), new KeyValuePair<string, string>("inspect", "Inspect project (read only)") };
            operation.DisplayMemberPath = "Value";
            operation.SelectedValuePath = "Key";
            operation.SelectedIndex = 0;
            var browse = new Button { Content = "Browse…", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 2, 10, 2) };
            browse.Click += (_, _) => Browse();
            var picker = new StackPanel { Orientation = Orientation.Horizontal };
            picker.Children.Add(file);
            picker.Children.Add(browse);
            Children.Add(Row(Field("Action", operation)));
            Children.Add(Field("ZapBop project file", picker, stretch: true));
            var workspace = Directory.Exists(profile.WorkspaceRoot) ? profile.WorkspaceRoot : "not set (Settings → Tools → Allowed workspace)";
            Children.Add(Note("Allowed workspace: " + workspace + ". The WAV is saved to this task's run folder; project files are not changed. Composing new music is not available through ZapBop's connector; do that in the ZapBop app."));
        }

        private void Browse()
        {
            var dialog = new OpenFileDialog { Filter = "ZapBop project|*.json", Title = "Choose a saved ZapBop project" };
            if (Directory.Exists(profile.WorkspaceRoot))
                dialog.InitialDirectory = profile.WorkspaceRoot;
            if (dialog.ShowDialog() == true)
                file.Text = dialog.FileName;
        }

        public (string Operation, JsonObject Input) Build()
        {
            var path = file.Text.Trim();
            if (path.Length == 0 || !File.Exists(path) || !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Choose a saved ZapBop project (.json).");
            if (Directory.Exists(profile.WorkspaceRoot))
            {
                var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profile.WorkspaceRoot)) + Path.DirectorySeparatorChar;
                if (!Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The project file must be inside " + profile.Name + "'s allowed workspace: " + profile.WorkspaceRoot);
            }
            return (operation.SelectedValue as string ?? "export", new JsonObject { ["projectFile"] = Path.GetFullPath(path) });
        }

        public void Clear() => file.Clear();
    }

    // Pixel Art Tool settings, from the tool's own capability file. Each action shows only the settings
    // it uses; everything the tool's editor offers is here.
    private sealed class PixelSettings : StackPanel
    {
        private static readonly Dictionary<string, string> ActionLabels = new()
        {
            ["animation"] = "Animate", ["create+animation"] = "Create, then animate", ["create"] = "Create new", ["edit"] = "Edit / restyle",
            ["direction"] = "Turn direction", ["skin"] = "Reskin", ["recolor"] = "Replace a colour (no AI)", ["hue"] = "Shift hue (no AI)", ["procedural"] = "Simple motion (no AI)"
        };
        private readonly PixelToolChoices choices;
        private readonly ComboBox action = new() { MinWidth = 200 };
        private readonly ComboBox artMode = new() { MinWidth = 140 };
        private readonly ComboBox provider = new() { MinWidth = 170 };
        private readonly ComboBox imageModel = new() { MinWidth = 190 };
        private readonly ImagePicker source = new("Source image");
        private readonly ImagePicker style = new("Style sample (optional)");
        public void ClearImages() { source.Reset(); style.Reset(); }
        private readonly ComboBox direction = new() { MinWidth = 130 };
        private readonly ComboBox width = new() { MinWidth = 90, IsEditable = true };
        private readonly ComboBox height = new() { MinWidth = 90, IsEditable = true };
        private readonly ComboBox frames = new() { MinWidth = 80 };
        private readonly ComboBox fps = new() { MinWidth = 80 };
        private readonly ComboBox motion = new() { MinWidth = 120 };
        private readonly TextBox fromColor = new() { Width = 90, Text = "#000000" };
        private readonly TextBox toColor = new() { Width = 90, Text = "#ffffff" };
        private readonly Slider hue = new() { Minimum = -180, Maximum = 180, Width = 260, TickFrequency = 15, IsSnapToTickEnabled = true };
        private readonly TextBlock hueValue = new() { Text = "0°", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        private readonly CheckBox wholeSheet = new() { Content = "Whole spritesheet" };
        private readonly CheckBox animationSheet = new() { Content = "Source is an animation sheet" };
        private readonly TextBox columns = new() { Width = 60, Text = "4" };
        private readonly TextBox rows = new() { Width = 60, Text = "2" };
        private readonly ComboBox scope = new() { MinWidth = 140 };
        private readonly TextBox frameNumber = new() { Width = 60, Text = "1" };
        private readonly CheckBox regionOn = new() { Content = "Only this area (x, y, width, height)" };
        private readonly TextBox regionX = new() { Width = 50, Text = "0" }, regionY = new() { Width = 50, Text = "0" }, regionW = new() { Width = 50, Text = "32" }, regionH = new() { Width = 50, Text = "32" };
        private readonly ComboBox exportScale = new() { MinWidth = 100 };
        private readonly CheckBox keepStyle = new() { Content = "Keep source style", Tag = "Keep the source's pixel size, outline and shading.", ToolTip = "Keep the source's pixel size, outline and shading." };
        private readonly CheckBox ownColours = new() { Content = "Match source colours", ToolTip = "Use the source image's colours, plus the few shades the new image needs that the source lacks." };
        private readonly CheckBox strictColours = new() { Content = "Exact source colours only", ToolTip = "Use only colours that appear in the source image, even where none is close." };
        private readonly CheckBox lockScale = new() { Content = "Keep size close to source", IsChecked = true, Tag = "The figure keeps the size it has in the source image.", ToolTip = "The figure keeps the size it has in the source image." };
        private readonly CheckBox anchor = new() { Content = "First frame = source image", IsChecked = true, Tag = "Frame 1 keeps the source image's exact pixels.", ToolTip = "Frame 1 keeps the source image's exact pixels." };
        private readonly CheckBox silhouette = new() { Content = "Keep outline and transparency", IsChecked = true };
        private readonly CheckBox transparent = new() { Content = "Transparent background", IsChecked = true };
        private readonly CheckBox autoSwitch = new() { Content = "Switch AI when usage runs out", IsChecked = true, ToolTip = "If the chosen AI has no usage left or is not signed in, the tool continues with the next one (Codex CLI, then Claude CLI). Paid APIs are never used unless the box below is ticked." };
        private readonly CheckBox paidFallback = new() { Content = "Also use paid services as last resort", IsChecked = false, ToolTip = "Only when both CLIs have no usage left: use PixelLab credits, then the API keys configured on the tool server (billed by the provider)." };
        private readonly CheckBox autoFix = new() { Content = "Auto-fix problems", IsChecked = true, ToolTip = "The tool checks what it made (lost details, wrong colours, extra copies, frames that do not move or are broken), fixes what it can, and may generate once more (one extra AI call)." };
        private readonly Dictionary<FrameworkElement, Func<bool>> shown = new();
        public string? ProjectRoot { set { source.ProjectRoot = value; style.ProjectRoot = value; } }

        public PixelSettings(PixelToolChoices choices)
        {
            this.choices = choices;
            var actions = choices.Actions.ToList();
            if (actions.Contains("create") && actions.Contains("animation")) actions.Insert(actions.IndexOf("animation") + 1, "create+animation");
            Fill(action, actions.Select(a => (a, ActionLabels.GetValueOrDefault(a, a))), "animation");
            Fill(artMode, (choices.AutoArtMode ? new[] { ("auto", "Auto detect") } : []).Concat(choices.CanvasMax.Keys.Select(k => (k, k == "pixel" ? "Pixel art" : k == "illustration" ? "Illustration" : k))), choices.DefaultArtMode);
            Fill(provider, new[] { ("codex", "Codex CLI") }.Concat(choices.Providers.Contains("claude") ? [("claude", "Claude CLI (draws by code)")] : Array.Empty<(string, string)>()).Concat(choices.Providers.Contains("pixellab") ? [("pixellab", "PixelLab API (pixel-art model)")] : Array.Empty<(string, string)>()).Concat(choices.ApiImageModels.Keys.Select(v => ("api:" + v, "API · " + (v == "openai" ? "OpenAI" : v == "gemini" ? "Gemini" : v)))), "codex");
            Fill(direction, choices.Directions.Select(d => (d, d + " · " + Compass(d))), choices.DefaultDirection);
            var sizes = new[] { 16, 32, 48, 64, 96, 128, 192, 256, 384, 512, 768, 1024 }.Where(s => s >= choices.CanvasMin && s <= choices.CanvasMax.Values.Max()).Select(s => (s.ToString(), s.ToString())).ToArray();
            Fill(width, sizes, choices.DefaultWidth.ToString());
            Fill(height, sizes, choices.DefaultHeight.ToString());
            Fill(frames, choices.Frames.Select(f => (f.ToString(), f.ToString())), choices.DefaultFrames.ToString());
            Fill(fps, Enumerable.Range(choices.FpsMin, choices.FpsMax - choices.FpsMin + 1).Select(f => (f.ToString(), f.ToString())), choices.DefaultFps.ToString());
            Fill(motion, choices.Motions.Select(m => (m, m == "idle" ? "Idle · gentle bob" : m == "bounce" ? "Bounce" : m == "shake" ? "Shake" : m)), choices.Motions.FirstOrDefault() ?? "idle");
            Fill(scope, new[] { ("all", "All frames"), ("current", "One frame") }, "all");
            Fill(exportScale, choices.ExportScales.Select(s => (s.ToString(), s == 1 ? "1× · original size" : s + "× · pixel perfect")), "1");
            hue.ValueChanged += (_, _) => hueValue.Text = $"{hue.Value:0}°";

            bool AI() => !IsOffline;
            Add(Row(Field("Action", action), Show(Field("Art style", artMode), AI), Show(Field("AI source", provider), AI), Show(Field("Image model", imageModel), () => AI() && Selected(provider)?.StartsWith("api:") == true)));
            Add(Row(Show(source, () => NeedsSource), Show(style, () => Act is "create" or "create+animation" or "edit" or "skin")));
            Add(Show(Row(Field("Replace", fromColor), Field("With", toColor)), () => Act == "recolor"));
            var hueRow = new StackPanel { Orientation = Orientation.Horizontal }; hueRow.Children.Add(hue); hueRow.Children.Add(hueValue);
            Add(Show(Field("Hue shift", hueRow), () => Act == "hue"));
            Add(Show(Row(wholeSheet), () => Act == "skin"));
            Add(Show(Row(animationSheet), () => Act == "edit"));
            Add(Show(Row(Field("Columns", columns), Field("Rows", rows)), () => (Act == "skin" && wholeSheet.IsChecked == true) || (Act == "edit" && animationSheet.IsChecked == true)));
            Add(Show(Row(Field("Apply to", scope), Show(Field("Frame number", frameNumber), () => Selected(scope) == "current")), () => Act == "edit" && animationSheet.IsChecked == true));
            Add(Show(Row(regionOn, Show(Row(Field("X", regionX), Field("Y", regionY), Field("Width", regionW), Field("Height", regionH)), () => regionOn.IsChecked == true)), () => Act == "edit" && animationSheet.IsChecked == true));
            Add(Row(Show(Field(Act == "direction" ? "Turn to" : "Facing", direction), AI),
                Show(Field("Width", width), () => AI() && !SheetWorkflow), Show(Field("Height", height), () => AI() && !SheetWorkflow),
                Show(Field("Frames", frames), () => Act is "animation" or "create+animation" or "procedural"),
                Show(Field("FPS", fps), () => Act is "animation" or "create+animation" || (Act == "edit" && animationSheet.IsChecked == true)),
                Show(Field("Motion", motion), () => Act == "procedural"),
                Show(Field("Export scale", exportScale), () => choices.ExportScales.Count > 1)));
            // "Lock to source", as in the tool's own panel: every lock option in one place, always
            // visible; the ones the chosen action does not use are greyed out with the reason.
            var lockAll = new Button { Content = "🔒 Lock to source", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 0, 6), HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = "One click: keep the source style, take its colours, keep its size and keep the first frame's pixels (where the action uses them)." };
            lockAll.Click += (_, _) => { foreach (var box in new[] { keepStyle, ownColours, lockScale, anchor }) if (box.IsEnabled) box.IsChecked = true; Refresh(); };
            var lockPanel = new StackPanel();
            lockPanel.Children.Add(lockAll);
            lockPanel.Children.Add(Row(keepStyle, ownColours, Show(strictColours, () => ownColours.IsChecked == true)));
            lockPanel.Children.Add(Row(lockScale, anchor));
            Add(Show(new GroupBox { Header = "Lock to source", Content = lockPanel, Padding = new Thickness(8, 6, 8, 4), Margin = new Thickness(0, 4, 0, 6) }, AI));
            Add(Row(Show(silhouette, () => Act == "skin"), Show(transparent, AI), Show(autoFix, AI)));
            Add(Row(Show(autoSwitch, AI), Show(paidFallback, () => AI() && autoSwitch.IsChecked == true && (choices.ApiImageModels.Count > 0 || choices.Providers.Contains("pixellab")))));

            action.SelectionChanged += (_, _) => { ApplyDefaults(); Refresh(); };
            provider.SelectionChanged += (_, _) => { FillModels(); Refresh(); };
            artMode.SelectionChanged += (_, _) => ApplyDefaults();
            scope.SelectionChanged += (_, _) => Refresh();
            foreach (var box in new[] { wholeSheet, animationSheet, regionOn, ownColours, autoSwitch })
            {
                box.Checked += (_, _) => Refresh();
                box.Unchecked += (_, _) => Refresh();
            }
            style.Changed += () => { ApplyDefaults(); Refresh(); };
            FillModels();
            ApplyDefaults();
            Refresh();
        }

        private string Act => Selected(action) ?? "animation";
        private bool IsOffline => Act is "recolor" or "hue" or "procedural";
        private bool NeedsSource => Act is not ("create" or "create+animation");
        private bool SheetWorkflow => (Act == "skin" && wholeSheet.IsChecked == true) || (Act == "edit" && animationSheet.IsChecked == true);

        private FrameworkElement Show(FrameworkElement element, Func<bool> when) { shown[element] = when; return element; }
        private void Add(FrameworkElement element) => Children.Add(element);
        private void Refresh()
        {
            foreach (var (element, when) in shown)
                element.Visibility = when() ? Visibility.Visible : Visibility.Collapsed;
            style.Title = Act == "skin" ? "New look reference (optional)" : "Style sample (optional)";
            style.ToolTip = Act == "skin" ? "The new character already reskinned once (e.g. its base image). Every frame of every sheet copies this look, so all of the character's animations match." : null;
            Usable(keepStyle, Act == "create" || Act == "create+animation" ? (style.Value != null ? null : "Choose a style sample to keep its style.") : null);
            Usable(lockScale, Act is "animation" or "create+animation" or "direction" or "edit" ? null : Act == "create" ? "A new image fills the frame; size is kept for animations and edits." : "Not used by this action.");
            Usable(anchor, Act is "animation" or "create+animation" ? null : "Only for animations.");
        }

        // A lock option the action does not use stays visible, greyed out, saying why.
        private static void Usable(CheckBox box, string? whyNot)
        {
            box.IsEnabled = whyNot == null;
            box.Opacity = whyNot == null ? 1 : 0.5;
            box.ToolTip = whyNot ?? box.Tag as string ?? box.ToolTip;
            if (whyNot == null && box.Tag is string tip) box.ToolTip = tip;
        }

        private void FillModels()
        {
            imageModel.Items.Clear();
            var vendor = Selected(provider)?.StartsWith("api:") == true ? Selected(provider)![4..] : null;
            var models = vendor == null ? [] : choices.ApiImageModels.GetValueOrDefault(vendor) ?? [];
            Fill(imageModel, models.Select(m => (m, m)), models.FirstOrDefault() ?? "");
        }

        // Animation keeps the source look; restyling after a sample takes the sample's colours.
        private void ApplyDefaults()
        {
            keepStyle.IsChecked = Act is "animation" or "skin" or "edit" or "direction" || (Act is "create" or "create+animation" && style.Value != null);
            ownColours.IsChecked = Selected(artMode) != "illustration" && !(Act == "edit" && style.Value != null) && Act != "skin";
        }

        // The request for the tool, and for "Create, then animate" also the first call (create).
        public (JsonObject Input, JsonObject? First, WorkAttachment[] Attachments) Build(string prompt)
        {
            if (NeedsSource && source.Value == null)
                throw new InvalidOperationException("Choose the source image.");
            static int Int(TextBox box, string name) => int.TryParse(box.Text.Trim(), out var n) ? n : throw new InvalidOperationException(name + " must be a whole number.");
            JsonObject Common(string act)
            {
                var input = new JsonObject { ["action"] = act };
                if (!IsOffline)
                {
                    input["prompt"] = prompt;
                    if (Selected(artMode) is { } mode) input["artMode"] = mode;
                    if (Selected(direction) is { } facing) input["direction"] = facing;
                    if (!SheetWorkflow && int.TryParse(width.Text, out var w)) input["outputWidth"] = w;
                    if (!SheetWorkflow && int.TryParse(height.Text, out var h)) input["outputHeight"] = h;
                    input["lockPalette"] = ownColours.IsChecked == true;
                    if (ownColours.IsChecked == true && strictColours.IsChecked == true) input["strictPalette"] = true;
                    input["preserveBackground"] = transparent.IsChecked != true;
                    input["autoRetry"] = autoFix.IsChecked == true;
                    input["autoSwitch"] = autoSwitch.IsChecked == true;
                    if (autoSwitch.IsChecked == true && paidFallback.IsChecked == true) input["fallbackToApi"] = true;
                    if (Selected(provider) is "claude" or "pixellab") input["provider"] = Selected(provider);
                    if (Selected(provider) is { } p && p.StartsWith("api:"))
                    {
                        input["provider"] = "api";
                        input["apiVendor"] = p[4..];
                        if (Selected(imageModel) is { Length: > 0 } model) input["imageModel"] = model;
                    }
                }
                if (int.TryParse(Selected(exportScale), out var scale) && scale > 1) input["exportScale"] = scale;
                return input;
            }

            JsonObject? first = null;
            JsonObject main;
            if (Act == "create+animation")
            {
                first = Common("create");
                first["prompt"] = "One still image of the character in the starting pose of this animation (a single figure, not a sprite sheet or sequence): " + prompt;
                first["preserveStyle"] = style.Value != null && keepStyle.IsChecked == true;
                first.Remove("exportScale");
                if (style.Value != null) first["styleGuide"] = style.Value.Reference();
                main = Common("animation");
                main["preserveStyle"] = true;
            }
            else
            {
                main = Common(Act);
                if (!IsOffline) main["preserveStyle"] = Act == "create" ? style.Value != null && keepStyle.IsChecked == true : keepStyle.IsChecked == true;
                if (NeedsSource) main["image"] = source.Value!.Reference();
                if (style.Value != null && Act is "create" or "edit") main["styleGuide"] = style.Value.Reference();
                // Reskin with a look reference: the new character already reskinned once, so every sheet of it matches.
                if (style.Value != null && Act == "skin")
                {
                    main["styleGuide"] = style.Value.Reference();
                    main["styleReferenceRole"] = "skin";
                }
            }
            switch (Act)
            {
                case "animation" or "create+animation":
                    main["frames"] = int.Parse(Selected(frames) ?? "8");
                    main["fps"] = int.Parse(Selected(fps) ?? "8");
                    main["anchorFirstFrame"] = anchor.IsChecked == true;
                    main["lockScale"] = lockScale.IsChecked == true;
                    break;
                case "direction":
                    main["lockScale"] = lockScale.IsChecked == true;
                    break;
                case "edit":
                    main["lockScale"] = lockScale.IsChecked == true;
                    if (animationSheet.IsChecked == true)
                    {
                        main["animation"] = new JsonObject { ["columns"] = Int(columns, "Columns"), ["rows"] = Int(rows, "Rows") };
                        main["scope"] = Selected(scope);
                        main["fps"] = int.Parse(Selected(fps) ?? "8");
                        if (Selected(scope) == "current") main["frameIndex"] = Int(frameNumber, "Frame number") - 1;
                        if (regionOn.IsChecked == true)
                            main["region"] = new JsonObject { ["x"] = Int(regionX, "X"), ["y"] = Int(regionY, "Y"), ["width"] = Int(regionW, "Width"), ["height"] = Int(regionH, "Height") };
                    }
                    break;
                case "skin":
                    main["keepSilhouette"] = silhouette.IsChecked == true;
                    if (wholeSheet.IsChecked == true)
                        main["sheet"] = new JsonObject { ["columns"] = Int(columns, "Columns"), ["rows"] = Int(rows, "Rows") };
                    break;
                case "recolor":
                    main["fromColor"] = fromColor.Text.Trim();
                    main["toColor"] = toColor.Text.Trim();
                    break;
                case "hue":
                    main["degrees"] = (int)hue.Value;
                    break;
                case "procedural":
                    main["motion"] = Selected(motion);
                    main["frames"] = int.Parse(Selected(frames) ?? "8");
                    break;
            }
            var files = new[] { NeedsSource ? source.Value : null, Act is "create" or "create+animation" or "edit" or "skin" ? style.Value : null }.Where(v => v?.Attachment != null).Select(v => v!.Attachment!).DistinctBy(a => a.Name).ToArray();
            return (main, first, files);
        }

        private static string? Selected(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string;
        private static void Fill(ComboBox box, IEnumerable<(string Value, string Label)> items, string selected)
        {
            foreach (var (value, label) in items)
                box.Items.Add(new ComboBoxItem { Content = label, Tag = value });
            box.SelectedItem = box.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == selected) ?? (box.Items.Count > 0 ? box.Items[0] : null);
            if (box.IsEditable) box.Text = selected;
        }
        private static string Compass(string d) => d switch { "N" => "back", "NE" => "back right", "E" => "right", "SE" => "front right", "S" => "front", "SW" => "front left", "W" => "left", "NW" => "back left", _ => d };
    }

    // A PNG chosen from disk: referenced by its project path when inside the project, attached otherwise.
    private sealed record PickedImage(string? ProjectPath, WorkAttachment? Attachment)
    {
        public JsonObject Reference() => ProjectPath != null ? new JsonObject { ["$projectPng"] = ProjectPath } : new JsonObject { ["$attachmentPng"] = Attachment!.Name };
    }

    private sealed class ImagePicker : StackPanel
    {
        private readonly Image preview = new() { Width = 56, Height = 56, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 8, 0) };
        private readonly TextBlock name = new() { Text = "None", TextWrapping = TextWrapping.Wrap, MaxWidth = 200, VerticalAlignment = VerticalAlignment.Center };
        public PickedImage? Value { get; private set; }
        public string? ProjectRoot { get; set; }
        public event Action? Changed;

        private readonly TextBlock title;
        public string Title { set => title.Text = value; }

        public ImagePicker(string title)
        {
            Margin = new Thickness(0, 0, 24, 0);
            MinWidth = 300;
            this.title = Label(title);
            Children.Add(this.title);
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            RenderOptions.SetBitmapScalingMode(preview, BitmapScalingMode.NearestNeighbor);
            line.Children.Add(preview);
            var choose = new Button { Content = "Choose…", Padding = new Thickness(10, 2, 10, 2), VerticalAlignment = VerticalAlignment.Center };
            var clear = new Button { Content = "×", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(4, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Clear" };
            choose.Click += (_, _) => Pick();
            clear.Click += (_, _) => Reset();
            line.Children.Add(choose);
            line.Children.Add(clear);
            line.Children.Add(name);
            Children.Add(line);
        }

        public void Reset()
        {
            Value = null;
            preview.Source = null;
            name.Text = "None";
            Changed?.Invoke();
        }

        private void Pick()
        {
            var dialog = new OpenFileDialog { Filter = "PNG images|*.png", InitialDirectory = ProjectRoot != null && Directory.Exists(ProjectRoot) ? ProjectRoot : "" };
            if (dialog.ShowDialog() != true)
                return;
            var bytes = File.ReadAllBytes(dialog.FileName);
            if (bytes.Length > 8 * 1024 * 1024 || bytes.Length < 8 || bytes[0] != 137 || bytes[1] != 80)
            {
                MessageBox.Show("Choose a PNG image up to 8 MB.", "Tool task", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            string? relative = null;
            if (ProjectRoot != null)
            {
                var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ProjectRoot)) + Path.DirectorySeparatorChar;
                var full = Path.GetFullPath(dialog.FileName);
                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    relative = full[root.Length..].Replace('\\', '/');
            }
            var fileName = Path.GetFileName(dialog.FileName);
            Value = new PickedImage(relative, relative == null ? new WorkAttachment(fileName, bytes) : null);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            preview.Source = image;
            name.Text = $"{fileName} · {image.PixelWidth}×{image.PixelHeight}";
            Changed?.Invoke();
        }
    }
}
