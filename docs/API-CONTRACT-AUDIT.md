# Đối chiếu hợp đồng API và adapter

Ngày rà soát: 2026-11-12. Phạm vi: bốn connector `pixel`, `marketing`, `video`, `music` và các provider được đăng ký trong `src/Employee.Desktop/App.xaml.cs`. Chỉ đọc tài liệu/mã có sẵn trong dự án; không gọi API, chạy CLI nhà cung cấp, khởi động backend, build, test hoặc cài đặt. Đây là đối chiếu tĩnh, không phải chứng nhận backend đang hoạt động.

## Trạng thái xử lý (cập nhật 2026-11-12, bước triển khai)

Phần dưới đây là ảnh chụp lúc audit. Đã sửa trong mã (chi tiết hợp đồng mới: [INTEGRATIONS.md → Connector compatibility contract](INTEGRATIONS.md)):

| Phát hiện | Trạng thái |
|---|---|
| Pixel `completed` thiếu PNG vẫn thành công | **Đã sửa:** bắt buộc PNG data URL hợp lệ; base64 lỗi báo rõ |
| Pixel chỉ poll `running`, ID phải là GUID | **Đã sửa:** poll `queued/pending/running`; ID là định danh an toàn `[A-Za-z0-9_-]{1,128}` (cả executor task) |
| Pixel lỗi HTTP khi poll bị Failed thay vì resume | **Đã sửa:** 408/429/5xx khi đã có RemoteId → Interrupted, resume được; 4xx khác vẫn Failed |
| Marketing `body` sai kiểu bị thay bằng `{}` | **Đã sửa:** `body` không phải object bị từ chối trước khi gửi HTTP |
| 2xx rỗng/HTML gây lỗi parse khó hiểu | **Đã sửa:** body rỗng → `{}`; non-JSON → thông báo có content type |
| Bridge mất JSON do trộn/cắt log | **Đã sửa:** kênh `--result-file` riêng; fallback dòng JSON object cuối cùng của stdout |
| TrailerForge không kiểm artifact; không ép UTF-8 | **Đã sửa:** file phải tồn tại trong WorkspaceRoot, MP4 có `ftyp`, có SHA-256; stdio UTF-8 |
| Endpoint Ollama cố định khác tài liệu | Tài liệu đã sửa theo mã (cố định `127.0.0.1:11434`) |

Còn mở, cần schema/tài liệu backend đúng phiên bản hoặc quyết định chính sách, nên không đoán trong mã: schema thành công/lỗi nghiệp vụ HTTP 200 của Rikerat, query phân trang, idempotency; fallback provider/`autoSwitch` của Pixel và việc tính AI cho thao tác offline; Stop không gọi route cancel; cờ CLI Qoder/Claude chưa được detect kiểm; `thinking.values` của Ollama. Chưa chạy backend thật nào, chỉ có contract fixture trong `tests/Employee.Checks`.

## Nguồn và mức bằng chứng

- Hợp đồng cục bộ: [INTEGRATIONS.md](INTEGRATIONS.md), [CLI-CAPABILITIES.md](CLI-CAPABILITIES.md), [TOOL_AUTOSTART.md](TOOL_AUTOSTART.md), [ZAPBOP-CONNECTOR.md](ZAPBOP-CONNECTOR.md), các `SKILL.md`/`tool-skill.json` trong `.agents/skills/employee-*`.
- Mã gọi: `ToolExecutionService.cs` (`Capabilities`, `Api`, `Request`, `AwaitPixel`, `Video`, `RunZapBop`, `Music`, `CheckAsync`); `ToolBackendLauncher.cs`; `PixelRequest.cs`; `PixelToolOptions.cs`; `CliProvider.cs`; `OllamaProvider.cs`; `ProcessRunner.cs` trong `src/Employee.Infrastructure/`.
- Hợp đồng bridge được kiểm tra trực tiếp: `connectors/trailerforge_bridge.py`, `connectors/zapbop_bridge.py`.
- Không có OpenAPI/schema phản hồi đầy đủ của Pixel/Rikerat trong các tài liệu đã kiểm tra. Không đọc source backend ở dự án khác và không truy cập các URL tham khảo bên ngoài. Các mô tả từng được AI viết trong tài liệu là bằng chứng thứ cấp; chỉ kết luận chắc chắn khi mã hiện tại xác nhận. Trạng thái tài khoản, phiên bản backend, lỗi thực tế và các schema thiếu được ghi là chưa xác minh.

