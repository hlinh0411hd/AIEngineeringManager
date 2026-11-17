# AI Employee Manager — V3/V4 source update

**New dashboard and V3/V4 source implementation:** see [setup, implemented behavior and remaining limits](docs/V3-V4.md). This update was not built or tested; the existing executable is unchanged. Tools use explicitly configured local interfaces, persisted workflows and reviewed updates.

**V2 runs directly inside Tasks.** Enter a request and choose **Giao việc & chạy**. The manager creates its bounded execution context, plans complex work when appropriate, runs dependencies, routes/delegates AI and batches approved tests. Progress, plans, decisions and final review stay in Tasks. Defaults live in Settings; no separate session setup or V2 tab is required. See [usage and limits](docs/V2.md).

Windows desktop foundation built with C#/.NET 10, WPF and SQLite/FTS5. Project, task, index, permission, routing and process management run without AI calls. AI generation only starts after a user starts/approves a task or explicitly runs a local evaluation.

## Start

1. Double-click **Build.bat** to restore dependencies, build Release and run the checks. This is a user-invoked build; it never creates an archive or installer.
2. Double-click **Run.bat**. A verified Debug build is also available from development verification.
3. Register your project folder. Select **Update file index** when needed.
4. In **AI providers**, detect installed capabilities. Select a provider, enable it and save. Add an exact model ID for each permitted task type. An empty cloud model ID means **CLI default, unknown exact model**; it is never represented as a discovered model.
5. Create a task. Choose **ReadProject** for review, or explicitly permit **WriteProject** both on the task and provider for code changes. Codex can write in its sandbox. Other CLI providers receive bounded file snapshots and return edit proposals; review them in History and select **Apply reviewed edits**. Ollama supports TextOnly work.
6. Choose **Giao việc & chạy**, or save a draft with **Lưu để chạy sau**. SemiAutomatic first proposes a route; use **Duyệt & tiếp tục**. Manual requires selecting a concrete provider/model. **Switch AI** stops and awaits the running process, checkpoints, then starts the selected permitted target.

Providers start disabled. No external project is automatically registered. The app does not install CLIs, log into accounts, pull models or create archives. Tool actions and existing local update packages require explicit configuration and approval; see V3-V4.md.

## Project discovery keywords

Registration scans local file/folder names and bounded root README/manifests. For existing projects, select a project and click **Scan project keywords**; **Update file index** also refreshes keywords. The Keywords column shows terms saved in the manager database, without modifying source or calling AI.

Automatic assignment first matches names, aliases and paths. Otherwise it requires at least two matching terms unique to one registered project and a clear score lead. Ambiguous requests open the project picker. Keyword matching ignores case and Vietnamese accents; rescanning replaces old terms.

Scans examine at most 10,000 filesystem entries, retain at most 512 terms and read at most 16,000 characters per root README/manifest. Hidden files, links, dependencies, build outputs and Data folders are skipped. Large projects may need explicit aliases for terms outside these bounds. Scanned text is search data, never an instruction or permission grant.

## What works

- Multiple project registry with aliases and rule-based ambiguity detection; Unity/.NET/Node/Python/Git detection from actual files.
- Bounded project-only metadata indexing, directory/link exclusions, incremental metadata reconciliation and project-scoped FTS5 filename search. Index refresh is explicit, not a background watcher.
- Persistent tasks, priorities, per-task access, logs, attempt budgets and checkpoints. AI routing has no switch limit; see [AI routing](docs/ROUTING.md). Interrupted Running tasks recover to Paused and need explicit resume.
- Pluggable `IAIProvider` contracts; adapters for Codex, Claude Code, Copilot CLI, Gemini CLI, OpenCode, Freebuff CLI and Ollama. Capability detection runs local help, not a model.
- Deterministic model-level priority/quality/cost routing, allowed task kinds, disabled/blocked model gates and per-kind local qualification. Manual, SemiAutomatic and Automatic policies with bounded availability fallback; ordinary task failures do not switch AI.
- One execution per registered project, application single-instance lock per data directory, bounded output and context, timeout, cancellation with process-tree exit, handoff before and after execution, logs and cooldown persistence.
- Ollama installed model list, six deterministic JSON/extraction/classification benchmark prompts with exact answers, category-specific scores and timings, zero cloud grading, `keep_alive=0` after each request. Coding is never qualified by this small suite.
- Pixel Art, TrailerForge, ZapBop Music Producer ([setup](docs/ZAPBOP-CONNECTOR.md)) and Marketing connectors; durable dependency workflows attached to Tasks; reviewed local-package updates with an independent helper. See V3-V4.md for configuration and verification limits.

