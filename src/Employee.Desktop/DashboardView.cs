using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Employee.Infrastructure;

namespace Employee.Desktop;
public sealed record ProjectOverview(string Name, string Detail, double Progress, string Id = "");
public sealed record ActivityOverview(string Time, string Title, string Detail);
public sealed record ProviderOverview(string Name, string State, string Usage, string Model = "");
public sealed record ProviderCallOverview(string Name, int Calls);
public sealed record DashboardSnapshot(int ActiveGoals, int Projects, int ReadyProviders, int ConnectedTools, int Calls, int? CallLimit, string Resources, string? GoalId, string GoalTitle, string ProjectName, string Coordinator, string Worker, string Step, string Elapsed, double Progress, IReadOnlyList<ProjectOverview> RecentProjects, IReadOnlyList<ActivityOverview> Activity, IReadOnlyList<ProviderOverview> Providers, IReadOnlyList<ProviderCallOverview>? ProviderCalls = null);
public sealed class DashboardView : UserControl
{
    private readonly StackPanel summary = new(), projects = new(), activity = new(), providers = new(), current = new(), usage = new(), resources = new();
    public event Action<string>? Navigate, OpenGoal, CreateProjectTask;
    public event Action? PauseRequested, StopRequested, SwitchRequested;
    private static readonly string[] Palette = ["#B57BF5", "#F69A60", "#53A9FB", "#55DBB1", "#9A90BF", "#7886ED"];
    public DashboardView()
    {
        // A single reference-sized composition scales down without wrapping the dashboard tiles.
        var layout = new Grid
        {
            Width = 1388,
            Height = 804,
            Margin = new Thickness(0)
        };
        layout.ColumnDefinitions.Add(new() { Width = new GridLength(948) });
        layout.ColumnDefinitions.Add(new() { Width = new GridLength(16) });
        layout.ColumnDefinitions.Add(new() { Width = new GridLength(424) });
        layout.RowDefinitions.Add(new() { Height = new GridLength(282) });
        layout.RowDefinitions.Add(new() { Height = new GridLength(16) });
        layout.RowDefinitions.Add(new() { Height = new GridLength(194) });
        layout.RowDefinitions.Add(new() { Height = new GridLength(16) });
        layout.RowDefinitions.Add(new() { Height = new GridLength(296) });
        layout.Children.Add(Hero());
        var middle = new Grid();
        foreach (var width in new[]
        {
            362,
            16,
            282,
            16,
            272
        }

        )
            middle.ColumnDefinitions.Add(new() { Width = new GridLength(width) });
        middle.Children.Add(Card(summary));
        var u = Card(usage);
        Grid.SetColumn(u, 2);
        middle.Children.Add(u);
        var hardware = Card(resources);
        Grid.SetColumn(hardware, 4);
        middle.Children.Add(hardware);
        Grid.SetRow(middle, 2);
        layout.Children.Add(middle);
        var bottom = new Grid();
        bottom.ColumnDefinitions.Add(new() { Width = new GridLength(464) });
        bottom.ColumnDefinitions.Add(new() { Width = new GridLength(16) });
        bottom.ColumnDefinitions.Add(new() { Width = new GridLength(468) });
        bottom.Children.Add(Card(projects));
        var feed = Card(activity);
        Grid.SetColumn(feed, 2);
        bottom.Children.Add(feed);
        Grid.SetRow(bottom, 4);
        layout.Children.Add(bottom);
        var right = new Grid();
        right.RowDefinitions.Add(new() { Height = new GridLength(396) });
        right.RowDefinitions.Add(new() { Height = new GridLength(16) });
        right.RowDefinitions.Add(new() { Height = new GridLength(392) });
        right.Children.Add(Card(current));
        var providerCard = Card(providers);
        Grid.SetRow(providerCard, 2);
        right.Children.Add(providerCard);
        Grid.SetColumn(right, 2);
        Grid.SetRowSpan(right, 5);
        layout.Children.Add(right);
        Content = new Viewbox
        {
            Child = layout,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(14, 8, 14, 8)
        };
    }

