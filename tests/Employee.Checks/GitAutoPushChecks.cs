using Employee.Core;
using Employee.Infrastructure;

static class GitAutoPushChecks
{
    public static async Task<int> Run()
    {
        var git = ProcessRunner.FindExecutable("git");
        if (git == null)
        {
            Console.WriteLine("SKIP git auto push: git is not installed");
            return 0;
        }

        var folder = Path.Combine(Path.GetTempPath(), "EmployeeGitPushChecks", Guid.NewGuid().ToString("N"));
        var runner = new ProcessRunner();
        async Task<string> G(string dir, params string[] args)
        {
            var r = await runner.RunAsync(git, args, dir, null, 8000, null, CancellationToken.None);
            if (r.ExitCode != 0)
                throw new Exception("git " + string.Join(' ', args) + " failed: " + r.Output);
            return r.Output;
        }

        void Assert(bool value, string what)
        {
            if (!value)
                throw new Exception("Git auto push assertion failed: " + what);
        }

        var push = new GitAutoPush(runner, git);
        var plain = Path.Combine(folder, "plain");
        var noRemote = Path.Combine(folder, "noremote");
        var bare = Path.Combine(folder, "remote.git");
        var work = Path.Combine(folder, "work");
        foreach (var d in new[] { plain, noRemote, bare, work })
            Directory.CreateDirectory(d);
        ProjectInfo P(string root) => new("p", "p", root);

        Assert((await push.RunAsync(P(plain), "x")).Outcome == GitPushOutcome.Skipped, "folder without .git is skipped");

        await G(noRemote, "init");
        Assert((await push.RunAsync(P(noRemote), "x")).Outcome == GitPushOutcome.Skipped, "repo without remote is skipped");

        await G(bare, "init", "--bare");
        await G(work, "init");
        await G(work, "config", "user.email", "check@example.com");
        await G(work, "config", "user.name", "Check");
        await G(work, "remote", "add", "origin", bare);
        File.WriteAllText(Path.Combine(work, "a.txt"), "one");
        File.WriteAllText(Path.Combine(work, ".env"), "SECRET=1");
        var first = await push.RunAsync(P(work), "Add the first file\nsecond line");
        Assert(first.Outcome == GitPushOutcome.Pushed, "first push: " + first.Message);
        var log = await G(bare, "log", "--all", "--format=%s");
        Assert(log.Contains("Task completed: Add the first file"), "commit message from the request: " + log);
        var files = await G(bare, "ls-tree", "-r", "--name-only", "HEAD");
        Assert(files.Contains("a.txt") && !files.Contains(".env"), "secret file is not pushed: " + files);

        File.WriteAllText(Path.Combine(work, "a.txt"), "two");
        Assert((await push.RunAsync(P(work), "Second")).Outcome == GitPushOutcome.Pushed, "second push uses the upstream");
        Assert((await G(bare, "rev-list", "--all", "--count")).Trim() == "2", "two commits on the remote");
        Assert((await push.RunAsync(P(work), "Nothing")).Outcome == GitPushOutcome.Pushed, "no changes is still fine");
        Assert((await G(bare, "rev-list", "--all", "--count")).Trim() == "2", "no empty commit");
        Assert(GitAutoPush.CommitMessage("").Length > 0 && GitAutoPush.CommitMessage(new string('x', 200)).Length < 100, "commit message is bounded");
        Console.WriteLine("PASS git auto push: skips without repo/remote, commits, pushes, keeps secrets out");
        return 0;
    }
}
