# Planner permission-proposal diagnosis

The reported case has an approved WriteProject root task and a ReadProject coordinator. The coordinator's request is to produce a plan, not execute it. It returned taskAccess/WriteProject instead of plan JSON. The old generic refusal presented this as WrongAccess even though the root grant was already sufficient for later implementation.

Changes: coordinators/classifiers no longer receive the worker permission-proposal instruction. Their role explicitly remains read-only planning/classification. A coordinator taskAccess response is now a failed protocol response, never a write-denial diagnosis or successful plan. Ordinary workers keep the existing task/project/comment/manual-edit validation and do not receive extra access. Rejection messages distinguish root access from step access and identify already-granted, stale-comment or mismatched-ID cases. Valid plan/result JSON merely mentioning taskAccess in nested content is not intercepted as a permission request.

The Comments/access panels explain historical coordinator rejections using saved role metadata rather than implying the current WriteProject root has no write permission. No historic task state, active runtime or database record was rewritten. Source changes take effect after a manual build/restart. If a prior planning attempt remains stopped, use the normal new-update/continue workflow to request a fresh plan, preserving the current task history. The existing planner and global budgets remain authoritative; no blind retry loop or permission bypass was added.

Verification: source reviewed and the reported task/log metadata inspected through SQLite read-only mode. No build/test, model call, app restart or user database mutation performed.
