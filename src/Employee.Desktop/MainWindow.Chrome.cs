using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Employee.Core;

namespace Employee.Desktop;
public partial class MainWindow
{
    private readonly Dictionary<string, TextBlock> navigationCounts = [];
    private void ApplyReferenceChrome()
    {
        foreach (var button in ShellNavigation.Children.OfType<Button>())
        {
            if (button.Tag is not string key)
                continue;
            var title = key switch
            {
                "office" => "Rik's office",
                "dashboard" => "Dashboard",
                "projects" => "Projects",
                "tasks" => "Tasks",
                "testing" => "Testing",
                "agents" => "Agents",
                "providers" => "Providers & Models",
                "local" => "Local AI (Ollama)",
                "tools" => "Tools",
                "workflows" => "Workflows",
                "activity" => "Activity & Logs",
                "approvals" => "Approvals",
                "guide" => "Guide",
                "settings" => "Settings",
                "updates" => "Updates",
                _ => key
            };
            var content = new Grid();
            content.ColumnDefinitions.Add(new() { Width = new GridLength(32) });
            content.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            content.Children.Add(UiArt.Icon(key == "dashboard" ? "home" : key, 24));
            var label = new TextBlock
            {
                Text = title,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };
            Grid.SetColumn(label, 1);
            content.Children.Add(label);
            if (key is "tasks" or "approvals")
            {
                var number = new TextBlock
                {
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    TextAlignment = TextAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                navigationCounts[key] = number;
                var badge = new Border
                {
                    Child = number,
                    CornerRadius = new CornerRadius(11),
                    Background = UiArt.Gradient("#9E6BE2", "#644198"),
                    Padding = new Thickness(8, 3, 8, 3),
                    Margin = new Thickness(6, 0, 0, 0)
                };
                Grid.SetColumn(badge, 2);
                content.Children.Add(badge);
            }

            button.Content = content;
            button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        }

        TaskModeButton.Content = ModeContent("home", "Task Mode");
        AutonomousModeButton.Content = ModeContent("rocket", "Autonomous");
        ShellAway.Content = ModeContent("coffee", "Away Mode");
    }

    private static FrameworkElement ModeContent(string icon, string label)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        row.Children.Add(UiArt.Icon(icon, 21));
        row.Children.Add(new TextBlock { Text = label, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 13 });
        return row;
    }

    private void RefreshNavigationCounts(IReadOnlyList<WorkItem> goals)
    {
        if (navigationCounts.TryGetValue("tasks", out var tasks))
            tasks.Text = goals.Count(w => w.State != WorkState.Completed).ToString();
        if (navigationCounts.TryGetValue("approvals", out var approvals))
            approvals.Text = (goals.Count(w => w.State is WorkState.AwaitingApproval or WorkState.NeedsReview or WorkState.WaitingForPermission or WorkState.WrongAccess or WorkState.WrongProject) + store.ToolRuns().Count(r => r.State is ToolRunState.WaitingApproval or ToolRunState.NeedsReview)).ToString();
    }
}
