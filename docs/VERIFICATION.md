# Verification — 2026-11-01

Current V2 verification: Debug build and **53 checks passed**, plus six main-tab/five V2-section UI rendering and handler acceptance. See [V2 verification and limits](V2.md). The evidence below is the original Phase 1 snapshot.

## Result

- Debug solution build: **passed, 0 warnings, 0 errors**, .NET SDK 10.0.201 / Windows x64.
- Executable acceptance checks: **22 passed, 0 failed**.
- WPF no-inference acceptance: **passed**. All five tabs rendered; UI handlers exercised project registration, metadata indexing, task creation, provider/model permission saving, orchestration settings and a SemiAutomatic routing proposal.
- CLI detection: actual Codex help accepted; Copilot launcher rejected; absent Claude/Gemini/OpenCode marked unavailable; local Ollama list detected.
- No Release build, installer or archive was generated. Build.bat is supplied for explicit user-invoked Release building and checks.

## What the 22 checks cover

1. SQLite persistence, settings, UTF-8 and interrupted-task recovery.
2. Project metadata index, excluded folders/files, deletion reconciliation and project-isolated FTS5 search.
3. Ambiguous project detection.
4. Unqualified-local rejection and independent qualification per task kind.
5. Enabled/blocked model, write permission and cooldown gates.
6. Manual selection and no automatic fallback.
7. SemiAutomatic proposal without inference, then exact approval.
8. Automatic availability fallback, handoff and bounded/cycle-free provider attempts.
9. Ordinary task error does not cause provider churn.
10. Attempt budgets persist across resumes.
11. Project ownership, awaited cancellation and user Switch AI.
12. Checkpoint before provider invocation, result awaiting review.
13. Successful output containing “rate limit” cannot trigger fallback.
14. Provider-specific command construction without full-access/bypass flags.
15. Real process argument escaping, spaces, shell metacharacters and Vietnamese text.
16. Real process cancellation and bounded output.
17. Mock Ollama discovery, exact-answer grading, persisted per-kind results and unload requests.
18. Reviewed file edits: SHA-256 preconditions, new files, path/device restrictions, duplicate/stale protection and batch prevalidation.
19. Coding-provider quota fallback to a structured proposal; no write before review; explicit application; prevent duplicate apply.
20. Local timeout and invalid-output qualification fail closed.
21. Actual capability detection logic rejects unsupported help and accepts required flags through fixtures.
22. Recognized npm shim resolves to Node without a command shell; wrong package rejected.

Automated provider runs use fakes to avoid consuming cloud usage. Real SQLite/FTS5 and operating-system processes are tested. These tests do not prove the quality of an AI's generated code.

## Real local evaluation

Installed model: **qwen2.5-coder:7b-instruct**.

- Classification: **2/3** exact answers passed, **68,430 ms** for the three examples (including loading/unloading).
- Extraction: the subsequent evaluation exceeded the original whole-suite four-minute deadline and was cancelled; no extraction qualification was recorded.
- Model therefore **did not qualify** at the default 100% threshold. No coding qualification was attempted or granted.
- After this observation, the evaluator added a **30-second deadline per example**, timeout/error counters and explicit failure gating. These paths were verified with mock timeouts/invalid responses; the slow real benchmark was not repeatedly rerun.
- RAM and VRAM were not measured. No resource usage or token savings are claimed.

Real-model evaluation used an isolated diagnostic database, not production settings. The production app still starts with providers disabled.

## Limits of verification

No authenticated cloud-model inference was invoked. Claude/Gemini/OpenCode are absent; Copilot's launcher is not the CLI. Their real login, permission enforcement, response formatting, quota/error messages and cancellation behavior must be checked when installed. No provider is labeled authenticated merely because it appears in PATH.

No autonomous/away coordinator, tool control, social publishing, updater, rollback installation or concurrent multi-agent workflow was run; these remain later phases. Cross-file edits are not crash-atomic if power is lost during apply. Use Git and review file changes before accepting generated work.

## Reproduce

```powershell
dotnet restore AIEmployeeManager.slnx -m:1 -nr:false
dotnet build AIEmployeeManager.slnx -c Debug --no-restore -m:1 -nr:false
dotnet run --project tests/Employee.Checks -c Debug --no-build
src/Employee.Desktop/bin/Debug/net10.0-windows/AIEmployeeManager.exe --smoke --data-dir artifacts/ui-acceptance-new
```

Development evidence is stored under ignored `artifacts/`: check output, provider detection, isolated diagnostic databases and rendered tab PNGs. No user project was automatically added to the normal data folder.

