using System.Windows;
using System.Windows.Controls;
using Employee.Core;

namespace Employee.Desktop;
public partial class MainWindow
{
    private async void TeachTaskIntent(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        var work = unified.GoalFor(SelectedWork());
        TaskIdleOnly(work);
        if (work.ToolWorkflowId != null)
            throw new InvalidOperationException("Tool workflows use their configured steps; AI labels are not learned from workflows.");
        var project = store.Projects().Single(p => p.Id == work.ProjectId);
        var kind = new ComboBox
        {
            ItemsSource = Enum.GetValues<WorkKind>().Where(k => k != WorkKind.Planning).ToArray(),
            SelectedItem = work.Kind,
            MinWidth = 200
        };
        var access = new ComboBox
        {
            ItemsSource = Enum.GetValues<AccessLevel>(),
            SelectedItem = work.Access,
            MinWidth = 200
        };
        var apply = new CheckBox
        {
            Content = "Apply to this task if it has no execution steps / plan yet",
            IsChecked = false
        };
        var examples = new DataGrid
        {
            AutoGenerateColumns = false,
            Height = 190
        };
        foreach (var column in new[]
        {
            "Request",
            "Kind",
            "Access",
            "Source"
        }

        )
            examples.Columns.Add(new DataGridTextColumn { Header = column, Binding = new System.Windows.Data.Binding(column), Width = new DataGridLength(column == "Request" ? 400 : 130) });
        void RefreshExamples() => examples.ItemsSource = store.IntentExamples(work.ProjectId);
        RefreshExamples();
        var panel = new StackPanel
        {
            Margin = new Thickness(20)
        };
        panel.Children.Add(new TextBlock { Text = "Teach classification · " + project.Name, FontSize = 22, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBox { Text = work.Request, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Height = 90, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 12, 0, 12) });
        panel.Children.Add(new TextBlock { Text = "Choose the correct type and access. Examples are learned only for this project; nothing enables providers, grants model permissions or runs tasks.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(kind);
        panel.Children.Add(access);
        panel.Children.Add(apply);
        var buttons = new WrapPanel();
        var save = new Button
        {
            Content = "Save correct example"
        };
        var forget = new Button
        {
            Content = "Forget selected example"
        };
        var import = new Button
        {
            Content = "Learn from approved tasks"
        };
        var done = new Button
        {
            Content = "Close"
        };
        buttons.Children.Add(save);
        buttons.Children.Add(forget);
        buttons.Children.Add(import);
        buttons.Children.Add(done);
        panel.Children.Add(buttons);
        panel.Children.Add(new TextBlock { Text = "Learned phrases for this project", FontSize = 17, Margin = new Thickness(0, 12, 0, 8) });
        panel.Children.Add(examples);
        var dialog = new Window
        {
            Owner = this,
            Title = "Fix type / teach recognition",
            Content = new ScrollViewer
            {
                Content = panel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            },
            Width = 980,
            Height = 720,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        save.Click += (_, _) =>
        {
            try
            {
                var selectedKind = (WorkKind)kind.SelectedItem;
                var selectedAccess = (AccessLevel)access.SelectedItem;
                if (MessageBox.Show(dialog, $"Save label {selectedKind} / {selectedAccess} for this phrase?" + (apply.IsChecked == true ? "\nAlso changes the current task's type and access. The task will wait for you to Run/resume." : "\nOnly saves the lesson; the current task is not changed."), "Confirm correct label", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                    return;
                if (apply.IsChecked == true)
                    store.CorrectTaskIntent(work.Id, selectedKind, selectedAccess);
                store.LearnIntent(new("", work.ProjectId, work.Request, selectedKind, selectedAccess, IntentExampleSource.UserCorrection, work.Id, DateTimeOffset.UtcNow));
                store.Log(work.Id, "IntentTeaching", "User confirmed " + selectedKind + " / " + selectedAccess + " for this project's example memory.");
                RefreshExamples();
                RefreshTasks(work.Id);
                Status.Text = "Saved the correct classification example for this project. No AI was called.";
            }
            catch (Exception error)
            {
                MessageBox.Show(dialog, error.Message, "Could not save classification");
            }
        };
        forget.Click += (_, _) =>
        {
            if (examples.SelectedItem is not IntentExample example)
                return;
            if (MessageBox.Show(dialog, "Forget the selected example? Tasks and history are kept.", "Forget learned example", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                return;
            store.ForgetIntent(example.Id);
            RefreshExamples();
        };
        done.Click += (_, _) => dialog.Close();
        import.Click += (_, _) =>
        {
            try
            {
                var count = store.ImportReviewedIntents(work.ProjectId);
                RefreshExamples();
                Status.Text = $"Read {count} tasks you marked complete; duplicate examples were merged. No AI was called.";
            }
            catch (Exception error)
            {
                MessageBox.Show(dialog, error.Message, "Could not read history");
            }
        };
        // Nested modal loop must not dispatch queued tasks while labels/permissions are being edited.
        TaskIdleOnly(work);
        updateReviewPending = true;
        try
        {
            dialog.ShowDialog();
        }
        finally
        {
            updateReviewPending = false;
        }

        return Task.CompletedTask;
    });
}
