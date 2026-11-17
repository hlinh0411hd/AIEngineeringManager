# Comment access verification — 2026-11-02

Focused local fixtures; no external AI, tool installation, deployment or production data changes.

Changed the bounded `taskAccess` protocol to support ReadProject as well as WriteProject. Read access requires a recognized project inspection/change request in the latest persisted user comment; write access requires an implementation request without a read-only restriction. The manager records the requested level, revalidates it after stopping, and starts a fresh execution at that level. Legacy pending requests without the new field retain their previous WriteProject meaning.

Validation:

- `dotnet build tests/Employee.Checks/Employee.Checks.csproj --no-restore -m:1 -nr:false -v minimal`: PASS, zero warnings/errors.
- `dotnet build src/Employee.Desktop/Employee.Desktop.csproj --no-restore -m:1 -nr:false -v minimal`: PASS, zero warnings/errors.
- `--access-proposal`: PASS. TextOnly → ReadProject/WriteProject and ReadProject → WriteProject through Comments; reload pending requests; duplicate requests; stale UI Run and Rerun; actual fake worker execution with the new access and original request; manual permission edits across saved/queued/paused/blocked/failed/completed states; manual edit supersedes pending request; active session guard; old session/worker access and budgets preserved; save failure rolls back and permits retry; unsupported write capability; runtime permission refusal stops without retry; wrong task/project, fabricated comment, assistant content, unsupported access and extra goal fields rejected.
- `--task-access`: PASS. Manual permission reset, archived checkpoints/history, running guard and no-op saves.
- `--task-rerun`: PASS. Latest persisted access, clean execution, preserved history and running guard.
- `--task-comments`: PASS. Comment persistence, reopened review/completed steps, original goal, budgets and approval gates.
- `--task-lifecycle`: FAIL at `TaskLifecycleEdgeChecks.cs:42`, assertion `Both sort directions: Project`. The wrapper stops there, so remaining lifecycle checks were not run. This sort assertion is outside the changed access code and was left unchanged.

The initial default parallel build exited unsuccessfully without diagnostics. Serial MSBuild with node reuse disabled succeeded; subsequent focused builds used those flags.

Limits: reopening SQLite simulates application reload; no interactive WPF smoke run or real provider execution was performed. Proposal reasons are untrusted explanatory text, not authority. These deterministic tests verify permission and protocol boundaries; they do not establish semantic proof that arbitrary model output stays within a natural-language goal. Runtime restrictions remain authoritative.