## Important Phase 1 limits

**CLI integration is capability-gated, not a claim that every installed version is supported.** Freebuff CLI is detected and catalogued, but it is an interactive terminal UI with no headless prompt/print/model flags in the inspected version, so execution stays blocked. Only Codex and Ollama are usable on this machine at verification time. Copilot's VS Code launcher reports the real CLI is missing. Claude, Gemini and OpenCode are absent. Their command construction and safety gates are tested with fixtures; authenticated inference against these absent providers is not tested.

**Other CLI providers use reviewed edits for coding.** They deny model tool use and run in a separate temporary working folder. ReadProject/WriteProject requests receive up to eight bounded source snapshots. WriteProject asks for a structured proposal with complete file text and original SHA-256 hashes. Existing files require an exact hash match, new files require `NEW`; duplicate paths, traversal, links, Windows device names, sensitive paths and unsupported file types are rejected. Review the full proposal in History before applying. File edits are prevalidated as a batch; a caught write failure rolls back completed writes, but a machine crash during a multi-file apply is not a crash-atomic transaction. No deletion is supported. CLI tool denial does not isolate provider startup plugins/hooks/configuration at the OS level; use trusted CLI installations and configuration. Codex uses its own sandbox, never full-access or bypass flags. Sandbox initialization failure is reported, not bypassed.

Authentication and quota are shown as Unknown unless a reliable interface provides them; CLI presence is not proof of login. No quota polling generates AI calls. Rate-limit and auth failures create bounded cooldowns. The app makes no paid API requests or automatic paid API fallback. CLI accounts/configuration are managed by their vendors; enabling a gateway such as OpenCode requires understanding its configured billing. Known API key environment variables are removed from child processes; existing vendor credential stores are not modified.

Cloud models are user catalog entries when no verified listing command exists. OpenCode lists via its verified `models` command; Ollama lists via `/api/tags`. No cloud model names or relative quality claims are hardcoded. User priority and model quality/cost rank are routing inputs, not measured benchmarks. Historical run/feedback scoring and Economy/Balanced/Performance routing are implemented; this is not automatic cloud benchmarking.

Checkpoint progress is bounded raw provider output, not an AI-certified completion summary. Handoffs contain original request, rules, current notes, Git status/diff when permitted, remaining-work guidance and explicit unknown test state. Git capture is bounded and excludes common secret paths, but is not a secret scanner. It does not preserve a native vendor session or roll files back. New agents must inspect existing changes before repeating work. The app never marks generated work independently Verified: successful runs become **NeedsReview**.

Text-only mode excludes project rules, Git and indexes from the request. ReadProject permits project context; Codex's read-only sandbox is not a privacy barrier for all readable files. Project data and raw output are stored locally unencrypted. Review sensitive project content before enabling cloud work.

Local evaluation is deliberately small (three examples per category), not evidence of broad model competence. RAM/manager CPU telemetry, optional NVIDIA metadata, RAM-aware worker slots and small objective JSON/arithmetic probes are implemented. Broad content quality scoring and executable local coding qualification remain later work. No GPU memory cap is claimed. The manager does not benchmark automatically when the app starts.

## Data and development

Default database: `<application root>\Data\manager.db` (here `D:\Projects\AIEmployeeManager\Data\manager.db`). The application root is the folder containing Run.bat and the solution, or the executable folder for a portable installation. Settings → **Đổi thư mục lưu data…** copies the current database into an empty selected folder and switches immediately. `data-location.json` at the application root remembers the path across Debug/Release builds. At first normal launch, existing legacy AppData data is copied to the default Data folder; the old copy is retained. `--data-dir <folder>` overrides this only for that run and never changes the saved path. Missing configured storage fails visibly instead of silently creating an empty database.

```powershell
dotnet restore AIEmployeeManager.slnx -m:1 -nr:false
dotnet build AIEmployeeManager.slnx -c Debug --no-restore -m:1 -nr:false
dotnet run --project tests/Employee.Checks -c Debug --no-build
```

The executable checks use fixtures and temporary folders; no paid inference. `Employee.Checks.exe --detect <diagnostics-folder>` runs only help/model discovery. `AIEmployeeManager.exe --smoke --data-dir <isolated-folder>` exercises UI handlers and renders each tab without AI calls. Do not point smoke mode at your production data folder.

