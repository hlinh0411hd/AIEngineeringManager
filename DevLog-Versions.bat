@echo off
setlocal EnableDelayedExpansion
cd /d "%~dp0"
rem Opens an old build of the app for dev-log videos. Every build gets its own empty data folder
rem (artifacts\devlog-data\<name>), so old code never touches your real Data\manager.db.

set n=0
for %%V in (
  "ui-refresh|01/11 16:15|First UI"
  "project-scan-check\desktop|01/11 18:02|Project scan"
  "attachment-check|01/11 20:12|Attachments"
  "request-state-filter|01/11 20:24|Request state filter"
  "clipboard-fix|01/11 20:28|Clipboard fix"
  "state-filter-check|01/11 20:35|State filter"
  "comment-queue-desktop|01/11 20:37|Comment queue"
  "notification-verification|01/11 20:41|Notifications"
  "tasks-layout-check|01/11 20:56|Tasks layout"
  "model-selection-check|01/11 21:15|Model selection"
  "task-navigation-check|01/11 22:47|Task navigation"
  "project-assignment|01/11 23:02|Project assignment"
  "theme-check|01/11 23:23|New theme"
  "task-delete-check|02/11 02:04|Task delete"
  "retention-check|02/11 02:06|Retention"
  "task-list-check|02/11 02:07|Task list"
  "attachment-preview-check|03/11 09:51|Attachment preview"
  "..\Release\AIEmployeeManager|latest|Latest package"
) do (
  set /a n+=1
  for /f "tokens=1-3 delims=|" %%a in (%%V) do (
    set "dir!n!=%%a"
    set "name!n!=%%c"
    echo   !n!. [%%b] %%c
  )
)

:ask
echo.
set "pick="
set /p pick=Version number to open (Enter to quit):
if not defined pick exit /b 0
if not defined dir%pick% (
  echo No version %pick%.
  goto ask
)
set "folder=artifacts\!dir%pick%!"
if not exist "!folder!\AIEmployeeManager.exe" (
  echo Not found: !folder!\AIEmployeeManager.exe
  goto ask
)
set "data=artifacts\devlog-data\version-%pick%"
if not exist "!data!" mkdir "!data!"
echo Opening "!name%pick%!" with its own data folder !data!
start "" "!folder!\AIEmployeeManager.exe" --data-dir "%CD%\!data!"
goto ask