## Quy tắc HTTP chung của connector

`Request` chỉ chấp nhận HTTP loopback (`127.0.0.1`, `localhost`, `::1`), không user-info, proxy hay redirect. Các route bắt đầu bằng `/`, nên phần path trong cấu hình base URL bị thay thế. HTTPS, server từ xa và reverse proxy đặt API dưới path prefix không tương thích.

Body là JSON UTF-8 `application/json`; GET không có body. Thành công phải có JSON parse được: body rỗng/204 hoặc HTML trên HTTP 2xx gây lỗi parse; JSON `null` được đổi thành `{}`. Không kiểm tra Content-Type hoặc schema tổng quát. Phản hồi tối đa 16.000.000 byte; lỗi HTTP được lưu dưới dạng `Tool HTTP <status>: <2.000 ký tự đầu body>`, không phân loại riêng 401/403/404/409/422/429/5xx, không xử lý Retry-After hay retry tự động. Lỗi nghiệp vụ nằm trong HTTP 200 chỉ được nhận biết nếu adapter có kiểm tra riêng.

Input chung phải là JSON object, giới hạn 12.000.000 **ký tự** (thông báo ghi 12 MB nhưng không đo byte UTF-8). Timeout thao tác được kẹp 5–7.200 giây, sau bước startup; startup có timeout riêng. Huỷ không chứng minh backend đã rollback. Mọi thao tác ghi/AI/publish chịu quyền profile và binding phê duyệt; quyền này không thay thế xác thực backend.

Lệch tài liệu lifecycle: `TOOL_AUTOSTART.md` nói Pixel/custom tiếp tục chạy sau khi đóng manager. `ToolBackendLauncher` hiện giữ tiến trình sở hữu trong `owned`, dừng sau hai phút idle và khi Dispose; có KillOnCloseJob cho lifetime. Không dừng backend người dùng mở sẵn. Cần cập nhật hướng dẫn lifecycle ở bước tài liệu tiếp theo; không dùng mô tả cũ để suy ra job từ xa còn sống sau khi manager đóng.

## Pixel Art Tool (`pixel`)

Endpoint mặc định theo tài liệu: `http://127.0.0.1:4317`. Manager không gửi Authorization; đăng nhập Codex thuộc backend. `CheckAsync` đọc `loggedIn` (boolean), `version` (string); không có `loggedIn:true` thì chỉ báo chưa xác nhận đăng nhập.

| Thao tác | Method/path | Yêu cầu gửi đi | Phản hồi manager dùng |
|---|---|---|---|
| status/readiness | GET `/api/codex/status` | Không body | JSON; check dùng `loggedIn`, `version`; readiness chỉ cần HTTP/JSON thành công |
| generate | POST `/api/codex/jobs` | Toàn bộ input JSON đã chuẩn bị | `id` string dạng GUID, `status` string |
| poll/resume | GET `/api/codex/jobs/{id}` | Không body | `status:"running"` để tiếp tục; `"completed"` để kết thúc; trạng thái khác lấy `error` và fail |
| cancel | POST `/api/codex/jobs/{id}/cancel` | `{}` | Có helper `CancelPixel`, nhưng không có caller trong service; Stop chỉ ngừng chờ, giữ ID để resume |

