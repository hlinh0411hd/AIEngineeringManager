using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Employee.Core;
using Employee.Infrastructure;


// Shared check runner, assertions and fixtures used by every check group.
static class Harness
{
    // Lower-case names keep the check bodies unchanged from the original top-level script.
    public static string root { get; private set; } = "";
    public static int Passed { get; private set; }
    public static int Failed { get; private set; }
    private static string[] args = [];
    public static void Start(string[] commandLine)
    {
        args = commandLine;
        root = Path.Combine(Path.GetTempPath(), "EmployeeChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
    }

    public static async Task Check(string name, Func<Task> test)
    {
        if (args.FirstOrDefault() == "--provider-resume" && !name.StartsWith("Provider resume:"))
            return;
        try
        {
            await test();
            Passed++;
            Console.WriteLine("PASS " + name);
        }
        catch (Exception e)
        {
            Failed++;
            Console.WriteLine("FAIL " + name + ": " + e);
        }
    }

    public static void Assert(bool condition, string message = "Assertion failed")
    {
        if (!condition)
            throw new Exception(message);
    }

    public static Fixture New(bool textOnly = false) => new(Path.Combine(root, Guid.NewGuid().ToString("N")), textOnly);
    public static WorkSession SessionFor(Fixture f, bool away = true, RoutingMode mode = RoutingMode.Automatic)
    {
        var session = new WorkSession
        {
            Name = "Test session",
            Goal = "Complete bounded work",
            ProjectIds = [f.Project.Id],
            AccessCeiling = f.Work.Access,
            Away = away,
            Mode = mode,
            MaxCalls = 8,
            MaxTasks = 10,
            DurationMinutes = 480
        };
        f.Store.SaveSession(session);
        f.Work.SessionId = session.Id;
        f.Work.DelegationAllowed = true;
        f.Store.SaveWork(f.Work);
        return session;
    }

    public static SessionEngine EngineFor(Fixture f) => new(f.Store, f.Store, f.Manager, new SmartTestingService(f.Store, f.Store, f.Manager, new ProcessRunner()));
    public static void SeedDelegationEvidence(Fixture f)
    {
        // These scenarios exercise a permitted automatic handoff, not the insufficient-history approval gate.
        for (var i = 0; i < 3; i++)
        {
            f.Store.SaveModelRun(new(Guid.NewGuid().ToString("N"), "historical-source", f.First.Id, "a", f.Work.Kind, FailureKind.TaskError, 1000, false, DateTimeOffset.UtcNow.AddDays(-1)));
            f.Store.SaveModelRun(new(Guid.NewGuid().ToString("N"), "historical-target", f.Second.Id, "b", f.Work.Kind, FailureKind.None, 1000, false, DateTimeOffset.UtcNow.AddDays(-1)));
        }
    }

    public static WorkItem ExtraWork(Fixture f, WorkSession session, string request = "Independent task", string[]? dependencies = null)
    {
        var work = new WorkItem
        {
            SessionId = session.Id,
            ProjectId = f.Project.Id,
            Request = request,
            Kind = f.Work.Kind,
            Access = f.Work.Access,
            DependsOn = dependencies?.ToList() ?? []
        };
        f.Store.SaveWork(work);
        return work;
    }

    public static UnifiedTaskService UnifiedFor(Fixture f)
    {
        // Unified project tasks now run with WriteProject. The fake Codex must
        // be allowed to write, while the shared fixture keeps its original policy.
        f.Settings.Providers.Single(p => p.Id == "codex").AllowProjectWrite = true;
        return new(f.Store, f.Store, f.Manager, new ProcessRunner());
    }

}
