using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Employee.Core;
using Employee.Infrastructure;

namespace Employee.Desktop;

// A presentation-only scene. It reads task state and never drives task execution.
public sealed class VirtualOfficeView : UserControl
{
    // Isometric office (v2 art), everything drawn at native size. The floor atlas holds 7 tiles of
    // 106x77 side by side: a 106x66 top face plus 11 px of edge, so one tile on the grid is 106x66.
    private const int MapWidth = 24, MapHeight = 24, TileWidth = 106, TileHeight = 66;
    private const int FloorTileWidth = 106, FloorTileHeight = 77, CorridorFloor = 6;
    private const int ActorWidth = 128, ActorHeight = 128, Step = 5, InteractionTicks = 36;
    private const double OriginX = MapHeight * TileWidth / 2 + 80, OriginY = 130;
    private const string Art = "v2/";
    // Eight department rooms (6x6 tiles) on a 3x3 grid with the middle left open; every room has two
    // rows of two desks, the manager one big desk in the middle. Research sits next to the manager:
    // a new task is researched first, then handed to the manager to plan.
    // Rooms have 2-tile corridors between them. Each room has tall walls
    // on its two back sides, its own floor, furniture along the walls and a fixed set of seats (one
    // per job in that department) grouped in the middle.
    private sealed record Seat(string Role, WorkKind? Kind = null);
    private sealed record Room(string Name, int X0, int Y0, int Floor, string[] Props, Seat[] Seats);
    private static readonly Room[] Rooms =
    [
        new("Manager", 1, 1, 5, ["plant-tall", "bookshelf", "bookshelf", "cabinet", "whiteboard", "plant-tall", "cooler", "cabinet", "bookshelf", "plant", "coffee", "plant-potted", "bin"], [new("Manager")]),
        new("Research", 9, 1, 3, ["bookshelf", "bookshelf", "whiteboard", "bookshelf", "cabinet", "plant-tall", "plant", "desk", "desk", "bin", "plant", "plant", "plant"], [new("Researcher", WorkKind.Research), new("Researcher", WorkKind.Research), new("Researcher", WorkKind.Research), new("Researcher", WorkKind.Research)]),
        new("Development", 17, 1, 1, ["cabinet", "printer", "bookshelf", "whiteboard", "cabinet", "plant-tall", "cooler", "desk", "desk", "plant", "bin", "plant", "plant"], [new("Developer"), new("Developer"), new("Developer"), new("Developer")]),
        new("Data & Analysis", 1, 9, 1, ["cabinet", "cabinet", "printer", "whiteboard", "cabinet", "plant-tall", "cooler", "desk", "desk", "bin", "plant", "plant", "plant"], [new("Classification", WorkKind.Classification), new("Extraction", WorkKind.Extraction), new("Classification", WorkKind.Classification), new("Extraction", WorkKind.Extraction)]),
        new("Design & Art", 17, 9, 2, ["whiteboard", "plant-tall", "whiteboard", "bookshelf", "printer", "plant-tall", "coffee", "desk", "desk", "bin", "plant", "plant-potted", "plant"], [new("Designer"), new("Designer"), new("Designer"), new("Designer")]),
        new("Creative", 1, 17, 0, ["plant-tall", "whiteboard", "cabinet", "bookshelf", "whiteboard", "plant-tall", "coffee", "desk", "desk", "bin", "plant", "plant", "plant"], [new("Writer"), new("Writer"), new("Writer"), new("Writer")]),
        new("QA & Review", 9, 17, 4, ["cabinet", "cabinet", "whiteboard", "printer", "bookshelf", "plant-tall", "cooler", "desk", "desk", "bin", "plant", "plant", "plant"], [new("Reviewer"), new("Reviewer"), new("Reviewer"), new("Reviewer")]),
        new("External Tools", 17, 17, 3, ["printer", "cabinet", "printer", "cabinet", "bookshelf", "plant-tall", "coffee", "desk", "desk", "bin", "plant", "plant-potted", "plant"], [new("Tool operator"), new("Tool operator"), new("Tool operator"), new("Tool operator")])
    ];
    // Each room has one desk style (sheet); the manager keeps table_1, the others are spread over the
    // desk sheets that exist (table_2 is the props sheet).
    private static readonly Dictionary<string, string> RoomTable = new()
    {
        ["Manager"] = "table_1.png", ["Development"] = "table_3.png", ["Research"] = "table_4.png",
        ["Creative"] = "table_1.png", ["QA & Review"] = "table_4.png", ["External Tools"] = "table_3.png",
        ["Data & Analysis"] = "table_3.png", ["Design & Art"] = "table_1.png"
    };
    private static string TableOf(string? room) => room != null && RoomTable.TryGetValue(room, out var t) ? t : "table_4.png";
    private const int RoomSize = 6, WallHeight = 104;
    // Desks and furniture are drawn larger than their sheets so they read well next to the people.
    private const double FurnitureScale = 1.5;
    private const double SpareDeskScale = 1.0;
    private const string PottedPlantFile = "office-plant-se.png";
    private const double PottedPlantScale = 0.55;
    // Furniture cells inside a room, in the order of a room's Props: the whole back wall, the left
    // wall (side desks against it), the right edge and the front corners. Row 1 and the front rows
    // stay free for walking.
    private static readonly (int X, int Y)[] PropCells = [(0, 0), (1, 0), (2, 0), (3, 0), (4, 0), (5, 0), (0, 1), (0, 2), (0, 4), (5, 1), (5, 3), (5, 5), (0, 5)];
    // Floor atlas order: 0 light wood, 1 marble, 2 peach, 3 green, 4 blue stone, 5 dark wood, 6 corridor.
    // Pieces of the v2 sheets (x, y, width, height).
    private static readonly Dictionary<string, Int32Rect> Pieces = new()
    {
        ["wall"] = new(13, 45, 50, 56),
        ["plant-tall"] = new(13, 50, 48, 106), ["whiteboard"] = new(180, 72, 61, 86), ["bookshelf"] = new(100, 77, 42, 80), ["cooler"] = new(147, 80, 27, 76),
        ["cabinet"] = new(59, 101, 35, 56), ["plant"] = new(27, 166, 31, 44), ["printer"] = new(68, 173, 42, 38), ["coffee"] = new(122, 168, 32, 43),
        ["bin"] = new(168, 180, 23, 29), ["lamp"] = new(201, 171, 28, 38)
    };
    private enum Pose { Idle, Working, Reporting, Walking, WalkingBack }

    private sealed class Actor(string zone, string key, Border element, Point home, (int X, int Y) homeCell)
    {
        public string Zone { get; } = zone;
        // The seat this worker sits at ("seat:<room>:<n>").
        public string Key { get; } = key;
        public Border Element { get; } = element;
        public Point Home { get; } = home;
        public (int X, int Y) HomeCell { get; } = homeCell;
        public SpriteAnimation? Sprite { get; set; }
        public string Character { get; set; } = "";
        public bool FacingLeft { get; set; }
        public Image? Badge { get; set; }
        public Movement? Moving { get; set; }
        // Set while this actor is the receiving side of someone else's handoff.
        public Movement? Receiving { get; set; }
        // Random ambient activity (visiting, reporting, strolling); AtDesk whenever none is going on.
        public AmbientState AmbientState { get; set; }
    }

    private sealed class Movement(string actionId, OfficeTransition transition, string target, Queue<Point> route, int outbound)
    {
        // The queued action this movement carries out; completed when the movement finishes.
        public string ActionId { get; } = actionId;
        // A timed action stays at the desk and finishes after StandTicks (no walking).
        public bool Timed { get; init; }
        public OfficeTransition Transition { get; } = transition;
        public string Target { get; } = target;
        public Queue<Point> Route { get; } = route;
        public int Outbound { get; set; } = outbound;
        public int Ticks { get; set; }
        public int StandTicks { get; set; } = 8;
        // Set for a random ambient activity: it has no queued action behind it and is never saved.
        public AmbientKind? Ambient { get; init; }
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
        // The manager-side standing spot reserved by a report, or -1.
        public int Spot { get; set; } = -1;
        // Points of the route where the worker stops (and for how long) before walking on.
        public Queue<(Point Point, int Ticks, bool Talk)> Stops { get; } = new();
        // Ambient movements are cut off only after their own route has had time to finish.
        public TimeSpan TimeLimit { get; init; }
        public bool Reporting => Transition.Activity.StartsWith("Reporting", StringComparison.Ordinal);
    }

    private readonly Canvas scene = new() { Width = (MapWidth + MapHeight) * TileWidth / 2 + 160, Height = (MapWidth + MapHeight) * TileHeight / 2 + 200, Background = Brushes.Transparent };
    private readonly ScaleTransform scale = new(DefaultScale, DefaultScale);
    private const double DefaultScale = 0.55;
    private readonly HashSet<(int X, int Y)> blockedCells = [];
    private readonly TranslateTransform offset = new();
    private Point dragStart;
    private Point offsetStart;
    private bool dragging;
    private readonly Dictionary<string, Border> desks = new();
    private readonly Dictionary<string, TextBlock> deskStates = new();
    private readonly Dictionary<string, StackPanel> infoPanels = new();
    private readonly DispatcherTimer infoTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly Dictionary<string, ProgressBar> deskProgress = new();
    private readonly Dictionary<string, Actor> actors = new();
    private readonly Dictionary<string, (string Zone, WorkState State)> previous = new();
    private readonly Dictionary<string, WorkState> previousGoals = new();
    // One FIFO lane per character: an action starts only when the previous one has completed.
    private readonly ActorActionQueue actions = new();
    private const int TimedActionTicks = 30;
    // Random ambient activity (see Employee.Core/OfficeAmbient.cs). Settings live in AmbientOptions.
    private readonly AmbientScheduler ambient = new();
    // Corridor cells (outside every room) a worker may stroll to.
    private static readonly (int X, int Y)[] HallwayWaypoints = [(7, 7), (8, 8), (7, 8), (8, 7), (15, 7), (16, 8), (7, 3), (8, 4), (15, 4), (16, 3), (7, 12), (8, 13), (15, 12), (16, 11)];
    // Where a visitor to the manager stands: in front of / beside the desk, relative to the manager room.
    private static readonly (int X, int Y)[] ManagerReportSpots = [(3, 4), (2, 4)];
    // How far a visitor stands from the desk owner while standing behind the same desk.
    private static readonly Vector VisitorOffset = new(-46, 8);
    private readonly HashSet<int> reportSpotsInUse = [];
    private IOfficeActionStore? actionStore;
    private bool actionsRestored;
    private int savedActionsVersion = -1;
    private readonly DispatcherTimer animationTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private int animationFrame;
    private WorkItem[] visibleWork = [];
    private readonly TextBlock transition = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private TaskExecutionPool? tasks;
    private Func<IEnumerable<WorkItem>>? readTasks;
    private Action<string>? openGoal;
    private Action<string>? openSwitch;
    private Func<WorkItem, string>? readDetails;
    private readonly StackPanel inspector = new() { Margin = new Thickness(12) };
    private Window? window;
    private bool subscribed;
    private bool enabled = true;
    private bool sceneBuilt;
    private bool hasSnapshot;
    private readonly Dictionary<string, DateTime> recentResults = new();
    private readonly UIElement officeContent;
    private readonly UIElement offContent;
    private int refreshPending;
    public event Action? EnableRequested;

