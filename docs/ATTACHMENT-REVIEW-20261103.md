# Attachment review — 2026-11-03

Scope: task creation, comment composer, and comment history/details. No application source changes were needed based on this review. No images were generated or replaced.

## Source review

- Both composers instantiate `AttachmentEditor`. Its ListBox uses a horizontal WrapPanel, disables horizontal scrolling, and permits vertical scrolling. This should constrain wrapping to the available width.
- History uses a WrapPanel inside a stretched message body with horizontal scrolling disabled on its parent ScrollViewer.
- All three locations use `AttachmentPreview.Create`: decoded images get a 140×100 thumbnail tile. The image branch contains no filename TextBlock or download button. The filename remains available as a tooltip/accessibility label and preview window title.
- History skips its Save file button when decoding succeeds. Non-image attachments retain their file controls.
- Clicking an image calls the in-app modal viewer using attachment bytes; that path does not save/download a file. Close and Escape handlers exist.
- Composer file selection appends validated files in order; clipboard paste appends an encoded image. The thumbnail removal callback removes its attachment and refreshes the list. The selected-file removal control remains present.
- Fixed-size tiles require about 148 logical pixels plus container chrome. Widths below one tile may clip; wrapping alone does not shrink an individual tile. This was not measured at runtime.

These are source findings, not successful UI interaction checks. One image, multiple images, narrow-width wrapping, actual click-to-open/close and add/remove interactions at the three locations remain unverified.

## Executed checks

1. `C:\Program Files\dotnet\dotnet.exe build src\Employee.Desktop\Employee.Desktop.csproj --no-restore -c Debug -m:1 -nr:false -v:minimal` — succeeded once, 0 warnings, 0 errors.
2. Existing desktop `--smoke` with isolated data at `artifacts/attachment-review-20261103` — exited 1 before attachment checks: `InvalidOperationException: UI acceptance: missing routing proposal`, `MainWindow.xaml.cs:93`. Result: `artifacts/attachment-review-20261103/ui-smoke.txt`. This is a smoke assertion failure, not a permission denial or compilation failure. No attachment runtime pass can be inferred.

No desktop interaction tool is available in this session. No tools/dependencies were installed, and no deployment, release, archive or external AI invocation was performed. `rg` is unavailable, so source inspection used native PowerShell. The workspace has no Git metadata.
