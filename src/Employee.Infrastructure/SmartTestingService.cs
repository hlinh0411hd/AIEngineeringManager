using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Employee.Core;

namespace Employee.Infrastructure;
public sealed class SmartTestingService(IEmployeeStore store, IAutonomyStore autonomy, Orchestrator orchestrator, IProcessRunner runner)
{
    public static ChangeImpact Classify(IReadOnlyList<string> paths)
    {
        if (paths.Any(p => new[] { "permission", "auth", "payment", "migration", "sqlite", "security", "credential" }.Any(k => p.Contains(k, StringComparison.OrdinalIgnoreCase))))
            return ChangeImpact.Critical;
        if (paths.Count > 10)
            return ChangeImpact.Major;
        if (paths.Count > 0 && paths.All(p => new[] { ".md", ".txt", ".png", ".jpg" }.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase)))
            return ChangeImpact.Minor;
        return ChangeImpact.Small;
    }

    public static bool CoversChanges(ProjectTestProfile profile, IReadOnlyList<string> paths) => profile.SourcePaths.Length == 0 || paths.Any(path => profile.SourcePaths.Any(prefix => path.Replace('\\', '/').Equals(prefix.Trim().Replace('\\', '/').TrimEnd('/'), StringComparison.OrdinalIgnoreCase) || path.Replace('\\', '/').StartsWith(prefix.Trim().Replace('\\', '/').TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)));
    public IReadOnlyList<TestJob> Enqueue(WorkItem work, IReadOnlyList<string> paths, string snapshot, string? profileId = null, bool force = false, bool updatedOnly = false)
    {
        if (paths.Count == 0 || (!force && Classify(paths) == ChangeImpact.Minor))
            return[];
        var project = store.Projects().Single(p => p.Id == work.ProjectId);
        var created = new List<TestJob>();
        var fingerprint = Fingerprint(project, paths, snapshot).Hash;
        foreach (var profile in autonomy.TestProfiles().Where(p => p.ProjectId == work.ProjectId && p.Enabled && (profileId == null || p.Id == profileId) && (!updatedOnly || CoversChanges(p, paths))))
        {
            var signature = Signature(profile);
            var existing = autonomy.TestJobs().FirstOrDefault(j => j.ProfileId == profile.Id && j.ProjectId == work.ProjectId && j.ProfileSignature == signature && (j.State == TestJobState.Pending || (!force && j.Fingerprint == fingerprint && j.State == TestJobState.Passed)));
            if (existing != null)
            {
                if (!existing.WorkIds.Contains(work.Id))
                    existing.WorkIds.Add(work.Id);
                if (existing.State == TestJobState.Pending)
                {
                    existing.Paths = [..existing.Paths.Concat(paths).Distinct()];
                    existing.Snapshot = snapshot;
                    existing.Fingerprint = Fingerprint(project, existing.Paths, snapshot).Hash;
                    existing.ProfileSignature = signature;
                    existing.Impact = (ChangeImpact)Math.Max((int)existing.Impact, (int)Classify(paths));
                }

                autonomy.SaveTestJob(existing);
                UpdateVerification(work.Id);
                created.Add(existing);
                continue;
            }

            var job = new TestJob
            {
                ProjectId = work.ProjectId,
                ProfileId = profile.Id,
                ProfileSignature = signature,
                Fingerprint = fingerprint,
                Snapshot = snapshot,
                Paths = [..paths],
                WorkIds = [work.Id],
                Impact = Classify(paths)
            };
            autonomy.SaveTestJob(job);
            UpdateVerification(work.Id);
            created.Add(job);
        }

        return created;
    }

    // Builds (and tests) the project with its own test command or a detected one. Ran=false when
    // the project has nothing to check with.
    public async Task<AutoCheckResult> AutoCheckAsync(ProjectInfo project, CancellationToken token)
    {
        var command = AutoCheck.Detect(project, autonomy.TestProfiles());
        if (command == null || ProcessRunner.FindExecutable(command.Executable) is not { } executable)
            return new(false, false, command?.Name ?? "", command == null ? "No build or test command for this project." : command.Executable + " was not found.");
        using var lease = orchestrator.AcquireForTesting(project.Root);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(command.TimeoutSeconds, 30, 1800)));
        try
        {
            var result = await runner.RunAsync(executable, command.Arguments, project.Root, null, 100000, null, deadline.Token);
            return new(true, result.ExitCode == 0, command.Name, $"Exit: {result.ExitCode}\n" + result.Output);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new(true, false, command.Name, "The check timed out after " + command.TimeoutSeconds + " s.");
        }
    }

    // The per-task "test after build" step: the project's own test command when one is configured,
    // otherwise dotnet test on its solution file.
    public async Task<AutoCheckResult> RunTestsAfterBuildAsync(ProjectInfo project, CancellationToken token)
    {
        var own = autonomy.TestProfiles().FirstOrDefault(p => p.ProjectId == project.Id && p.Enabled);
        AutoCheckCommand command;
        if (own != null)
            command = new(own.Name, own.Executable, own.Arguments, own.TimeoutSeconds);
        else
        {
            var root = project.Root;
            if (!Directory.Exists(root))
                return new(false, false, "Tests", "The project folder is not available.");
            var solutions = Directory.GetFiles(root, "*.sln").Concat(Directory.GetFiles(root, "*.slnx")).Distinct().ToArray();
            if (solutions.Length != 1)
                return new(false, false, "Tests", solutions.Length == 0 ? "No solution file was found to test." : "The project has several solution files; configure a test command for it.");
            command = new("dotnet test", "dotnet", ["test", solutions[0], "-nologo", "-v:q"], 900);
        }
        if (ProcessRunner.FindExecutable(command.Executable) is not { } executable)
            return new(false, false, command.Name, command.Executable + " was not found.");
        using var lease = orchestrator.AcquireForTesting(project.Root);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(command.TimeoutSeconds, 30, 1800)));
        try
        {
            var result = await runner.RunAsync(executable, command.Arguments, project.Root, null, 100000, null, deadline.Token);
            return new(true, result.ExitCode == 0, command.Name, $"Exit: {result.ExitCode}\n" + result.Output);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new(true, false, command.Name, "The tests timed out after " + command.TimeoutSeconds + " s.");
        }
    }

    public async Task RunAsync(TestJob job, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (job.State != TestJobState.Pending)
            throw new InvalidOperationException("Test job is not pending.");
        var profile = autonomy.TestProfiles().Single(p => p.Id == job.ProfileId);
        var project = store.Projects().Single(p => p.Id == job.ProjectId);
        if (!profile.Enabled)
            throw new InvalidOperationException("Test command has been disabled.");
        ValidateProfile(profile);
        if (profile.ProjectId != job.ProjectId || Signature(profile) != job.ProfileSignature)
            throw new InvalidOperationException("Test configuration changed after queuing. Queue tests again to use the new configuration.");
        if (!Directory.Exists(project.Root) || (File.GetAttributes(project.Root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Test project is unavailable or has become a linked path.");
        using var lease = orchestrator.AcquireForTesting(project.Root);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(profile.TimeoutSeconds));
        var source = Fingerprint(project, job.Paths, job.Snapshot);
        job.Fingerprint = source.Hash;
        job.State = TestJobState.Running;
        autonomy.SaveTestJob(job);
        try
        {
            var result = await runner.RunAsync(ProcessRunner.FindExecutable(profile.Executable)!, profile.Arguments, project.Root, null, 100000, null, deadline.Token);
            job.State = result.ExitCode == 0 ? TestJobState.Passed : TestJobState.Failed;
            job.Output = $"Exit: {result.ExitCode}\n" + result.Output;
            if (source.Reliable && Fingerprint(project, job.Paths, job.Snapshot).Hash != source.Hash)
            {
                job.State = TestJobState.Failed;
                job.Output += "\nSource files changed during testing; this result cannot verify the snapshot.";
            }
        }
        catch (OperationCanceledException)
        {
            job.State = TestJobState.Interrupted;
            job.Output = "Cancelled or timed out. The process has stopped.";
        }
        catch (Exception ex)
        {
            job.State = TestJobState.Failed;
            job.Output = ex.Message;
        }
        finally
        {
            autonomy.SaveTestJob(job);
            foreach (var id in job.WorkIds)
            {
                UpdateVerification(id);
                store.Log(id, "SmartTest", $"{profile.Name}: {job.State}. Verified only means the configured test suite passed.\n{job.Output}");
            }
        }
    }

    public static void ValidateProfile(ProjectTestProfile profile)
    {
        if (profile.SourcePaths == null || profile.SourcePaths.Any(p => string.IsNullOrWhiteSpace(p) || Path.IsPathRooted(p) || p.Replace('\\', '/').Split('/').Any(s => s == "..") || p.Contains('*') || p.Contains('?')))
            throw new InvalidOperationException("Source paths must be relative file or directory paths without wildcards or parent traversal.");
        if (string.IsNullOrWhiteSpace(profile.Name) || ProcessRunner.FindExecutable(profile.Executable) == null || profile.TimeoutSeconds is < 5 or > 1800 || profile.Arguments == null || profile.Arguments.Any(a => a == null || a.Contains('\0')))
            throw new InvalidOperationException("Invalid test command configuration; select an .exe executable and an argument array.");
        var name = Path.GetFileNameWithoutExtension(profile.Executable);
        if (new[]
        {
            "cmd",
            "powershell",
            "pwsh",
            "wscript",
            "cscript",
            "mshta"
        }.Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Do not use a command shell as a test runner. Select dotnet, python or a runner directly.");
        if (profile.Arguments.Any(a => a.Equals("Release", StringComparison.OrdinalIgnoreCase) || a.EndsWith("=Release", StringComparison.OrdinalIgnoreCase) || a.Equals("-cRelease", StringComparison.OrdinalIgnoreCase) || a.Equals("publish", StringComparison.OrdinalIgnoreCase) || a.Equals("pack", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Smart testing does not automatically run Release/publish/pack.");
    }

    private static string Signature(ProjectTestProfile profile) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { profile.ProjectId, profile.Executable, profile.Arguments, profile.TimeoutSeconds, profile.FullValidation, profile.SourcePaths }))));
    private void UpdateVerification(string id)
    {
        var work = store.WorkItems().Single(w => w.Id == id);
        var latest = autonomy.TestJobs().Where(j => j.WorkIds.Contains(id)).GroupBy(j => j.ProfileId).Select(g => g.OrderByDescending(j => j.At).First()).ToArray();
        var allPassed = latest.Length > 0 && latest.All(j => j.State == TestJobState.Passed);
        work.Verification = allPassed && latest.Any(j => autonomy.TestProfiles().Any(p => p.Id == j.ProfileId && p.FullValidation)) ? VerificationState.Verified : latest.Any(j => j.State == TestJobState.Passed) ? VerificationState.PartiallyTested : VerificationState.Untested;
        store.SaveWork(work);
    }

    private static (string Hash, bool Reliable) Fingerprint(ProjectInfo project, IReadOnlyList<string> paths, string snapshot)
    {
        var content = new StringBuilder(snapshot);
        var reliable = paths.Count is> 0 and <= 100;
        foreach (var path in paths.Take(100).Order())
        {
            try
            {
                if (path == "project" || paths.Count > 100)
                {
                    reliable = false;
                    content.Append(Guid.NewGuid());
                    continue;
                }

                var full = FileEditService.ResolveSafe(project.Root, path.Contains(" -> ") ? path.Split(" -> ").Last() : path);
                content.Append(path);
                if (File.Exists(full) && new FileInfo(full).Length <= 10_000_000)
                    content.Append(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(full))));
                else if (File.Exists(full))
                {
                    reliable = false;
                    content.Append(Guid.NewGuid());
                }
                else
                    content.Append("MISSING");
            }
            catch (Exception ex)when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                reliable = false;
                content.Append(Guid.NewGuid());
            }
        }

        return (Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content.ToString()))), reliable);
    }
}
