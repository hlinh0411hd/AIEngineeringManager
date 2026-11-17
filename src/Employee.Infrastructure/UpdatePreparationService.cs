using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Employee.Infrastructure;
public sealed record PreparedUpdate(string PlanFile, string ApprovedHash, string Target, int Files, bool SelfUpdate, string Backup);
public static class UpdatePreparationService
{
    public static PreparedUpdate Prepare(SqliteStore store, string dataRoot, string stagedFolder, string target, string applicationId, string entry, string[] protectedPaths, bool selfUpdate)
    {
        stagedFolder = Path.GetFullPath(stagedFolder);
        target = Path.GetFullPath(target);
        if (stagedFolder.StartsWith("\\\\") || target.StartsWith("\\\\"))
            throw new InvalidOperationException("Choose local update folders.");
        dataRoot = Path.GetFullPath(dataRoot);
        bool ContainsPath(string root, string path) => path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (ContainsPath(stagedFolder, dataRoot) || ContainsPath(stagedFolder, target) || ContainsPath(target, stagedFolder))
            throw new InvalidOperationException("Use a separate staging folder outside installed files and data. Staging cannot contain the manager data folder.");
        if (Path.GetFileName(entry) != entry || string.IsNullOrWhiteSpace(entry))
            throw new InvalidOperationException("Entry must be a simple executable filename.");
        if (!File.Exists(Path.Combine(stagedFolder, entry)))
            throw new InvalidOperationException("The staged build must contain " + entry);
        var id = Guid.NewGuid().ToString("N");
        var work = Path.Combine(dataRoot, "updates", id);
        var payload = Path.Combine(work, "payload");
        Directory.CreateDirectory(payload);
        var files = new List<object>();
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(stagedFolder, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }))
        {
            var relative = Path.GetRelativePath(stagedFolder, file);
            var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(file);
            if (parts.Any(p => new[] { ".git", ".agents", "Data", "node_modules", "backups", "updates" }.Contains(p, StringComparer.OrdinalIgnoreCase)) || name.StartsWith(".env", StringComparison.OrdinalIgnoreCase) || new[]
            {
                "data-location.json",
                "settings.json",
                "config.json",
                "profiles.json",
                "notifications.json",
                "project.json"
            }.Contains(name, StringComparer.OrdinalIgnoreCase) || new[]
            {
                ".db",
                ".sqlite",
                ".bak",
                ".pem",
                ".key",
                ".pfx"
            }.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase))
                continue;
            total += new FileInfo(file).Length;
            if (total > 2_000_000_000 || files.Count >= 10000)
                throw new InvalidOperationException("Staged update exceeds 2 GB or 10,000 files.");
            var copy = Path.Combine(payload, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy, false);
            using var stream = File.OpenRead(copy);
            files.Add(new { Path = relative, Sha256 = Convert.ToHexString(SHA256.HashData(stream)) });
        }

        var backup = Path.Combine(work, "backup");
        var databaseBackup = Path.Combine(work, "manager-before.db");
        if (selfUpdate)
            store.Backup(databaseBackup);
        var health = Path.Combine(work, "startup.ready");
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var plan = new
        {
            ApplicationId = applicationId,
            Source = payload,
            Target = target,
            Backup = backup,
            Entry = entry,
            ParentPid = selfUpdate ? Environment.ProcessId : 0,
            Files = files,
            ProtectedPaths = protectedPaths.Append(dataRoot).ToArray(),
            DataRoot = dataRoot,
            DatabaseBackup = selfUpdate ? databaseBackup : "",
            HealthFile = health,
            HealthNonce = nonce
        };
        var planFile = Path.Combine(work, "plan.json");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(plan, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllBytes(planFile, bytes);
        return new(planFile, Convert.ToHexString(SHA256.HashData(bytes)), target, files.Count, selfUpdate, backup);
    }

    public static Process Launch(PreparedUpdate update, string appRoot)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "updater", "Employee.Updater.exe"),
            Path.Combine(appRoot, "src", "Employee.Updater", "bin", "Release", "net10.0", "Employee.Updater.exe"),
            Path.Combine(appRoot, "src", "Employee.Updater", "bin", "Debug", "net10.0", "Employee.Updater.exe")
        };
        var helper = candidates.Where(File.Exists).OrderByDescending(p => File.GetLastWriteTimeUtc(Path.ChangeExtension(p, ".dll"))).FirstOrDefault() ?? throw new InvalidOperationException("Updater helper is not built. Run Build.bat explicitly before applying an update.");
        var runnerRoot = Path.Combine(Path.GetDirectoryName(update.PlanFile)!, "runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runnerRoot);
        var helperRoot = Path.GetDirectoryName(helper)!;
        foreach (var file in Directory.EnumerateFiles(helperRoot, "*", SearchOption.TopDirectoryOnly))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Linked updater file rejected.");
            File.Copy(file, Path.Combine(runnerRoot, Path.GetFileName(file)), false);
        }

        helper = Path.Combine(runnerRoot, Path.GetFileName(helper));
        var start = new ProcessStartInfo(helper)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = appRoot
        };
        start.ArgumentList.Add(update.PlanFile);
        start.ArgumentList.Add(update.ApprovedHash);
        return Process.Start(start) ?? throw new InvalidOperationException("Could not launch the independent updater.");
    }
}
