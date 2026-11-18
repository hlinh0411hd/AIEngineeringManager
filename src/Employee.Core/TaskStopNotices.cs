namespace Employee.Core;

public enum StopNoticeKind { WrongAccess, WrongProject, WaitingForProvider, Approval, AwaitingApproval, Permission, NeedsReview, Failed, Paused, Completed }

// A task stopped in a state the user should know about. Shared by the Windows toast and Telegram.
public sealed record StopNotice(string WorkId, StopNoticeKind Kind, string Title, string RequestExcerpt, WorkState State)
{
    // The one-line text kept in the notification history and shown in the Windows toast.
    public string HistoryText => Title + " · " + RequestExcerpt;

    // Short "done / not done / why" text (see TaskSummary). Filled in by the app before the notice is sent to Telegram.
    public string Summary { get; init; } = "";

    // The user comment this run answers, if any, so the message can say what was done for it.
    public string LastComment { get; init; } = "";
}

public static class TaskStopNotices
{
    public static string Signature(WorkItem work, IReadOnlyCollection<string> decisionIds) => work.State + ":" + string.Join(",", decisionIds.Order(StringComparer.Ordinal));

    // Returns null when nothing new needs announcing: the task is unchanged since previousSignature,
    // only pending decisions were resolved, the state has no notice, or a Completed task is seen for the first time.
    public static StopNotice? Evaluate(WorkItem work, IReadOnlyCollection<string> decisionIds, string? previousSignature)
    {
        var ids = decisionIds.Order(StringComparer.Ordinal).ToArray();
        var known = previousSignature != null;
        if (known && previousSignature == Signature(work, ids))
            return null;
        // Resolving a decision must not reannounce the remaining pending decisions.
        if (known && previousSignature!.Split(':')[0] == work.State.ToString() && ids.All(id => previousSignature[(previousSignature.IndexOf(':') + 1)..].Split(',').Contains(id)))
            return null;
        var kind = KindOf(work.State, ids.Length > 0);
        // On first launch, surface only outstanding attention, not old completed work.
        if (kind == null || (!known && work.State == WorkState.Completed))
            return null;
        return new(work.Id, kind.Value, Title(kind.Value), Excerpt(work.Request), work.State);
    }

    public static StopNoticeKind? KindOf(WorkState state, bool hasDecisions) => state switch
    {
        WorkState.WrongAccess => StopNoticeKind.WrongAccess,
        WorkState.WrongProject => StopNoticeKind.WrongProject,
        WorkState.WaitingForProvider => StopNoticeKind.WaitingForProvider,
        _ when hasDecisions => StopNoticeKind.Approval,
        WorkState.AwaitingApproval => StopNoticeKind.AwaitingApproval,
        WorkState.WaitingForPermission => StopNoticeKind.Permission,
        WorkState.NeedsReview => StopNoticeKind.NeedsReview,
        WorkState.Failed => StopNoticeKind.Failed,
        WorkState.Paused => StopNoticeKind.Paused,
        WorkState.Completed => StopNoticeKind.Completed,
        _ => null
    };

    public static string Title(StopNoticeKind kind) => kind switch
    {
        StopNoticeKind.WrongAccess => "Wrong access - correct task permissions",
        StopNoticeKind.WrongProject => "Wrong project - correct project/folder",
        StopNoticeKind.WaitingForProvider => "Waiting for AI - see usage/auth/availability reason",
        StopNoticeKind.Approval => "Approval needed",
        StopNoticeKind.AwaitingApproval => "Approval needed to continue",
        StopNoticeKind.Permission => "Permission needed",
        StopNoticeKind.NeedsReview => "Result ready for review",
        StopNoticeKind.Failed => "Task failed",
        StopNoticeKind.Paused => "Task paused",
        _ => "Task completed"
    };

    public static string Excerpt(string? request, int max = 100)
    {
        var text = (request ?? "").Replace('\r', ' ').Replace('\n', ' ');
        return text[..Math.Min(max, text.Length)];
    }
}
