# Codex Windows write permissions — 2026-11-01

The production task log showed WriteProject requested but an immediate agent response saying the session was read-only, with no file/tool attempt. The adapter used `exec --ignore-user-config`, omitting the user's configured `[windows] sandbox = "unelevated"` along with other user settings. A successful CLI exit was incorrectly presented as NeedsReview even when the agent explicitly refused to work due to read-only permissions.

The adapter now passes requested sandbox access and project root at the top-level CLI, and preserves only the existing Windows sandbox implementation through a validated explicit `-c windows.sandbox=...` override. It continues to ignore unrelated user configuration. It does not change saved Codex config, select a different Windows sandbox implementation, disable restrictions or use full-access/bypass options. Missing/unrecognized Windows sandbox configuration blocks write work before inference.

Before a WriteProject invocation, the adapter uses the installed Codex sandbox command with the workspace permission profile and managed requirements to create/delete one uniquely named temporary file in the selected project. The temporary file uses CreateNew and is removed by exact literal path. Failure/timeout stops before model inference. The diagnostic probe checks sandbox command write access; it is not proof that an AI completed the user's request.

Explicit read-only refusal in a Codex final response is classified as Permission failure. The orchestrator saves its checkpoint and sets WaitingForPermission instead of NeedsReview, without auto-fallback or privilege expansion. Handoff instructions state that old checkpoint permission statements describe the old run and that current runtime permissions remain authoritative.

Verification: Debug build succeeded with zero warnings/errors; 28 acceptance checks passed. A real no-inference write probe using the adapter's argument builder returned EMPLOYEE_PROJECT_WRITE_OK in D:\Projects\AIEmployeeManager. The earlier direct sandbox check also returned PROJECT_WRITE_OK. No cloud inference was invoked to rerun the UI-update request; no existing task permissions, attempt counters or production records were changed. No Release build or archive was created.

OpenAI references: [Windows sandbox configuration](https://learn.chatgpt.com/docs/windows/windows-sandbox) and [permissions](https://learn.chatgpt.com/docs/permissions). Existing vendor/managed restrictions still apply.

To try the corrected adapter: close the previous application, open Run.bat, select the existing WriteProject task and use Start / Resume. Existing history remains visible; this new run is still subject to the task's persisted attempt budget.


Run/resume and Rerun permission refresh (2026-11-02):
- Reload the stored task rather than trusting the UI snapshot. Apply validated pending comment permissions before choosing the route.
- Comment access changes use the same atomic archive/reset as manual access edits. Old sessions, handoffs, results and approvals remain in archived history; the current task starts without an execution checkpoint or old route.
- Explicit saved grants survive review-request normalization. Rerun preserves an already classified task instead of classifying only its original request again.
- ModelRouter validates provider/model eligibility against the current access. If no eligible route exists, keep a blocked/waiting state and its explanation; do not execute a read-only fallback. CLI execution remains fresh and ephemeral.
