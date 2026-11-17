namespace Employee.Core;
public static class SessionTiming
{
    // Zero is unlimited; the timestamp is persisted so pause/restart cannot reset a finite window.
    public static void Start(WorkSession session)
    {
        session.StartedAt ??= DateTimeOffset.UtcNow;
        if (session.DurationMinutes == 0)
            session.Deadline = null;
        else
            session.Deadline ??= session.StartedAt.Value.AddMinutes(session.DurationMinutes);
    }

    public static TimeSpan? Remaining(WorkSession session) => session.DurationMinutes == 0 ? null : session.Deadline - DateTimeOffset.UtcNow;
    public static void Renew(WorkSession session, int minutes)
    {
        if (minutes is < 0 or > 43200)
            throw new InvalidOperationException("Duration must be between 0 and 43200 minutes; 0 means unlimited.");
        session.DurationMinutes = minutes;
        session.Deadline = minutes == 0 ? null : DateTimeOffset.UtcNow.AddMinutes(minutes);
    }
}
