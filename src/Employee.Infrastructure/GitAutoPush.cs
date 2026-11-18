using Employee.Core;

namespace Employee.Infrastructure;

public enum GitPushOutcome { Skipped, Pushed, Failed }

public sealed record GitPushResult(GitPushOutcome Outcome, string Message);

// After the user confirms a task as complete, commits what is pending in the project and pushes it, but only when the project root
// is itself a Git repository with a remote. Never forces, never rewrites history, never prompts for credentials.
public sealed class GitAutoPush
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly string[] SecretExclusions = [":(exclude)*.env*", ":(exclude)*.pem", ":(exclude)*.key", ":(exclude)*secret*", ":(exclude)*credential*"];
    private static readonly Dictionary<string, string> NoPrompt = new() { ["GIT_TERMINAL_PROMPT"] = "0", ["GCM_INTERACTIVE"] = "never" };

    private readonly IProcessRunner runner;
    private readonly string? git;

    public GitAutoPush(IProcessRunner runner, string? gitExecutable = null)
    {
        this.runner = runner;
        git = gitExecutable ?? ProcessRunner.FindExecutable("git");
    }

    public static string CommitMessage(string request)
    {
        var line = (request ?? "").Split('\n', 2)[0].Trim();
        if (line.Length > 72)
            line = line[..72].TrimEnd() + "...";
        return line.Length == 0 ? "Task completed" : "Task completed: " + line;
    }

    public async Task<GitPushResult> RunAsync(ProjectInfo project, string request, CancellationToken token = default)
    {
        var root = project.Root;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return new(GitPushOutcome.Skipped, "Project folder not found.");
        // The project root must own the repository; a parent repository is not this project's Git config.
        var marker = Path.Combine(root, ".git");
        if (!Directory.Exists(marker) && !File.Exists(marker))
            return new(GitPushOutcome.Skipped, "No Git repository in the project.");
        if (git == null)
            return new(GitPushOutcome.Skipped, "Git is not installed.");

        await Gate.WaitAsync(token);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            var t = timeout.Token;
            Task<ProcessResult> Run(params string[] args) => runner.RunAsync(git, args, root, null, 8000, null, t, NoPrompt);

            var remotes = (await Run("remote")).Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (remotes.Length == 0)
                return new(GitPushOutcome.Skipped, "Git has no remote configured.");
            var branch = (await Run("rev-parse", "--abbrev-ref", "HEAD")).Output.Trim();
            if (branch.Length == 0 || branch == "HEAD")
                return new(GitPushOutcome.Skipped, "Git is not on a branch (detached HEAD or no commits).");

            var add = await Run(["add", "-A", "--", ".", .. SecretExclusions]);
            if (add.ExitCode != 0)
                return new(GitPushOutcome.Failed, "git add failed: " + Tail(add.Output));
            var staged = await Run("diff", "--cached", "--quiet");
            var committed = false;
            if (staged.ExitCode == 1)
            {
                var commit = await Run("commit", "-m", CommitMessage(request));
                if (commit.ExitCode != 0)
                    return new(GitPushOutcome.Failed, "git commit failed: " + Tail(commit.Output));
                committed = true;
            }
            else if (staged.ExitCode != 0)
                return new(GitPushOutcome.Failed, "git diff failed: " + Tail(staged.Output));

            var upstream = await Run("rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}");
            var remote = remotes.Contains("origin") ? "origin" : remotes[0];
            var push = upstream.ExitCode == 0 ? await Run("push") : await Run("push", "-u", remote, "HEAD");
            if (push.ExitCode != 0)
                return new(GitPushOutcome.Failed, (committed ? "Committed locally, but git push failed: " : "git push failed: ") + Tail(push.Output));
            return new(GitPushOutcome.Pushed, (committed ? "Committed and pushed " : "Pushed ") + branch + " to Git.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new(GitPushOutcome.Failed, "Git push timed out.");
        }
        finally
        {
            Gate.Release();
        }
    }

    private static string Tail(string output)
    {
        var text = output.Trim();
        return text.Length <= 400 ? text : "..." + text[^400..];
    }
}
