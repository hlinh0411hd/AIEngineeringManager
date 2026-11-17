> Current status (2026-11-02): the following early-phase audit is historical. [V3-V4.md](V3-V4.md) supersedes deferred-tool/updater/telemetry statements and lists actual source implementation, configuration needs and remaining gaps. V3/V4 changes were not built or tested.

# Master specification audit — 2026-11-01

Source: user-supplied Complete Master Development Specification v2.0. This is a source review, not runtime certification. The specification version 2.0 covers four implementation phases; calling the application V2 does not mean every section is complete. Current scope remains the Phase 1 foundation plus bounded Phase 2 orchestration integrated into Tasks.

No tests, builds, provider detection commands or model inference were run for this repair, as requested. Existing earlier verification records are historical evidence only.

| Specification sections | Current source implementation | Missing or limited |
|---|---|---|
| 1–4: purpose, environment, architecture | C#/.NET 10 WPF, SQLite/FTS5, modular monolith; C# orchestrator and pluggable provider interface | Hardware telemetry and resource-aware coordination absent |
| 5: task/autonomous/supervision/Away | UnifiedTaskService + SessionEngine; bounded goals, sequential steps, dependencies, Automatic/SemiAutomatic/Manual, pending decisions, Away | Goal-driven scope is bounded; no background service or open-ended discovery loop |
| 6: providers | Separate capability/argument branches for Codex, Claude Code, Copilot CLI, Gemini CLI, OpenCode; optional Ollama API | Adapter presence does not establish installed/authenticated usability. Custom arbitrary CLI configuration absent. No exact quota when unavailable |
| 7: coordinator/delegation | Optional on-demand planner, structured delegation, validation, route history, depth/cycle limits | No independent measured-benefit gate proving one cloud model better than another; coordinator does not continuously manage workers |
| 8: model routing | Enabled/ready/cooldown, task/access/model gates, priority/quality/cost ranks, local category qualification; provider-specific automatic model choice added | No Economy/Balanced/Performance policy, historical-success/speed/resource scoring or automatic cloud benchmarking. Cloud CLI default is explicitly unknown rather than a fabricated exact model |
| 9–10: switch/fallback/handoff | Stop/await/checkpoint, permitted worker override, bounded availability fallback, project rules/Git/output handoff | UI does not implement every coordinator-versus-worker/restart option in the mockup; no vendor-native session transfer |
| 11–12: local runtime/evaluation | Ollama discovery, installed model names, bounded generation, unload, Classification/Extraction qualification and logged timings | No RAM/VRAM monitoring; no broad summarization/reasoning/content/coding benchmark; no continuous degradation/requalification policy |
| 13: zero-usage infrastructure | Queue, indexing, routing score, budgets, state, validation and scheduling use code | Optional automatic task-type/access assignment currently invokes AI; it is separate from deterministic model routing |
| 14: projects/context | Registry, aliases, chosen-folder scanner, technology detection, metadata index/FTS, scoped rules and bounded Git context | No full project feature/description/memory editor; no complete semantic identification pipeline |
| 15: tasks | Durable saved/queued tasks, priorities, dependencies, comments/attachments, checkpoints, recovery, resume, time renewal/unlimited option | No daily-report generator or full calendar scheduling system |
| 16: memory | SQLite task/project history, checkpoints, decisions, evaluation records | Dedicated experience/workflow/performance memory and retrieval policies are incomplete |
| 17: adaptive agents | Complex goals can become bounded steps/delegated workers with route, context, rights, budgets and results | Workers run sequentially. No concurrent independent-project pool or measured adaptive agent-count policy |
| 18: smart testing | Impact classification, approved command profiles, pending batches, deduplication/fingerprints, verification state | Heuristic file impact; not a full coverage/dependency analyzer; requires configured project commands |
| 19–21: tool connectors/workflows/social media | ToolManifest and IToolConnector extension contracts only | No executable Pixel Art, Video Editor, Marketing, cross-tool workflow or social publishing connectors |
| 22–24: updates/desktop automation | Existing coding task/process/file/Git mechanisms can support later work | No tool update manager, safe installer/rollback, independent self-updater, browser or arbitrary app automation engine |
| 25: permissions | Task/project scope, provider writes, model task kinds, sandbox/proposals, pending decisions, bounded execution | Not a complete action-by-action policy engine for later publishing/spending/installing/tool operations |
| 26: UI | Projects, Tasks, AI providers, Settings, Activity; plans, workers, approvals and local evaluation embedded; task diagram, notifications | Full standalone Dashboard/Agents/Tools/etc. tabs absent. User requested V2 features inside Tasks, so separate V2 navigation intentionally absent |
| 27–34: roadmap/build/rules | Build.bat/Run.bat; no Release/ZIP on edits; source architecture supports later phases | Full master-spec acceptance cannot be claimed; live fallback on an installed authenticated second CLI is not established by this source review |

## Current addendum: model scoring and independent-project parallelism

The earlier table records the original audit. Subsequent source changes add Economy/Balanced/Performance, persisted per-model/task-kind execution statistics, explicit user ratings, conservative learned routing, a 1–4 slot independent-project execution pool (default 2), one Ollama slot, deterministic simple-task assignment and bounded combined context. See [implementation and limits](MODEL-PERFORMANCE-AND-PARALLELISM.md). The whole master specification is still not complete; same-project parallel edits, broad benchmarks, exact usage accounting, connectors and updater features remain absent. No runtime tests were run for this addition.

## Routing failure found in current data

Read-only inspection of the configured database found Automatic mode. Codex is enabled, project writes allowed, and its only permitted model entry is the CLI default for Coding. Claude/Copilot/Gemini/OpenCode/Ollama are disabled in these saved settings. A recent task was automatically assigned Review / ReadProject, then rejected because there was no permitted Review model. The task also retained a Codex override. Historical RateLimit text exists; it is not evidence of the current quota or a still-active cooldown.

No user provider settings, model permissions, task history or database records were edited during this repair. More adapters in source do not automatically enable other providers.

## Code repairs

- Automatic assignment can dispatch read-only code review through an already permitted Coding model when no eligible Review route exists. It preserves ReadProject and logs the compatibility decision; it never grants new model/provider permissions. On explicit resume, the same repair applies to previously automatically classified tasks and their unexecuted matching waiting step. Manually chosen Review tasks retain their explicit task type.
- Assignment-route probing now considers text-only as well as read-only eligible providers and stays below the original access ceiling. Classification cannot increase the approved scope.
- SemiAutomatic requests approval before classification and separately before the classified worker starts.
- Selecting a task no longer copies its last execution route into the user's routing override. Returning to Automatic selection clears the old override on runnable direct steps.
- A provider may be fixed with Automatic model selection. The router chooses among that provider's allowed models at execution; explicit exact models and the real CLI-default entry remain distinct.
- No-route messages list disabled providers, missing runtime/capabilities, cooldown, task-kind/model restrictions and access restrictions. Preflight waits retain their visible state.
- Task model cells label CLI default as unknown exact model instead of leaving the cell blank.

These changes are implemented in source only and have not been compiled or exercised in this request. Rebuild explicitly with Build.bat before expecting a previously built executable to contain them.


## V2 completion addendum

The user confirmed V2 first. See [current V2 feature matrix](V2-COMPLETION.md) for scheduling, adaptive RAM slots, shared daily invocation limits, project memory, deterministic daily reports, measured-benefit delegation gates and coordinator switching. Phase 3–4 remain deferred. Metadata-only provider setup preserves Codex and enables only installed/qualified local categories; it does not install or authenticate missing CLIs. No runtime tests or inference are performed for this request.
