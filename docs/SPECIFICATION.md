# Consolidated specification and Phase 1 boundary

Source: referenced conversation “Giới thiệu Subagent Headless Agent”, ID `6ae5fcbd-8420-83ec-a4ec-b68858eec9ae`, retrieved on 2026-11-01, plus the explicit request in the current chat. Retrieved five turns, including the main specification, local-model strategy, multi-CLI amendment, dynamic routing amendment and final phase ordering. The service capped the main specification message at 20,000 characters (midway through permissions); there were no older-page cursors or attachments. This document consolidates available requirements and does not claim to reproduce the missing tail verbatim.

## Product

An AI Employee Manager for Windows: software/game development, bug fixes, research, planning, pixel art, video, marketing and linked Windows-tool work. Eventually it can work toward bounded goals while the user is away. It is a general work orchestrator, not exclusively a coding agent. The machine must be on for it to work.

**AI is the worker, not the operating system.** Rule-based logic and deterministic automation come first. Local models are optional and must qualify for each capability. Cloud models handle work that needs them. Do not route through every tier unnecessarily or keep an expensive coordinator running continuously.

## Architecture

- C#/.NET WPF desktop, SQLite with FTS5, Git integration where available.
- Local-first modular monolith. Independent provider/tool contracts; no mandatory distributed services, agent framework, vector database or provider.
- Offline project/task/history/settings management. Vendor CLI inference can require networking.
- Central C# orchestrator owns state, routing, permissions, budgets, checkpoints, logs and validation.
- Later coordinator models can propose assignments but cannot bypass the C# policy layer.
- Separate context and memory by project/task. Transfer bounded relevant context, not entire unrelated histories.

## Work/session modes

- Phase 1 task mode: select project, create/queue task, choose type, permission and provider/model policy; start, stop/checkpoint, explicitly resume or switch.
- Automatic: deterministic selection and availability fallback among enabled permitted models.
- SemiAutomatic: propose provider/model and await approval. Fallback also requires a user choice.
- Manual: require explicit selection; no automatic switch.
- Later: configurable coordinator/worker roles, autonomous objectives and sessions with time window, priorities, parallelism, usage budget and stop conditions.
- Away mode is independent: defer decisions needing the user, continue other authorized work, do not expand privileges. Optional inactivity activation is later.
- Distinguish stopping new assignments, requesting a safe agent stop and killing a process. No assumption that vendors support native pause/resume. Phase 1 stops/kills and restarts from a manager checkpoint.

## Project/context/memory

- Registry: ID/name, aliases, selected physical root, technology, rules and task history.
- Scan only user-selected folders. Detect Unity/.NET/Python/Node/Git from actual markers.
- Resolve active project first, then names/aliases, keywords/FTS and optional qualified local reasoning later. Ambiguity must not cause writes to another project.
- Metadata file index with exclusions, bounded traversal and project-scoped search; explicit incremental refresh in Phase 1.
- Context: project rules, relevant bounded source snapshots when permitted, Git changes, task request/checkpoint and decisions.
- Later structured project/experience/workflow memory, dependency graphs and richer project features/descriptions. No forced vector infrastructure.

## Tasks/checkpoints/permissions

- Durable queue, priority, status, attempt/switch budgets, timeout, logs and handoff history.
- Startup recovery moves interrupted Running work to Paused; never silently restarts inference.
- Before a switch, stop and await the previous execution. Never run two writers in one project. Phase 1 is conservative: one execution per project, one active execution from the UI.
- Handoff contains original request, current project/goal/rules, progress, remaining work, Git status/diff, tests, issues/decisions and reason. Unknown completion/testing is explicitly unknown.
- Preserve changes; no automatic rollback or vendor-session conversion during a handoff.
- Scope permissions by task/project/provider/action; later tool-specific policies. Phase 1 has TextOnly, ReadProject and WriteProject plus enabled providers, write permission and allowed model task kinds.
- No full-access/bypass switches. No automatic publication, deployment, spending, destructive cleanup, tool installs or release packaging.
- Successful provider exit is a result awaiting review, not proof the requested task is correct.

## Provider/model registry

Adapters: Codex, Claude Code, GitHub Copilot CLI, Gemini CLI, OpenCode, Freebuff CLI (interactive-only; execution blocked), optional Ollama, extensible custom adapters. Installing every provider is unnecessary. Credentials remain in vendor-managed stores; no automatic login.

Each adapter must inspect its real CLI help/version or local endpoint and declare supported behavior: installation, noninteractive execution, model selection/listing, output, cancellation, file/tool permissions, authentication, usage and errors. Unsupported versions remain blocked rather than receiving guessed flags. Windows npm launchers may be recognized by package path and launched as Node argument arrays without a command shell.

