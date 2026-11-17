# Tool and API integrations

This page is an inventory of the integrations wired into the current source. It describes the manager's call boundary; it does not claim that optional backends, credentials, vendor accounts or network services are installed or available on a particular machine. Last reviewed 2026-11-12.

Detailed static comparison of endpoints, authentication, request/response contracts, errors and compatibility findings: [API contract audit](API-CONTRACT-AUDIT.md). This audit records missing backend specifications explicitly; it is not a live API certification.

## Project map

| Area | Responsibility |
|---|---|
| `src/Employee.Core` | Provider, routing and workflow contracts; deterministic policy. No WPF, process, HTTP or SQLite dependency. |
| `src/Employee.Infrastructure` | Provider adapters, tool registry/execution, local HTTP calls, process lifecycle, persistence and policy enforcement. |
| `src/Employee.Desktop` | WPF composition/settings/tasks UI. `Settings > Tools` configures connector profiles. |
| `connectors/` | Manager-owned Python bridges for saved TrailerForge and ZapBop projects; included in desktop output by the project file. |
| `.agents/skills/*/tool-skill.json` and `SKILL.md` | Metadata and just-in-time operating guidance for implemented operations. Metadata cannot add APIs or permissions. |
| `docs/` | Architecture, provider evidence, connector setup, workflows and policies. Older feature notes may describe the state at their date. |

## AI model providers

Provider selection/routing is separate from media/tool connectors. The router chooses a configured provider/model; `IAIProvider` adapters execute model requests. CLI capability is inspected from the installed executable's help and unsupported versions/flags are blocked. Authentication and quota are not inferred from executable presence.

| Provider | Adapter / call boundary | Configuration and known status |
|---|---|---|
| Codex | `CliProvider` launches the native `codex exec` noninteractive command using argument arrays and configured sandbox/access. | Configure executable, enabled state, write permission, priority and model in `Settings > AI & Models`. `docs/CLI-CAPABILITIES.md` records a prior local CLI inspection; it is not a live account check. |
| Claude Code | `CliProvider`; print/model/tool and MCP restriction flags are required by its profile. | Same provider settings. Earlier inspection found no executable; do not treat adapter existence as installation/authentication. |
| GitHub Copilot | `CliProvider`; requires the actual Copilot CLI headless/model/tool-denial capabilities. | Same provider settings. Earlier machine evidence found only a VS Code launcher, not the actual CLI. |
| Gemini | `CliProvider`; headless/model and effective admin-policy support required. | Same provider settings. Earlier inspection found it absent from PATH. |
| OpenCode | `CliProvider`; run/model/JSON help and documented permission configuration required. | Same provider settings. Earlier inspection found it absent from PATH. |
| Qoder | `CliProvider`; print/stdin interface with access-specific permission modes; models from `--list-models`. | Registered in current source; live compatibility/authentication not verified in this review. See audit for permission-mode concerns. |
| Antigravity | `CliProvider`; `agy --print`, text output, sandbox and mode flags; models from `agy models`. | Registered in current source; live compatibility/authentication not verified in this review. |
| CommandCode | Detection and model catalog via `cmdc` / `command-code`. | Execution explicitly disabled until compatible tool permissions are verified. |
| Freebuff | Detected/catalogued only. Interactive TUI lacks a supported headless prompt interface in the inspected version. | Not executable through the manager until a supported API/CLI is implemented. |
| Ollama | `OllamaProvider` uses local HTTP (`/api/tags`, `/api/show`, `/api/generate`). | The base URL remains `http://127.0.0.1:11434/`. Local service/model must already exist; manager does not download models. |

The CLI adapter invokes child processes without shell concatenation, uses bounded output/time and removes known API-key environment variables. Native CLI startup hooks/plugins are outside the manager's sandbox guarantee. See [CLI capability evidence](CLI-CAPABILITIES.md), [routing](ROUTING.md), and the relevant provider source (`CliProvider.cs`, `CliModelCatalog.cs`, `OllamaProvider.cs`).

## Tool connector inventory

