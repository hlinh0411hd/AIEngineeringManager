using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// A separate process, invoked only after explicit user review. Never builds, downloads or installs packages.
try
{
    if (args.Length != 2)
        throw new InvalidOperationException("Usage: Employee.Updater plan.json approved-sha256");
    var planPath = Path.GetFullPath(args[0]);
    var bytes = File.ReadAllBytes(planPath);
    if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(args[1], StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Approved update plan changed.");
    var plan = JsonSerializer.Deserialize<Plan>(bytes) ?? throw new InvalidOperationException("Invalid update plan.");
    if (plan.Files.Count is < 1 or > 10000 || string.IsNullOrWhiteSpace(plan.ApplicationId))
        throw new InvalidOperationException("Invalid update manifest.");
    var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(plan.Source));
    var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(plan.Target));
    var backup = Path.GetFullPath(plan.Backup);
    bool Under(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    if ((Under(source, target) && !Under(source, Path.GetFullPath(plan.DataRoot))) || Under(target, source) || Under(backup, source) || Under(source, backup) || Under(target, backup))
        throw new InvalidOperationException("Source, target and backup paths overlap unsafely.");
    void NoLinks(string path)
    {
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(path)!); directory != null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Linked directories are not supported by the updater.");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Linked update file rejected.");
    }

    string Resolve(string root, string relative)
    {
        if (Path.IsPathFullyQualified(relative) || relative.Split('/', '\\').Any(p => p is ".." or ".git" or ".agents" or "Data" or "data" or "node_modules"))
            throw new InvalidOperationException("Invalid package path.");
        var name = Path.GetFileName(relative);
        if (new[]
        {
            "data-location.json",
            "settings.json",
            "config.json",
            "profiles.json",
            "notifications.json",
            "project.json"
        }.Contains(name, StringComparer.OrdinalIgnoreCase) || name.StartsWith(".env", StringComparison.OrdinalIgnoreCase) || new[]
        {
            ".db",
            ".sqlite",
            ".bak",
            ".pem",
            ".key",
            ".pfx"
        }.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Data/secrets must not be included in an update package.");
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!Under(full, root) || full == root)
            throw new InvalidOperationException("Update path escapes its root.");
        NoLinks(full);
        return full;
    }

    var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    long total = 0;
    foreach (var file in plan.Files)
    {
        if (!unique.Add(file.Path))
            throw new InvalidOperationException("Duplicate package path.");
        var input = Resolve(source, file.Path);
        var output = Resolve(target, file.Path);
        if (plan.ProtectedPaths.Any(p => Under(output, Path.GetFullPath(p))))
            throw new InvalidOperationException("Update would modify protected user data.");
        total += new FileInfo(input).Length;
        if (total > 2_000_000_000)
            throw new InvalidOperationException("Update package exceeds 2 GB.");
        using var stream = File.OpenRead(input);
        if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Staged file changed: " + file.Path);
    }

    var entry = Resolve(target, plan.Entry);
    if (!plan.Files.Any(f => f.Path.Equals(plan.Entry, StringComparison.OrdinalIgnoreCase)))
        throw new InvalidOperationException("Package does not include its entry executable.");
    if (plan.ParentPid > 0)
    {
        try
        {
            var parent = Process.GetProcessById(plan.ParentPid);
            if (!parent.WaitForExit(60000))
                throw new InvalidOperationException("Application did not close; no files replaced.");
        }
        catch (ArgumentException)
        {
        }
    }

    foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(entry)))
    {
        using (process)
        {
            try
            {
                if (process.MainModule?.FileName?.Equals(entry, StringComparison.OrdinalIgnoreCase) == true)
                    throw new InvalidOperationException("Target application is still running.");
            }
            catch (System.ComponentModel.Win32Exception)
            {
                throw new InvalidOperationException("Cannot verify target application is closed.");
            }
        }
    }

    Directory.CreateDirectory(backup);
    var changed = new List<(string Output, string? Original)>();
    Process? child = null;
    try
    {
        // Copy and verify the entire payload into private scratch before changing installed files.
        var prepared = Path.Combine(backup, "prepared");
        Directory.CreateDirectory(prepared);
        foreach (var file in plan.Files)
        {
            var staged = Resolve(prepared, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
            File.Copy(Resolve(source, file.Path), staged, false);
            using var stream = File.OpenRead(staged);
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Payload changed while copying.");
        }

        foreach (var file in plan.Files)
        {
            var output = Resolve(target, file.Path);
            string? original = null;
            if (File.Exists(output))
            {
                original = Resolve(Path.Combine(backup, "original"), file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                File.Copy(output, original, false);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            changed.Add((output, original));
            File.Copy(Resolve(prepared, file.Path), output, true);
        }

        if (plan.ApplicationId == "AIEmployeeManager")
        {
            if (plan.Entry != "AIEmployeeManager.exe" || string.IsNullOrWhiteSpace(plan.HealthFile) || string.IsNullOrWhiteSpace(plan.HealthNonce))
                throw new InvalidOperationException("Invalid self-update health protocol.");
            var start = new ProcessStartInfo(entry)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = target
            };
            foreach (var arg in new[]
            {
                "--data-dir",
                plan.DataRoot,
                "--update-health-file",
                plan.HealthFile,
                "--update-health-nonce",
                plan.HealthNonce
            }

            )
                start.ArgumentList.Add(arg);
            child = Process.Start(start) ?? throw new InvalidOperationException("Updated app did not start.");
            var until = DateTime.UtcNow.AddSeconds(40);
            bool healthy = false;
            while (DateTime.UtcNow < until && !child.HasExited)
            {
                try
                {
                    healthy = File.Exists(plan.HealthFile) && File.ReadAllText(plan.HealthFile) == plan.HealthNonce;
                }
                catch (IOException)
                {
                }

                if (healthy)
                    break;
                Thread.Sleep(100);
            }

            if (!healthy)
                throw new InvalidOperationException("Updated app did not pass startup readiness; restoring previous files.");
            File.WriteAllText(plan.HealthFile + ".commit", plan.HealthNonce);
        }

        File.WriteAllText(Path.Combine(backup, "result.json"), JsonSerializer.Serialize(new { Success = true, plan.ApplicationId, At = DateTimeOffset.UtcNow, Note = plan.ApplicationId == "AIEmployeeManager" ? "Startup readiness acknowledged" : "Files verified; functional tool health requires an explicit connector check" }));
        File.WriteAllText(Path.Combine(backup, "rollback.json"), JsonSerializer.Serialize(changed.Select(c => new { c.Output, c.Original })));
    }
    catch (Exception failure)
    {
        File.WriteAllText(Path.Combine(backup, "result.json"), JsonSerializer.Serialize(new { Success = false, Error = failure.Message, At = DateTimeOffset.UtcNow, Rollback = "Requested" }));
        if (child is { HasExited: false })
        {
            child.Kill(true);
            child.WaitForExit(10000);
        }

        foreach (var item in changed.AsEnumerable().Reverse())
        {
            if (item.Original != null)
                File.Copy(item.Original, item.Output, true);
            else if (File.Exists(item.Output))
                File.Delete(item.Output);
        }

        if (plan.ApplicationId == "AIEmployeeManager" && File.Exists(plan.DatabaseBackup))
        {
            var db = Path.Combine(Path.GetFullPath(plan.DataRoot), "manager.db");
            File.Copy(plan.DatabaseBackup, db, true);
            foreach (var suffix in new[]
            {
                "-wal",
                "-shm"
            }

            )
                if (File.Exists(db + suffix))
                    File.Delete(db + suffix);
            Process.Start(new ProcessStartInfo(entry) { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "--data-dir", plan.DataRoot } });
        }

        throw;
    }

    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}

sealed record PayloadFile(string Path, string Sha256);
sealed class Plan
{
    public string ApplicationId { get; set; } = "";
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
    public string Backup { get; set; } = "";
    public string Entry { get; set; } = "";
    public int ParentPid { get; set; }
    public List<PayloadFile> Files { get; set; } = [];
    public string[] ProtectedPaths { get; set; } = [];
    public string DataRoot { get; set; } = "";
    public string DatabaseBackup { get; set; } = "";
    public string HealthFile { get; set; } = "";
    public string HealthNonce { get; set; } = "";
}
