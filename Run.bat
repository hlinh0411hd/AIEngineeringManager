@echo off
setlocal
cd /d "%~dp0"
powershell -NoProfile -Command "$candidates = @('src\Employee.Desktop\bin\Release\net10.0-windows\AIEmployeeManager.exe', 'src\Employee.Desktop\bin\Debug\net10.0-windows\AIEmployeeManager.exe'); $app = $candidates | ForEach-Object { $file = Get-Item -LiteralPath $_ -ErrorAction SilentlyContinue; if ($file) { $latest = @('AIEmployeeManager.dll', 'Employee.Infrastructure.dll', 'Employee.Core.dll') | ForEach-Object { Get-Item -LiteralPath (Join-Path $file.DirectoryName $_) -ErrorAction SilentlyContinue } | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1; [pscustomobject]@{ File = $file.FullName; Updated = $latest.LastWriteTimeUtc } } } | Sort-Object Updated -Descending | Select-Object -First 1; if ($null -eq $app) { exit 1 }; Start-Process -FilePath $app.File"
if errorlevel 1 (
  echo Run Build.bat first, or open the built application directly.
  pause
)