Operations marked **write**, **AI**, or **publish** are additionally gated by the matching profile permission (`AllowWrites`, `AllowModelCalls`, `AllowPublishing`). Requests are bound to exact input and reviewed unless the configured task automation policy applies. Successful writes/AI/publishing usually enter `NeedsReview`.

| Connector | Operations and API | Backend / configuration | Boundaries |
|---|---|---|---|
| Pixel Art Tool (`pixel`) | `GET /api/codex/status`; `POST /api/codex/jobs`; poll `GET /api/codex/jobs/{id}`; cancel `POST /api/codex/jobs/{id}/cancel`. `status` is inspection; `generate` is write + AI. | Local HTTP loopback endpoint, default `http://127.0.0.1:4317`; source with `server.mjs`; Node >=22 when using preset auto-start; Codex login is backend-owned. Enable connector and required write/model/task permissions. | API inputs/outputs and PNG handling documented in [employee-pixel-art](../.agents/skills/employee-pixel-art/SKILL.md). Generation may consume the tool's own Codex usage. |
| TrailerForge (`video`) | `inspect`, `export` are manager bridge operations, not HTTP endpoints. Calls `connectors/trailerforge_bridge.py --source ... --root ... --operation ... --result-file ...`, JSON request `{"projectFolder": ...}` on stdin; bridge imports TrailerForge `engine.Runner`, `project_storage.prepare`, `export_latest.export_latest`. | Configure source folder, installed Python executable/dependencies and allowed workspace root containing saved `project.json`. Renderer runs per operation; there is no resident API backend. | Only inspect/export of saved projects; not text-to-video or project editing. Export can replace TrailerForge's own `export/trailer.mp4`; exact approval applies. |
| ZapBop (`music`) | `status`, `inspect`, `export` through `connectors/zapbop_bridge.py`; uses ZapBop `Project.load`, `render_project`, `write_wav`, no HTTP API. | Configure source containing `main.py`, `pro_engine.py`, `audio_engine.py`; use its existing `.venv` Python or explicit interpreter; set allowed workspace for saved `.json` projects. | Inspect is read-only. Export writes a new manager run artifact. No prompt composition, AI composer, playback or sound-device API. See [ZapBop connector](ZAPBOP-CONNECTOR.md). |
| Rikerat Marketing (`marketing`) | `GET /health`, `/projects`, `/jobs`; `POST /projects`, `/media`, `/jobs`; `GET /jobs/{id}`; `POST /jobs/{id}/run`, `/review`, `/schedule`, `/export`, `/publish/youtube`; `PUT /jobs/{id}/content`. | Local HTTP loopback endpoint, preset default `http://127.0.0.1:4319`; configured source/working directory, existing `.venv` and Rikerat module, absolute backend DataRoot, session token protected by the manager. Token is sent as HTTP auth and via stdin only for preset startup. | Publishing requires `AllowPublishing` and per-request explicit approval; backend OAuth/review gates still apply. No other social network publisher is implemented. `job.run` may invoke backend AI. |

Custom connector profiles can supply a native executable, working directory and JSON argument list; script/shell launchers are rejected. All HTTP connectors are restricted to loopback HTTP (no remote URL, HTTPS, proxy or redirect). The manager starts eligible backends only on demand, probes readiness before sending a tool operation, does not start a duplicate when a listener already exists, and idles/stops only processes it owns. Video/music are per-operation processes.

### Connector operation routes (marketing)

The endpoint path is fixed by `ToolExecutionService`; operation bodies are JSON from the prepared input. For job-specific operations the manager takes `jobId` and sends `input.body` (or an empty object) as request body.
If `body` is present it must be a JSON object; invalid scalar/array bodies are rejected before an HTTP request instead of being silently replaced.

| Operation | Request |
|---|---|
| `status` | `GET /health` |
| `projects`, `jobs` | `GET /projects`, `GET /jobs` |
| `project.create`, `media.import`, `job.create` | `POST /projects`, `/media`, `/jobs` |
| `job.status`, `job.run` | `GET /jobs/{jobId}`, `POST /jobs/{jobId}/run` |
| `content.set`, `review`, `schedule`, `export` | `PUT /jobs/{jobId}/content`, `POST /jobs/{jobId}/review`, `/schedule`, `/export` |
| `youtube.publish` | `POST /jobs/{jobId}/publish/youtube` |

