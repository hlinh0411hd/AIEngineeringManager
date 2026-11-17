# Design brief — Hoạt động ngẫu nhiên của nhân viên trong Virtual Office

Phạm vi: chỉ thiết kế, chưa có code. Mọi số dòng lấy từ `src/Employee.Desktop/VirtualOfficeView.cs` (VOV) và `src/Employee.Core/VirtualOffice.cs` (VO) tại thời điểm viết; hãy kiểm tra lại trước khi sửa.

## 1. Mục tiêu và nguyên tắc

- Nhân viên đang rảnh không đứng im: thỉnh thoảng đi report cho quản lý, ghé bàn đồng nghiệp, hoặc đi dạo hành lang, rồi về bàn.
- View vẫn **presentation-only** (comment VOV:13). Hoạt động ngẫu nhiên không được ảnh hưởng việc thực thi task.
- Task thật luôn được ưu tiên hơn hoạt động ngẫu nhiên. Ambient chỉ chạy khi actor rảnh hoàn toàn.
- Logic chọn và các luật nằm trong lớp thuần ở Core (test được, không phụ thuộc WPF). View chỉ lấy snapshot, thực thi route và pose.

## 2. Hiện trạng cần biết (đã đối chiếu code)

- 21 actor: Manager 1 ghế, 5 phòng × 4 ghế. Mỗi ghế có một `Actor` cố định (VOV:66). Sprite gán theo ghế.
- `Idle(actor)` = `Moving == null && Receiving == null` (VOV:789).
- `Movement` (VOV:83) gắn chặt với `OfficeTransition` + `ActionId` của `ActorActionQueue`. Queue này có persist, FIFO mỗi ghế và timeout 90s. **Ambient không được đi qua queue này**, vì sẽ bị lưu xuống DB, khôi phục sau restart và bị timeout ép kết thúc.
- `Finish()` (VOV:902) gọi `actions.Complete(actor.Key, moving.ActionId)` và luôn `Place(actor, Home)`.
- `ZoneActor(zone)` (VOV:724) lấy actor đầu tiên của phòng làm đại diện nhận handoff/report. Nếu actor đó đang đi dạo thì sai, cần sửa.
- `AssignSeats` (VOV:705) gán work vào ghế trống bất kỳ trong phòng. Đúng ý "người bất kỳ", nhưng chưa biết ghế đó có người hay không.
- `Path()` (VOV:753) là BFS cell→cell, dùng được cho mọi đích. `CanStep` (VOV:509) chặn xuyên tường sau (cạnh x<X0 hoặc y<Y0 của phòng), nên phòng mở ở cạnh phải và cạnh dưới.
- Layout phòng 6×6 (`Slot`, VOV:522): người làm ở cell `worker`, bàn ở `desk = worker + (1,0)`, người ngồi phía sau-trái bàn.
- Hành lang: các cell không thuộc phòng nào (`RoomAt == null`). Rộng 2 tile: x∈{7,8,15,16}, y∈{7,8}, cộng viền x=0, y=0 và các cell cuối map. Không có waypoint định nghĩa sẵn.
- Z-order theo Y trong `Place` (VOV:500), nên hai người đứng gần nhau sẽ tự che đúng thứ tự.
- Pose hiện có đủ dùng: `Idle`, `Reporting`, `Walking`, `WalkingBack`. **Không cần thêm clip sprite.** `RowFor` (VOV:924) cho Pose.Idle khi `StandTicks>0` và Pose.Reporting khi `Ticks>0`.

## 3. Mô hình state

State là enum mới `AmbientState`, lưu trên `Actor`. Không thêm vào enum `Pose`, vì `Pose` chỉ là hình hiển thị.

| State | Ý nghĩa | Pose hiển thị |
|---|---|---|
| `AtDesk` | Ở bàn của mình (state mặc định, gồm cả lúc làm task) | Working/Idle như hiện tại |
| `MovingTo` | Đang đi ra đích của hoạt động | Walking/WalkingBack theo hướng |
| `ReportingToManager` | Đứng tại điểm report cạnh bàn quản lý | Reporting (dùng `Ticks`) |
| `VisitingColleague` | Đứng sau bàn đồng nghiệp | Reporting (nói chuyện, dùng `Ticks`, quay mặt về chủ bàn) |
| `HallwayStroll` | Dừng ngắn tại waypoint hành lang | Idle (dùng `StandTicks`) |
| `Returning` | Đang đi về bàn | Walking/WalkingBack |

Chuyển state:

```
AtDesk --(scheduler chọn, qua mọi ràng buộc)--> MovingTo
MovingTo --(tới điểm cuối chặng ra)--> ReportingToManager | VisitingColleague | HallwayStroll
HallwayStroll --(còn waypoint)--> MovingTo (waypoint kế)   --(hết)--> Returning
Reporting/Visiting --(hết dwell, hoặc bị recall)--> Returning
Returning --(tới Home)--> AtDesk (đặt cooldown)
```

State chỉ tồn tại ở phía view và không persist. Khi restart hoặc `RebuildOffice` thì mọi actor về `AtDesk`.

## 4. Ràng buộc (luật cứng)

Mọi luật được kiểm tra **tại thời điểm chọn**, trên trạng thái giả định "sau khi actor này rời bàn". Mỗi tick chọn tối đa 1 actor, và `actor.Moving` được set ngay trong cùng tick, nên không có race giữa hai lần chọn.

Định nghĩa dùng chung:
- **Present** = actor ở bàn: `Moving == null` (hoặc `Moving.Timed`), `AmbientState == AtDesk`. Actor đang `Returning` chưa tính là Present cho tới khi tới Home.
- **Away** = actor không Present, kể cả khi đang chạy movement của task (report/handoff).

1. **MAX_AWAY.** `awayCount(toàn văn phòng) + 1 <= MaxAway` (mặc định 3, có thể 4). Đếm cả movement của task. Movement của task không bị chặn bởi cap, nên ambient sẽ nhường: nếu task kéo `awayCount` lên thì ambient tạm thời không được chọn.
2. **Mỗi phòng luôn ≥ 1 Present.** Actor chỉ được rời nếu `presentCount(zone) - 1 >= MinPresentPerZone` (mặc định 1). Người cuối cùng không bao giờ được chọn. Người ở lại là ai thì tuỳ động, vì luật chỉ đếm. Thêm hai điều kiện:
   - Actor đang giữ work hiển thị (`WorkOf(actor) != null`, đặc biệt `Running`) không được rời. Nhờ vậy người đang làm việc không đi dạo.
   - Actor có `Receiving != null` hoặc lane `actions` còn pending/current (`PendingCount(seat) > 0 || Current(seat) != null`) thì không được chọn.
3. **Quản lý không bao giờ rời.** Zone `Manager` bị loại khỏi tập ứng viên rời bàn. Riêng `ReportingToManager`: người report chỉ đứng ở `ManagerReportSpots` (xem mục 6), **không bao giờ** vào cell `worker` của bàn quản lý (sau bàn). Mỗi spot chỉ cho 1 người; hết spot thì không chọn hoạt động report.
4. **Ghé bàn đồng nghiệp.**
   - Chủ bàn: actor `Zone != "Manager"`, `Zone != zone của khách`  (ưu tiên khác phòng; cho phép cùng phòng với trọng số thấp, xem `SameRoomVisitWeight`), đang Present, `Receiving == null`, chưa có khách khác.
   - Khách đứng ở cell `worker` của chủ bàn, lệch pixel `VisitorOffset` so với `Home` của chủ để hai người không chồng nhau. Hai người cùng đứng sau một bàn.
   - Khi `Receiving` được set, chủ bàn không còn `Idle`, nên không bị chọn đi đâu và task tới sẽ chờ (dwell ngắn, chấp nhận được).
5. **Hallway.** Chỉ đi qua `HallwayWaypoints` rồi về. Waypoint phải thoả `Walkable && RoomAt == null` và không nằm ở cell viền cuối map (x=MapWidth-1, y=MapHeight-1, các cell này vẽ tile cạnh).

Thêm luật phụ:
- Khi task cần ghế trong zone: `AssignSeats` ưu tiên ghế trống có actor Present (mục 7). Vì mỗi zone luôn còn ≥1 Present nên task luôn có người nhận.
- `ZoneActor(zone)` ưu tiên actor Present của zone, fallback actor đầu tiên.
- Task không bao giờ bị chặn bởi ambient. Chỉ có dwell của ambient được cắt ngắn khi cần (recall).

## 5. Scheduler (tần suất, cooldown, thời lượng)

Một lớp thuần ở Core. Thuật toán mỗi lần được gọi:

1. Bỏ qua nếu `now < nextDecisionAt` (mặc định thăm dò mỗi `DecisionIntervalTicks` ≈ 1,5 s; tick = 33 ms).
2. Tính `awayCount`, `presentByZone` từ snapshot. Nếu `awayCount >= MaxAway` thì thoát, đặt lại `nextDecisionAt`.
3. Lọc ứng viên: Idle, Present, không đang cooldown cá nhân, không giữ work, không phải Manager, qua luật mục 4.
4. Roll xác suất `AmbientChancePerDecision` (mặc định ~0,25) để quyết định có hoạt động nào không. Cách này giúp không phải lúc nào cũng đầy `MaxAway`.
5. Chọn actor ngẫu nhiên đều trong ứng viên. Chọn loại hoạt động theo trọng số (`ReportWeight`, `VisitWeight`, `HallwayWeight`), loại bỏ loại không khả thi (hết spot report, không có chủ bàn hợp lệ).
6. Trả về `AmbientPlan` (kind, actorKey, targetKey?, dwellTicks, waypoints). Plan được tạo mới hoàn toàn nên không cần persist.

Cooldown:
- Cooldown cá nhân: sau khi về bàn, `ActorCooldownMin..Max` giây trước khi actor đó được chọn lại.
- Cooldown toàn cục ngắn giữa hai lần xuất phát để tránh cả đám đi cùng lúc.
- Cooldown theo cặp (khách→chủ) để cùng một cặp không lặp lại liền.

Thời lượng (đều ngắn):
- Report: dwell 3–5 s. Visit: dwell 4–8 s. Hallway: 2–3 waypoint, mỗi chỗ dừng 0,5–1,5 s.
- Tổng một lượt tối đa `MaxActivitySeconds` (mặc định 25 s). Quá thì bị ép về (watchdog, xem mục 6).

Tính ngẫu nhiên: `Random` được inject (`Func<int,int>` hoặc `Random` có seed) để test xác định.

## 6. File / class / hàm cần thêm hoặc sửa

### 6.1 File mới: `src/Employee.Core/OfficeAmbient.cs`

- `enum AmbientState { AtDesk, MovingTo, ReportingToManager, VisitingColleague, HallwayStroll, Returning }`
- `enum AmbientKind { ReportToManager, VisitColleague, HallwayStroll }`
- `sealed record AmbientOptions` — toàn bộ hằng số cấu hình (mục 8) với giá trị mặc định, và `Validate()` kẹp giá trị hợp lệ (MaxAway ≥ 0, MinPresentPerZone ≥ 1...).
- `readonly record struct AmbientSeat(string Key, string Zone, bool IsManager, bool Present, bool Busy, bool Hosting)`
  - `Busy` = giữ work, lane còn action, hoặc `Receiving`.
  - `Hosting` = đang có khách.
- `sealed record AmbientPlan(AmbientKind Kind, string ActorKey, string? TargetKey, int DwellTicks, int[] WaypointIndexes)`
- `static class OfficeAmbientRules` — các hàm thuần để test riêng:
  - `bool CanLeave(AmbientSeat seat, IReadOnlyList<AmbientSeat> all, AmbientOptions o)` — gộp luật 1, 2, 3.
  - `bool IsValidHost(AmbientSeat visitor, AmbientSeat host)` — luật 4.
  - `int AwayCount(...)`, `int PresentCount(zone, ...)`.
- `sealed class AmbientScheduler(AmbientOptions options, Random random)`
  - `AmbientPlan? TryPlan(DateTimeOffset now, IReadOnlyList<AmbientSeat> seats, int freeReportSpots, int waypointCount)`
  - `void NotifyReturned(string actorKey, DateTimeOffset now)` — đặt cooldown cá nhân.
  - `void Reset()` — gọi khi rebuild office.

### 6.2 `src/Employee.Desktop/VirtualOfficeView.cs`

**Hằng số/dữ liệu mới**
- `HallwayWaypoints` (`static readonly (int X,int Y)[]`): ví dụ `(7,7) (8,8) (7,8) (15,7) (16,8) (8,7)`. Kiểm tra lúc `BuildWorkers`: bỏ waypoint nào không `Walkable` hoặc `RoomAt != null`, log nếu bị bỏ.
- `ManagerReportSpots` (offset tương đối so với `Manager.X0/Y0`): đề xuất `(3,4)` và `(2,4)`, tức phía trước/bên cạnh bàn quản lý (bàn ở `(3,3)`, quản lý ở `(2,3)`). Kiểm tra lúc build: phải `Walkable`, không phải cell `worker`/`desk`, không trùng prop. Nếu không đạt thì tìm cell Walkable gần nhất cách bàn ≤2 và không phải cell `worker`. Giá trị cuối cùng cần xác nhận trực quan khi chạy.