    public VirtualOfficeView()
    {
        infoTimer.Tick += (_, _) => HideInfoPanels();
        animationTimer.Tick += Animate;
        var root = new DockPanel { Margin = new Thickness(24) };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 8, 8, 0) };
        Panel.SetZIndex(buttons, 10);
        AddRoundButton(buttons, "+", "Zoom in", () => Zoom(1.2));
        AddRoundButton(buttons, "−", "Zoom out", () => Zoom(1 / 1.2));
        AddRoundButton(buttons, "⟲", "Reset view", Reset);
        var title = new StackPanel();
        title.Children.Add(new TextBlock { Text = "Rik's office", FontSize = 24, FontWeight = FontWeights.SemiBold });
        title.Children.Add(new TextBlock { Text = "Scroll to zoom · Drag to pan · Click a task on the right to open it", Foreground = Brush("#B8B0CF"), Margin = new Thickness(0, 5, 0, 0) });
        title.Children.Add(transition);
        header.Children.Add(title);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var viewport = new Border { Background = Brush("#141424"), BorderBrush = Brush("#403954"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), ClipToBounds = true, Focusable = true };
        var transforms = new TransformGroup();
        transforms.Children.Add(scale);
        transforms.Children.Add(offset);
        scene.RenderTransform = transforms;
        viewport.Child = scene;
        viewport.MouseWheel += (_, e) => { Zoom(e.Delta > 0 ? 1.2 : 1 / 1.2); e.Handled = true; };
        viewport.MouseLeftButtonDown += (_, e) => { dragging = true; dragStart = e.GetPosition(viewport); offsetStart = new Point(offset.X, offset.Y); viewport.CaptureMouse(); };
        viewport.MouseMove += (_, e) => { if (!dragging) return; var p = e.GetPosition(viewport); offset.X = offsetStart.X + p.X - dragStart.X; offset.Y = offsetStart.Y + p.Y - dragStart.Y; };
        viewport.MouseLeftButtonUp += (_, _) => { dragging = false; viewport.ReleaseMouseCapture(); };
        viewport.LostMouseCapture += (_, _) => dragging = false;
        var inspectorBorder = new Border { Width = 340, Background = Brush("#1D1A2E"), CornerRadius = new CornerRadius(10), Margin = new Thickness(12, 0, 0, 0), Child = new ScrollViewer { Content = inspector, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        DockPanel.SetDock(inspectorBorder, Dock.Right);
        root.Children.Add(inspectorBorder);
        var viewportHost = new Grid();
        viewportHost.Children.Add(viewport);
        viewportHost.Children.Add(buttons);
        root.Children.Add(viewportHost);
        inspector.Children.Add(new TextBlock { Text = "Active tasks", FontSize = 18, FontWeight = FontWeights.SemiBold });
        inspector.Children.Add(new TextBlock { Text = "No task is being worked on.", Foreground = Brush("#B8B0CF"), Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap });
        officeContent = root;
        var off = new StackPanel { Margin = new Thickness(24), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        off.Children.Add(new TextBlock { Text = "Rik's office is off", FontSize = 24, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        off.Children.Add(new TextBlock { Text = "Tasks and AI keep running. Nothing is drawn or animated.", Foreground = Brush("#B8B0CF"), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Center });
        var enableButton = new Button { Content = "Turn on", Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        enableButton.Click += (_, _) => EnableRequested?.Invoke();
        off.Children.Add(enableButton);
        offContent = off;
        Loaded += (_, _) => { window = Window.GetWindow(this); if (window != null) window.StateChanged += WindowStateChanged; UpdateActivity(); };
        Unloaded += (_, _) => { Pause(); if (window != null) window.StateChanged -= WindowStateChanged; window = null; };
        IsVisibleChanged += (_, _) => UpdateActivity();
    }

    public void Attach(TaskExecutionPool service, Func<IEnumerable<WorkItem>> snapshot, Action<string> open, Action<string> switchAi, Func<WorkItem, string> details, IOfficeActionStore? actionStore = null)
    {
        Unsubscribe();
        this.actionStore = actionStore;
        // Restore the saved queue once at startup; stale entries are dropped when their turn comes.
        if (actionStore != null && !actionsRestored)
        {
            actionsRestored = true;
            try { actions.Restore(actionStore.OfficeActions()); savedActionsVersion = actions.Version; }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Data.Common.DbException or System.Text.Json.JsonException) { actions.Clear(); }
        }
        tasks = service;
        readTasks = snapshot;
        openGoal = open;
        openSwitch = switchAi;
        readDetails = details;
        if (IsLoaded) UpdateActivity();
    }

    public void SetEnabled(bool value)
    {
        enabled = value;
        if (!enabled)
        {
            Pause();
            foreach (var actor in actors.Values) { actor.Moving = null; actor.Receiving = null; }
            scene.Children.Clear();
            desks.Clear();
            deskStates.Clear();
            deskProgress.Clear();
            actors.Clear();
            seatWork.Clear();
            workSeat.Clear();
            sceneBuilt = false;
        }
        Content = enabled ? officeContent : offContent;
        UpdateActivity();
    }

    private bool Rendering => IsLoaded && VirtualOffice.ShouldRender(enabled, IsVisible, window?.WindowState == WindowState.Minimized);

    private void UpdateActivity()
    {
        if (!Rendering)
        {
            Pause();
            return;
        }
        if (!sceneBuilt)
        {
            BuildScene();
            sceneBuilt = true;
            // Lanes of seats that no longer exist can never run.
            actions.RemoveSeats(seat => !actors.ContainsKey(seat));
        }
        Subscribe();
        RefreshOffice();
        animationTimer.Start();
        foreach (var actor in actors.Values)
            if (actor.Sprite != null)
                actor.Sprite.IsPlaying = true;
    }

    // Stops all drawing work; the next activation starts again from a fresh snapshot, not from history.
    private void Pause()
    {
        Unsubscribe();
        animationTimer.Stop();
        HideInfoPanels();
        // Drawing stops: running actions go back to the front of their lane (kept, and saved), and
        // restart from the desk when the office is shown again, if they are still current.
        actions.InterruptAll();
        PersistActions();
        previous.Clear();
        previousGoals.Clear();
        hasSnapshot = false;
        recentResults.Clear();
        visibleWork = [];
        foreach (var actor in actors.Values)
        {
            actor.Moving = null;
            actor.Receiving = null;
            actor.AmbientState = AmbientState.AtDesk;
            if (actor.Sprite != null) actor.Sprite.IsPlaying = false;
            Place(actor, actor.Home);
        }
        transition.Text = "";
        reportSpotsInUse.Clear();
        ambient.Reset();
    }

    private void Subscribe() { if (!subscribed && tasks != null) { tasks.Changed += TasksChanged; subscribed = true; } }
    private void Unsubscribe() { if (subscribed && tasks != null) tasks.Changed -= TasksChanged; subscribed = false; }
    private void WindowStateChanged(object? sender, EventArgs e) => UpdateActivity();
    private void TasksChanged()
    {
        if (System.Threading.Interlocked.Exchange(ref refreshPending, 1) != 0) return;
        Dispatcher.InvokeAsync(() => { System.Threading.Interlocked.Exchange(ref refreshPending, 0); RefreshOffice(); });
    }

    private static string Zone(WorkItem work) => VirtualOffice.ZoneFor(work);

    private void RefreshOffice()
    {
        if (!Rendering || readTasks == null) return;
        // Only current work is shown; opening the tab does not replay history.
        var all = readTasks().ToArray();
        var candidates = OfficeWork(all);
        if (hasSnapshot)
            foreach (var item in candidates)
                if (IsResult(item.State) && previous.TryGetValue(item.Id, out var old) && old.State != item.State)
                    recentResults[item.Id] = DateTime.UtcNow.AddSeconds(10);
        foreach (var id in recentResults.Keys.Where(id => recentResults[id] <= DateTime.UtcNow || !candidates.Any(w => w.Id == id && IsResult(w.State))).ToArray())
            recentResults.Remove(id);
        if (recentResults.Count == 0 && actions.TotalCount == 0) transition.Text = "";
        var work = candidates.Where(w => !IsResult(w.State) || recentResults.ContainsKey(w.Id)).ToArray();
        visibleWork = work;
        var current = work.ToDictionary(w => w.Id, w => (Zone: Zone(w), State: w.State));
        var created = new List<OfficeTransition>();
        if (hasSnapshot)
        {
            var goalSteps = all.Where(w => w.ParentTaskId != null).GroupBy(w => w.ParentTaskId!).ToDictionary(g => g.Key, g => (IReadOnlyCollection<WorkItem>)g.ToArray());
            foreach (var changed in work.Where(w => !previous.TryGetValue(w.Id, out var old) || old.State != w.State || old.Zone != Zone(w)).OrderBy(w => w.UpdatedAt ?? w.CreatedAt))
                created.Add(TransitionFor(changed, goalSteps));
            // A finished step is not a finished goal: report only when the goal itself reaches a
            // reportable state, carried by the step of that goal that finished last.
            foreach (var goal in all.Where(w => w.IsGoal && previousGoals.TryGetValue(w.Id, out var state) && state != w.State && VirtualOffice.IsReportable(w.State)))
            {
                var messenger = goalSteps.TryGetValue(goal.Id, out var steps) ? VirtualOffice.Messenger(steps) : null;
                if (messenger != null && current.ContainsKey(messenger.Id))
                    created.Add(new(messenger.Id, Zone(messenger), messenger.State, $"Reporting · {VirtualOffice.ActivityFor(goal.State)}", Zone(messenger), "Manager", goal.Id, goal.State));
            }
        }
        previousGoals.Clear();
        foreach (var goal in all.Where(w => w.IsGoal)) previousGoals[goal.Id] = goal.State;
        previous.Clear();
        foreach (var item in candidates) previous[item.Id] = (Zone(item), item.State);
        hasSnapshot = true;
        AssignSeats(work);
        RecallForWaitingWork();
        // Each new action joins the end of the lane of the character who performs it. A running action
        // is never cancelled or reset: later actions wait for it to complete.
        var queuedAt = DateTimeOffset.UtcNow;
        foreach (var newAction in created)
            if (MoverFor(newAction) is { } owner)
                actions.Enqueue(owner.Key, newAction, queuedAt);
        foreach (var (name, desk) in desks)
        {
            var assigned = work.Where(w => Zone(w) == name).ToArray();
            var menu = new ContextMenu();
            foreach (var item in assigned)
            {
                var goalId = tasks?.GoalFor(item).Id ?? item.Id;
                var entry = new MenuItem { Header = $"{item.State} · {item.RequestPreview} · {item.ProviderId ?? "—"} / {item.ModelDisplay}" };
                entry.Click += (_, _) => openGoal?.Invoke(goalId);
                menu.Items.Add(entry);
            }
            if (assigned.Length == 0) menu.Items.Add(new MenuItem { Header = "No active work", IsEnabled = false });
            desk.ContextMenu = menu;
            desk.ToolTip = $"{name}: {assigned.Length} item(s) · click to view";
            deskStates[name].Text = assigned.Length == 0 ? "Idle" : string.Join("\n", assigned.OrderByDescending(w => w.State == WorkState.Running).Take(3).Select(w => ShortLabel(w))) + (assigned.Length > 3 ? $"\n+{assigned.Length - 3}" : "");
            var selected = assigned.OrderByDescending(w => w.State == WorkState.Running).ThenByDescending(w => w.UpdatedAt ?? w.CreatedAt).FirstOrDefault();
            var progressGoalId = selected == null ? null : tasks?.GoalFor(selected).Id;
            var steps = progressGoalId == null ? Array.Empty<WorkItem>() : all.Where(w => w.ParentTaskId == progressGoalId).ToArray();
            var bar = deskProgress[name];
            bar.Visibility = selected?.State == WorkState.Running && steps.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
            if (steps.Length > 1) { bar.Maximum = steps.Length; bar.Value = steps.Count(w => w.State == WorkState.Completed); bar.ToolTip = $"{bar.Value}/{steps.Length} steps completed"; }
        }
        ShowTaskList(all);
    }

    // The visual for one task state change. A step that starts receives its work from where its
    // input actually came from (the step it depends on, or the Manager when it depends on nothing).
    private OfficeTransition TransitionFor(WorkItem changed, IReadOnlyDictionary<string, IReadOnlyCollection<WorkItem>> goalSteps)
    {
        var zone = Zone(changed);
        var owner = changed.ParentTaskId == null ? null : tasks?.GoalFor(changed);
        var goalId = owner?.IsGoal == true ? owner.Id : null;
        var activity = Phase(changed);
        string? source = null, target = null;
        if (changed.State == WorkState.Running && !changed.IsGoal && goalId != null && goalSteps.TryGetValue(goalId, out var steps)
            && VirtualOffice.HandoffSource(changed, steps) is { } from)
        {
            source = from;
            target = zone;
            activity = from == "Manager" ? "Delegation" : "Handoff";
        }
        return new(changed.Id, zone, changed.State, activity, source, target, goalId, owner?.IsGoal == true ? owner.State : null);
    }

    private static string Phase(WorkItem work) => work.State == WorkState.Running && (work.IsCoordinator || work.Kind == WorkKind.Planning)
        ? "Planning"
        : VirtualOffice.ActivityFor(work.State);

    private static bool IsResult(WorkState state) => state is WorkState.Failed or WorkState.Saved || TaskOutcomes.IsClosed(state);

    private static WorkItem[] OfficeWork(WorkItem[] all)
    {
        var parents = all.Where(w => w.ParentTaskId != null).Select(w => w.ParentTaskId!).ToHashSet();
        return all.Where(w => (!w.IsGoal || !parents.Contains(w.Id)) && w.State is (WorkState.Running or WorkState.Paused or WorkState.AwaitingApproval or WorkState.WaitingForProvider or WorkState.WaitingForPermission or WorkState.NeedsReview or WorkState.Delegated or WorkState.Failed or WorkState.Completed or WorkState.Saved or WorkState.Skipped or WorkState.WrongAccess or WorkState.WrongProject)).ToArray();
    }

    // The side panel lists the tasks being worked on (top-level tasks, not their steps): running first,
    // then those waiting for an AI, an approval or the user's review. A click opens the task's details.
    private string taskListSignature = "";
    private static readonly WorkState[] ActiveStates = [WorkState.Running, WorkState.Queued, WorkState.Delegated, WorkState.WaitingForProvider, WorkState.AwaitingApproval, WorkState.WaitingForPermission, WorkState.NeedsReview, WorkState.Paused];

    private void ShowTaskList(WorkItem[] all)
    {
        var steps = all.Where(w => w.ParentTaskId != null && !w.IsCoordinator).GroupBy(w => w.ParentTaskId!).ToDictionary(g => g.Key, g => g.ToArray());
        var active = all.Where(w => !w.IsCoordinator && (w.IsGoal || w.ParentTaskId == null) && ActiveStates.Contains(w.State))
            .OrderBy(w => Array.IndexOf(ActiveStates, w.State)).ThenByDescending(w => w.UpdatedAt ?? w.CreatedAt).Take(40).ToArray();
        string Progress(WorkItem goal) => steps.TryGetValue(goal.Id, out var list) && list.Length > 0 ? $"{list.Count(w => w.State is WorkState.Completed or WorkState.Skipped)}/{list.Length} steps" : "";
        string Where(WorkItem goal) => steps.TryGetValue(goal.Id, out var list) && list.OrderByDescending(w => w.State == WorkState.Running).ThenByDescending(w => w.UpdatedAt ?? w.CreatedAt).FirstOrDefault() is { } step ? Zone(step) : Zone(goal);
        var signature = string.Join("|", active.Select(w => $"{w.Id}:{w.State}:{Progress(w)}:{Where(w)}:{w.RequestPreview}"));
        if (signature == taskListSignature && inspector.Children.Count > 0)
            return;
        taskListSignature = signature;
        inspector.Children.Clear();
        inspector.Children.Add(new TextBlock { Text = active.Length == 0 ? "Active tasks" : $"Active tasks · {active.Length}", FontSize = 18, FontWeight = FontWeights.SemiBold });
        if (active.Length == 0)
        {
            inspector.Children.Add(new TextBlock { Text = "No task is being worked on.", Foreground = Brush("#B8B0CF"), Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap });
            return;
        }
        foreach (var goal in active)
        {
            var card = new Button
            {
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 8, 0, 0),
                Padding = new Thickness(10, 8, 10, 8),
                Background = Brush(goal.State == WorkState.Running ? "#2B2546" : "#24203A"),
                BorderBrush = Brush(goal.State == WorkState.Running ? "#7C66C7" : "#3A3452"),
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "Open task details"
            };
            System.Windows.Automation.AutomationProperties.SetName(card, "Open task: " + goal.RequestPreview);
            var body = new StackPanel();
            body.Children.Add(new TextBlock { Text = goal.RequestPreview, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxHeight = 40, TextTrimming = TextTrimming.CharacterEllipsis });
            var line = string.Join(" · ", new[] { goal.State.ToString(), Progress(goal), Where(goal) }.Where(part => !string.IsNullOrWhiteSpace(part)));
            body.Children.Add(new TextBlock { Text = line, Foreground = Brush(goal.State == WorkState.Running ? "#C9B8FF" : "#B8B0CF"), FontSize = 12, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap });
            card.Content = body;
            var id = goal.Id;
            card.Click += (_, _) => openGoal?.Invoke(id);
            inspector.Children.Add(card);
        }
    }

    private static Brush Brush(string value) => (Brush)new BrushConverter().ConvertFromString(value)!;
    private static void AddButton(Panel parent, string label, Action action)
    {
        var button = new Button { Content = label, MinWidth = 44, Margin = new Thickness(4, 0, 0, 0), ToolTip = label };
        button.Click += (_, _) => action();
        parent.Children.Add(button);
    }
    private static void AddRoundButton(Panel parent, string glyph, string tooltip, Action action)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, Brush("#2A2640"));
        border.SetValue(Border.BorderBrushProperty, Brush("#6B628A"));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(11));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        var button = new Button
        {
            Content = glyph, ToolTip = tooltip, Width = 22, Height = 22, Padding = new Thickness(0), FontSize = 12,
            Margin = new Thickness(4, 0, 0, 0), Foreground = Brushes.White, Cursor = Cursors.Hand,
            Template = new ControlTemplate(typeof(Button)) { VisualTree = border }
        };
        button.Click += (_, e) => { action(); e.Handled = true; };
        parent.Children.Add(button);
    }
    private static string ShortLabel(WorkItem work)
    {
        string Words(string text, int max) => string.Join(' ', text.Split(new[] { ' ', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries).Take(max));
        string label;
        if (work.Tool != null)
        {
            var op = work.Tool.Operation.ToLowerInvariant();
            var verb = op.Contains("animat") ? "animating" : op.Contains("export") ? "exporting" : op.Contains("edit") ? "editing" : "drawing";
            label = $"Tool {Words(work.Tool.ToolId, 3).ToLowerInvariant()} {verb}";
        }
        else if (work.IsCoordinator || work.Kind == WorkKind.Planning) label = "Planning";
        else label = work.Kind switch
        {
            WorkKind.Coding => work.RequestPreview.Contains("debug", StringComparison.OrdinalIgnoreCase) || work.RequestPreview.Contains("fix", StringComparison.OrdinalIgnoreCase) ? "Debugging" : "Coding",
            WorkKind.Review => "QA testing",
            WorkKind.Research => "Researching",
            WorkKind.Content => "Content writing",
            WorkKind.Classification => "Classifying",
            WorkKind.Extraction => "Extracting data",
            _ => Words(work.RequestPreview, 4)
        };
        return work.State == WorkState.Running ? label : $"{label} ({VirtualOffice.ActivityFor(work.State)})";
    }
    private void Zoom(double factor) { var next = Math.Clamp(scale.ScaleX * factor, 0.5, 2.5); scale.ScaleX = scale.ScaleY = next; }
    private void Reset() { scale.ScaleX = scale.ScaleY = DefaultScale; offset.X = offset.Y = 0; }

    // Things further down the screen are drawn in front; the floor is always behind.
    private void Put(UIElement element, double x, double y, double depth)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        Panel.SetZIndex(element, (int)depth);
        scene.Children.Add(element);
    }

    private static void Place(Actor actor, Point point)
    {
        Canvas.SetLeft(actor.Element, point.X);
        Canvas.SetTop(actor.Element, point.Y);
        Panel.SetZIndex(actor.Element, (int)(point.Y + ActorHeight) + 1);
    }

    private static Point MapPoint(int x, int y) => new(OriginX + (x - y) * TileWidth / 2.0, OriginY + (x + y) * TileHeight / 2.0);
    // Top-left of a worker standing on a cell: feet on the cell centre.
    private static Point Stand((int X, int Y) cell) { var foot = MapPoint(cell.X, cell.Y); return new(foot.X - ActorWidth / 2.0, foot.Y - ActorHeight + 14); }
    private bool Walkable(int x, int y) => x >= 0 && y >= 0 && x < MapWidth && y < MapHeight && !blockedCells.Contains((x, y));
    private static Room? RoomAt(int x, int y) => Rooms.FirstOrDefault(r => x >= r.X0 && x < r.X0 + RoomSize && y >= r.Y0 && y < r.Y0 + RoomSize);
    // The tall back walls can't be crossed; rooms open to the corridor at the front.
    private static bool CanStep((int X, int Y) a, (int X, int Y) b)
    {
        foreach (var room in new[] { RoomAt(a.X, a.Y), RoomAt(b.X, b.Y) })
        {
            if (room == null) continue;
            var other = RoomAt(a.X, a.Y) == room ? b : a;
            if (RoomAt(other.X, other.Y) != room && (other.X < room.X0 || other.Y < room.Y0)) return false;
        }
        return true;
    }
    // Worker cell and desk cell (relative to the room) for seat i of n: desks grouped in the
    // middle with a free tile between the two desks of a row, each worker behind (up-left of) the
    // desk, looking at the screen.
    private static ((int X, int Y) Worker, (int X, int Y) Desk) Slot(Room room, int i)
    {
        (int, int)[][] layouts =
        [
            [(2, 3)],
            [(1, 2), (1, 4)],
            [(1, 2), (1, 4), (3, 2)],
            [(1, 2), (1, 4), (3, 2), (3, 4)]
        ];
        var (x, y) = layouts[Math.Clamp(room.Seats.Length, 1, 4) - 1][i];
        return ((room.X0 + x, room.Y0 + y), (room.X0 + x + 1, room.Y0 + y));
    }

    private static Image Sprite(BitmapSource source, double scaleBy = 1)
    {
        var image = new Image { Source = source, Width = source.PixelWidth * scaleBy, Height = source.PixelHeight * scaleBy, Stretch = Stretch.Fill, IsHitTestVisible = false };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        return image;
    }
    private static BitmapSource? Piece(string sheet, string name) => OfficeAssets.Load(Art + sheet) is { } image && Pieces.TryGetValue(name, out var rect) ? OfficeAssets.Crop(image, rect) : null;
    // Tile n of the floor atlas; withEdge keeps the side edge (only the office's front rim shows it).
    private static BitmapSource? FloorTile(int n, bool withEdge) => OfficeAssets.Load(Art + "1._Light_Wood_Floor_Warm_beige_subtle_horizonta.png") is { } atlas
        ? OfficeAssets.Crop(atlas, new Int32Rect(n * FloorTileWidth, 0, FloorTileWidth, withEdge ? FloorTileHeight : TileHeight + 1)) : null;
    // One tile's stretch of a tall back wall: face from the edge's upper end a to its lower end b,
    // a baseboard, and a top cap leaning back by outward.
    private void TallWall(Point a, Point b, Vector outward, bool lit, double depth)
    {
        var up = new Vector(0, WallHeight);
        var face = new System.Windows.Shapes.Polygon { Points = [a, b, b - up, a - up], Fill = Brush(lit ? "#F3E4CF" : "#E2CDB2"), IsHitTestVisible = false };
        var baseboard = new System.Windows.Shapes.Polygon { Points = [a, b, b - new Vector(0, 9), a - new Vector(0, 9)], Fill = Brush("#B08F6E"), IsHitTestVisible = false };
        var cap = new System.Windows.Shapes.Polygon { Points = [a - up, b - up, b - up + outward, a - up + outward], Fill = Brush("#D6BC98"), IsHitTestVisible = false };
        var rim = new System.Windows.Shapes.Line { X1 = a.X, Y1 = a.Y - WallHeight, X2 = b.X, Y2 = b.Y - WallHeight, Stroke = Brush("#7A5C44"), StrokeThickness = 1, IsHitTestVisible = false };
        var edge = new System.Windows.Shapes.Line { X1 = a.X + outward.X, Y1 = a.Y - WallHeight + outward.Y, X2 = b.X + outward.X, Y2 = b.Y - WallHeight + outward.Y, Stroke = Brush("#7A5C44"), StrokeThickness = 1, IsHitTestVisible = false };
        foreach (var part in new UIElement[] { face, baseboard, cap, rim, edge })
            Put(part, 0, 0, depth);
    }
    // The open front end of a wall: its thickness seen from the side.
    private void WallEnd(Point bottom, Vector outward, double depth)
    {
        var up = new Vector(0, WallHeight);
        Put(new System.Windows.Shapes.Polygon { Points = [bottom, bottom + outward, bottom + outward - up, bottom - up], Fill = Brush("#C9AC86"), Stroke = Brush("#7A5C44"), StrokeThickness = 1, IsHitTestVisible = false }, 0, 0, depth);
    }
    // Which way each sheet piece faces as drawn (plants and bins look the same either way).
    private static readonly HashSet<string> FacesDownRight = ["bookshelf", "cabinet", "cooler", "printer", "desk"];
    private static readonly HashSet<string> FacesDownLeft = ["whiteboard", "coffee"];
    private void Prop(string name, (int X, int Y) cell)
    {
        // A spare desk against a wall, screen turned to the room: desk 0 of the sheet faces down-right
        // (for the left wall), mirrored it faces down-left (for the right wall).
        var spare = name == "desk" && OfficeAssets.Load(Art + TableOf(RoomAt(cell.X, cell.Y)?.Name)) is { } desks ? OfficeAssets.Crop(desks, new Int32Rect(0, 0, 128, 128)) : null;
        var potted = name == "plant-potted" && OfficeAssets.Load(PottedPlantFile) is { } plantImg
            ? OfficeAssets.Crop(plantImg, OfficeAssets.OpaqueBounds(plantImg, new Int32Rect(0, 0, plantImg.PixelWidth, plantImg.PixelHeight)))
            : null;
        if ((spare ?? potted ?? Piece("table_2.png", name == "plant-potted" ? "plant" : name)) is not { } prop) return;
        var p = MapPoint(cell.X, cell.Y);
        // Spare desks against a wall are drawn smaller so neighbours don't overlap; generated 128x128 props scale to match.
        var scaleBy = spare != null ? SpareDeskScale : potted != null ? PottedPlantScale : FurnitureScale;
        var sprite = Sprite(prop, scaleBy);
        // Turn furniture to face the room: against the left wall it faces down-right, elsewhere
        // (right wall, open edges) down-left. Mirror the pieces drawn the other way.
        var room = RoomAt(cell.X, cell.Y);
        var onLeftWall = room != null && cell.X == room.X0 && cell.Y != room.Y0;
        // Desk 0 of table_1 and table_4 is drawn facing down-left (table_3: down-right), so their
        // spare desks are mirrored the other way round.
        var deskDrawnDownLeft = spare != null && TableOf(room?.Name) is "table_1.png" or "table_4.png";
        var mirror = spare != null ? onLeftWall == deskDrawnDownLeft
            : FacesDownRight.Contains(name) ? !onLeftWall : FacesDownLeft.Contains(name) && onLeftWall;
        if (mirror)
            sprite.RenderTransform = new ScaleTransform(-1, 1, sprite.Width / 2, 0);
        // Sheet cells have empty space under the desk legs; props are cropped to their pixels.
        Put(sprite, Math.Round(p.X - sprite.Width / 2), spare != null ? p.Y + 22 - 108 * scaleBy : p.Y + 12 - sprite.Height, p.Y + 12);
        blockedCells.Add(cell);
    }

    private void BuildScene()
    {
        // Floor, back to front: each room its own floor, the corridors the shared tile.
        foreach (var (x, y) in Enumerable.Range(0, MapHeight).SelectMany(r => Enumerable.Range(0, MapWidth).Select(c => (c, r))).OrderBy(c => c.c + c.r))
        {
            var centre = MapPoint(x, y);
            if (FloorTile(RoomAt(x, y)?.Floor ?? CorridorFloor, x == MapWidth - 1 || y == MapHeight - 1) is { } tile)
                Put(Sprite(tile), centre.X - TileWidth / 2.0, centre.Y - TileHeight / 2.0, -100000 + x + y);
        }
        foreach (var room in Rooms)
        {
            int x0 = room.X0, y0 = room.Y0, last = RoomSize - 1;
            // Tall walls on the two back sides, drawn one tile at a time so people stay correctly in
            // front of or behind them.
            var length = Math.Sqrt(TileWidth * TileWidth / 4.0 + TileHeight * TileHeight / 4.0);
            var backOut = new Vector(TileWidth / 2.0, -TileHeight / 2.0) * (9 / length);
            var leftOut = new Vector(-TileWidth / 2.0, -TileHeight / 2.0) * (9 / length);
            for (var i = 0; i < RoomSize; i++)
            {
                var back = MapPoint(x0 + i, y0);
                TallWall(back - new Vector(0, TileHeight / 2.0), back + new Vector(TileWidth / 2.0, 0), backOut, false, back.Y - 5);
                var left = MapPoint(x0, y0 + i);
                TallWall(left - new Vector(0, TileHeight / 2.0), left - new Vector(TileWidth / 2.0, 0), leftOut, true, left.Y - 5);
            }
            var corner = MapPoint(x0, y0) - new Vector(0, TileHeight / 2.0 + WallHeight);
            Put(new System.Windows.Shapes.Polygon { Points = [corner, corner + backOut, corner + backOut + leftOut, corner + leftOut], Fill = Brush("#D6BC98"), IsHitTestVisible = false }, 0, 0, MapPoint(x0, y0).Y - 4);
            var backEnd = MapPoint(x0 + last, y0);
            WallEnd(backEnd + new Vector(TileWidth / 2.0, 0), backOut, backEnd.Y - 4);
            var leftEnd = MapPoint(x0, y0 + last);
            WallEnd(leftEnd - new Vector(TileWidth / 2.0, 0), leftOut, leftEnd.Y - 4);
            var top = MapPoint(x0, y0);
            // Furniture along the walls, desks in the middle.
            for (var i = 0; i < room.Props.Length && i < PropCells.Length; i++)
                Prop(room.Props[i], (x0 + PropCells[i].X, y0 + PropCells[i].Y));
            // Department name on the room's front-left tile.
            var signCell = MapPoint(x0 + 1, y0 + last) + new Vector(0, 8);
            var label = new Border { Background = Brush("#E61D1A2E"), BorderBrush = Brush("#7545D6"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 2, 8, 2), IsHitTestVisible = false,
                Child = new TextBlock { Text = room.Name, Foreground = Brush("#EEEAFB"), FontWeight = FontWeights.SemiBold, FontSize = 15 } };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Put(label, signCell.X - label.DesiredSize.Width / 2, signCell.Y - label.DesiredSize.Height / 2, 90000);
            // Room info panel (shown on click).
            var contents = new StackPanel { Width = 150, IsHitTestVisible = false, Background = Brush("#303047"), Visibility = Visibility.Collapsed };
            infoPanels[room.Name] = contents;
            contents.Children.Add(new TextBlock { Text = room.Name, FontWeight = FontWeights.SemiBold, FontSize = 12, TextWrapping = TextWrapping.Wrap, Padding = new Thickness(4, 1, 4, 1) });
            var progress = new ProgressBar { Width = 110, Height = 5, Minimum = 0, Visibility = Visibility.Collapsed, ToolTip = "Completed workflow steps" };
            deskProgress[room.Name] = progress;
            contents.Children.Add(progress);
            var state = new TextBlock { Text = "Idle", TextWrapping = TextWrapping.Wrap, Foreground = Brush("#D7C9B6"), Padding = new Thickness(4, 1, 4, 1) };
            deskStates[room.Name] = state;
            contents.Children.Add(state);
            Put(contents, top.X + 70, top.Y - WallHeight - 140, 100000);
            // The room itself is the click target for its department.
            var area = new System.Windows.Shapes.Polygon
            {
                Points = [MapPoint(x0, y0) - new Vector(0, TileHeight / 2.0), MapPoint(x0 + last, y0) + new Vector(TileWidth / 2.0, 0), MapPoint(x0 + last, y0 + last) + new Vector(0, TileHeight / 2.0), MapPoint(x0, y0 + last) - new Vector(TileWidth / 2.0, 0)],
                Fill = Brushes.Transparent, Cursor = Cursors.Hand
            };
            var name = room.Name;
            area.MouseLeftButtonDown += (_, e) => { ShowInfoPanel(name); e.Handled = true; };
            Panel.SetZIndex(area, -50000);
            scene.Children.Add(area);
            desks[room.Name] = new Border();
        }
        BuildWorkers();
    }

    // Seat key -> the work item shown there, and back.
    private readonly Dictionary<string, string> seatWork = new();
    private readonly Dictionary<string, string> workSeat = new();

    // One desk and worker per seat; the seats never change, only the work given to them.
    private void BuildWorkers()
    {
        actors.Clear();
        var roster = CharacterAnimations.Ids;
        var seated = 0;
        foreach (var room in Rooms)
            for (var i = 0; i < room.Seats.Length; i++)
            {
                var (workerCell, deskCell) = Slot(room, i);
                var deskPoint = MapPoint(deskCell.X, deskCell.Y);
                // Desk 5 of the 3x3 sheets: the screen turned up-left to the worker behind it, so
                // the viewer sees the back of the monitor. The manager gets the bigger desk.
                if (OfficeAssets.Load(Art + TableOf(room.Name)) is { } sheet)
                {
                    var scaleBy = room.Name == "Manager" ? FurnitureScale * 1.15 : FurnitureScale;
                    var desk = Sprite(OfficeAssets.Crop(sheet, new Int32Rect(256, 128, 128, 128)), scaleBy);
                    Put(desk, deskPoint.X - desk.Width / 2, deskPoint.Y + 22 - 108 * scaleBy, deskPoint.Y + 10);
                }
                blockedCells.Add(deskCell);
                var key = $"seat:{room.Name}:{i}";
                var element = new Border { Width = ActorWidth, Height = ActorHeight, Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = $"{room.Name} · {room.Seats[i].Role}" };
                // The manager's big desk is deeper: they stand a little further back from it.
                var home = Stand(workerCell) + (room.Name == "Manager" ? new Vector(16, -22) : new Vector());
                var actor = new Actor(room.Name, key, element, home, workerCell);
                var visual = new Grid();
                // Every seat takes the next animated character; a missing animation never reaches this point.
                visual.Children.Add(Character(actor, roster[seated++ % roster.Count]));
                actor.Badge = new Image { Width = 24, Height = 24, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -12, 0, 0), IsHitTestVisible = false };
                RenderOptions.SetBitmapScalingMode(actor.Badge, BitmapScalingMode.NearestNeighbor);
                visual.Children.Add(actor.Badge);
                element.Child = visual;
                var name = room.Name;
                element.MouseLeftButtonDown += (_, e) => { ShowInfoPanel(name); e.Handled = true; };
                actors[key] = actor;
                scene.Children.Add(element);
                Place(actor, actor.Home);
            }
    }

    // Gives each department's work to its workers: a work item keeps its worker while it is shown;
    // a new one goes to any worker who is at their desk right now and holds no work yet — never a
    // fixed seat or role. When nobody in the department is free at a desk (rare: a desk worker always
    // remains) the work is left unassigned here and handed over as soon as somebody is back.
    private readonly Random assignRandom = new();
    private void AssignSeats(WorkItem[] work)
    {
        var shown = work.ToDictionary(w => w.Id);
        foreach (var (seat, id) in seatWork.ToArray())
            if (!shown.TryGetValue(id, out var item) || Zone(item) != actors[seat].Zone) { seatWork.Remove(seat); workSeat.Remove(id); }
        foreach (var item in work.Where(w => !workSeat.ContainsKey(w.Id) && !IsResult(w.State)).OrderBy(w => w.CreatedAt))
        {
            var zone = Zone(item);
            if (VirtualOffice.PickAssignee(actors.Values.Where(a => a.Zone == zone).Select(a => (a.Key, AtDesk(a), seatWork.ContainsKey(a.Key))), assignRandom) is not { } key) continue;
            seatWork[key] = item.Id;
            workSeat[item.Id] = key;
        }
    }

    // The room's first worker stands for the department in handoffs and reports.
    // A worker who is away strolling is not the one to receive: prefer one at the desk, else the first.
    private Actor? ZoneActor(string zone) => actors.Values.Where(a => a.Zone == zone).OrderBy(a => Idle(a) && AtDesk(a) ? 0 : 1).FirstOrDefault();
    private Actor? FirstActor(string zone) => actors.Values.FirstOrDefault(a => a.Zone == zone);
    private Actor? ActorFor(string workId, string zone) => workSeat.TryGetValue(workId, out var seat) && actors.TryGetValue(seat, out var actor) ? actor : ZoneActor(zone);
    // The work a worker holds. Work that has not found a free desk worker in its department yet is
    // queued: until somebody is back at a desk, no one shows or works on it.
    private WorkItem? WorkOf(Actor actor) => seatWork.TryGetValue(actor.Key, out var id) ? visibleWork.FirstOrDefault(w => w.Id == id) : null;

    private FrameworkElement Character(Actor actor, string character)
    {
        actor.Character = character;
        actor.Sprite = new SpriteAnimation { Width = ActorWidth, Height = ActorHeight, VerticalAlignment = VerticalAlignment.Bottom, RenderTransformOrigin = new Point(0.5, 0.5) };
        SetFrame(actor, Pose.Idle);
        return actor.Sprite;
    }

    private static void SetFrame(Actor actor, Pose pose)
    {
        if (actor.Sprite == null) return;
        var clipName = pose switch
        {
            Pose.Working => "working", Pose.Reporting => "reporting",
            Pose.Walking => "walking", Pose.WalkingBack => "walkingBack", _ => "idle"
        };
        if (CharacterAnimations.Clip(actor.Character, clipName) is { } clip)
            actor.Sprite.Source = clip;
        actor.Sprite.RenderTransform = new ScaleTransform(actor.FacingLeft ? -1 : 1, 1);
    }

    // Breadth-first search over walkable cells; desks and planters are obstacles. The manager room is
    // only crossed through its front row, and only when the walk starts or ends there, so nobody ever
    // passes behind the management desk.
    private List<Point> Path((int X, int Y) start, (int X, int Y) goal)
    {
        var usesManagerRoom = InManagerRoom(start) || InManagerRoom(goal);
        var queue = new Queue<(int X, int Y)>();
        var parent = new Dictionary<(int X, int Y), (int X, int Y)> { [start] = start };
        queue.Enqueue(start);
        while (queue.TryDequeue(out var cell) && cell != goal)
            foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                var next = (X: cell.X + dx, Y: cell.Y + dy);
                if (next.X < 0 || next.X >= MapWidth || next.Y < 0 || next.Y >= MapHeight || !Walkable(next.X, next.Y) || !CanStep(cell, next) || parent.ContainsKey(next)) continue;
                if (next != goal && InManagerRoom(next) && !(usesManagerRoom && ManagerFrontRow(next))) continue;
                parent[next] = cell; queue.Enqueue(next);
            }
        var path = new List<Point>();
        if (!parent.ContainsKey(goal)) return path;
        for (var cell = goal; cell != start; cell = parent[cell]) path.Add(Stand(cell));
        path.Reverse();
        return path;
    }

    private static bool InManagerRoom((int X, int Y) cell)
    {
        var room = Rooms[0];
        return cell.X >= room.X0 && cell.X < room.X0 + RoomSize && cell.Y >= room.Y0 && cell.Y < room.Y0 + RoomSize;
    }

    private static bool ManagerFrontRow((int X, int Y) cell) => cell.Y >= Rooms[0].Y0 + 4;

    // The character who performs a transition: the one who walks for a handoff or report, otherwise
    // the one who holds the task. A delegation is walked by the worker who receives the new step,
    // because the Manager never leaves the management desk.
    private Actor? MoverFor(OfficeTransition t)
    {
        if (t.Source == "Manager" && t.Target != null)
            return ActorFor(t.WorkId, t.Target);
        if (t.Source == null || t.Target == null || t.Source == t.Target)
            return ActorFor(t.WorkId, t.Zone);
        return t.Target == t.Zone ? ZoneActor(t.Source) : ActorFor(t.WorkId, t.Source);
    }

    // The character who receives a walking action; null for an action that stays at the desk.
    private Actor? ReceiverFor(OfficeTransition t, Actor mover)
    {
        var zone = VirtualOffice.ReceiverZone(t);
        if (zone == null) return null;
        var receiver = zone == "Manager" ? FirstActor("Manager") : t.Target == t.Zone ? ActorFor(t.WorkId, zone) : ZoneActor(zone);
        return receiver == mover ? null : receiver;
    }

    private static bool Idle(Actor actor) => actor.Moving == null && actor.Receiving == null;

    // Starts the next action of every idle character. An action begins only after the previous action
    // of that character has completed (its lane has no current action), so nothing is cut short.
    private void StartTransitions()
    {
        var current = visibleWork.ToDictionary(w => w.Id, w => (Zone: Zone(w), State: w.State));
        var now = DateTimeOffset.UtcNow;
        RecallForWaitingTask();
        foreach (var actor in actors.Values)
        {
            if (!Idle(actor)) continue;
            var action = actions.TryStartNext(actor.Key, now,
                a => OfficeTransitionQueue.IsCurrent(a.Transition, current, previousGoals),
                a => CanStartAction(actor, a.Transition));
            if (action != null) Begin(actor, action);
        }
        StartAmbient(now);
    }

    // A task that needs a walk waits at the office-wide away cap. Bring one wanderer back so the wait
    // is bounded; task movements always take priority over random activity.
    private void RecallForWaitingTask()
    {
        if (OfficeAmbientRules.AwayBelowCap(AmbientSeats(), ambient.Options)) return;
        if (!actors.Keys.Any(key => actions.PendingCount(key) > 0)) return;
        if (actors.Values.Any(a => a.AmbientState == AmbientState.Returning)) return;
        if (actors.Values.FirstOrDefault(a => a.Moving is { Ambient: not null } && a.AmbientState != AmbientState.Returning) is { } walker)
            RecallToDesk(walker);
    }

    // True while the worker sits at the own desk and is not on a walk, whether for a task or for fun.
    private static bool AtDesk(Actor actor) => actor.AmbientState == AmbientState.AtDesk && (actor.Moving is null or { Timed: true });

    private IReadOnlyList<AmbientSeat> AmbientSeats()
    {
        // A department with work nobody could take yet must keep every present worker at the desk.
        var waiting = visibleWork.Where(w => !IsResult(w.State) && !workSeat.ContainsKey(w.Id)).Select(Zone).ToHashSet(StringComparer.Ordinal);
        return actors.Values.Select(a =>
        {
            var work = WorkOf(a) != null;
            return new AmbientSeat(
                a.Key, a.Zone, a.Zone == "Manager", AtDesk(a),
                // Busy: holds work, has queued/running office actions, takes part in a handoff, or is moving.
                work || actions.Current(a.Key) != null || actions.PendingCount(a.Key) > 0 || a.Receiving != null || a.Moving != null,
                a.Receiving is { Ambient: not null }, work, waiting.Contains(a.Zone));
        }).ToArray();
    }

    private (int X, int Y)[] ValidWaypoints() => HallwayWaypoints.Where(w => w.X < MapWidth - 1 && w.Y < MapHeight - 1 && Walkable(w.X, w.Y) && RoomAt(w.X, w.Y) == null).ToArray();

    private (int X, int Y)? ReportSpot(int index)
    {
        var room = Rooms[0];
        var spot = (X: room.X0 + ManagerReportSpots[index].X, Y: room.Y0 + ManagerReportSpots[index].Y);
        // Never the manager's own cell (behind the desk), and always a free cell.
        return Walkable(spot.X, spot.Y) && spot != Slot(room, 0).Worker ? spot : null;
    }

    private enum WalkDecision { Timed, Wait, Walk }
    private sealed record TaskWalk(Actor Receiver, (int X, int Y) Destination, int Spot, List<Point> Outbound);

    // An action starts unless it is a walk that must wait: either the receiver is still busy or the
    // office is at its away cap. Everything that cannot walk at all becomes a timed desk action, so
    // a one-seat department can never be emptied forever.
    private bool CanStartAction(Actor mover, OfficeTransition transition)
    {
        var (decision, _) = PlanTaskWalk(mover, transition);
        if (decision == WalkDecision.Timed) return true;
        if (decision == WalkDecision.Wait) return false;
        return OfficeAmbientRules.AwayBelowCap(AmbientSeats(), ambient.Options);
    }

    private (WalkDecision Decision, TaskWalk? Plan) PlanTaskWalk(Actor mover, OfficeTransition transition)
    {
        // The Manager never walks; its own task updates stay at the management desk.
        if (mover.Zone == "Manager") return (WalkDecision.Timed, null);
        var receiver = ReceiverFor(transition, mover);
        if (receiver == null) return (WalkDecision.Timed, null);
        if (!Idle(receiver)) return (WalkDecision.Wait, null);
        // A department must keep one free worker at a desk for a new task. If it cannot spare the
        // walker, the handoff is shown as a timed action at the walker's own desk instead.
        if (!OfficeAmbientRules.RoomCanSpare(mover.Key, AmbientSeats(), ambient.Options))
            return (WalkDecision.Timed, null);
        var spot = -1;
        (int X, int Y) destination;
        Point stop;
        if (receiver.Zone == "Manager")
        {
            spot = FreeReportSpot();
            if (spot < 0) return (WalkDecision.Wait, null);
            destination = ReportSpot(spot)!.Value;
            stop = Stand(destination);
        }
        else
        {
            destination = receiver.HomeCell;
            // Stop beside the receiver (not on top of them) so both stay visible during the handoff.
            stop = receiver.Home + new Vector(-70, 34);
        }
        var outbound = Path(mover.HomeCell, destination);
        if (outbound.Count == 0) return (WalkDecision.Timed, null);
        outbound[^1] = stop;
        return (WalkDecision.Walk, new TaskWalk(receiver, destination, spot, outbound));
    }

    private int FreeReportSpot()
    {
        for (var i = 0; i < ManagerReportSpots.Length; i++)
            if (!reportSpotsInUse.Contains(i) && ReportSpot(i) != null) return i;
        return -1;
    }

    private bool TryReserveSpot(int spot)
    {
        if (spot < 0 || spot >= ManagerReportSpots.Length || reportSpotsInUse.Contains(spot) || ReportSpot(spot) is null) return false;
        reportSpotsInUse.Add(spot);
        return true;
    }

    // The ticker for random activities. The scheduler checks every rule (nobody away from the manager's
    // desk, at least one worker left per department, fewer than MaxAway people away) before it picks someone.
    private void StartAmbient(DateTimeOffset now)
    {
        if (!ambient.Options.Enabled) return;
        var free = Enumerable.Range(0, ManagerReportSpots.Length).Count(i => !reportSpotsInUse.Contains(i) && ReportSpot(i) != null);
        var waypoints = ValidWaypoints();
        if (ambient.TryPlan(now, AmbientSeats(), free, waypoints.Length) is { } plan && actors.TryGetValue(plan.ActorKey, out var actor))
            BeginAmbient(actor, plan, waypoints);
    }

    private void BeginAmbient(Actor actor, AmbientPlan plan, (int X, int Y)[] waypoints)
    {
        var legs = new List<((int X, int Y) Cell, Point? Stop, int Ticks, bool Talk)>();
        var spot = -1;
        var target = "";
        switch (plan.Kind)
        {
            case AmbientKind.ReportToManager:
                spot = FreeReportSpot();
                if (spot < 0 || FirstActor("Manager") is not { } boss) return;
                target = boss.Key;
                legs.Add((ReportSpot(spot)!.Value, null, plan.DwellTicks, true));
                break;
            case AmbientKind.VisitColleague:
                if (plan.TargetKey == null || !actors.TryGetValue(plan.TargetKey, out var host) || host.Zone == "Manager") return;
                target = host.Key;
                legs.Add((host.HomeCell, host.Home + VisitorOffset, plan.DwellTicks, true));
                break;
            default:
                foreach (var index in plan.WaypointIndexes.Where(i => i >= 0 && i < waypoints.Length))
                    legs.Add((waypoints[index], null, plan.DwellTicks, false));
                if (legs.Count == 0) return;
                break;
        }
        var route = new Queue<Point>();
        var stops = new List<(Point Point, int Ticks, bool Talk)>();
        route.Enqueue(Stand(actor.HomeCell));
        var from = actor.HomeCell;
        foreach (var (cell, stop, ticks, talk) in legs)
        {
            var leg = Path(from, cell);
            if (leg.Count == 0) return;
            if (stop is { } custom) leg[^1] = custom;
            foreach (var point in leg) route.Enqueue(point);
            stops.Add((leg[^1], ticks, talk));
            from = cell;
        }
        var back = Path(from, actor.HomeCell);
        if (back.Count == 0) return;
        foreach (var point in back) route.Enqueue(point);
        route.Enqueue(actor.Home);
        var movement = new Movement("", new OfficeTransition("", actor.Zone, WorkState.Queued, "Ambient"), target, route, 0)
            { Ambient = plan.Kind, Spot = spot, StandTicks = 0, TimeLimit = AmbientLimit(route, stops) };
        foreach (var stop in stops) movement.Stops.Enqueue(stop);
        if (spot >= 0 && !TryReserveSpot(spot)) return;
        if (plan.Kind == AmbientKind.VisitColleague) actors[target].Receiving = movement;
        actor.Moving = movement;
        actor.AmbientState = AmbientState.MovingTo;
    }

    private TimeSpan AmbientLimit(Queue<Point> route, IEnumerable<(Point Point, int Ticks, bool Talk)> stops)
    {
        double distance = 0;
        Point? previous = null;
        foreach (var point in route)
        {
            if (previous is { } from) distance += (point - from).Length;
            previous = point;
        }
        var ticks = (int)Math.Ceiling(distance / Step) + stops.Sum(stop => stop.Ticks) + 60;
        var limit = TimeSpan.FromMilliseconds(ticks * AmbientOptions.TickMilliseconds) + TimeSpan.FromSeconds(5);
        var floor = TimeSpan.FromSeconds(ambient.Options.MaxActivitySeconds);
        return limit > floor ? limit : floor;
    }

    private void Begin(Actor actor, OfficeAction action)
    {
        var next = action.Transition;
        var item = visibleWork.FirstOrDefault(w => w.Id == next.WorkId);
        transition.Text = item == null ? $"{next.Activity} · {next.Zone}" : $"{next.Activity} · {next.Zone} · {ShortLabel(item)}";
        transition.ToolTip = item?.RequestPreview;
        var (decision, plan) = PlanTaskWalk(actor, next);
        var back = decision == WalkDecision.Walk && plan != null ? Path(plan.Destination, actor.HomeCell) : new List<Point>();
        if (decision != WalkDecision.Walk || plan == null || (back.Count == 0 && plan.Destination != actor.HomeCell)
            || (plan.Spot >= 0 && !TryReserveSpot(plan.Spot)))
        {
            // Delegation by the Manager, a department that cannot spare the walker, a taken report spot
            // or a blocked path: the action stays at the desk as a short timed pose.
            actor.Moving = new Movement(action.Id, next, actor.Key, new Queue<Point>(), 0) { Timed = true, StandTicks = TimedActionTicks };
            return;
        }
        var route = new Queue<Point>();
        route.Enqueue(Stand(actor.HomeCell));
        foreach (var point in plan.Outbound) route.Enqueue(point);
        foreach (var point in back) route.Enqueue(point);
        route.Enqueue(actor.Home);
        var movement = new Movement(action.Id, next, plan.Receiver.Key, route, plan.Outbound.Count + 1) { Spot = plan.Spot };
        actor.Moving = movement;
        plan.Receiver.Receiving = movement;
    }

    // Safety net: an action that has not completed within the timeout is finished by force so that its
    // character's lane (and anyone waiting for that character) cannot be blocked forever.
    private void ReleaseStuckActions()
    {
        foreach (var stuck in actions.Expired(DateTimeOffset.UtcNow))
            if (actors.TryGetValue(stuck.Seat, out var actor) && actor.Moving is { } moving && moving.ActionId == stuck.Id)
                Finish(actor);
            else
                actions.Complete(stuck.Seat, stuck.Id);
        // A delayed UI tick must never make an ambient walker teleport home. Once its route time has
        // elapsed, cancel the remaining outing and give it a real path back to the desk. Returning
        // walkers are left alone so the same wall-clock timeout cannot repeatedly rebuild that path.
        foreach (var actor in actors.Values.Where(a => a.Moving is { Ambient: not null } m
                     && a.AmbientState != AmbientState.Returning
                     && DateTimeOffset.UtcNow - m.StartedAt > m.TimeLimit).ToArray())
            RecallToDesk(actor);
    }

    private void PersistActions()
    {
        if (actionStore == null || actions.Version == savedActionsVersion) return;
        var version = actions.Version;
        try { actionStore.SaveOfficeActions(actions.Snapshot()); savedActionsVersion = version; }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Data.Common.DbException) { }
    }

    private void Animate(object? sender, EventArgs e)
    {
        if (readTasks == null) return;
        animationFrame++;
        if (animationFrame % 30 == 0 && recentResults.Count > 0 && recentResults.Values.Any(expiry => expiry <= DateTime.UtcNow)) RefreshOffice();
        StartTransitions();
        foreach (var actor in actors.Values)
            Move(actor);
        if (animationFrame % 30 == 0) ReleaseStuckActions();
        if (animationFrame % 15 == 0) PersistActions();
        // Work that found no free desk worker waits; retry about once a second so it is handed to
        // the first worker who comes back to a desk of its department.
        if (animationFrame % 30 == 0 && visibleWork.Any(w => !workSeat.ContainsKey(w.Id)))
        {
            AssignSeats(visibleWork);
            RecallForWaitingWork();
        }
        if (animationFrame % 6 == 0)
            foreach (var actor in actors.Values)
            {
                SetFrame(actor, RowFor(actor));
                UpdateBadge(actor);
            }
    }

    private void Move(Actor actor)
    {
        var moving = actor.Moving;
        if (moving == null) return;
        if (moving.StandTicks > 0) { moving.StandTicks--; return; }
        if (moving.Ticks > 0) { moving.Ticks--; return; }
        // The dwell of an ambient stop is over: on to the next stop, or back to the desk.
        if (moving.Ambient != null) actor.AmbientState = moving.Stops.Count > 0 ? AmbientState.MovingTo : AmbientState.Returning;
        if (!moving.Route.TryPeek(out var destination))
        {
            Finish(actor);
            return;
        }
        var x = Canvas.GetLeft(actor.Element);
        var y = Canvas.GetTop(actor.Element);
        var dx = destination.X - x;
        var dy = destination.Y - y;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        if (Math.Abs(dx) > 0.5)
            actor.FacingLeft = dx < 0;
        actor.Element.Tag = dy < -0.5 ? "back" : dy > 0.5 ? "front" : actor.Element.Tag;
        if (distance > Step)
        {
            Place(actor, new Point(x + dx / distance * Step, y + dy / distance * Step));
            return;
        }
        Place(actor, destination);
        moving.Route.Dequeue();
        if (moving.Ambient is { } kind && moving.Stops.TryPeek(out var stop) && stop.Point == destination)
        {
            moving.Stops.Dequeue();
            if (stop.Talk) moving.Ticks = stop.Ticks; else moving.StandTicks = stop.Ticks;
            actor.AmbientState = kind switch
            {
                AmbientKind.ReportToManager => AmbientState.ReportingToManager,
                AmbientKind.VisitColleague => AmbientState.VisitingColleague,
                _ => AmbientState.HallwayStroll
            };
        }
        if (moving.Outbound > 0 && --moving.Outbound == 0)
            moving.Ticks = InteractionTicks;
        if (moving.Route.Count == 0)
            Finish(actor);
    }

    private void Finish(Actor actor)
    {
        if (actor.Moving is { } moving)
        {
            if (actors.TryGetValue(moving.Target, out var receiver) && receiver.Receiving == moving)
                receiver.Receiving = null;
            // Completion: this is what lets the character's next queued action start.
            if (moving.ActionId.Length > 0) actions.Complete(actor.Key, moving.ActionId);
            if (moving.Spot >= 0)
            {
                reportSpotsInUse.Remove(moving.Spot);
                moving.Spot = -1;
            }
            if (moving.Ambient != null) ambient.NotifyReturned(actor.Key, DateTimeOffset.UtcNow);
        }
        actor.AmbientState = AmbientState.AtDesk;
        actor.Moving = null;
        actor.FacingLeft = false;
        Place(actor, actor.Home);
    }

    // A department with unassigned work brings a wanderer back immediately, so the next task gets a
    // free worker at a desk as soon as possible.
    private void RecallForWaitingWork()
    {
        var waiting = visibleWork.Where(w => !IsResult(w.State) && !workSeat.ContainsKey(w.Id)).Select(Zone).ToHashSet(StringComparer.Ordinal);
        if (waiting.Count == 0) return;
        foreach (var zone in waiting)
            if (actors.Values.FirstOrDefault(a => a.Zone == zone && a.Moving is { Ambient: not null } && a.AmbientState != AmbientState.Returning) is { } walker)
                RecallToDesk(walker);
    }

    // Ends a random activity early without cutting through the manager room: rebuild the walk home
    // from the nearest cell the worker is standing on.
    private void RecallToDesk(Actor actor)
    {
        if (actor.Moving is not { Ambient: not null } moving) return;
        var current = NearestCell(new Point(Canvas.GetLeft(actor.Element), Canvas.GetTop(actor.Element)));
        var back = Path(current, actor.HomeCell);
        if (back.Count == 0 && current != actor.HomeCell) return;
        if (actors.TryGetValue(moving.Target, out var receiver) && receiver.Receiving == moving)
            receiver.Receiving = null;
        if (moving.Spot >= 0)
        {
            reportSpotsInUse.Remove(moving.Spot);
            moving.Spot = -1;
        }
        moving.Stops.Clear();
        moving.Ticks = 0;
        moving.StandTicks = 0;
        moving.Outbound = 0;
        moving.Route.Clear();
        foreach (var point in back) moving.Route.Enqueue(point);
        moving.Route.Enqueue(actor.Home);
        actor.AmbientState = AmbientState.Returning;
    }

    private (int X, int Y) NearestCell(Point position)
    {
        var best = (X: 0, Y: 0);
        var bestDistance = double.MaxValue;
        for (var y = 0; y < MapHeight; y++)
            for (var x = 0; x < MapWidth; x++)
            {
                if (!Walkable(x, y)) continue;
                var point = Stand((x, y));
                var dx = point.X - position.X;
                var dy = point.Y - position.Y;
                var distance = dx * dx + dy * dy;
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = (x, y);
            }
        return best;
    }

    // While two workers meet, each turns to face the other: the one standing on the left faces right.
    private static void FaceCounterpart(Actor actor, Actor? other)
    {
        if (other == null) return;
        var dx = Canvas.GetLeft(other.Element) - Canvas.GetLeft(actor.Element);
        if (Math.Abs(dx) > 0.5) actor.FacingLeft = dx < 0;
    }

    private Pose RowFor(Actor actor)
    {
        // A timed action keeps the desk pose; only walking actions change it.
        var walking = actor.Moving is { Timed: false } w ? w : null;
        if (walking is { StandTicks: > 0 }) return Pose.Idle;
        // Talking, handing over and receiving while two workers meet.
        if (walking is { Ticks: > 0 })
        {
            FaceCounterpart(actor, actors.GetValueOrDefault(walking.Target));
            return Pose.Reporting;
        }
        if (actor.Receiving is { } receiving && receiving.Ticks > 0)
        {
            FaceCounterpart(actor, actors.Values.FirstOrDefault(a => a.Moving == receiving));
            return Pose.Reporting;
        }
        if (walking != null) return Equals(actor.Element.Tag, "back") ? Pose.WalkingBack : Pose.Walking;
        actor.FacingLeft = false;
        var status = WorkOf(actor);
        if (status?.State == WorkState.Completed && recentResults.TryGetValue(status.Id, out var until) && until > DateTime.UtcNow.AddSeconds(8))
            return Pose.Reporting;
        return status?.State == WorkState.Running ? Pose.Working : Pose.Idle;
    }

    private void UpdateBadge(Actor actor)
    {
        if (actor.Badge == null) return;
        var work = WorkOf(actor);
        var icon = work?.State switch
        {
            WorkState.Running => actor.Zone switch { "Development" => 0, "Research" or "Data & Analysis" => 1, "Creative" or "Design & Art" => 2, "QA & Review" => 3, "External Tools" => 4, _ => 5 },
            WorkState.Completed => 3, WorkState.Failed => 9, WorkState.WrongAccess or WorkState.WrongProject => 10,
            WorkState.AwaitingApproval or WorkState.NeedsReview or WorkState.WaitingForPermission => 7,
            WorkState.WaitingForProvider => 8, WorkState.Paused => 6, _ => -1
        };
        if (icon >= 0 && OfficeAssets.Load("status-icons.png") is { } icons)
            actor.Badge.Source = OfficeAssets.TrimmedCell(icons, icon, 11);
        actor.Badge.ToolTip = work == null ? null : Phase(work);
        actor.Badge.Visibility = icon >= 0 && actor.Badge.Source != null ? Visibility.Visible : Visibility.Collapsed;
        actor.Badge.RenderTransform = new TranslateTransform(0, work?.State == WorkState.Running ? Math.Sin(animationFrame / 8.0) * 2 : 0);
    }

    private void ShowInfoPanel(string zone)
    {
        HideInfoPanels();
        if (!infoPanels.TryGetValue(zone, out var panel)) return;
        panel.Visibility = Visibility.Visible;
        infoTimer.Start();
    }

    private void HideInfoPanels()
    {
        infoTimer.Stop();
        foreach (var panel in infoPanels.Values) panel.Visibility = Visibility.Collapsed;
    }

}

