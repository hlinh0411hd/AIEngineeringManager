using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Employee.Core;

namespace Employee.Desktop;
public sealed record TaskDiagramSnapshot(WorkItem Goal, WorkSession? Session, WorkItem[] Steps, PlanRecord[] Plans, PendingDecision[] Decisions);
public sealed class TaskDiagramWindow : Window
{
    private sealed record Node(string Id, string Title, string Detail, Brush Color);
    private sealed record Edge(string From, string To, bool Dependency);
    private readonly Canvas canvas = new();
    private readonly TextBlock summary = new()
    {
        TextTrimming = TextTrimming.CharacterEllipsis,
        Margin = new Thickness(0, 8, 0, 8)
    };
    private readonly TextBlock status = new()
    {
        TextWrapping = TextWrapping.Wrap
    };
    private readonly Button more = new()
    {
        Content = "Show more",
        HorizontalAlignment = HorizontalAlignment.Left,
        Visibility = Visibility.Collapsed
    };
    private string fullRequest = "";
    private readonly Func<TaskDiagramSnapshot> read;
    private readonly DispatcherTimer timer = new()
    {
        Interval = TimeSpan.FromSeconds(2)
    };
    private string? previous;
    public TaskDiagramWindow(Func<TaskDiagramSnapshot> read)
    {
        SetResourceReference(StyleProperty, typeof(Window));
        this.read = read;
        Title = "Task orchestration diagram";
        Width = 1180;
        Height = 800;
        MinWidth = 760;
        MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel
        {
            Margin = new Thickness(20)
        };
        var header = new StackPanel();
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);
        header.Children.Add(new TextBlock { Text = "Orchestration diagram", FontSize = 23, FontWeight = FontWeights.SemiBold });
        header.Children.Add(summary);
        more.Click += (_, _) => ShowInfo("Task details", fullRequest);
        header.Children.Add(more);
        header.Children.Add(status);
        header.Children.Add(new TextBlock { Text = "Solid arrow: prerequisite → next step · Dashed line: delegation / handoff · Click a node for details", TextWrapping = TextWrapping.Wrap });
        var controls = new WrapPanel
        {
            Margin = new Thickness(0, 10, 0, 0)
        };
        header.Children.Add(controls);
        var refresh = new Button
        {
            Content = "Refresh"
        };
        refresh.Click += (_, _) => Refresh(true);
        controls.Children.Add(refresh);
        var zoom = new Slider
        {
            Minimum = .4,
            Maximum = 1.6,
            Value = 1,
            Width = 170,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Diagram zoom"
        };
        zoom.ValueChanged += (_, _) => canvas.LayoutTransform = new ScaleTransform(zoom.Value, zoom.Value);
        controls.Children.Add(zoom);
        panel.Children.Add(new ScrollViewer { Content = canvas, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = panel;
        timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) =>
        {
            Refresh(true);
            timer.Start();
        };
        Closed += (_, _) => timer.Stop();
    }

    private void Refresh(bool force = false)
    {
        try
        {
            var data = read();
            var signature = JsonSerializer.Serialize(data);
            if (!force && signature == previous)
                return;
            previous = signature;
            Render(data);
        }
        catch (Exception ex)
        {
            summary.Text = "Cannot load diagram: " + ex.Message;
        }
    }