See [consolidated specification](docs/SPECIFICATION.md), [architecture](docs/ARCHITECTURE.md), [verification](docs/VERIFICATION.md) and [CLI evidence](docs/CLI-CAPABILITIES.md).



## Sơ đồ điều phối task

Trong **Tasks**, chọn công việc rồi bấm **Xem sơ đồ điều phối**. Cửa sổ hiển thị yêu cầu gốc, coordinator, các bước thực thi và kế hoạch chưa được duyệt hoặc đã bị từ chối. Mũi tên liền chỉ quan hệ phụ thuộc; nét đứt chỉ quan hệ giao việc/chuyển giao. Mỗi ô hiển thị trạng thái và AI/model; bấm ô để xem yêu cầu, quyền, kiểm thử và các quyết định liên quan. Dùng thanh thu/phóng và thanh cuộn để xem sơ đồ lớn. Dữ liệu tự cập nhật mỗi 2 giây khi thay đổi, hoặc bấm **Làm mới**. Mở sơ đồ không gọi AI và không thực thi task.

## Next task

Trong Tasks, nh?p y?u c?u r?i b?m **Next task** ?? l?u v?i state `Queued`. Ho?c ch?n task `Saved` r?i b?m **??a task ?? ch?n v?o queue**. H?ng ??i ch?y theo th? t? th?m v?o khi task, ki?m th?, qu?t/index ho?c ki?m tra provider hi?n t?i k?t th?c. Task v?n tu?n theo ch? ?? routing, quy?n v? c?c y?u c?u duy?t; khi c?n duy?t, task ch? ng??i d?ng v? h?ng ??i c? th? chuy?n sang task k? ti?p.

**L?u ?? ch?y sau** d?ng state `Saved` v? kh?ng t? ch?y. Ch?n task ?ang queued r?i b?m **D?ng & l?u** ?? ??a v? `Saved`. D?ng task ?ang ch?y s? t?m d?ng vi?c l?y task k? ti?p; th?m task v?o queue ?? ti?p t?c h?ng ??i. H?ng ??i ???c l?u qua l?n m? ?ng d?ng; task ?ang ch?y b? gi?n ?o?n v?n c?n ti?p t?c th? c?ng. Draft c? ? state `Queued` ???c chuy?n th?nh `Saved`, c?c b??c n?i b? gi? nguy?n.

## Đính kèm và bình luận

Trong Tasks, chọn **Đính kèm ảnh / file…** bên dưới ô giao việc hoặc nhấn **Ctrl+V** trong ô nhập để dán ảnh clipboard. Có thể chọn nhiều file và bỏ từng file trước khi gửi. Mỗi task hỗ trợ tối đa 20 file, 10 MB/file và 20 MB tổng cộng (gồm file trong bình luận).

Chọn task rồi mở **Bình luận & đính kèm** để gửi thông tin bổ sung và lưu lại các file đã đính kèm. Bình luận được gửi khi không có tác vụ đang chạy; AI nhận bổ sung ở lần chạy/tiếp tục kế tiếp. File được lưu trong database cùng task, không phụ thuộc đường dẫn gốc, và đi cùng chức năng chuyển thư mục data.

Codex nhận bản sao file tạm để đọc bằng công cụ được cấp quyền. Các provider khác nhận phần xem trước văn bản có giới hạn; file nhị phân/ảnh không được gửi như đầu vào đa phương thức cho các provider đó. Không tự OCR hoặc gọi dịch vụ chuyển đổi. Nội dung bổ sung dài được giới hạn trong ngữ cảnh AI; lịch sử đầy đủ vẫn được lưu.

### Tiếp tục cập nhật task bằng bình luận

Bấm vào dòng task để mở phần **Bình luận / yêu cầu cập nhật**. Nhập nội dung cần sửa và chọn **Gửi & tiếp tục cập nhật** để lưu bình luận rồi tiếp tục task đó. **Gửi bình luận** chỉ lưu để dùng trong lần chạy sau. Nếu task đang chạy, chọn **Dừng & lưu** trước khi gửi.

Task đã hoàn thành có thể mở lại bằng yêu cầu cập nhật. Ứng dụng giữ yêu cầu gốc, lịch sử, checkpoint, quyền và ngân sách hiện có; không tự gia hạn thời gian hoặc tăng lượt AI. Chế độ routing và các yêu cầu duyệt vẫn áp dụng.

## Thông báo

