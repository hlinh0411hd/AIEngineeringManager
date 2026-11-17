# Model history, parallel workers and usage controls

Implemented in source on 2026-11-01. No runtime tests, UI smoke, benchmarks or AI inference were run for this request. Compilation is reported separately and does not certify runtime behavior.

## Model scoring

Settings → **Model performance & parallel agents** contains Economy / Balanced / Performance, measured-history enablement, the concurrent-agent limit, and a performance table. Model quality/cost ranks remain user configuration. The modes weight those ranks differently; they are not actual currency or token measurements.

Every newly executed provider attempt records provider, exact selected model ID (or explicitly unknown CLI default), task kind, outcome and elapsed time in SQLite. Worker history is separate from coordinator/classification runs. Up to 5,000 recent eligible worker samples are aggregated by provider + model + task kind. Cancelled/permission/quota/authentication/unavailability outcomes do not count as quality failures. Technical success means the provider finished successfully; it does not mean the result was correct.

In Tasks → **Steps & approvals**, select a completed worker and choose **Kết quả tốt** or **Cần cải thiện**. One feedback row per latest successful execution is updated, so repeated clicks cannot inflate the sample count. Historical runs from before this feature are not fabricated or backfilled from unverified logs. Human ratings do not automatically mark a task complete or tests Verified.

A learned adjustment starts after at least 3 eligible worker runs of that model/task kind. It uses smoothed technical success and explicit feedback, bounded to ±30 points. A small speed contribution needs at least 3 successful runs. User priority remains dominant. Economy increases the cost-rank penalty; Performance increases quality-rank weight; Balanced uses equal quality/cost weights. The router keeps all permission, model block, capability, cooldown and local-qualification gates before scoring. Explicit exact model choices are honored. This is heuristic historical routing, not an independent quality benchmark or a claim that the newest model is best. CLI-default history may include a vendor changing its underlying default model.

## Parallel work

Default maximum is 2 active goal pipelines, configurable from 1 to 4 in Settings. Tasks → Assign & run starts an independent goal if a slot is available; otherwise it queues it. The queue scans ready goals in priority order and can skip a busy project to fill a slot with an independent project. Each goal still executes its own dependent worker steps sequentially. A single-project goal is not multiplied into redundant agents.

The pool reserves all registered project roots belonging to each active goal. Identical or nested project paths cannot run concurrently. Existing project locks also protect worker/test file ownership. Separate goals retain separate planner/session/deadline/call/switch/delegation state. Shared provider cooldowns apply across the pool. Ollama has a single inference slot regardless of agent limit, avoiding simultaneous local model loads. No additional model is downloaded or automatically qualified.

Task selection filters streamed output to that goal. Stop/Switch AI target the selected pipeline; closing cancels and awaits all active pipelines before disposing storage. Stop also pauses queue intake; already running independent goals may finish. New queue submissions can enable intake again. A waiting-for-review goal releases its slot, preserving its result/checkpoint. Resume remains explicit. Project reservations cover this app's workers; external editors are not locked.

This adds concurrent independent-project pipelines, not same-project file-level parallel edits, OS isolation or automatic RAM/VRAM-aware scheduling. No aggregate cross-task spending limit is claimed: persisted AI budgets remain per goal. Parallel execution can consume allowed usage faster without reducing the number of calls.

## Usage reduction already present and added here

- Queue management, routing, score calculation, provider detection, permission checks and scheduling use ordinary code.
- Clear coding/review/content/research requests now use narrow deterministic intent rules, avoiding the previous mandatory classifier call. Ambiguous automatic-assignment requests still use AI; manual task type/access skips classification.
- Optional planning uses the existing complexity heuristic. Simple requests run directly; planning requires enough remaining attempts and reserves a worker attempt. No continuously polling AI coordinator.
- Economy can prefer a lower configured cost rank within the user's provider priorities and permission gates. Historical routing avoids blindly preferring models that have performed poorly for the same kind of task.
- Bounded context now includes attachment/comment previews inside MaxContextChars, rather than adding those previews beyond the configured budget. This limits captured request context, not a provider's independently billed token count.
- Scoped indexes/search/Git and bounded handoffs avoid retransmitting unrelated full history or whole projects.
- Persisted checkpoints, completed dependencies, conservative retry/switch/depth/time limits and quota cooldowns prevent blind reruns and delegation loops.
- Smart testing batches/deduplicates approved test commands and caches unchanged fingerprints using ordinary code. No cloud AI is called to grade the small local qualification suite.
- Ollama is optional, category-qualified, bounded and unloaded after requests; it is not automatically assigned coding/planning.

Still absent: exact cross-provider token/quota/currency accounting when CLIs do not expose it, a global spending cap, automatic cloud model discovery for unsupported vendors, broad local capability benchmarks, semantic cross-task result caching and RAM/VRAM-based concurrency. No automatic reduction percentage is claimed.

## V2 completion update

A shared daily invocation-reservation cap (default 100, configurable including 0 unlimited) now supplements per-goal budgets. This is not currency/token billing. RAM-aware dispatch reduces effective slots before starting another goal. Delegation without sufficient measured benefit waits for user approval. See [current V2 scope and limits](V2-COMPLETION.md).