Hợp đồng input từ skill: action `create/edit/animation/direction/skin`, prompt tối đa 4.000 ký tự; ảnh `image`/`styleGuide` thành data URL PNG sau khi manager resolve `$imageFrom`, `$projectPng`, `$attachmentPng`. Các marker này là cú pháp nội bộ, không phải API backend. `PixelRequest.Complete` bổ sung/chuẩn hoá action, direction, frames, fps, outputWidth/Height, artMode, preserveStyle, lockPalette, autoRetry, exactFrames. Giá trị mặc định legacy: canvas 16–512, frames 4/8/12, fps 1–30, tám hướng; capability file version 1 có thể mở rộng canvas đến 4.096, frames đến 64 và fps đến 120. Cần dùng capability file của backend tương ứng, không coi giới hạn trong skill là schema đầy đủ hiện hành.

Khi hoàn tất, manager chỉ lưu ảnh nếu `result.image` bắt đầu chính xác bằng `data:image/png;base64,`; kiểm chữ ký PNG và đọc kích thước từ header, giới hạn 12.000.000 byte. Lưu `<data>/artifacts/tool-runs/<runId>/image.png`, SHA-256; bỏ base64 khỏi output. Tối đa 15 candidate bổ sung, mỗi ảnh tối đa 4.000.000 byte; đọc `candidates.Skip(1)` với giả định candidate đầu là ảnh chính. Có thể xử lý nền cho animation/direction/skin và lưu bản gốc. Kiểm header chưa chứng minh giải mã toàn bộ PNG hay bố cục frame đúng.

Các điểm không tương thích/rủi ro:

- **P1, thấy trực tiếp trong mã:** `completed` không có PNG đúng prefix vẫn trả thành công/NeedsReview, không bắt buộc artifactPath. URL ảnh, JPEG/WebP, base64 thuần hoặc output text không đáp ứng hợp đồng downstream nhưng chưa bị chặn tại đây.
- **P1, giả định giao thức:** chỉ poll `running`; backend trả `queued/pending` sẽ fail ngay. ID không phải GUID bị từ chối dù server có thể chấp nhận. Chưa có schema backend độc lập để xác nhận các trạng thái/ID khác có tồn tại.
- **P1, khả năng lệch chính sách:** `CompleteCommon` mặc định `autoSwitch:true`, nhận `fallbackToApi:true` và provider `api/pixellab/claude` nếu capability có khai báo. Skill mô tả dùng connector/Codex, còn capability service tính một lượt AI cho mọi generate. Binding duyệt input không chứng minh ngân sách hoặc sự cho phép riêng cho từng lượt/backend fallback; không chạy hay thay đổi các đường gọi này trong đợt audit.
- **P2:** offline recolor/hue/procedural được chuẩn hoá khi capability có hỗ trợ nhưng service vẫn gắn toàn bộ `generate` là UsesAI; có thể chặn/đếm AI cho thao tác không gọi model. `autoRetry:true` có thể sinh thêm lượt bên trong backend mà manager không đo được.
- **P2:** lỗi HTTP khi đã có RemoteId chuyển Interrupted; lỗi HTTP non-2xx do `Request` ném InvalidOperationException lại chuyển Failed. Hai dạng mất khả năng poll được xử lý khác nhau; chưa xác minh recovery cho từng status.
- **P2:** tài liệu inventory liệt kê cancel như một route nhưng UI Stop không gọi nó; không được hiểu Stop là huỷ job từ xa. Chưa kiểm chứng giả định candidate đầu trùng ảnh chính.

## Rikerat Marketing (`marketing`)

Endpoint preset theo tài liệu: `http://127.0.0.1:4319`. Mọi HTTP request, kể cả `/health`, có `Authorization: Bearer <session token>` lấy qua `SecretProtection.Unprotect`; token trống bị chặn trước khi gửi. Preset startup truyền token qua stdin, không qua argument. OAuth YouTube và review gate thuộc backend, không được thay thế bằng token manager.

