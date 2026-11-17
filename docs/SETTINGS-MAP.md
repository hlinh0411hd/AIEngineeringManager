# Central settings map

2026-11-02 — source-only UI reorganization. No build, tests, runtime UI preview, AI calls or database changes were performed.

Settings now has category tabs down its left side:

| Tab | Contents |
|---|---|
| Chung & dữ liệu | Current database location and existing data-folder change action. |
| Projects | Register/scan project folders; project name and aliases for registration; select an existing project; indexing/keyword actions; saved project memory. The selector follows the Projects list selection in both directions. This does not add an unsupported edit-existing-project API. |
| Tasks | Task retention; planning policy/coordinator defaults; duration, call/step/delegation budgets; delegation, continuation, Away and batch-test defaults; idle-to-Away; shortcut to confirmed intent learning for the selected task. |
| Agents & tài nguyên | Maximum parallel agents, adaptive free-RAM intake and RAM reserve. Its save action changes only agent/resource settings. |
| AI & Models | Automatic/SemiAutomatic/Manual routing; attempt/switch/time limits; Economy/Balanced/Performance preference and historical model scoring; global daily call budget; provider detection/setup, executable, enable/write permission, priority; model IDs, allowed kinds, quality/cost ranks and blocked status; measured performance table. Separate saves do not silently apply an unsaved agent limit. |
| Local AI | Installed Ollama model selection for qualification, standard/advanced evaluation actions and explicit GPU metadata read. Provider/model eligibility remains in AI & Models. Results and actual reported usage remain visible in the Local AI workspace. |
| Tools | Single authoritative connector editor: source/workspace/Python/native app paths, endpoint, protected token, enabled state, write/model-call/publishing permissions and timeout; profile creation/save/connection inspection. The Tools workspace keeps a tool selector and execution/review controls. |
| Workflows | Explains shared connector permissions and links to their Tools settings and the workflow workspace. Existing per-workflow steps, inputs, dependencies and continuation policy remain part of the saved work plan. There are no newly invented global workflow switches. |
| Kiểm thử | Existing approved project test-suite editor and its execution/history panel. |
| Thông báo | Existing Windows notification preference. |
| Cập nhật | Existing local-package update policy and link to the reviewed-update operation. Installed target executable paths are configured under Tools. No new automatic download/build settings were invented. |

Projects, Tasks, Agents, Providers, Local AI and Tools/Workflows/Updates have contextual links to the relevant settings category. The original first six main-tab positions and all existing named controls/event handlers are retained. Settings controls are moved, not duplicated; connector settings have their own visual host and do not share one WPF element between two parents.

Task-specific project/type/access/priority, schedule, AI override, workflow selection, Stop/Switch/Approve/Extend actions and task-list sorting/filtering remain in the work view. They are inputs or actions for the selected task/view, rather than shared application defaults. Project registration and memory editors, provider/model editors, connector editors and shared defaults now have Settings as their home.

Existing persisted values and schema are unchanged by this reorganization. The source was inspected for markup structure, preservation of named controls and event-handler references; this does not establish compilation success or visual/runtime correctness. The executable remains unchanged until the user invokes Build.bat.