Dependency restore initially identified a native SQLite security advisory in the original transitive version. The project explicitly pins `SQLitePCLRaw.bundle_e_sqlite3` 3.0.5, which restored and built with no reported warning. Shared compiler-server startup was unreliable in this execution environment; shared compilation is disabled and Build.bat uses one build node.

## Unified Tasks workflow — 2026-11-01

V2 is integrated into Tasks, with no standalone V2 navigation. A request starts bounded orchestration directly, with deterministic complexity detection, optional planning, dependent steps, inline decisions, aggregation and final human review. Legacy sessions become visible task roots without resetting budgets. Schema 3 backs up schema 1/2 before logical root migration and protects against reopening in the old schema-2 app.

Debug build: zero warnings/errors. 68 executable checks passed, including all previous 53 checks and new unified task, planning, manual/semi policy, proposal gate, legacy migration, cancellation, budget reconciliation and final review checks. Isolated WPF smoke renders five tabs and asserts inline plan approval, two plan rows, two internal steps and root-only task listing. No live model inference was used. Rendered Tasks screenshot visually checked for both internal rows and inline decision controls. No Release build, archive or production-data migration was performed during this verification.

## Data location and renewable time windows — 2026-11-01

Debug build has zero warnings/errors. All 76 executable checks passed: prior checks plus portable path resolution/override isolation, live-WAL SQLite relocation and integrity, nonempty destination refusal, instance leases, unlimited worker/planner timing, explicit renewal without redoing completed work or resetting usage, and cancellation/checkpoints for both worker and coordinator deadlines. WPF smoke additionally saves duration=0 and verifies persisted task time/data controls, then renders five tabs with isolated data. No production database was opened/migrated during development; legacy import occurs on the user's next normal launch. No live inference, Release build or archives were used.

## Routing repair and master specification source audit — 2026-11-01

See MASTER-SPEC-AUDIT.md for the current implementation/gap matrix and concrete routing failure found in read-only user data. Repaired automatic assignment compatibility, provider-scoped automatic model selection, accidental override retention, SemiAutomatic classification gating and actionable no-route diagnostics. No tests, build, UI smoke, provider probing or inference were run for this request, per user instruction. Earlier passing counts do not verify this change.

## Model scoring and parallel project pipelines — 2026-11-01

Added model run/feedback tables and aggregates, bounded learned routing adjustments, Economy/Balanced/Performance, 1–4 independent-project pipeline slots (default 2), one Ollama inference slot, per-goal output/stop/switch/closing coordination, deterministic simple-request assignment and a shared context cap that includes supplementary previews. See MODEL-PERFORMANCE-AND-PARALLELISM.md for limits.

The application project compiled in Debug with zero warnings/errors. Per user instruction, no executable checks, runtime tests, UI smoke, benchmarks, capability probing or AI inference were run. Earlier test counts do not validate this feature. User data was not opened or migrated during this implementation; additive performance tables are created when the app next opens its configured store.

## V2 completion source changes — 2026-11-01

User confirmed V2 first. Added future scheduling with explicit missed-schedule recovery, RAM-aware dispatch, atomic global daily call reservations, project memories/reviewed-result capture, code-generated daily reports, evidence-gated automatic delegation, safe coordinator switching and metadata-only provider activation. Added bounded local-to-cloud assignment fallback in Automatic mode and reduced idle queue/stream-output database reads.

Application-only Debug compilation succeeded with no warnings/errors during implementation. No test suite, UI smoke, model evaluation or AI generation was run per user instruction. Provider setup is a requested real configuration operation, with a database backup and data-folder lease, using CLI help/runtime metadata and existing qualification records. See V2-COMPLETION.md and the resulting Data/provider-setup-report.json for scope/status; compilation does not verify unattended runtime behavior.

## Ollama task-intent correction — 2026-11-01

Observed original coding requests misassigned Classification/TextOnly and incorrectly reported finished. Corrected assignment prompts/contracts, deterministic coding detection, local execution gates and atomic safe repair of affected unreviewed goals. Debug application compiled; no tests, benchmark or inference run. See OLLAMA-ROUTING-FIX.md and the actual routing-repair-report.json for repair results.

## Compatible read-role routing — 2026-11-01

Source-only update requested by user: exact-kind models first, then compatible cloud Coding models for read-only Research/Review/internal planning in automatic supervision modes. Planner hints can be reassigned without altering explicit user choices. Existing provider-wait approval/resume can select an available route while retaining budgets/history. No build, test, inference or production task execution performed. See COMPATIBLE-READ-ROUTING.md.
