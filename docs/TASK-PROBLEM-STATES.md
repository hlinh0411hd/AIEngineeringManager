# Task problem states

2026-11-02. Source-only change, no build/tests or application/database execution performed during implementation.

- **WrongAccess**: the task or step needs a higher approved access, a provider refuses permissions, Codex receives TextOnly, or otherwise eligible providers lack project-write permission. The reason reports the requested/current scope or the provider's refusal. Correct task/provider access, then explicitly resume.
- **WrongProject**: missing registered project ID, unavailable project folder, or task project outside the session's approved projects. The reason includes the relevant ID/path/scope. Select the right project or restore the folder, then explicitly resume. This does not claim semantic detection of a valid but unintended project selected by the user.
- **WaitingForProvider** remains provider/model availability/catalog/cooldown/budget waiting; **AwaitingApproval** remains actual AI/plan/delegation consent; **NeedsReview** remains output/file review.

New enum values are appended, retaining all existing persisted numeric state values. Task and step grids have a dedicated problem-reason column with tooltip and distinct error text colors. State filters derive from the enum, so both states are available. The attention list includes explicit state and reason columns; notification titles prioritize configuration errors over generic pending-decision messages.

Configuration errors propagate from the worker to its parent task before pending approvals are considered. They have separate decision kinds and Approve does not fix/bypass them. Explicit resume rechecks project/provider conditions and can retry a corrected fault once; wrong scopes are not retried by every scheduler iteration. Missing-project details can still be opened without a report lookup exception. Invalid queued projects enter preflight to acquire the correct diagnostic rather than waiting forever for a slot.

At application startup, unfinished legacy permission-wait records and approval/provider waits with concrete known permission/project evidence are relabeled. The migration changes only state/reason/queue metadata, preserves access, project, usage/history and finished/running tasks, and does not start execution. Generic approval messages with no diagnostic evidence remain approval waits. Existing project/access editors continue to invalidate pending decisions and require explicit resume.

Source markup/named-control/handler inspection does not establish compilation or runtime correctness. No current database was edited as part of applying these source changes. Rebuild and reopen the application to use the new states and startup relabeling.
