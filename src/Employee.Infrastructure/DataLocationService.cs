using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Employee.Infrastructure;
public sealed class DataLocationService(string applicationRoot)
{
    public string ConfigPath => Path.Combine(applicationRoot, "data-location.json");
    public string DefaultFolder => Normalize(Path.Combine(applicationRoot, "Data"));

    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    public string Resolve(string? explicitFolder = null)
    {
        if (explicitFolder != null)
            return Normalize(explicitFolder);
        if (!File.Exists(ConfigPath))
            return DefaultFolder;
        var config = JsonSerializer.Deserialize<Location>(File.ReadAllText(ConfigPath));
        if (string.IsNullOrWhiteSpace(config?.Folder))
            throw new InvalidOperationException("Invalid data folder configuration: " + ConfigPath);
        var folder = Normalize(config.Folder);
        if (!Directory.Exists(folder) || !File.Exists(Path.Combine(folder, "manager.db")))
            throw new InvalidOperationException("Configured data was not found at " + folder + ". Reconnect the drive or fix data-location.json; the app will not replace it with empty data automatically.");
        return folder;
    }

    public void Save(string folder)
    {
        folder = Normalize(folder);
        var temporary = ConfigPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Location(folder), new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, ConfigPath, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public static string FindApplicationRoot(string executableFolder)
    {
        for (var directory = new DirectoryInfo(executableFolder); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AIEmployeeManager.slnx")) && File.Exists(Path.Combine(directory.FullName, "Run.bat")))
                return directory.FullName;
        return Normalize(executableFolder);
    }

    public static IDisposable Acquire(string folder)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(folder).ToUpperInvariant())))[..20];
        var mutex = new Mutex(false, "Local\\AIEmployeeManager-" + hash);
        bool acquired;
        try
        {
            acquired = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
        }

        if (!acquired)
        {
            mutex.Dispose();
            throw new InvalidOperationException("Another app instance is using this data folder. Close it first.");
        }

        return new Lease(mutex);
    }

    public static void CopyDatabase(string source, string destination)
    {
        source = Normalize(source);
        destination = Normalize(destination);
        if (source.Equals(destination, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The new folder is the current data folder.");
        Directory.CreateDirectory(destination);
        if (Directory.EnumerateFileSystemEntries(destination).Any())
            throw new InvalidOperationException("Choose an empty folder to avoid overwriting existing data.");
        var sourceDb = Path.Combine(source, "manager.db");
        if (!File.Exists(sourceDb))
            throw new InvalidOperationException("Source database not found.");
        var targetDb = Path.Combine(destination, "manager.db");
        try
        {
            // Online backup includes committed WAL contents. Never copy a live .db file directly.
            using var input = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sourceDb, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            input.Open();
            using var output = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = targetDb, Pooling = false }.ToString());
            output.Open();
            input.BackupDatabase(output);
            using var check = output.CreateCommand();
            check.CommandText = "PRAGMA integrity_check";
            if (!string.Equals(check.ExecuteScalar()?.ToString(), "ok", StringComparison.Ordinal))
                throw new InvalidOperationException("Database copy failed the integrity check.");
            var notices = Path.Combine(source, "notifications.json");
            if (File.Exists(notices))
                File.Copy(notices, Path.Combine(destination, "notifications.json"));
        }
        catch
        {
            foreach (var suffix in new[]
            {
                "",
                "-wal",
                "-shm"
            }

            )
            {
                var path = targetDb + suffix;
                if (File.Exists(path))
                    File.Delete(path);
            }

            throw;
        }
    }

    private sealed record Location(string Folder);
    private sealed class Lease(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }
}
