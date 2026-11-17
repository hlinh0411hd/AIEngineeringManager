using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Employee.Infrastructure;

namespace Employee.Desktop;
public sealed class TaskReviewWindow : Window
{
    public TaskReviewWindow(TaskReviewReport report)
    {
        SetResourceReference(StyleProperty, typeof(Window));
        Title = "Task result";
        Width = 1050;
        Height = 760;
        MinWidth = 760;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var grid = new Grid
        {
            Margin = new Thickness(22),
            Background = new SolidColorBrush(Color.FromRgb(29, 26, 46))
        };
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var title = new TextBlock
        {
            Text = report.Title,
            FontSize = 23,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };
        grid.Children.Add(title);
        var summary = new TextBlock
        {
            Text = report.Summary,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        };
        Grid.SetRow(summary, 1);
        grid.Children.Add(summary);
        var tabs = new TabControl();
        Grid.SetRow(tabs, 2);
        grid.Children.Add(tabs);
        AddTab("AI result", report.Result);
        AddTab("Changed files", report.Changes);
        AddTab("How to verify", report.Verification);
        AddTab("Full log", report.RawLog);
        void AddTab(string name, string text) => tabs.Items.Add(new TabItem { Header = name, Content = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, VerticalContentAlignment = VerticalAlignment.Top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas"), FontSize = 13, Margin = new Thickness(12), Padding = new Thickness(10) } });
        var footer = new StackPanel
        {
            Margin = new Thickness(0, 14, 0, 0)
        };
        Grid.SetRow(footer, 3);
        grid.Children.Add(footer);
        footer.Children.Add(new TextBlock { Text = "Project: " + report.ProjectRoot, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        var buttons = new WrapPanel();
        footer.Children.Add(buttons);
        var folder = new Button
        {
            Content = "Open project folder"
        };
        folder.Click += (_, _) =>
        {
            try
            {
                var start = new ProcessStartInfo("explorer.exe")
                {
                    UseShellExecute = false
                };
                start.ArgumentList.Add(report.ProjectRoot);
                Process.Start(start);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Cannot open folder");
            }
        };
        buttons.Children.Add(folder);
        var close = new Button
        {
            Content = "Close"
        };
        close.Click += (_, _) => Close();
        buttons.Children.Add(close);
        Content = grid;
    }
}