    private Border Card(UIElement content) => new()
    {
        Style = (Style)FindResource("DashboardCard"),
        Child = content
    };
    private static TextBlock Text(string value, double size = 13, bool bold = false, string color = "#EEEAFB") => new()
    {
        Text = value,
        FontSize = size,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = UiArt.Brush(color),
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center
    };
    private static Border Rule() => new()
    {
        Height = 1,
        Background = UiArt.Brush("#3B3551"),
        Margin = new Thickness(0, 10, 0, 10)
    };
    private static ProgressBar Bar(double value, string color = "#B17BF9", double height = 8) => new()
    {
        Minimum = 0,
        Maximum = 100,
        Value = Math.Clamp(value, 0, 100),
        Height = height,
        Foreground = UiArt.Brush(color),
        Background = UiArt.Brush("#39334F"),
        BorderThickness = new Thickness(0),
        VerticalAlignment = VerticalAlignment.Center
    };
    private UIElement Heading(string icon, string title, string? action = null, Action? clicked = null)
    {
        var row = new DockPanel
        {
            Margin = new Thickness(0, 0, 0, 12)
        };
        var graphic = UiArt.Icon(icon, 22);
        graphic.Margin = new Thickness(0, 0, 10, 0);
        row.Children.Add(graphic);
        if (action != null)
        {
            var link = new Button
            {
                Content = action + "  →",
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(2),
                Margin = new Thickness(4, 0, 0, 0),
                MinHeight = 20,
                FontSize = 12,
                Foreground = UiArt.Brush("#C3A4FF")
            };
            link.Click += (_, _) => clicked?.Invoke();
            DockPanel.SetDock(link, Dock.Right);
            row.Children.Add(link);
        }

        row.Children.Add(Text(title, 16, true));
        return row;
    }

    private Border Hero()
    {
        var root = new Grid
        {
            ClipToBounds = true
        };
        root.Children.Add(new Image { Source = new BitmapImage(new Uri("pack://application:,,,/Assets/dashboard-hero.png")), Width = 680, Height = 227, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -12, -1, 0), IsHitTestVisible = false });
        var title = new StackPanel
        {
            Margin = new Thickness(26, 25, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            MaxWidth = 670
        };
        title.Children.Add(Text("AI Employee Manager", 36, true));
        var subtitle = Text("Orchestrates multiple AIs, projects and tools — like a real employee.", 14, false, "#C5BDDC");
        subtitle.Margin = new Thickness(0, 8, 0, 0);
        title.Children.Add(subtitle);
        root.Children.Add(title);
        var shortcuts = new Grid
        {
            Margin = new Thickness(20, 0, 18, 22),
            VerticalAlignment = VerticalAlignment.Bottom
        };
        var entries = new[]
        {
            ("code", "Development\nCoding", "tasks"),
            ("game", "Game\nUnity", "tasks"),
            ("image", "Pixel Art", "tools"),
            ("video", "Video\nEditing", "tools"),
            ("marketing", "Marketing /\nSocial", "tools"),
            ("search", "Research", "tasks"),
            ("idea", "Planning", "workflows"),
            ("more", "", "tools")
        };
        for (var i = 0; i < entries.Length; i++)
        {
            shortcuts.ColumnDefinitions.Add(new() { Width = new GridLength(i == 7 ? 0.6 : 1, GridUnitType.Star) });
            var entry = entries[i];
            var body = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center
            };
            body.Children.Add(UiArt.Icon(entry.Item1, 32, i == 5 ? "#9D9BFF" : "#D194F4"));
            var label = Text(entry.Item2, 13);
            label.TextAlignment = TextAlignment.Center;
            label.Margin = new Thickness(0, 12, 0, 0);
            body.Children.Add(label);
            var button = new Button
            {
                Content = body,
                Height = 109,
                Padding = new Thickness(5, 8, 5, 8),
                Margin = new Thickness(i == 0 ? 0 : 12, 0, 0, 0),
                Background = UiArt.Gradient("#302947", "#1F1C32"),
                BorderBrush = UiArt.Brush("#49405F"),
                BorderThickness = new Thickness(1)
            };
            button.Click += (_, _) => Navigate?.Invoke(entry.Item3);
            Grid.SetColumn(button, i);
            shortcuts.Children.Add(button);
        }