Body fields are backend-specific; use existing IDs and inspect current state before creating jobs. See [employee-marketing](../.agents/skills/employee-marketing/SKILL.md) and [tool autostart](TOOL_AUTOSTART.md).

## Shared call and approval flow

```text
Task / Tool UI
  -> ToolSkillRegistry: identify requested configured tool; load metadata, then selected guidance
  -> plan validation: connector + operation, project/access, declared inputs/dependencies and permissions
  -> ToolExecutionService: exact-input approval/binding, call/time budget reservation, per-connector gate
  -> ToolBackendLauncher (loopback tools): probe existing service; start only if enabled/configured and absent
  -> fixed HTTP route OR manager-owned bridge process
  -> persist result/artifact path/hash and review state; reuse declared dependency outputs in later steps
```

Changing an approved input/profile or selected skill invalidates its binding. Completed operations may be reused; failed/interrupted writes are not blindly replayed. Pixel jobs resume by remote ID. `Check connection` is status/health/import inspection and does not intentionally start a backend or submit a write. Connection logs are stored with the task/tool run. Task-level project access and tool profile permissions remain independent gates.

### Connector compatibility contract (implemented 2026-11-12)

- **HTTP (pixel, marketing):** a non-2xx response raises `ToolHttpException` (`Tool HTTP <status>: …`; 408/429/5xx count as transient). An empty 2xx body (e.g. 204) is an empty JSON object; a non-JSON 2xx body (HTML, proxy pages) fails with `returned a non-JSON response (<content type>)` instead of a raw parser error.
- **Pixel jobs:** `queued`, `pending` and `running` are polled every second; only `completed` is terminal success, and any other status fails with the backend `error` (or the status it reported). Job IDs are opaque path-safe identifiers `[A-Za-z0-9_-]{1,128}` (GUIDs still work); a resume GET without `id` uses the saved RemoteId. `completed` requires `result.image` as a `data:image/png;base64,` URL with a valid PNG header; otherwise the run fails rather than reporting success without an image. Lost connections and transient HTTP errors (408/429/5xx) while polling leave the run `Interrupted` and resumable by its RemoteId; other errors fail it. Status reads `loggedIn` (boolean) and `version` without failing on unexpected types. Stop still does not call the backend cancel route.
- **Bridges (video, music):** the manager adds `--result-file <data>/artifacts/bridge-results/<guid>.json`; the bridge writes its single JSON object result there (written to a temp file, then renamed), so renderer/engine logs on stdout/stderr can neither hide nor truncate it. The manager reads the file (≤16 MB, must be a JSON object) and deletes it. Without the file (older bridges), the last JSON object line on stdout is used. Both bridges force UTF-8 stdio, and errors exit 1 with `ExceptionType: message` on stderr.
- **TrailerForge export:** `artifactPath` must be an existing, non-empty file inside the tool WorkspaceRoot; `.mp4` files must have an `ftyp` box. The manager records the full path, `artifactSha256` and `artifactBytes`.
- **ZapBop export:** unchanged WAV checks: the file must be in the run folder, end in `.wav`, have a RIFF/WAVE header, and is hashed.

Contract fixtures in `tests/Employee.Checks` (fake loopback backend and fake bridge runners) cover these paths. Real Pixel/Rikerat/TrailerForge/ZapBop backends were not run during this change.

### Readiness and remaining limits (reviewed 2026-11-12)

Results of the single verification run on 2026-11-12: `dotnet build AIEmployeeManager.slnx` (all 5 projects, including Employee.Desktop) succeeded with **0 errors, 0 warnings**. Employee.Desktop build status: **succeeded**. `tests/Employee.Checks`: **157 passed / 157 total, 0 failed**. No fixes were needed. Checks use only a fake loopback backend and fake bridge runners; **no real backend or engine has been run**, so real compatibility is unverified. The app was not opened for a visual check.

