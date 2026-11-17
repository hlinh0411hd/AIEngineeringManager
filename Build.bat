@echo off
setlocal
cd /d "%~dp0"
where dotnet >nul 2>nul
if errorlevel 1 (
  echo Install .NET 10 SDK first: https://dotnet.microsoft.com/download/dotnet/10.0
  pause
  exit /b 1
)
dotnet restore AIEmployeeManager.slnx -m:1 -nr:false
if errorlevel 1 goto failed
dotnet build AIEmployeeManager.slnx -c Release --no-restore -m:1 -nr:false
if errorlevel 1 goto failed
dotnet run --project tests\Employee.Checks -c Release --no-build
if errorlevel 1 goto failed
powershell -NoProfile -Command "$destination = 'src\Employee.Desktop\bin\Release\net10.0-windows\updater'; New-Item -ItemType Directory -Path $destination -Force | Out-Null; Copy-Item -Path 'src\Employee.Updater\bin\Release\net10.0\*' -Destination $destination -Recurse -Force"
if errorlevel 1 goto failed

rem Portable package: Release\AIEmployeeManager holds only the newest build, with the .NET
rem runtime included, so the folder can be copied to another PC and run from AIEmployeeManager.exe.
rem No user data is included; the app creates a Data folder next to the exe on first start.
set "PACKAGE=Release\AIEmployeeManager"
if exist "Release" rmdir /s /q "Release"
if exist "Release" (
  echo Could not delete the old Release folder. Close AIEmployeeManager if it runs from there, then build again.
  goto failed
)
dotnet publish src\Employee.Desktop\Employee.Desktop.csproj -c Release -r win-x64 --self-contained true -o "%PACKAGE%" -m:1 -nr:false
if errorlevel 1 goto failed
dotnet publish src\Employee.Updater\Employee.Updater.csproj -c Release -r win-x64 --self-contained true -o "%PACKAGE%\updater" -m:1 -nr:false
if errorlevel 1 goto failed
echo.
echo Build and checks passed.
echo Package for other PCs: %CD%\%PACKAGE%  (copy the whole folder, run AIEmployeeManager.exe)
echo Run here: src\Employee.Desktop\bin\Release\net10.0-windows\AIEmployeeManager.exe
pause
exit /b 0
:failed
echo Build or verification failed. Review the messages above.
pause
exit /b 1
