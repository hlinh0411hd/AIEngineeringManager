using Employee.Core;

namespace Employee.Infrastructure;

// The task actions behind the Telegram commands. Lives outside the Desktop so it can be driven headlessly over a real store
// and a real TaskExecutionPool. Every method that touches task state runs through the dispatcher (the UI thread in the app).
// Each action returns a one-line reply, or throws InvalidOperationException with a readable message.
public sealed class TelegramTaskActions
{
    private readonly TaskExecutionPool tasks;
    private readonly IEmployeeStore store;
    private readonly Func<Func<string>, Task<string>> dispatch;
    private readonly Action<WorkItem?> onChanged;
    private readonly Action onResumed;
    private readonly Func<bool> autonomous;
    private readonly Action<WorkItem> completed;

    // dispatch: runs the function on the thread that owns the task state and returns its result.
    // onChanged: called after every change; null means a plain refresh, a work item means "a new task was created, show it".
    // onResumed: called when a command puts work back in the queue, so the app can clear its "queue suspended" flag.
    // autonomous: whether new tasks get PlanningPolicy.Always.
    public TelegramTaskActions(TaskExecutionPool tasks, IEmployeeStore store, Func<Func<string>, Task<string>> dispatch, Action<WorkItem?> onChanged, Action? onResumed = null, Func<bool>? autonomous = null, Action<WorkItem>? completed = null)
    {
        this.completed = completed ?? (_ => { });
        this.tasks = tasks;
        this.store = store;
        this.dispatch = dispatch;
        this.onChanged = onChanged;
        this.onResumed = onResumed ?? (() => { });
        this.autonomous = autonomous ?? (() => false);
    }

    // Only top-level tasks can be addressed from Telegram: goals and parentless items, never coordinators.
    public static bool IsTopLevel(WorkItem work) => !work.IsCoordinator && (work.IsGoal || work.ParentTaskId == null);

    public TelegramActions ToActions() => new(FindByShortId, Describe, Comment, Complete, Close, Run, Projects, New);

    private WorkItem? Find(string id) => store.WorkItems().FirstOrDefault(w => w.Id == id && IsTopLevel(w));
    private WorkItem Require(string id) => Find(id) ?? throw new InvalidOperationException("That task no longer exists.");

    // Unique prefix match. Runs on the poller thread; the store is thread-safe for reads and the match is read-only.
    public string? FindByShortId(string prefix)
    {
        if (prefix.Length == 0)
            return null;
        var matches = store.WorkItems().Where(w => IsTopLevel(w) && w.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0].Id : null;
    }

    public Task<string> Describe(string id) => dispatch(() =>
    {
        var work = Require(id);
        var kind = TaskStopNotices.KindOf(work.State, tasks.Decisions(work).Count > 0);
        return "State: " + work.State + "\n" + (kind == null ? "" : TaskStopNotices.Title(kind.Value) + "\n") + "Request: " + TaskStopNotices.Excerpt(work.Request, 200) + "\n\n" + tasks.Summary(work, true);
    });

    public Task<string> Comment(string id, string text) => dispatch(() =>
    {
        var work = Require(id);
        var goal = tasks.AddComment(work, text, [], continueUpdating: true);
        onResumed();
        var current = store.WorkItems().First(w => w.Id == goal.Id);
        // AddComment usually queues the task itself; queue it here only when its state still allows a run.
        var queued = current.State == WorkState.Queued && current.QueuedAt != null;
        if (!queued && current.State != WorkState.Running && !TaskOutcomes.IsClosed(current.State))
        {
            tasks.QueueForRun(current);
            queued = true;
        }

        onChanged(null);
        return queued ? "Comment saved. The task is queued to continue." : "Comment saved. The task state is " + current.State + ", so it was not queued.";
    });

    public Task<string> Complete(string id) => dispatch(() =>
    {
        var done = tasks.CompleteAnyway(Require(id));
        onChanged(null);
        completed(done);
        return "Task marked completed.";
    });

    public Task<string> Close(string id, string reason) => dispatch(() =>
    {
        tasks.Close(Require(id), WorkState.Cancelled, reason);
        onChanged(null);
        return "Task closed (cancelled).";
    });

    public Task<string> Run(string id) => dispatch(() =>
    {
        var work = Require(id);
        onResumed();
        tasks.QueueForRun(work);
        onChanged(null);
        return "Task queued to run.";
    });

    public Task<string> Projects() => dispatch(() => TelegramProjects.List(store.Projects()));

    // /new <project> <request>: same defaults as an automatic task from the form.
    public Task<string> New(string args) => dispatch(() => Create(args));

    private string Create(string args)
    {
        var projects = store.Projects();
        var (project, request) = TelegramProjects.Resolve(projects, args);
        if (project == null)
            throw new InvalidOperationException("I could not find that project. Send /projects to see the list, then /new <number> <request>.");
        if (request.Length == 0)
            throw new InvalidOperationException("Add the request after the project, for example: /new " + (projects.ToList().IndexOf(project) + 1) + " fix the login button.");
        if (request.Length > TelegramProjects.MaxRequestLength)
            throw new InvalidOperationException("The request is too long (max " + TelegramProjects.MaxRequestLength + " characters).");
        var work = new WorkItem
        {
            State = WorkState.Saved,
            IsGoal = true,
            ProjectId = project.Id,
            Request = request,
            AutoAssign = true,
            AutomaticIntent = true,
            Kind = WorkKind.Coding,
            Access = AccessLevel.WriteProject,
            GrantedAccess = AccessLevel.WriteProject,
            Priority = 50,
            PlanningOverride = autonomous() ? PlanningPolicy.Always : null
        };
        store.SaveWork(work);
        store.Log(work.Id, "Created", "Task created from Telegram. Access: " + work.Access);
        tasks.Enqueue(work, null);
        onResumed();
        onChanged(work);
        return "Task #" + work.Id[..8] + " created in " + project.Name + " and queued. You will get a message when it stops.";
    }
}