| Tool | Ready in the manager | Not verified / remaining limit |
| --- | --- | --- |
| Pixel (image) | Job flow, PNG validation, transient-error resume, opaque job IDs | No real backend run; Stop does not call backend cancel; `autoSwitch`/fallback not modelled; offline operations still count as one AI run |
| Marketing (Rikerat) | HTTP envelope, empty/HTML response handling, `body` type check | Business errors returned with HTTP 200, pagination and idempotency need the backend schema |
| TrailerForge (video) | Bridge result-file channel, UTF-8 stdio, export artifact checks (inside WorkspaceRoot, non-empty, MP4 `ftyp`, SHA-256) | Real export not run; needs configured native Python and source path |
| ZapBop (music) | Bridge result-file channel, WAV export checks | Real engine not run; needs interpreter or project `.venv` |
| Qoder/Claude CLI providers | Detection and routing as documented | CLI flags are not validated at detect time |

## Configuration checklist

1. In `Settings > Tools`, select/create the connector profile and set source path, workspace, interpreter/endpoint and backend fields required by that connector.
2. Enable the profile and use **Check connection**. For video it checks source/Python presence; for music it imports the engine; HTTP profiles call health/status. A successful check is not proof of export/generation/publishing capability.
3. Enable only the needed write/model/publishing permission. Configure `Allow Tasks to use tool` only when internal task automation is intended. Publish remains per-request approval gated.
4. Ensure the task uses the correct project and access level. Tool workspace and project read/write access are separate boundaries.
5. Use inspect/status before creating or exporting when current project/backend state is unknown. Review final artifacts and results.

Secrets are not included in skill text or task prompts. Marketing tokens are protected locally and never supplied as command-line arguments. The manager does not install tools, packages, models, authenticate accounts, or repair backend data/configuration.

## Source of truth and limits

Implementation inventory is based on `ToolExecutionService.Capabilities`, its route dispatch, `ToolBackendLauncher`, `ToolSkillRegistry`, connector bridges, profile UI/settings, and each skill descriptor. Operation schemas and backend behavior may evolve independently; inspect the linked skill and the actual backend before relying on optional fields. `docs/ARCHITECTURE.md` contains older historical statements that connectors were future work; current connector execution is implemented as described here. `docs/CLI-CAPABILITIES.md` is dated evidence, not a fresh installation/authentication probe. No tool/back-end API was called for this documentation review.

## Verified against the real tools (2026-11-13)

`Employee.Checks --tool-check <dataDir> --settings-from <manager.db>` starts every configured tool the way a task does and calls it. It uses no AI, generates nothing, publishes nothing, and runs the marketing write routes against a throwaway data folder and port.

Result on this machine: the manager auto-started Pixel Art Tool (`/api/codex/status`) and Rikerat (`/health`, `/projects`, `/jobs`, `/platforms`, `/queue`), and ZapBop's bridge answered `status`. TrailerForge has no read-only operation without a saved project; its bridge was compared with the source (`engine.Runner`, `project_storage.prepare`, `export_latest.export_latest`).

Corrections made after comparing with the Rikerat source (`services/backend/rikerat/api.py`, `models.py`):

| Operation | Before | Now |
|---|---|---|
| `schedule` | `POST /jobs/{id}/schedule` (the backend answers 405) | `PUT /jobs/{id}/schedule`, body `{"scheduled_at": ISO time with offset or null}` |
| `youtube.publish` | `POST /jobs/{id}/publish/youtube` (no such route) | `POST /targets/{targetId}/publish`, body `{"confirmed":true}`; also available as `target.publish` |
| `content.create` | missing | `POST /content`; the manager waits for the AI task (`GET /tasks/{id}`) and fails the step on `ACTION_REQUIRED`/`CANCELED` |
| `task.status`, `platforms`, `queue` | missing | `GET /tasks/{id}`, `GET /platforms`, `GET /queue` |
| `target.content`, `target.schedule` | missing | `PUT /targets/{id}/content` (`{"fields":{...}}`), `PUT /targets/{id}/schedule` |

A tool's saved "Unavailable: connection refused" status only meant its backend was not running at the time of the last manual check. The planner now sees `Usable` and a readiness reason computed from the tool's configuration; a tool it picks is switched on and started for the task. Publishing operations are never chosen by the planner.
