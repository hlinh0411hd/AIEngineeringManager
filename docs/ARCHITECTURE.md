# Architecture

V2 now adds `SessionEngine`, `PlanningService`, `SmartTestingService`, persistent decisions/dependency graphs and SQLite schema 2. See [V2 architecture, behavior and limits](V2.md). The sections below document the retained Phase 1 baseline; statements that Phase 2 is future work are historical.

```text
WPF Desktop
  -> ProjectIndexer / IManagerStore
  -> Orchestrator -> ModelRouter (Core, deterministic)
                  -> IAIProvider adapters
                  -> reviewed FileEditService
                  -> SQLite logs / handoffs / settings / evaluations

Future coordinator/tool workflow -> the same permission and task boundary
```

`Employee.Core` has models, policy enums, interfaces and the rule-based model router. It has no WPF, HTTP, process or SQLite dependencies.

`Employee.Infrastructure` implements SQLite, bounded index/search, process lifecycle, CLI adapters, Ollama, local evaluation, reviewed file transactions and orchestration. Provider addition requires an `IAIProvider` implementation and composition registration, not an orchestrator rewrite. The built-in CLI adapter uses per-vendor command profiles; an unrelated provider can implement the interface independently.

`Employee.Desktop` composes services and presents five tabs with asynchronous UI handlers, batched output rendering and explicit permission/settings controls. It avoids an additional MVVM/DI dependency for this foundation. Future larger workflows should extract view models without changing Core/Infrastructure contracts.

`Employee.Checks` is a dependency-light executable acceptance suite. Fakes prevent test runs consuming cloud usage. Real process tests cover escaping, UTF-8, bounded output and cancellation. SQLite and FTS5 tests use actual native SQLite.

## State and concurrency

Queued -> (AwaitingApproval | WaitingForProvider | Running).

Running -> (NeedsReview | Paused | Failed | WaitingForProvider | AwaitingApproval).

A successful vendor result becomes NeedsReview. Resume runs a fresh vendor session with a bounded handoff. Budgets persist across resumes, so reruns cannot silently reset them. Manual Switch AI consumes the same switch budget as automatic fallback. No model-to-model recursion or agent delegation is implemented in Phase 1.

Semaphore per canonical project root prevents simultaneous manager tasks for that project. UI conservatively runs one task at a time. A named mutex per data folder prevents two instances sharing a database. Separate processes using different data folders do not share project locks; do not register the same working tree in multiple running instances. External editors are not locked; reviewed edits require exact file hashes and re-check before writing.

Child processes use `ProcessStartInfo.ArgumentList`, no shell command concatenation, hidden windows, redirected streams and bounded output. Timeout/cancellation kills the process tree, awaits exit and records a checkpoint before releasing ownership. npm `.cmd` shims are parsed only for recognized package-relative JS entrypoints and run directly via Node. Arbitrary shell scripts are not launched.

Provider capability probes run help with bounded timeout. Authentication and remaining quota are unknown rather than guessed. Error fallback is restricted to availability/auth/quota failures and excludes attempted providers within a run. Failed providers receive persisted cooldowns; task attempt/switch limits stop repeated resumes from churning forever. Ordinary task failures require user review.

## Data

SQLite schema version 1: projects, work, settings, logs, handoffs, evaluations, files and FTS5 file_search. Structured state is serialized JSON; the file index has explicit metadata columns and project/path primary key. SQL uses bound values. Metadata refresh and FTS reconciliation are transactional. A newer schema is rejected to prevent downgrade. Future versions need explicit migrations; the current release does not claim an upgrade framework beyond schema version 1.

WAL, foreign keys and busy timeout are enabled. Logs and checkpoints preserve task ID and timestamp. Startup recovers interrupted work to Paused. The app never restarts a model merely because it was previously Running.

Manager storage is local and unencrypted. No credential tables exist. Native vendor stores handle authentication. The app removes known API-key variables from child environments but does not alter user configuration or accounts.

## Managed coding handoff

Codex can read or write through its own sandbox. Other cloud CLIs run tool-free in a temporary folder and receive bounded snapshots only when project access was granted. A coding response is strict JSON containing summary and complete proposed file edits with SHA-256 preconditions. Providers never apply those writes themselves.

The user reviews the proposal and explicitly applies it. The manager rejects path traversal, links, drive/alternate-stream paths, device names, duplicate files, excluded paths/types, oversized edits and stale file hashes. It prevalidates all edits before writing and attempts rollback on a caught write failure. It does not delete files or claim crash-atomic multi-file writes. A machine/power failure while applying can leave a partial set; inspect logs/current files before resuming.

CLI tool denials are vendor policy, not an OS sandbox for startup hooks/plugins. Capability flag presence alone cannot verify every vendor/version's behavior. Uninstalled adapters remain unverified for real authenticated execution. Use trusted configurations and add vendor-specific integration tests when installing a new CLI.

## Extension boundaries

The optional future coordinator submits proposals to the C# policy boundary; it cannot directly spawn workers. Task budgets, project ownership, immutable handoffs and provider interfaces are reused. Rich dependency scheduling, away sessions, delegation cycle/depth controls and history-based model learning remain later implementation.

Current tool connectors are registered and executed by `ToolSkillRegistry`, `ToolExecutionService` and `ToolBackendLauncher`; implemented operations and exact API routes are listed in [Tool and API integrations](INTEGRATIONS.md). The inventory distinguishes loopback HTTP tools from the per-operation TrailerForge and ZapBop Python bridges. The `IToolConnector`/`ToolManifest` paragraph above is retained as historical Phase 1 context, not the current implementation status.

## Storage, maintenance and structure (2026-11-04)

- **One store dependency.** Services take `IEmployeeStore` (Core), which combines every store
  interface; `SqliteStore` is its only implementation. No service casts the store at run time.
- **Attachments outside task rows.** Attachment bytes live in `attachment_blobs`, keyed by SHA-256
  (identical files stored once); task JSON keeps name and key. Reads fill bytes from an in-memory
  cache. Rows written by earlier versions migrate on first open, followed by `VACUUM`.
- **Maintenance on start** (`SqliteStore.RunMaintenance`): keep the 5 newest checkpoints per task,
  trim execution logs older than 30 days to 4,000 characters. Database backups keep the 10 newest
  files in `Data/backups` (other copy destinations are never pruned).
- **Model history cache.** Model performance and judged outcomes used by routing are cached and
  invalidated on every write to `model_runs` / `model_feedback`.
- **Block codes.** Blocked work carries `BlockCode` (missing write permission, no usable provider,
  wrong access/project); logic reads the code, the reason text is for people.
- **Queue dispatch** lives in `TaskExecutionPool.DispatchQueued`; the window only decides when it
  may dispatch.
- **Keyword lists** for difficulty, image requests and planning live in `Core/Vocabulary.cs`.
- **Checks** are grouped in `tests/Employee.Checks`: `DataChecks`, `RoutingChecks`,
  `TaskFlowChecks`, `GeneralChecks`, sharing `Harness` and `Fixtures`.