**`Actor`** (VOV:66): thêm `AmbientState AmbientState`, `AmbientKind? AmbientKind`, `DateTimeOffset CooldownUntil`, `Actor? Host` (khách ghi chủ bàn), `(int X,int Y)? ReservedSpot`, và `bool Away => Moving is { Timed: false } || AmbientState != AtDesk`.

**`Movement`** (VOV:83):
- Cho phép `Transition` nullable hoặc thêm constructor/factory `Movement.Ambient(...)`. Thêm `AmbientKind? Ambient`.
- `ActionId` rỗng cho ambient.
- Thêm `Queue<(int PointsLeft, int StandTicks)> Stops` để hỗ trợ nhiều điểm dừng (hallway). Với report/visit vẫn dùng cơ chế `Outbound`/`Ticks` sẵn có.
- `Reporting` (VOV:95) đọc `Transition.Activity`: phải null-safe cho ambient.

**`StartTransitions()`** (VOV:793): giữ nguyên cho task. Ngay sau vòng `foreach`, nếu ambient đang bật thì gọi `StartAmbient(now)`.

**`StartAmbient(DateTimeOffset now)`** (mới): build `AmbientSeat[]` từ `actors` (Present, Busy, Hosting), gọi `scheduler.TryPlan`, nếu có plan thì gọi `BeginAmbient`.

**`BeginAmbient(Actor actor, AmbientPlan plan)`** (mới, theo mẫu `Begin` VOV:807):
- `ReportToManager`: đích = spot đã reserve; route = `Stand(HomeCell)` → `Path(HomeCell, spot)` → dwell (`Ticks`) → `Path(spot, HomeCell)` → `Home`. Hướng nhìn về bàn quản lý. Giải phóng spot khi kết thúc.
- `VisitColleague`: đích = cell `worker` của chủ bàn. Cell cuối chặng ra đổi thành `host.Home + VisitorOffset`. Set `host.Receiving = movement` (như `Begin` dòng 830), `Target = host.Key` để `FaceCounterpart` hoạt động.
- `HallwayStroll`: route = `Stand(HomeCell)` → với mỗi waypoint `Path(prev, wp)` rồi `Stops` → `Path(last, HomeCell)` → `Home`. `Target = ""` để không quay mặt về ai.
- Set `actor.AmbientState = MovingTo`.

**`Move()`** (VOV:870): thêm cập nhật `AmbientState` khi hết chặng ra (sang Reporting/Visiting/Hallway), khi hết `Stops` (sang `Returning`). Với hallway dùng `StandTicks` cho dừng (pose Idle), không dùng `Ticks` (pose Reporting).

**`Finish()`** (VOV:902):
- Chỉ gọi `actions.Complete` khi `moving.ActionId` không rỗng (không phải ambient).
- Giải phóng `receiver.Receiving` (đã có, dùng `moving.Target`), giải phóng `ReservedSpot`.
- Đặt `AmbientState = AtDesk`, `scheduler.NotifyReturned(...)`.
- Vẫn `Place(actor, Home)`: hợp lệ vì route ambient luôn kết thúc bằng `Home`.

**`ReleaseStuckActions()`** (VOV:835): thêm watchdog riêng cho ambient (so với `MaxActivitySeconds`), gọi `Finish` nếu quá hạn. Hành động ambient không nằm trong `actions.Expired`.

**`ZoneActor()`** (VOV:724): ưu tiên `a.Zone == zone && Present(a)`, fallback actor đầu tiên.

**`AssignSeats()`** (VOV:705): khi chọn ghế trống, ưu tiên ghế có actor Present; ghế có actor đang Away chỉ được dùng khi không còn ghế khác, và khi đó gọi `RecallToDesk(actor)`.

**`RecallToDesk(Actor actor)`** (mới): khi actor đang dwell (ở `Ticks>0`/`StandTicks>0` của ambient), đặt dwell về 0 để chuyển ngay sang `Returning`. Khi đang đi ra thì **không** cắt route (đi thẳng tới đầu chặng về có thể xuyên tường); để route tự kết thúc.

**`RefreshOffice()`/`RebuildOffice` (nơi `BuildWorkers` được gọi lại)**: reset `AmbientState`, giải phóng spot, `scheduler.Reset()`, và dọn `Receiving` của ambient.

**`Animate()`** (VOV:852): không cần sửa cấu trúc. `StartTransitions()` đã gọi mỗi tick và `Move()` đã chạy cho mọi actor. Chỉ cần đảm bảo ambient bị tắt khi `readTasks == null` (đã có `return` ở đầu).

