# Tool selection and partial failure recovery — 2026-11-03

Worker context previously said `Stop when blocked by permissions` without
distinguishing the failed operation or independent work. It now includes shared
WorkerToolGuidance in both worker context and coordinator planning requests.

The policy distinguishes a failed/unavailable helper from a denied underlying
operation. A PowerShell PSSecurityException does not establish that PNG generation
or all project file access failed. Stop the denied action, preserve completed
artifacts and continue independent work using capabilities already permitted for
the task. Do not change execution policy, encode/relaunch the denied script,
expand access, install dependencies, switch providers or use paid API fallbacks.
Existing role/access gates, explicit user stop requirements and explicit media
tool routes remain authoritative. Runtime permission failure classification is
unchanged; this change does not turn actual refusals into successes/retries.

Verification: `Employee.Checks --tool-recovery` compiled and passed 91 checks
against actual BuildContext output across Codex/Claude, all three access levels
and coordinator/worker roles. No live AI worker or image-generation call was
made, and no Windows security setting changed. This validates prompt delivery,
not a guarantee of a model's future decisions.

Source changes require rebuilding/restarting the manager to update its executable.
A matching project AGENTS.md policy is also installed in NihaoGarden so subsequent
worker requests can receive guidance through the existing project-rules reader.
It does not rewrite an already-dispatched worker prompt or resume stopped tasks.

Official boundary guidance: https://learn.chatgpt.com/docs/windows/windows-sandbox