| Operation | Method/path | Body thực sự gửi |
|---|---|---|
| status | GET `/health` | Không body |
| projects / jobs | GET `/projects`, GET `/jobs` | Không body; input filter/pagination không được chuyển thành query |
| project.create | POST `/projects` | Input nguyên vẹn; skill: `name`, `description`, `language` |
| media.import | POST `/media` | Input nguyên vẹn; skill: `project_id`, `path` (đã resolve dependency) |
| job.create | POST `/jobs` | Input nguyên vẹn; skill: `project_id`, `media_id`, `platforms`, `export_mode` |
| job.status | GET `/jobs/{jobId}` | Không body |
| job.run | POST `/jobs/{jobId}/run` | `input.body` object hoặc `{}` |
| content.set | PUT `/jobs/{jobId}/content` | `input.body`; skill minh hoạ `content` |
| review | POST `/jobs/{jobId}/review` | `input.body`; skill: `gate`, `decision`, `revision` thực tế |
| schedule | POST `/jobs/{jobId}/schedule` | `input.body`; không có schema lịch/timezone được xác nhận trong tài liệu cục bộ |
| export | POST `/jobs/{jobId}/export` | `input.body` hoặc `{}` |
| youtube.publish | POST `/jobs/{jobId}/publish/youtube` | `input.body`; skill: `confirmed:true`, `privacy_status:"private"` |

`jobId` là wrapper nội bộ, phải khớp `[A-Za-z0-9_-]{1,128}`. Dữ liệu trường khác đặt ở ngoài `body` cho thao tác theo job sẽ bị bỏ qua. Skill và mã dispatch khớp cấu trúc wrapper này. Schema thành công của projects/jobs/create/media/review/schedule/export/publish không được adapter xác thực; JSON được lưu nguyên và chuyển cho bước sau. Health check chỉ lấy `version`, không kiểm tra hợp đồng phiên bản hoặc khả năng publish.

Các điểm cần ghi nhận:

- **P1, khoảng trống hợp đồng:** chưa có schema backend đầy đủ, enum, required fields, kiểu ID đầu ra, phân trang, revision conflict, timezone và error envelope để xác nhận route/body tương thích thực tế. Các ví dụ skill không đủ làm API specification. Không tự suy đoán schema schedule.
- **P1:** `body` không phải object bị thay bằng `{}` thay vì báo lỗi; input sai có thể thành một mutation khác dự kiến. HTTP 200 chứa lỗi nghiệp vụ vẫn có thể đi đến NeedsReview.
- **P2:** không hỗ trợ query pagination/filter của danh sách; backend bắt buộc phân trang hoặc trả quá 16 MB sẽ không tương thích. ID có ký tự ngoài regex bị chặn trước HTTP.
- **P2:** `job.run` chỉ được manager tính một lượt AI; số lượt nội bộ backend không được đo. Không có idempotency key chung hay retry mutation; sau timeout cần kiểm trạng thái trước khi tạo lại. Chưa xác minh backend có hỗ trợ idempotency hay không.

## TrailerForge (`video`)

Không có HTTP endpoint/token. Đây là bridge manager sở hữu, không phải CLI chính thức của TrailerForge. Argument: `<python.exe> connectors/trailerforge_bridge.py --source <source> --root <workspace> --operation inspect|export`; JSON UTF-8 stdin `{"projectFolder":"<saved folder>"}`. Root/folder phải tồn tại, folder resolve trong root, chứa `project.json` tối đa 5.000.000 byte. Project đọc bằng UTF-8 BOM được hỗ trợ.

| Operation | Engine call / phản hồi |
|---|---|
| inspect | Đọc JSON, không import renderer; trả `projectFolder`, `project` |
| export | `project_storage.prepare(project, folder)` → `engine.Runner` → `export_latest.export_latest(prepared, folder, runner)`; trả `artifactPath`, `projectFolder` |

Exit 1 và stderr chứa thông báo exception khi lỗi; manager coi exit != 0 là thất bại, exit 0 parse dòng cuối của output thành JSON. Export có thể thay `export/trailer.mp4` theo tài liệu; không có compose/edit từ prompt. Xác thực là quyền tiến trình/filesystem và phạm vi WorkspaceRoot, không phải token dịch vụ.