**`RowFor()`** (VOV:924): thường không phải sửa vì ambient dùng `Movement` non-Timed. Cần kiểm tra `FaceCounterpart` với `Target` rỗng trả về null (đã có `GetValueOrDefault`).

### 6.3 `src/Employee.Core/Domain.cs` — `ManagerSettings` (gần dòng 230)

Thêm `bool VirtualOfficeAmbientEnabled = true`. Các hằng số chi tiết dùng `AmbientOptions` mặc định. Nếu muốn chỉnh runtime, thêm `AmbientMaxAway` vào settings (ràng buộc 0..4), nhưng đây là tuỳ chọn.

### 6.4 UI Settings

Một checkbox "Office ambient activity" (text UI **tiếng Anh**, đúng quy ước app) cạnh tuỳ chọn `VirtualOfficeEnabled`. Chỉ cần nếu muốn người dùng tắt được. Vị trí cụ thể xem `docs/SETTINGS-MAP.md`.

### 6.5 Test: `tests/Employee.Checks/VirtualOfficeChecks.cs`

Thêm check cho phần logic thuần ở Core (không cần WPF):
- MaxAway: với 21 ghế, mô phỏng 10.000 lần `TryPlan`, `awayCount` không bao giờ vượt `MaxAway`.
- Mỗi phòng luôn ≥1 Present, kể cả khi chỉ còn 2 Present và cả hai đều ứng viên. Phòng 1 ghế (Manager) không bao giờ bị chọn.
- Actor `Busy`/giữ work không bao giờ bị chọn.
- Không bao giờ chọn chủ bàn thuộc `Manager`, chủ bàn đang `Hosting`, hoặc chủ bàn không Present.
- Cooldown: actor vừa về không được chọn lại trước `CooldownUntil`.
- Với `Random` có seed, kết quả lặp lại được.
- Kiểm tra dữ liệu: mọi `HallwayWaypoints` và `ManagerReportSpots` hợp lệ (nếu các hằng được đưa ra ngoài view hoặc có hàm kiểm tra thuần).

## 7. Tích hợp với luồng task hiện có

- Task movement (`Begin`, qua `ActorActionQueue`) giữ nguyên. Nó chỉ bắt đầu khi sender/receiver `Idle`, nên task chỉ phải chờ khi actor đang ambient. Thời gian chờ bị chặn trên bởi dwell ngắn + watchdog.
- Ambient không `Enqueue` vào `ActorActionQueue`, nên không có persist, không có timeout 90s, không bị `InterruptAll`/`Restore` ảnh hưởng.
- Chống va chạm: ambient không chọn actor có lane pending; task chỉ start cho actor `Idle`. Hai bên đều dựa trên cùng `Idle()`.
- Giới hạn đã biết: `MaxAway` và "phòng luôn ≥1 người" chỉ được đảm bảo tuyệt đối cho ambient. Movement của task (report/handoff thật) vẫn có thể làm thêm người rời bàn. Đó là hành vi hiện có, và scheduler ambient sẽ nhường khi cap đã đầy. Nếu cần ép cả task theo luật này, thêm điều kiện vào lambda `canStart` của `TryStartNext` (VOV:802), nhưng khi đó phải có giới hạn chờ để không làm trễ task.

## 8. Hằng số cấu hình cần expose (trong `AmbientOptions`)

| Hằng số | Mặc định | Ý nghĩa |
|---|---|---|
| `Enabled` | true | Bật/tắt toàn bộ ambient |
| `MaxAway` | 3 (cho phép 4) | Số người vắng bàn tối đa toàn văn phòng |
| `MinPresentPerZone` | 1 | Số người tối thiểu ở bàn mỗi phòng |
| `DecisionIntervalTicks` | ~45 | Chu kỳ scheduler thăm dò |
| `AmbientChancePerDecision` | 0,25 | Xác suất bắt đầu hoạt động mỗi lần thăm dò |
| `ReportWeight` / `VisitWeight` / `HallwayWeight` | 3 / 4 / 3 | Trọng số chọn loại hoạt động |
| `SameRoomVisitWeight` | 0,3 | Hệ số giảm khi ghé bàn cùng phòng |
| `ActorCooldownMinSeconds` / `Max` | 20 / 45 | Cooldown cá nhân sau khi về bàn |
| `GlobalCooldownSeconds` | 3 | Cách nhau tối thiểu giữa hai lần xuất phát |
| `PairCooldownSeconds` | 60 | Không lặp lại cùng cặp khách→chủ |
| `ReportDwellMinTicks` / `Max` | ~90 / ~150 | Thời gian đứng report |
| `VisitDwellMinTicks` / `Max` | ~120 / ~240 | Thời gian đứng sau bàn đồng nghiệp |
| `HallwayWaypointMin` / `Max` | 2 / 3 | Số waypoint mỗi lượt |
| `HallwayDwellMinTicks` / `Max` | ~15 / ~45 | Dừng tại mỗi waypoint |
| `MaxActivitySeconds` | 25 | Watchdog ép về |
| `VisitorOffset` | khoảng (-46, 8) px | Độ lệch của khách so với chủ bàn |
| `HallwayWaypoints` | xem 6.2 | Danh sách cell hành lang |
| `ManagerReportSpots` | xem 6.2 | Điểm đứng report quanh bàn quản lý |