// Office images are decoded once and shared; frames and trimmed cells are cut once and reused.
internal static class OfficeAssets
{
    private static readonly Dictionary<string, BitmapSource?> Images = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<(BitmapSource, int, int), BitmapSource> Frames = new();
    private static readonly Dictionary<(BitmapSource, int), BitmapSource> Cells = new();

    public static BitmapSource? Load(string file)
    {
        if (Images.TryGetValue(file, out var cached)) return cached;
        BitmapSource? image = null;
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "VirtualOffice", file);
        if (File.Exists(path))
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                image = bitmap;
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or InvalidOperationException) { }
        }
        Images[file] = image;
        return image;
    }

    private static readonly Dictionary<(BitmapSource, Int32Rect), BitmapSource> Crops = new();
    public static BitmapSource Crop(BitmapSource image, Int32Rect rect)
    {
        if (Crops.TryGetValue((image, rect), out var cut)) return cut;
        cut = new CroppedBitmap(image, rect);
        cut.Freeze();
        Crops[(image, rect)] = cut;
        return cut;
    }

    public static BitmapSource Frame(BitmapSource sheet, int row, int column, int rows, int columns)
    {
        if (Frames.TryGetValue((sheet, row, column), out var frame)) return frame;
        var width = sheet.PixelWidth / columns;
        var height = sheet.PixelHeight / rows;
        // Every frame of a sheet is cut to the same visible area, so characters drawn on wide or
        // padded cells keep the same size as the others and do not jitter between frames.
        var used = SheetBounds(sheet, rows, columns);
        frame = new CroppedBitmap(sheet, new Int32Rect(column * width + used.X, Math.Min(row, rows - 1) * height + used.Y, used.Width, used.Height));
        frame.Freeze();
        Frames[(sheet, row, column)] = frame;
        return frame;
    }

    private static readonly Dictionary<BitmapSource, Int32Rect> SheetAreas = new();
    private static readonly Dictionary<(BitmapSource, int, int, int, int), Vector> AnchorOffsets = new();

    // Compare the lower central part of each cell with the idle cell. This excludes most
    // raised hands and status marks while retaining the contact point of seated sprites.
    public static Vector FrameAnchorOffset(BitmapSource sheet, int row, int column, int rows, int columns)
    {
        var key = (sheet, row, column, rows, columns);
        if (AnchorOffsets.TryGetValue(key, out var offset)) return offset;
        var width = sheet.PixelWidth / columns;
        var height = sheet.PixelHeight / rows;
        Point Anchor(int r, int c)
        {
            var area = new Int32Rect(c * width + width / 4, r * height + height / 2,
                width / 2, height - height / 2);
            var bounds = OpaqueBounds(sheet, area);
            return new Point(bounds.X - c * width + bounds.Width / 2.0,
                bounds.Y - r * height + bounds.Height);
        }
        var reference = Anchor(0, 0);
        var current = Anchor(row, column);
        offset = reference - current;
        AnchorOffsets[key] = offset;
        return offset;
    }

    // The union of the visible pixels of all cells, in cell coordinates.
    private static Int32Rect SheetBounds(BitmapSource sheet, int rows, int columns)
    {
        if (SheetAreas.TryGetValue(sheet, out var area)) return area;
        var width = sheet.PixelWidth / columns;
        var height = sheet.PixelHeight / rows;
        int left = width, top = height, right = 0, bottom = 0;
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < columns; c++)
            {
                var b = OpaqueBounds(sheet, new Int32Rect(c * width, r * height, width, height));
                left = Math.Min(left, b.X - c * width);
                top = Math.Min(top, b.Y - r * height);
                right = Math.Max(right, b.X - c * width + b.Width);
                bottom = Math.Max(bottom, b.Y - r * height + b.Height);
            }
        area = right <= left || bottom <= top ? new Int32Rect(0, 0, width, height) : new Int32Rect(left, top, right - left, bottom - top);
        SheetAreas[sheet] = area;
        return area;
    }

    // Cell index of a horizontal strip, cut down to the rectangle that holds visible pixels.
    public static BitmapSource TrimmedCell(BitmapSource strip, int index, int count)
    {
        if (Cells.TryGetValue((strip, index), out var cell)) return cell;
        var width = strip.PixelWidth / count;
        var area = new Int32Rect(index * width, 0, width, strip.PixelHeight);
        var bounds = OpaqueBounds(strip, area);
        cell = new CroppedBitmap(strip, bounds);
        cell.Freeze();
        Cells[(strip, index)] = cell;
        return cell;
    }

    internal static Int32Rect OpaqueBounds(BitmapSource image, Int32Rect area)
    {
        var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var stride = area.Width * 4;
        var pixels = new byte[stride * area.Height];
        bgra.CopyPixels(area, pixels, stride, 0);
        int left = area.Width, top = area.Height, right = -1, bottom = -1;
        for (var y = 0; y < area.Height; y++)
            for (var x = 0; x < area.Width; x++)
                if (pixels[y * stride + x * 4 + 3] > 24)
                {
                    if (x < left) left = x;
                    if (x > right) right = x;
                    if (y < top) top = y;
                    if (y > bottom) bottom = y;
                }
        return right < 0 ? area : new Int32Rect(area.X + left, area.Y + top, right - left + 1, bottom - top + 1);
    }
}