- **P1, thấy trực tiếp:** ProcessRunner trộn stdout/stderr, cắt phần đầu chung tại 200.000 ký tự; bridge in log renderer vào stderr. Log dài có thể làm mất JSON cuối hoặc log sau JSON khiến manager parse sai dù export đã chạy. Không có kênh result riêng hay giữ tail cho bridge.
- **P1:** manager không kiểm tra artifactPath tồn tại, nằm trong workspace, chữ ký/loại MP4 hay SHA-256 sau export; tương phản với kiểm WAV của music. Tài liệu nói có artifact không đồng nghĩa mã xác nhận artifact hợp lệ.
- **P2:** bridge không ép encoding các Python pipe về UTF-8 như ZapBop; trên Windows môi trường ANSI có thể lỗi tên/path Unicode trong khi ProcessRunner đọc UTF-8. Đây là rủi ro theo cấu hình, chưa tái hiện.
- **P2:** input manager tối đa 12 triệu ký tự nhưng bridge chỉ đọc 1 triệu ký tự; inspect trả nguyên project đến 5 MB nhưng output manager chỉ 200 nghìn ký tự. Project hợp lệ theo bridge vẫn có thể mất JSON ở caller.
- **P2:** Check connection chỉ kiểm `editor_core.py` và Python; không kiểm `engine`, `project_storage`, `export_latest` hay dependencies, không chứng minh export hoạt động. Không chạy import/renderer trong đợt audit.

## ZapBop (`music`)

Không endpoint, token hoặc account. Bridge gọi engine đã cài, không composer/AI. Python cấu hình hoặc `.venv/Scripts/python.exe` trong source; argument `-I <bridge> --source <source> --operation status|inspect|export`, thêm `--root` cho inspect/export, `--output-dir` cho export. Stdin/stdout/stderr được bridge ép UTF-8; `-I` bỏ PYTHON* và đường dẫn working directory khỏi import tự động, nhưng bridge chủ động import source đã cấu hình.

| Operation | Input / phản hồi |
|---|---|
| status | `{}`; import `audio_engine`, `pro_engine`, numpy; trả `source`, `projectFormat`, `numpy`, `sounds`, `sampleRate` |
| inspect | `{"projectFile":"<saved .json>"}`; `Project.load` rồi `to_dict`; trả `projectFile`, `title`, `bpm`, `root`, `scale`, `bars`, `durationSeconds`, `instruments`, `patterns`, `layers`, `sampleRate` |
| export | Cùng input; `render_project(data)` → `write_wav`; thêm `artifactPath`, `channels`, `frames`, `bytes`; manager thêm `artifactSha256` |

File resolve trong WorkspaceRoot, đuôi `.json`, tối đa 5.000.000 byte; schema `zapbop-pro`/version 1 theo tài liệu, validation thực tế uỷ quyền cho `Project.load`. Export chặn bar_count=0, duration >900 giây và tên output đã tồn tại. Output ở run folder, tên đã sanitize `<stem>_<bpm>bpm.wav`. Manager kiểm đường dẫn trong folder, `.wav`, tồn tại, độ dài >=44, RIFF/WAVE rồi hash. Tài liệu mô tả 16-bit stereo 44,1 kHz; caller không xác nhận PCM bit depth, số kênh hoặc sample rate trong WAV header, các thuộc tính đó phụ thuộc engine.

Lỗi bridge: exit 1, stderr `ExceptionType: message`; manager lấy dòng cuối output và tiền tố `ZapBop:`. Thiếu source/Python/bridge, file ngoài root, schema project lỗi, quá duration hoặc thiếu WAV đều có đường fail rõ. Check connection import engine, không render.