Ứng dụng báo qua Windows khi task cần duyệt/cấp quyền, có kết quả cần xem, gặp lỗi, chờ AI, tạm dừng hoặc hoàn thành. Các lần làm mới không tạo lại cùng một thông báo. Thông báo Windows dùng biểu tượng ở khay hệ thống; Windows có thể ẩn thông báo tùy cài đặt thông báo của máy.

Nút **Thông báo** hiển thị số mục chưa đọc; biểu tượng ứng dụng trên taskbar có chấm đỏ khi còn mục chưa đọc. Bấm thông báo Windows hoặc biểu tượng khay hệ thống để mở danh sách, bấm đúp một mục để mở task tương ứng. Chọn **Đánh dấu tất cả đã đọc** để xóa dấu chấm. Lịch sử giữ tối đa 200 mục và được lưu trong thư mục data, đi cùng khi đổi thư mục data.

Có thể bật/tắt thông báo Windows trong **Settings**; danh sách và dấu chấm trong ứng dụng vẫn hoạt động. Khi mở lại ứng dụng, các task còn cần chú ý xuất hiện trong danh sách mà không phát lại thông báo nổi cũ. Thông báo hoạt động khi ứng dụng đang mở.


Task assignment: new tasks default to AI selection of task type and minimum project access when execution starts, using the request and supplementary comment/attachment text. Uncheck **Automatic task type and project access** to enable both manual choices. Both choices and priority are saved with **Assign & run**, **Add to queue**, or **Save for later**; no separate permission/priority save button is needed. Classification uses one session call, requires at least two remaining calls, and stops on invalid output. Codex classification uses ReadProject (its existing TextOnly restriction still applies); other adapters use TextOnly. Provider/model write permissions remain required for WriteProject execution. Existing tasks keep their stored configuration. Priority is set at creation: higher values run first in the queue, with FIFO for equal priority. Session access remains fixed after assignment.

### Pass through a blocked task step

In task details, open **Steps & approvals**, select a stopped unfinished step, and click **Pass through (skip step)**. If the task is running, use **Stop & save** first. The step is saved as `Skipped`; its pending decisions are rejected and dependent steps can proceed. Click **Run / resume** to continue, or **Send & continue updating** to run a new comment. When all earlier steps are completed/skipped, an update creates a new step without rerunning skipped work, subject to the existing step and execution budgets.

Skipping preserves outputs and test results; it does not approve changes, grant permissions, or mark tests passed. Pending batch tests belonging only to skipped steps are not automatically run. Shared test batches may still run for other steps. The final task still requires explicit review.

Focused regression check: `dotnet run --project tests/Employee.Checks -- --step-skip` (uses an isolated temporary database and no AI providers).


## V5: AI and tool skills in Tasks

Tasks can now plan mixed AI → Tool → AI steps. Configure tools and task automation permission in Settings > Tools; describe the outcome in the normal Tasks composer without choosing a saved workflow. Project tool skills are discovered from .agents/skills and loaded only when selected. See [V5 setup and limits](docs/V5.md). Source-only implementation; no V5 application build or runtime checks have been performed.

## Testing workspace

Use the **Kiểm thử** navigation tab to select a project and the task receiving test results. **Test toàn bộ** runs every enabled suite for that project. **Test phần cập nhật** reads the selected task's latest edit proposal and selects suites by `SourcePaths` (relative file/directory prefixes, no wildcards). An empty list always participates. Each suite's configured command determines the tests executed. Select a child task with an edit proposal when the parent has none.

**Chạy bộ test đã chọn** runs one enabled suite. Manual requests rerun passed suites. Pending jobs, retry, stop and output are scoped to the selected project.

The JSON expander supports import, inspection, editing and export of a `ProjectTestProfile` object. Saving imported JSON creates a new suite in the selected project with execution disabled; inspect and enable it before running. Create/update AI buttons open a Review task draft with ReadProject access in Tasks. Submit that draft to generate proposed test cases/files and suite JSON, then bring the reviewed JSON back to Testing. The buttons do not automatically call AI or apply generated files.
## Large goals ("make a game")

A goal that builds something new is planned as milestones with acceptance criteria; after each milestone and at the end a tester step runs the project against them, failures are fixed and tested again, and the run stops when the criteria pass or the rounds run out. The planner may choose connected tools and research online. See [docs/AUTONOMOUS-GOALS.md](docs/AUTONOMOUS-GOALS.md).

# Tool and API integrations

See [docs/INTEGRATIONS.md](docs/INTEGRATIONS.md) for the current inventory of AI providers, tool connector APIs, call flows, configuration requirements and permission boundaries.

