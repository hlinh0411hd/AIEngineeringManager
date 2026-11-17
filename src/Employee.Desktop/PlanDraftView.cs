using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Employee.Core;
using Employee.Infrastructure;

namespace Employee.Desktop;

// "Plan draft" tab: describe what you want, get back a full plan in a fixed template and a
// ready-to-send task request; copy it or send it to the AI task tab. Nothing runs from here.
public sealed class PlanDraftView : StackPanel
{
    private readonly Func<IReadOnlyList<ProjectInfo>> projects;
    private readonly ComboBox project = new() { MinWidth = 260 };
    private readonly ComboBox workType = new() { MinWidth = 200 };
    private readonly TextBox request = new() { Height = 120, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top,
        ToolTip = "What you want: the goal, what it is for, what exists already, anything that must or must not happen. Rough notes are fine." };
    private readonly TextBox result = new() { MinHeight = 320, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top, FontFamily = new FontFamily("Segoe UI") };
    private readonly Button draft = new() { Content = "✨ Draft plan", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button useAsTask = new() { Content = "Use as AI task", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0), IsEnabled = false, ToolTip = "Puts the ready-to-send task request into the AI task tab, with this project selected." };
    private readonly Button copyRequest = new() { Content = "Copy task request", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
    private readonly Button copyAll = new() { Content = "Copy full plan", Padding = new Thickness(12, 4, 12, 4), IsEnabled = false };
    private readonly TextBlock status = new() { Margin = new Thickness(0, 6, 0, 6), TextWrapping = TextWrapping.Wrap };

    // Supplied by the window: runs the draft through the AIs. UseAsTask: the project and the request to put in the AI task form.
    public Func<string, ProjectInfo?, string?, Task<(string Draft, string Provider)>>? Draft { get; set; }
    public event Action<ProjectInfo?, string>? UseAsTask;

    public PlanDraftView(Func<IReadOnlyList<ProjectInfo>> projects)
    {
        this.projects = projects;
        Margin = new Thickness(12);
        Children.Add(new TextBlock { Text = "Describe what you want. You get back a full plan (goal, inputs, scope, requirements, deliverables, acceptance criteria, phases, risks, suggestions) and a ready-to-send task request. Nothing runs until you send it as a task.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        var top = new WrapPanel();
        top.Children.Add(Field("Project", project));
        top.Children.Add(Field("Work type", workType));
        Children.Add(top);
        foreach (var (value, label) in new[] { ("", "Let the AI decide"), ("development", "Development"), ("art", "Art (pixel / sprites)"), ("video & marketing", "Video & marketing"), ("research", "Research / design doc"), ("mixed", "Mixed") })
            workType.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        workType.SelectedIndex = 0;
        Children.Add(new TextBlock { Text = "What you want", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 4) });
        Children.Add(request);
        var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var b in new[] { draft, useAsTask, copyRequest, copyAll })
            buttons.Children.Add(b);
        Children.Add(buttons);
        Children.Add(status);
        Children.Add(new TextBlock { Text = "Plan", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 4) });
        Children.Add(result);

        project.DropDownOpened += (_, _) => Refresh(Selected?.Id);
        draft.Click += async (_, _) => await RunDraft();
        copyAll.Click += (_, _) => Copy(result.Text, "Full plan copied.");
        copyRequest.Click += (_, _) => Copy(TaskRequestText(), "Task request copied.");
        useAsTask.Click += (_, _) => UseAsTask?.Invoke(Selected, TaskRequestText());
        Refresh(null);
    }

    private ProjectInfo? Selected => project.SelectedItem is ComboBoxItem { Tag: ProjectInfo p } ? p : null;

    public void Refresh(string? projectId)
    {
        var keep = projectId ?? Selected?.Id;
        project.Items.Clear();
        project.Items.Add(new ComboBoxItem { Content = "(no project)", Tag = null });
        foreach (var p in projects())
            project.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p });
        project.SelectedItem = project.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is ProjectInfo p && p.Id == keep) ?? project.Items[project.Items.Count > 1 ? 1 : 0];
    }

    private string TaskRequestText() => PlanDrafter.TaskRequest(result.Text) ?? result.Text;

    private async Task RunDraft()
    {
        if (Draft == null)
            return;
        if (string.IsNullOrWhiteSpace(request.Text))
        {
            status.Text = "Describe what you want first.";
            return;
        }
        draft.IsEnabled = false;
        status.Text = "Drafting the plan… (a strong model, usually under a minute or two)";
        try
        {
            var type = (workType.SelectedItem as ComboBoxItem)?.Tag as string;
            var (text, provider) = await Draft(request.Text.Trim(), Selected, string.IsNullOrEmpty(type) ? null : type);
            result.Text = text.Replace(PlanDrafter.RequestStart, "\n── " + "Task request (ready to send)" + " ──").Replace(PlanDrafter.RequestEnd, "──────────").Trim();
            var hasRequest = PlanDrafter.TaskRequest(text) != null;
            useAsTask.IsEnabled = copyRequest.IsEnabled = copyAll.IsEnabled = true;
            status.Text = $"Drafted by {provider}." + (hasRequest ? " Edit your description and draft again to change it, or use the task request below." : " The AI did not mark a task request; the whole plan is used.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or System.ComponentModel.Win32Exception)
        {
            status.Text = ex.Message;
        }
        finally
        {
            draft.IsEnabled = true;
        }
    }

    private void Copy(string text, string done)
    {
        try
        {
            Clipboard.SetText(text);
            status.Text = done;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            status.Text = "The clipboard is busy; try again.";
        }
    }

    private static FrameworkElement Field(string label, FrameworkElement control)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        panel.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(control);
        return panel;
    }
}