Ghi chú: các giá trị tick tính theo timer 33 ms (≈30 tick/giây). Các con số trên là điểm khởi đầu, cần chỉnh theo cảm nhận khi chạy thực tế.

## 9. Rủi ro và điểm cần xác nhận khi chạy

- `ManagerReportSpots`, `HallwayWaypoints` và `VisitorOffset` cần xem trực quan: prop trong phòng quản lý có thể chiếm cell, và offset có thể làm sprite chồng lên bàn.
- Phòng Manager chỉ có 1 ghế và ở góc, nên đường tới spot report phải đi qua cạnh phải/dưới (cạnh trên/trái là tường). `Path()` xử lý được, nhưng cần xác nhận không có cell bị chặn.
- Nếu 14 sprite lặp qua 21 ghế, hai nhân viên cạnh nhau có thể trùng nhân vật. Đây là hạn chế đã có, không thuộc phạm vi này.
- Vì ambient chỉ chọn actor không giữ work, khi mọi ghế của một phòng đều có work thì phòng đó không có ai đi dạo. Đó là hành vi mong muốn.
- Chưa build hay chạy app trong bước này. Mọi tên hàm/số dòng cần kiểm tra lại khi bắt đầu code.

## 10. Cập nhật thực thi: giao task theo "vị trí bất kỳ" (2026-11-08)

Bổ sung sau khi ambient đã chạy được; mục này thay các bullet cũ về `AssignSeats` (mục 2, mục 4 "luật phụ", mục 6.2 — trong đó `RecallToDesk` không cần nữa vì task chờ người về bàn thay vì gọi lại người đang đi):

- **Giao theo người đang ở bàn, không theo ghế/Kind.** Task đến phòng nào được giao cho nhân viên BẤT KỲ đang ở tại bàn của phòng đó lúc đó và chưa giữ work nào, chọn ngẫu nhiên trong số họ (`VirtualOffice.PickAssignee`, `src/Employee.Core/VirtualOffice.cs`). Toàn bộ ưu tiên cố định cũ (ghế khớp `Kind`, ghế trống đầu tiên, fallback dồn work lên ghế đầu phòng) đã bị bỏ.
- **Chờ khi phòng tạm vắng.** Nếu không còn ai rảnh tại bàn (hiếm nhờ bất biến `MinPresentPerZone = 1`), task không được gán cho ai: không ai hiển thị hay làm nó (`WorkOf` không còn fallback), và `Animate` thử lại khoảng mỗi giây (`animationFrame % 30`) cho tới khi có người về bàn — người về đầu tiên nhận.
- **Người nhận task rời khỏi pool ambient.** Người đang giữ work là `Busy` trong `AmbientSeats`, nên `OfficeAmbientRules.CanLeave` không bao giờ chọn họ; vì việc gán chỉ xảy ra khi họ đang `AtDesk`, họ đứng tại bàn đến khi task xong (cộng cửa sổ hiển thị kết quả 10 giây).
- **Handoff/delegation cũng chờ người ở bàn.** `ZoneActor` ưu tiên người đang `Idle && AtDesk` và `canStart` yêu cầu người nhận `Idle`, nên đường đi giao việc chỉ bắt đầu khi có người thực sự tại bàn.
- Kiểm thử: `AssigneeChecks` trong `tests/Employee.Checks/VirtualOfficeChecks.cs` — chỉ chọn người ở bàn và chưa giữ work, không ghim vào một người cố định, tái lập được với seed, và trả null (xếp hàng) khi phòng không còn ai rảnh tại bàn.