- **P2:** stdout/stderr vẫn trộn và giới hạn 200.000 ký tự như video, nên output lớn/log engine có thể mất JSON; chưa thấy fixture thực tế gây lỗi.
- **P2:** giới hạn stdin bridge 1 triệu ký tự khác giới hạn input chung. Điều này ít ảnh hưởng schema hiện chỉ có projectFile nhưng phải ghi rõ cho caller.
- **Chưa xác minh:** chữ ký hàm engine và dependency phiên bản thực tế hiện tại; tài liệu có ghi lần thử trước nhưng không phải kết quả của audit này. Không có lệch tên trường/cách truyền input giữa skill, C# và bridge được tìm thấy.

## Provider AI và CLI (phạm vi bổ sung của inventory)

Các CLI không được manager gọi qua HTTP; endpoint upstream, credential refresh, quota và wire JSON request/response do CLI quản lý, không có hợp đồng HTTP trực tiếp để đối chiếu ở đây. Manager bỏ các biến API-key đã biết khỏi môi trường child, dùng phiên CLI đã cấu hình; điều này không chứng minh không có credential/billing trong config riêng của CLI. Không kiểm tra tài khoản hay chạy AI.

| Provider | Gọi thực tế / input | Output và xác thực manager quan sát |
|---|---|---|
| Codex | `--ask-for-approval never --sandbox read-only|workspace-write --cd <root> exec --ignore-user-config --ephemeral --json --skip-git-repo-check [--model id] -`; prompt stdin | JSONL; nhận `turn.failed`/`error`; dữ liệu kết quả vẫn là JSONL ở adapter. Login/quota không suy ra từ help; có kiểm cấu hình/probe sandbox Windows trước write |
| Claude | `--print`, giới hạn tools/MCP theo access, thêm `--model`; prompt stdin | Text mặc định; optional `auth status --json` đọc `loggedIn`, `authMethod` khi help có hỗ trợ |
| Copilot | prompt argument `--prompt`, tool denial, no-ask-user/no-auto-update, model tuỳ chọn | Text; auth/quota Unknown; phân biệt launcher thiếu CLI |
| Gemini | `--admin-policy <deny.toml> --prompt <prompt> [--model]` | Text; block khi tồn tại central policy có thể làm policy phụ bị bỏ qua |
| OpenCode | `run --format json [--model] <prompt>`; env deny tools/default plugins/LSP download | JSONL `type:text`, `part.text` được ghép; `type:error` được phân loại |
| Qoder | `--print --no-session-persistence`; write dùng `--permission-mode bypass_permissions`, read dùng `dont_ask`, TextOnly dùng `--tools ""`; prompt stdin | Text; catalog từ `--list-models`; không có tài liệu API/CLI độc lập cho Qoder trong nguồn đã kiểm tra |
| Antigravity | `--output-format text --mode accept-edits|plan --sandbox [--add-dir root] [--model] --print <prompt>` | Text; catalog `agy models` lấy cột trước tab; không có tài liệu độc lập xác nhận trong dự án |
| CommandCode | Detection/catalog qua `cmdc` hoặc `command-code`, có thể `--list-models` | Execution bị vô hiệu hoá; không có case chạy trong Arguments |
| Freebuff | Help/local settings chỉ dùng metadata | Không hỗ trợ headless đã xác minh; execution bị chặn |

Lỗi CLI: executable/flag không đủ → Unavailable; khởi chạy Win32 lỗi → LaunchFailed; vượt command line ~30.000 ký tự đối với prompt argument → InputTooLarge; exit khác 0 phân loại bằng chuỗi thành ModelRefused/ModelUnavailable/RateLimit/Authentication/Unavailable/TaskError. Exit 0 mặc định thành công, ngoại trừ các JSONL error đã nhận biết và kiểm permission refusal Codex. Thay đổi wording/locale hoặc schema event có thể phân loại sai; đây không phải hợp đồng error code vendor.

Các lệch/giới hạn cần xử lý ở bước triển khai sau:

- **P1:** Qoder WriteProject dùng `bypass_permissions`, ReadProject không truyền allowlist Read/Glob/Grep. Cờ này không phải bằng chứng sandbox theo thư mục; mô tả read-only dựa vào tên permission mode chưa đủ. Không áp dụng/chạy cờ này trong audit.
- **P2:** Detect kiểm một tập con flags nhưng Arguments dùng thêm cờ chưa được kiểm, ví dụ Claude `--allowedTools/--add-dir/--permission-mode`, Qoder `--no-session-persistence`, các giá trị enum permission. Có thể detect Ready rồi lỗi invocation với phiên bản khác.
- **P2:** [CLI-CAPABILITIES.md](CLI-CAPABILITIES.md) là snapshot 2026-11-01, thiếu Qoder/Antigravity/CommandCode hiện đã có trong registration. [INTEGRATIONS.md](INTEGRATIONS.md) trước audit cũng thiếu ba provider này. Không suy ra chúng chưa cài hay đã login từ snapshot cũ.
- **P2:** ProcessRunner gộp stderr/stdout; JSONL có thể bị xen log. Chỉ Codex có tail retention đặc biệt; không có xác thực identity/version vendor chỉ bằng help chứa flag.

### Ollama HTTP

`OllamaProvider` hard-code `http://127.0.0.1:11434/`, không gửi Authorization, không dùng endpoint từ ProviderSettings. Redirect tắt; không đặt UseProxy=false như connector HTTP.

| Method/path | Body / phản hồi cần có |
|---|---|
| GET `/api/tags` | Không body; JSON `models:[{"name":"..."}]` |
| POST `/api/show` | `{"model":"..."}`; đọc `thinking.values` array có boolean false để cache khả năng `think:false`; non-2xx chỉ bỏ qua metadata |
| POST `/api/generate` | `model`, `prompt`, `stream:false`, `format:"json"` hoặc null, `think:false` hoặc null, `keep_alive:0`, `options:{temperature:0,num_predict:1024,num_ctx:4096}`; JSON `response` string |

Detect có timeout 5 giây; generate theo cancellation caller và prompt <=8.000 ký tự. Worker chỉ cho TextOnly Classification/Extraction, context/request <=7.500 và kiểm JSON kết quả; lỗi API/parse chuyển Unavailable, JSON nghiệp vụ không hợp lệ chuyển Validation. Không tải model.

- **P1, lệch tài liệu chắc chắn:** INTEGRATIONS từng nói có thể cấu hình local endpoint, nhưng code luôn dùng địa chỉ cố định trên.
- **P2:** schema `thinking.values` không được tài liệu Ollama cục bộ xác nhận; nếu metadata khác, cache false và gửi think:null. Chưa thể kết luận tối ưu thinking tương thích phiên bản hiện có.
- **P2:** không đọc error envelope, không giới hạn kích thước body; không phân loại auth/rate limit riêng. Không kiểm field `done`, chỉ lấy `response`; tương thích server cần JSON không streaming như đã yêu cầu.

## Kết luận phạm vi và việc còn cần xác minh

Đã đối chiếu toàn bộ operation khai báo trong `ToolExecutionService.Capabilities` với dispatch, bốn skill connector và hai bridge; đồng thời kiểm các provider được đăng ký. Các mục P1/P2 là phát hiện tĩnh hoặc giả định tương thích có ghi rõ mức bằng chứng, không phải lỗi backend đã tái hiện. Chưa có error-code catalog/schema đầy đủ Pixel/Rikerat; cần tài liệu backend đúng phiên bản để khép phần chứng nhận hợp đồng server. Báo cáo đã ghi khoảng trống này thay vì tạo schema thay thế.

Không sửa application source và không thực hiện bước cập nhật tích hợp của mục tiêu nền. Đề xuất thứ tự cho bước triển khai riêng: bảo đảm artifact Pixel bắt buộc và xử lý state; tách result bridge khỏi log; xác thực schema marketing/body; rà soát provider fallback/quyền CLI; đồng bộ endpoint Ollama và tài liệu; sau đó mới chạy contract fixtures và kiểm backend dưới quyền được cấp.