        root.Children.Add(shortcuts);
        return new Border
        {
            Child = root,
            CornerRadius = new CornerRadius(16),
            BorderBrush = UiArt.Brush("#50466F"),
            BorderThickness = new Thickness(1),
            Background = UiArt.Brush("#141326")
        };
    }

    public void Refresh(DashboardSnapshot value)
    {
        summary.Children.Clear();
        summary.Children.Add(Heading("chart", "System overview"));
        var counts = new UniformGrid
        {
            Columns = 4
        };
        var metrics = new[]
        {
            ("activity", value.ActiveGoals, "Running\ngoals"),
            ("projects", value.Projects, "Registered\nprojects"),
            ("local", value.ReadyProviders, "Available\nAI providers"),
            ("tools", value.ConnectedTools, "Connected\ntools")
        };
        foreach (var item in metrics)
        {
            var tile = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center
            };
            tile.Children.Add(UiArt.Icon(item.Item1, 24, "#BA90FC"));
            var number = Text(item.Item2.ToString(), 23, true);
            number.TextAlignment = TextAlignment.Center;
            number.Margin = new Thickness(0, 9, 0, 5);
            tile.Children.Add(number);
            var label = Text(item.Item3, 11);
            label.TextAlignment = TextAlignment.Center;
            tile.Children.Add(label);
            counts.Children.Add(new Border { Child = tile, Height = 126, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 10, 0), Background = UiArt.Gradient("#302A46", "#221E34"), BorderBrush = UiArt.Brush("#49405D"), BorderThickness = new Thickness(1) });
        }

        summary.Children.Add(counts);
        RefreshUsage(value);
        RefreshResources(value.Resources);
        projects.Children.Clear();
        projects.Children.Add(Heading("folder", "Recent projects", "View all", () => Navigate?.Invoke("projects")));
        foreach (var project in value.RecentProjects.Take(4))
        {
            var row = new Grid
            {
                Height = 55
            };
            row.ColumnDefinitions.Add(new() { Width = new GridLength(52) });
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = new GridLength(144) });
            var thumb = UiArt.ProjectThumbnail(project.Name, 42);
            thumb.ToolTip = "Project illustration";
            row.Children.Add(thumb);
            var label = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center
            };
            label.Children.Add(Text(project.Name, 14, true));
            var detail = Text(project.Detail, 10, false, "#B3A9CC");
            detail.Margin = new Thickness(0, 4, 5, 0);
            label.Children.Add(detail);
            Grid.SetColumn(label, 1);
            row.Children.Add(label);
            var progress = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center
            };
            var percent = Text($"{project.Progress:F0}%", 12);
            percent.TextAlignment = TextAlignment.Right;
            percent.Margin = new Thickness(0, 0, 0, 7);
            progress.Children.Add(percent);
            progress.Children.Add(Bar(project.Progress));
            progress.ToolTip = "Share of tasks approved as complete, not overall project progress.";
            Grid.SetColumn(progress, 2);
            row.Children.Add(progress);
            var projectButton = new Button
            {
                Content = row,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(0),
                Margin = new Thickness(0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "Create task for " + project.Name
            };
            projectButton.Click += (_, _) => CreateProjectTask?.Invoke(project.Id);
            projects.Children.Add(projectButton);
            projects.Children.Add(new Border { Height = 1, Background = UiArt.Brush("#39334A") });
        }

        if (value.RecentProjects.Count == 0)
            projects.Children.Add(Text("Register a folder in Settings → Projects to get started.", 13, false, "#B5ABC9"));
        activity.Children.Clear();
        activity.Children.Add(Heading("pulse", "Recent activity", "Xem log", () => Navigate?.Invoke("activity")));
        foreach (var item in value.Activity.Take(5))
        {
            var row = new Grid
            {
                Height = 45
            };
            row.ColumnDefinitions.Add(new() { Width = new GridLength(15) });
            row.ColumnDefinitions.Add(new() { Width = new GridLength(51) });
            row.ColumnDefinitions.Add(new() { Width = new GridLength(34) });
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            var timeline = new Grid();
            timeline.Children.Add(new Border { Width = 1, Background = UiArt.Brush("#736091") });
            timeline.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = UiArt.Brush("#B58AF5"), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(timeline);
            var time = Text(item.Time.Length >= 5 ? item.Time[^5..] : item.Time, 11, false, "#B7ACCC");
            Grid.SetColumn(time, 1);
            row.Children.Add(time);
            var icon = UiArt.Badge(item.Title.Contains("Switch", StringComparison.OrdinalIgnoreCase) ? "switch" : "approvals", 29);
            Grid.SetColumn(icon, 2);
            row.Children.Add(icon);
            var line = Text(item.Title + " · " + item.Detail, 11);
            line.TextWrapping = TextWrapping.NoWrap;
            line.TextTrimming = TextTrimming.CharacterEllipsis;
            line.ToolTip = item.Time + "\n" + item.Title + "\n" + item.Detail;
            line.Margin = new Thickness(6, 0, 0, 0);
            Grid.SetColumn(line, 3);
            row.Children.Add(line);
            activity.Children.Add(row);
        }

        if (value.Activity.Count == 0)
            activity.Children.Add(Text("Activity appears once you assign work.", 13, false, "#B5ABC9"));
        RefreshCurrent(value);
        RefreshProviders(value);
    }

    private void RefreshUsage(DashboardSnapshot value)
    {
        usage.Children.Clear();
        usage.Children.Add(Heading("providers", "AI usage · today"));
        var body = new Grid();
        body.ColumnDefinitions.Add(new() { Width = new GridLength(114) });
        body.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var ratio = value.CallLimit is> 0 ? Math.Clamp(100.0 * value.Calls / value.CallLimit.Value, 0, 100) : 0;
        var ring = new Grid
        {
            Width = 108,
            Height = 108,
            Margin = new Thickness(0, 5, 0, 0)
        };
        ring.Children.Add(new Ellipse { Stroke = UiArt.Brush("#4C3F6E"), StrokeThickness = 11, Margin = new Thickness(0) });
        if (ratio > 0)
        {
            if (ratio >= 100)
                ring.Children.Add(new Ellipse { Stroke = UiArt.Gradient("#D594FF", "#8251D6"), StrokeThickness = 11, Margin = new Thickness(0) });
            else
            {
                var angle = ratio * Math.PI * 2 / 100;
                var end = new Point(54 + 48 * Math.Sin(angle), 54 - 48 * Math.Cos(angle));
                var figure = new PathFigure
                {
                    StartPoint = new Point(54, 6)
                };
                figure.Segments.Add(new ArcSegment(end, new Size(48, 48), 0, ratio > 50, SweepDirection.Clockwise, true));
                ring.Children.Add(new System.Windows.Shapes.Path { Data = new PathGeometry(new[] { figure }), Stroke = UiArt.Gradient("#D594FF", "#8251D6"), StrokeThickness = 11, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
            }
        }

        var center = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center
        };
        var main = Text(value.CallLimit is> 0 ? $"{ratio:F0}%" : value.Calls.ToString(), 23, true);
        main.TextAlignment = TextAlignment.Center;
        center.Children.Add(main);
        var caption = Text(value.CallLimit is> 0 ? $"{value.Calls} / {value.CallLimit}" : "calls", 10, false, "#B4A6CE");
        caption.TextAlignment = TextAlignment.Center;
        center.Children.Add(caption);
        ring.Children.Add(center);
        ring.ToolTip = "Orchestrated calls / your limit; not your AI account quota.";
        body.Children.Add(ring);
        var legend = new StackPanel
        {
            Margin = new Thickness(4, 5, 0, 0)
        };
        var known = value.ProviderCalls ?? Array.Empty<ProviderCallOverview>();
        var index = 0;
        foreach (var name in new[]
        {
            "codex",
            "claude",
            "copilot",
            "gemini",
            "ollama"
        }

        )
        {
            var row = new DockPanel
            {
                Height = 23
            };
            var dot = new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = UiArt.Brush(Palette[index++]),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(dot);
            var calls = known.FirstOrDefault(p => p.Name == name)?.Calls ?? 0;
            var number = Text(calls.ToString(), 11);
            DockPanel.SetDock(number, Dock.Right);
            row.Children.Add(number);
            row.Children.Add(Text(ProviderName(name).Split(' ')[0], 11));
            legend.Children.Add(row);
        }

        Grid.SetColumn(legend, 1);
        body.Children.Add(legend);
        usage.Children.Add(body);
        var note = Text("Manager calls · not quota", 9, false, "#A89DBD");
        note.Margin = new Thickness(0, 8, 0, 0);
        usage.Children.Add(note);
    }

    private void RefreshResources(string reported)
    {
        resources.Children.Clear();
        resources.Children.Add(Heading("monitor", "Machine resources"));
        var cpu = Regex.Match(reported, @"CPU manager: ([\d.,]+)%");
        var cpuKnown = double.TryParse(cpu.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var cpuValue);
        Resource("CPU · Manager", cpuKnown ? $"{cpuValue:F1}%" : "—", cpuKnown ? cpuValue : 0, "#62E5BE");
        var memory = ResourceMonitor.Snapshot();
        var used = memory == null ? 0 : memory.TotalMemoryMb - memory.AvailableMemoryMb;
        Resource("RAM", memory == null ? "—" : $"{used / 1024.0:F1} / {memory.TotalMemoryMb / 1024.0:F0} GB", memory is { TotalMemoryMb: > 0 } ? 100.0 * used / memory.TotalMemoryMb : 0, "#48B8F2");
        var gpu = Regex.Match(reported, @"VRAM \[.*?\]:\s*(\d+)\s*,\s*(\d+)");
        var gpuKnown = long.TryParse(gpu.Groups[1].Value, out var total) && total > 0;
        long.TryParse(gpu.Groups[2].Value, out var gpuUsed);
        Resource("VRAM", gpuKnown ? $"{gpuUsed / 1024.0:F1} / {total / 1024.0:F0} GB" : "Unknown", gpuKnown ? 100.0 * gpuUsed / total : 0, "#B46BEE");
        void Resource(string label, string detail, double amount, string color)
        {
            var row = new DockPanel
            {
                Margin = new Thickness(0, 3, 0, 5)
            };
            var value = Text(detail, 11, false, "#DBD4EB");
            DockPanel.SetDock(value, Dock.Right);
            row.Children.Add(value);
            row.Children.Add(Text(label, 12));
            resources.Children.Add(row);
            var bar = Bar(amount, color, 7);
            bar.Margin = new Thickness(0, 0, 0, 9);
            resources.Children.Add(bar);
        }
    }

    private void RefreshCurrent(DashboardSnapshot value)
    {
        current.Children.Clear();
        current.Children.Add(Heading("rocket", "Current goal", value.GoalId == null ? null : "Details", () =>
        {
            if (value.GoalId is { } id)
                OpenGoal?.Invoke(id);
        }));
        var header = new Grid
        {
            Height = 82
        };
        header.ColumnDefinitions.Add(new() { Width = new GridLength(96) });
        header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var badge = UiArt.ProjectThumbnail(value.ProjectName, 82);
        header.Children.Add(badge);
        var task = new StackPanel();
        task.Children.Add(Text(value.ProjectName, 18, true));
        var title = Text(value.GoalTitle, 13);
        title.MaxHeight = 30;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.Margin = new Thickness(0, 5, 0, 6);
        task.Children.Add(title);
        task.Children.Add(new Border { Child = Text(value.GoalId == null ? "Waiting for work" : $"In progress ({value.Progress:F0}%)", 11, false, "#E0BAFF"), Background = UiArt.Brush("#43305F"), CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 4, 10, 4), HorizontalAlignment = HorizontalAlignment.Left });
        Grid.SetColumn(task, 1);
        header.Children.Add(task);
        current.Children.Add(new Border { Child = header, Padding = new Thickness(10), Background = UiArt.Gradient("#28243C", "#211D32"), CornerRadius = new CornerRadius(13), BorderBrush = UiArt.Brush("#46405B"), BorderThickness = new Thickness(1) });
        var progress = new DockPanel
        {
            Margin = new Thickness(0, 12, 0, 0)
        };
        var percent = Text($"{value.Progress:F0}%", 12);
        percent.Margin = new Thickness(12, 0, 0, 0);
        DockPanel.SetDock(percent, Dock.Right);
        progress.Children.Add(percent);
        progress.Children.Add(Bar(value.Progress));
        current.Children.Add(progress);
        current.Children.Add(Rule());
        Detail("agents", "Coordinator", value.Coordinator);
        Detail("providers", "Worker", value.Worker);
        Detail("activity", "Current step", value.Step);
        Detail("clock", "Elapsed", value.Elapsed);
        var actions = new UniformGrid
        {
            Columns = 3,
            Margin = new Thickness(0, 8, 0, 0)
        };
        foreach (var action in new[]
        {
            ("pause", "Pause", PauseRequested),
            ("switch", "Switch AI", SwitchRequested),
            ("stop", "Stop", StopRequested)
        }

        )
        {
            var content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            content.Children.Add(UiArt.Icon(action.Item1, 15));
            var label = Text(action.Item2, 12);
            label.Margin = new Thickness(7, 0, 0, 0);
            content.Children.Add(label);
            var button = new Button
            {
                Content = content,
                Padding = new Thickness(6, 8, 6, 8),
                Margin = new Thickness(0, 0, 7, 0),
                MinHeight = 36,
                IsEnabled = value.GoalId != null
            };
            button.Click += (_, _) => action.Item3?.Invoke();
            actions.Children.Add(button);
        }

        current.Children.Add(actions);
        void Detail(string icon, string label, string data)
        {
            var row = new Grid
            {
                Height = 25
            };
            row.ColumnDefinitions.Add(new() { Width = new GridLength(28) });
            row.ColumnDefinitions.Add(new() { Width = new GridLength(102) });
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(UiArt.Icon(icon, 18));
            var name = Text(label, 12, false, "#C2B6D8");
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            var detail = Text(data, 12);
            detail.TextWrapping = TextWrapping.NoWrap;
            detail.TextTrimming = TextTrimming.CharacterEllipsis;
            detail.ToolTip = data;
            Grid.SetColumn(detail, 2);
            row.Children.Add(detail);
            current.Children.Add(row);
        }
    }

    private void RefreshProviders(DashboardSnapshot value)
    {
        providers.Children.Clear();
        providers.Children.Add(Heading("providers", "AI provider status", "Manage", () => Navigate?.Invoke("settings:AI")));
        var list = new StackPanel();
        foreach (var provider in value.Providers.Take(6))
        {
            var row = new Grid
            {
                Height = 51
            };
            row.ColumnDefinitions.Add(new() { Width = new GridLength(46) });
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = new GridLength(86) });
            row.Children.Add(UiArt.Badge(provider.Name == "ollama" ? "local" : provider.Name == "codex" ? "spark" : provider.Name == "copilot" ? "game" : "providers", 36));
            var labels = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center
            };
            labels.Children.Add(Text(ProviderName(provider.Name), 13, true));
            var ready = provider.State.Contains("ready", StringComparison.OrdinalIgnoreCase);
            var state = Text("●  " + provider.State, 10, false, ready ? "#65E5A4" : "#B0A5C6");
            state.Margin = new Thickness(0, 4, 0, 0);
            state.TextWrapping = TextWrapping.NoWrap;
            state.TextTrimming = TextTrimming.CharacterEllipsis;
            labels.Children.Add(state);
            Grid.SetColumn(labels, 1);
            row.Children.Add(labels);
            var quota = Text(provider.Model.Length > 0 ? provider.Model : "Quota —", 10, false, "#C5BCD9");
            quota.TextAlignment = TextAlignment.Right;
            quota.ToolTip = provider.Usage;
            Grid.SetColumn(quota, 2);
            row.Children.Add(quota);
            row.ToolTip = provider.Usage;
            list.Children.Add(row);
            list.Children.Add(new Border { Height = 1, Background = UiArt.Brush("#3B354E") });
        }

        providers.Children.Add(new Border { Child = list, CornerRadius = new CornerRadius(13), BorderBrush = UiArt.Brush("#48405F"), BorderThickness = new Thickness(1), Padding = new Thickness(10, 4, 10, 4) });
    }

    private static string ProviderName(string name) => name switch
    {
        "codex" => "Codex (OpenAI)",
        "claude" => "Claude (Anthropic)",
        "copilot" => "Copilot (GitHub)",
        "gemini" => "Gemini (Google)",
        "ollama" => "Ollama (Local)",
        "opencode" => "OpenCode",
        "qoder" => "Qoder",
        "antigravity" => "Antigravity CLI (agy)",
        _ => name
    };
}