Provider settings include executable, enabled flag, priority, project-write permission, models, last failure and cooldown. Authentication/quota that cannot be reliably read are **Unknown**. Health checks cannot generate model calls. Observe exit/event errors rather than polling paid inference.

Models must be exact discovered IDs or explicit user catalog entries. A blank cloud ID delegates to the vendor default and is labeled unknown, never a fabricated model. Task-kind allowlists, blocked models, capability/qualification and user quality/cost rank drive deterministic routing. Historical scoring, latency/resource adaptation and optimization profiles are later.

**Switch AI**: select permitted provider/model, stop and checkpoint current work, transfer bounded context, continue without reverting changes. Coordinator/worker role switching and selective restart of subtasks belong to Phase 2.

Availability fallback: quota/rate limit, auth failure or unavailable service -> checkpoint -> cooldown -> next permitted provider. Ordinary coding errors do not cause provider churn. No repeat loops within a run; persisted task budgets bound later resumes. When none are usable, wait for the user/provider. No automatic paid API fallback.

Tool-free cloud adapters can take over coding via structured, user-reviewed file proposals. The manager performs hash/path/permission checks and writes only after review. This allows multi-provider coding without granting a vendor unrestricted shell access. CLI startup behavior still requires trusted installations/configuration.

## Local AI

Target reference machine: i7-12700F, 16 GB RAM, RTX 3070 8 GB, SSD, Windows. Existing Qwen Coder must not be presumed a reliable primary coding worker. Candidate model names from the conversation were suggestions, not mandatory downloads or known capability scores.

- Optional Ollama loopback API. Detect installed models; never pull a model automatically.
- Local models qualify independently by category, not a single global score.
- Phase 1: six exact-answer examples, three classification and three JSON extraction; store accuracy and latency per category. Default threshold 100%. No cloud grader, no automatic coding approval.
- Restrict local routing to those tested categories. Qualification on a tiny suite is limited evidence, not a guarantee.
- Request unload with `keep_alive=0`; one local model job at a time through the desktop UI.
- Later: intent/project recognition, summarization/content rubrics, test-verified coding benchmarks, resource monitoring and throttling, failure history, requalification and idle/resource-aware scheduling. No RAM/VRAM metrics are invented.

## Later tool ecosystem

Phase 3 connectors integrate the user's existing **Pixel Art Tool**, **Video Editor** and **Marketing Tool** rather than rebuilding them. Prefer CLI, local API, scripts, then UI automation. Manifest: ID/name/version, executable/source path, connector version, capabilities, parameters/input schema, output schema and permissions. V3/V4 now supplies concrete inspected local connectors; see V3-V4.md for paths, setup and limits.

Cross-tool example: sprite generation -> Unity integration -> trailer creation -> marketing draft -> explicit publishing approval. Track dependencies, artifacts, safe retries and workflow history. Official platform APIs where available; if unavailable, save drafts/pending state instead of bypassing controls.

## Roadmap

| Phase | Deliverables |
|---|---|
| **1 — Foundation (implemented)** | WPF, SQLite, project/task management, indexing, CLI/provider/model registry, deterministic routing, three routing modes, manual Switch AI, checkpoints, bounded fallback, basic Ollama/evaluator, connector contracts |
| **2 — Autonomy (implemented)** | Optional on-demand AI coordinator, AI-to-AI proposals, bounded delegation/cycle/depth controls, autonomous/away sessions, task dependencies and smart testing. Usage/limits: V2.md |
| **3 — Tools (source implemented)** | Pixel Art/Video/Marketing connectors, cross-tool workflows, controlled Windows automation and publishing |
| **4 — Advanced (source implemented; limits below)** | Historical model performance, adaptive selection/cost optimization, tool/manager self-update, backups, health checks and rollback |

Smart testing later: classify minor/small/major/critical changes, deduplicate and batch appropriate tests, immediately check critical safety work, track Implemented/Untested/Partially Tested/Verified separately. Do not build/test everything after every edit.

Updates later: explicit build permission, isolated output, check build, backup old version, preserve user data, wait for executable exit, install, health check, refresh manifest and roll back on failure. An independent updater updates the manager itself.

**Build policy:** never automatically create release builds, installers or ZIP/RAR on edits. Provide one-click Build.bat. Development verification uses Debug builds and focused checks. The user subsequently authorized Phase 3/4. Their source implementation and remaining limits are documented in V3-V4.md; no build/tests were run for that update.