    private void Render(TaskDiagramSnapshot data)
    {
        var nodes = new List<Node>();
        var edges = new List<Edge>();
        var goal = data.Goal;
        fullRequest = goal.Request;
        var preview = goal.RequestPreview;
        summary.Text = preview.Length > 100 ? preview[..100] + "…" : preview;
        more.Visibility = fullRequest.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        status.Text = $"State: {goal.State} · Session: {data.Session?.State.ToString() ?? "Not started"} · {data.Steps.Count(w => !w.IsCoordinator)} steps · {data.Decisions.Count(d => d.State == DecisionState.Pending)} pending approvals";
        void AddWork(WorkItem work, string title)
        {
            var route = work.ProviderId == null ? work.PreferredRoute : new Route(work.ProviderId, work.ModelId ?? "", "", AutoModel: string.IsNullOrWhiteSpace(work.ModelId));
            var ai = route == null ? "No AI selected" : route.ProviderId + " / " + (string.IsNullOrEmpty(route.ModelId) ? "CLI default" : route.ModelId);
            var decisions = data.Decisions.Where(d => d.WorkId == work.Id).Select(d => $"{d.Kind} ({d.State}): {d.Message}");
            nodes.Add(new(work.Id, title + "\n" + work.State + " · " + ai, $"{work.Request}\n\nID: {work.Id}\n{work.Kind} · {work.Access} · Tests: {work.Verification}\nAI: {ai}\nAttempts: {work.Attempts} · AI switches: {work.Switches}\n{work.Notes}\n" + string.Join("\n", decisions), work.State == WorkState.Completed ? Brushes.SeaGreen : work.State == WorkState.Running ? Brushes.RoyalBlue : work.State == WorkState.NeedsReview || work.State == WorkState.AwaitingApproval ? Brushes.DarkOrange : Brushes.SlateGray));
        }

        AddWork(goal, "Assigned request");
        foreach (var work in data.Steps)
        {
            AddWork(work, (work.IsCoordinator ? "Planning: " : "Step: ") + Short(work.Request));
            edges.Add(new(work.ParentTaskId ?? goal.Id, work.Id, false));
            edges.AddRange(work.DependsOn.Select(id => new Edge(id, work.Id, true)));
        }

        // Pending/rejected plans are proposals, never represented as executing steps.
        foreach (var plan in data.Plans.Where(p => p.State != DecisionState.Approved))
        {
            var planId = "plan:" + plan.Id;
            nodes.Add(new(planId, "Plan · " + plan.State, plan.Plan.Summary, Brushes.DarkOrange));
            edges.Add(new(goal.Id, planId, false));
            foreach (var task in plan.Plan.Tasks)
            {
                var id = planId + ":" + task.Key;
                nodes.Add(new(id, task.Key + " · Proposal\n" + Short(task.Request), $"{task.Request}\n{task.Kind} · {task.Access}\nPlan: {plan.State}\nAfter step: {string.Join(", ", task.DependsOn)}", Brushes.DarkOrange));
                edges.Add(new(planId, id, false));
                edges.AddRange(task.DependsOn.Select(key => new Edge(planId + ":" + key, id, true)));
            }
        }

        canvas.Children.Clear();
        var levels = nodes.ToDictionary(n => n.Id, _ => 0);
        // Bounded relaxation also tolerates malformed legacy relationships without recursion.
        for (var pass = 0; pass < nodes.Count; pass++)
        {
            var changed = false;
            foreach (var edge in edges.Where(e => levels.ContainsKey(e.From) && levels.ContainsKey(e.To)))
                if (levels[edge.To] <= levels[edge.From])
                {
                    levels[edge.To] = Math.Min(nodes.Count, levels[edge.From] + 1);
                    changed = true;
                }

            if (!changed)
                break;
        }

        var positions = new Dictionary<string, Point>();
        foreach (var group in nodes.GroupBy(n => levels[n.Id]))
        {
            var row = 0;
            foreach (var node in group)
                positions[node.Id] = new Point(24 + group.Key * 350, 24 + row++ * 145);
        }

        canvas.Width = positions.Values.Max(p => p.X) + 330;
        canvas.Height = positions.Values.Max(p => p.Y) + 150;
        foreach (var edge in edges)
        {
            if (!positions.TryGetValue(edge.From, out var start) || !positions.TryGetValue(edge.To, out var end))
                continue;
            var a = new Point(start.X + 290, start.Y + 55);
            var b = new Point(end.X, end.Y + 55);
            var line = new Line
            {
                X1 = a.X,
                Y1 = a.Y,
                X2 = b.X,
                Y2 = b.Y,
                Stroke = edge.Dependency ? Brushes.RoyalBlue : Brushes.SlateGray,
                StrokeThickness = 2,
                ToolTip = edge.Dependency ? "Dependencies" : "Delegation / handoff"
            };
            if (!edge.Dependency)
                line.StrokeDashArray = new DoubleCollection
                {
                    4,
                    3
                };
            canvas.Children.Add(line);
            var direction = b - a;
            if (direction.Length > 0)
                direction.Normalize();
            var normal = new Vector(-direction.Y, direction.X);
            canvas.Children.Add(new Polygon { Points = new PointCollection { b, b - direction * 11 + normal * 5, b - direction * 11 - normal * 5 }, Fill = line.Stroke });
        }

        foreach (var node in nodes)
        {
            var button = new Button
            {
                Width = 290,
                Height = 110,
                Background = new SolidColorBrush(Color.FromRgb(41, 37, 63)),
                BorderBrush = node.Color,
                BorderThickness = new Thickness(2),
                Margin = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = new TextBlock
                {
                    Text = node.Title,
                    TextWrapping = TextWrapping.Wrap,
                    MaxHeight = 85
                }
            };
            button.Click += (_, _) => ShowInfo("Step details", node.Detail);
            Canvas.SetLeft(button, positions[node.Id].X);
            Canvas.SetTop(button, positions[node.Id].Y);
            canvas.Children.Add(button);
        }
    }

    private void ShowInfo(string title, string text)
    {
        var body = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(16)
        };
        var window = new Window
        {
            Owner = this,
            Title = title,
            Width = Math.Min(760, SystemParameters.WorkArea.Width),
            Height = Math.Min(560, SystemParameters.WorkArea.Height),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = body
        };
        window.SetResourceReference(StyleProperty, typeof(Window));
        window.ShowDialog();
    }

    private static string Short(string text) => text.Length <= 90 ? text : text[..90] + "…";
}
