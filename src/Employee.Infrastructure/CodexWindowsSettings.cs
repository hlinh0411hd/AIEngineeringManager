using System.Text.RegularExpressions;

namespace Employee.Infrastructure;
public static class CodexWindowsSettings
{
    public static string[] WriteProbeArguments(string root, string mode)
    {
        if (mode is not ("elevated" or "unelevated" or "mxc"))
            throw new ArgumentException("Unsupported Windows sandbox implementation.");
        const string probe = """
            $probe = Join-Path (Get-Location) ('.employee-write-check-' + [guid]::NewGuid().ToString('N') + '.tmp'); $created = $false; try { $file = [IO.File]::Open($probe, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None); $created = $true; try { $file.WriteByte(1) } finally { $file.Dispose() }; Write-Output 'EMPLOYEE_PROJECT_WRITE_OK' } finally { if ($created) { Remove-Item -LiteralPath $probe -ErrorAction Stop } }
            """;
        return["--sandbox", "workspace-write", "-c", "windows.sandbox=\"" + mode + "\"", "sandbox", "--cd", root, "--permission-profile", ":workspace", "--include-managed-config", "--", "powershell.exe", "-NoProfile", "-NonInteractive", "-Command", probe];
    }

    public static string? ReadCurrentMode()
    {
        var home = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(home))
            home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        try
        {
            var path = Path.Combine(home, "config.toml");
            if (!File.Exists(path) || new FileInfo(path).Length > 1_000_000)
                return null;
            return ParseMode(File.ReadAllText(path));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Read only the explicitly selected Windows sandbox implementation, never import other user config.
    public static string? ParseMode(string toml)
    {
        bool windows = false, top = true;
        foreach (var original in toml.Split('\n'))
        {
            var line = original.Trim();
            if (line.StartsWith('['))
            {
                windows = line == "[windows]";
                top = false;
                continue;
            }

            var key = windows ? "sandbox" : top ? "windows\\.sandbox" : null;
            if (key == null)
                continue;
            var match = Regex.Match(line, "^" + key + "\\s*=\\s*[\"'](?<mode>elevated|unelevated|mxc)[\"']\\s*(?:#.*)?$");
            if (match.Success)
                return match.Groups["mode"].Value;
        }

        return null;
    }

    public static bool IsPermissionRefusal(string output)
    {
        var text = TaskReviewReport.ReadableOutput(output).ToLowerInvariant();
        var refuses = new[]
        {
            "chưa thể",
            "không thể",
            "cannot",
            "can't",
            "unable to"
        }.Any(text.Contains);
        var readOnly = new[]
        {
            "chỉ có quyền đọc",
            "không có quyền ghi",
            "không cho phép sửa",
            "read-only",
            "read only"
        }.Any(text.Contains);
        return refuses && readOnly;
    }
}
