# Compatible read-role routing

Requested behavior: when a suitable installed AI exists, avoid blocking a software project's preliminary reading/research/review step solely because its model catalog says Coding.

The router now uses two stages after normal availability/access gates:

1. Prefer eligible models explicitly allowed for the requested kind.
2. If none exist in Automatic/SemiAutomatic, allow an enabled, unblocked cloud Coding model to perform Research/Review with ReadProject only. An internal read-only planning role can also use this compatibility rule. The original task kind, project, access, catalog and model qualification records are not rewritten.

Example: Research / ReadProject → Codex's configured Coding model, still read-only. Coding writes still require task/provider/runtime write permission. TextOnly is never promoted to project access. Ollama's limited classification/extraction profile is not expanded. Disabled providers, blocked models, unsupported runtimes, cooldowns and budgets remain excluded. Manual keeps strict catalog-kind selection.

Planner-selected routes are now soft recommendations, distinct from explicit user overrides. If unavailable, Automatic selects another eligible exact/compatible model; SemiAutomatic proposes the replacement for approval; Manual does not silently replace it. Choosing Switch AI or an explicit run target records a fixed override. Older unstarted planner recommendations can be recognized from their approved plan on explicit resume, except where a per-step user approval/switch already fixed the route.

Approving an existing provider-wait decision with Automatic selection can select a suitable available route instead of demanding an already assigned provider. The existing explicit-resume path requeues provider waits once and preserves checkpoints, completed dependencies, attempts and limits. Actual provider/model changes after a previous attempt spend switch budget; automatic retries do not double-count the normal within-run fallback.

No build, test, inference, executable update or production-state change was performed for this request. These are source changes only. Existing executables require the user's later build to contain the fix. After updating the executable, resume the waiting goal; no need to recreate the plan or rerun completed steps.
