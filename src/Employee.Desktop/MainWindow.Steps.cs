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

    private ListCollectionView StepsView(WorkItem goal, IReadOnlyList<WorkItem> steps)
    {
        ((StepNumberConverter)Resources["StepNumber"]).Numbers = steps.Select((w, i) => (w.Id, i + 1)).ToDictionary(x => x.Id, x => x.Item2, StringComparer.Ordinal);
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
