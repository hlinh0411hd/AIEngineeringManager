# V2 completion and provider setup — 2026-11-01

Scope confirmed by the user: finish V2 first; Pixel Art/Video/Marketing, social publishing and updater work remain Phase 3–4. No runtime test suite, UI smoke, model benchmark or AI generation is run for this request. Compilation and read-only capability discovery are reported separately. A compiled feature is not runtime-certified.

## V2 feature matrix

| V2 requirement | Implementation |
|---|---|
| AI coordinator | On-demand bounded planning; fixed/automatic choice; Tasks can switch coordinator while preserving deadline, project/access, spent calls and accepted steps. Stopped planner output is handed off; changing an unaccepted planner supersedes its pending plan without deleting history. |
| AI-to-AI delegation | Validated targets, task/access limits, handoff, lineage/cycles/depth. Automatic acceptance now needs at least 3 samples on both routes and a measured adjustment improvement of 5 points. Without evidence, the proposal waits for user approval. Manual approval still checks all rights and budgets. |
| Advanced routing | Automatic/SemiAutomatic/Manual, explicit provider/model override, provider-specific automatic model choice, Economy/Balanced/Performance, measured execution history and explicit user ratings. Unknown cloud model defaults/quotas stay unknown. |
| Bounded autonomous work / Away | Goals inside Tasks, validated plans/dependencies, independent ready steps, persisted decisions/checkpoints, optional app-inactivity Away, duration 0 or explicit renewable windows, final human review. |
| Adaptive agents | Complex goals can be split; a bounded independent-project pool defaults to 2, configurable 1–4. Effective slots now shrink with actual free Windows RAM and a configurable reserve (default 1536 MB). Low memory pauses new dispatch, not an in-flight worker. Same/nested project roots remain sequential; one Ollama inference slot. |
| Approval queue | Inline plans, worker approvals/results, delegation and testing decisions in Tasks; notifications and history remain integrated rather than a standalone V2 tab. |
| Smart testing | Approved native commands, change impact, deduplicated batches, fingerprints, separate verification state. No test commands are run as part of this development request. |
| Advanced task scheduling | Priority, dependencies, independent-project scheduling, future date/time from the Tasks composer. Scheduled execution needs the app open; missed schedules at startup become Saved for explicit resume. No blind catch-up. Explicit Run / resume can override a future schedule, with a log. |
| Model performance | Durable worker measurements and one replaceable user rating per execution, minimum sample threshold and bounded adjustments; coordinator/classifier outcomes remain separate from worker quality. |
| Auto Continue | Eligible successful internal steps progress automatically; blocked permissions, unapplied edits, explicit approvals and final goal review remain enforced. |
| Shared usage control | Global default 100 manager invocation reservations/day, 0 unlimited, in addition to per-goal limits. Reservations are atomic across agents and persist across restarts. No exact vendor token, quota or money accounting is claimed. A reservation may be consumed before the CLI actually bills anything. |
| Memory / reporting | Project-scoped user notes and explicitly reviewed completed results, bounded relevant retrieval, separate task/decision/workflow histories. Daily activity report and Markdown export use code, not AI. Report states are current, not historical state reconstruction. |

## Where to use the additions

- Tasks composer: **Schedule** checkbox + date + HH:mm in computer-local time. Assign & run queues future work. Existing Run / resume explicitly starts early.
- Tasks controls: select provider/model, check **Coordinator**, then **Switch AI** to change planner. Accepted worker steps are preserved; use Run / resume afterward.
- Tasks completed goal: **Remember reviewed result** stores a bounded result note for the same project.
- Projects → **Project memory**: save/edit/delete notes for the selected registered project. These are context, never permission grants; TextOnly work does not receive project memory.
- Settings → **Model performance & parallel agents**: global daily-call cap, adaptive RAM slots, minimum RAM reserve and live memory/dispatch/call counters.
- Activity & history: date, **Daily report**, **Export report**.
- AI providers → **Enable usable installed AI**: metadata-only discovery; enables qualified installed local models or a cloud CLI with verified authentication. Explicit model blocks are preserved; no login, installation, model pull or inference is performed. A database backup precedes setup.

## Provider setup policy and machine observations

Read-only command discovery found Codex and Ollama. Copilot's VS Code shim explicitly reports that the real CLI is missing. Claude Code, Gemini CLI and OpenCode were not found through current executable discovery; no installation is attempted. This does not prove that no separate installation exists elsewhere.

Ollama lists qwen3.5:4b and qwen2.5-coder:7b-instruct. Existing qualification records show qwen3.5:4b Classification 3/3, Extraction 2/3. With the current pass threshold of 1.0, automatic setup enables qwen3.5 only for Classification. Qwen2.5-Coder has no qualifying record and is not made routable. Local Coding/Planning remain excluded. These are existing evaluations, not a fresh benchmark.

The setup report records actual metadata results in the configured data folder as provider-setup-report.json. --configure-providers performs the same operation without opening the UI while holding the data-folder instance lease. Existing Codex permissions/model catalog/cooldown are retained. No cloud CLI with unknown authentication is silently enabled; no paid API is added.

## Boundaries

V2 remains an app-open, bounded goal executor. There is no full calendar recurrence engine, OS background service, same-project concurrent editing, cross-provider exact billing/quota meter, automatic broad cloud benchmarking or GPU-aware placement. Broad local benchmarks and continuous semantic quality grading are not implied by simple execution metrics. Tool connectors/updaters are deliberately outside this confirmed scope. Runtime behavior of the new scheduling, memory, RAM and delegation changes remains untested at the user's request.
