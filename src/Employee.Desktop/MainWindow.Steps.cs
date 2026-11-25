using System.Globalization;
using System.Windows.Data;
using Employee.Core;

namespace Employee.Desktop;

// The step's number in the task: the order steps were planned and created (1, 2, 3 ...).
public sealed class StepNumberConverter : IValueConverter
{
    public Dictionary<string, int> Numbers { get; set; } = new(StringComparer.Ordinal);
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is string id && Numbers.TryGetValue(id, out var n) ? n.ToString(culture) : "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

// When a step ran: "start → end" in local time, from its attempts' logs (empty until it has run).
public sealed class StepTimeConverter : IValueConverter
{
    public Dictionary<string, string> Times { get; set; } = new(StringComparer.Ordinal);
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is string id && Times.TryGetValue(id, out var text) ? text : "not run yet";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

// The second line of a milestone group's header: what the milestone does, its status and progress.
public sealed class MilestoneInfoConverter : IValueConverter
{
    public Dictionary<string, string> Info { get; set; } = new(StringComparer.Ordinal);
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is string title && Info.TryGetValue(title, out var info) ? info : "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

// Groups the steps by milestone (the step's own record, or worked out for older steps).
public sealed class StepMilestoneConverter(IReadOnlyDictionary<string, string> titles) : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is string id && titles.TryGetValue(id, out var title) ? title : "Other steps";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public partial class MainWindow
{
    // Planned order: creation time, then the plan key (t3 before t3.2 before t4).
    private static IReadOnlyList<WorkItem> OrderedSteps(IReadOnlyList<WorkItem> steps) =>
        steps.OrderBy(w => w.CreatedAt).ThenBy(w => w.PlanKey ?? "", StringComparer.Ordinal).ToArray();

    // A step's first attempt starts at its first "Route" log, and its last attempt ends at its last "Execution" log.
    private Dictionary<string, string> StepRunTimes(IReadOnlyList<WorkItem> steps)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            var logs = store.Logs(step.Id);
            var starts = logs.Where(l => l.Event == "Route").Select(l => l.At).ToArray();
            if (starts.Length == 0)
                continue;
            var ends = logs.Where(l => l.Event == "Execution").Select(l => l.At).ToArray();
            var start = starts.Min().ToLocalTime();
            var end = ends.Length > 0 ? ends.Max().ToLocalTime() : start;
            result[step.Id] = $"{start:dd/MM HH:mm} → {end:dd/MM HH:mm}";
        }
        return result;
    }

    private ListCollectionView StepsView(WorkItem goal, IReadOnlyList<WorkItem> steps)
    {
        ((StepNumberConverter)Resources["StepNumber"]).Numbers = steps.Select((w, i) => (w.Id, i + 1)).ToDictionary(x => x.Id, x => x.Item2, StringComparer.Ordinal);
        ((StepTimeConverter)Resources["StepTime"]).Times = StepRunTimes(steps);
        var view = new ListCollectionView(steps.ToList());
        if (unified.SessionFor(goal) is not { Phases.Count: > 1 } session)
            return view;
        var assigned = MilestoneView.Assign(session, steps, store.Logs(goal.Id));
        var groups = MilestoneView.Groups(session, steps, assigned);
        ((MilestoneInfoConverter)Resources["MilestoneInfo"]).Info = groups.ToDictionary(g => g.Title,
            g => $"{g.Goal}\n{char.ToUpper(g.Status[0], CultureInfo.InvariantCulture)}{g.Status[1..]} · {g.Done}/{g.Total} steps done", StringComparer.Ordinal);
        var titles = assigned.ToDictionary(a => a.Key, a => groups[a.Value].Title, StringComparer.Ordinal);
        var grouping = new PropertyGroupDescription(nameof(WorkItem.Id), new StepMilestoneConverter(titles));
        // Every milestone shows, in order, even before it has steps.
        foreach (var group in groups)
            grouping.GroupNames.Add(group.Title);
        view.GroupDescriptions.Add(grouping);
        return view;
    }
}
