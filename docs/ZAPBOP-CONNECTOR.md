# ZapBop Music Producer connector

Added 2026-11-11. Connector id `music`, seeded profile `zapbop` (disabled until you enable it).

## What ZapBop actually offers

ZapBop (`D:\Projects\MusicProducer`, built as `build\ZapBop.exe`) is a Python 3.12 Tkinter/CustomTkinter desktop app. `main.py` only opens the window: there is **no CLI, HTTP API, MCP server, token or account**. Its stable, inspectable interface is the Python engine its own Professional tab uses:

| Function | Use |
|---|---|
| `pro_engine.Project.load(path)` | Read and strictly validate a saved project (`.json`, `"format": "zapbop-pro"`, `"version": 1`) |
| `pro_engine.render_project(project.to_dict())` | Render to stereo float32 at 44.1 kHz |
| `audio_engine.write_wav(path, audio)` | Write 16-bit PCM WAV |

The connector uses only these functions, through the manager-owned bridge `connectors/zapbop_bridge.py`. It works the same way as the TrailerForge bridge.

## Operations

| Operation | Approval | What it does |
|---|---|---|
| `status` | none (read-only) | Imports ZapBop's engine in the configured Python. Returns the numpy version, sound names and sample rate. Takes `{}`. |
| `inspect` | none (read-only) | `{"projectFile": "...json"}` → title, BPM, key/scale, bars, duration, instruments, patterns, layers. |
| `export` | exact-input approval + **Allow writing/exporting files** | Renders the project to `<data>/artifacts/tool-runs/<run id>/<name>_<bpm>bpm.wav`. Returns `artifactPath`, `artifactSha256`, channels, frames and bytes. The result waits in NeedsReview. |

Limits and safety:

- `projectFile` must resolve (after following links) inside the profile's **Allowed workspace**, be a `.json` file and be at most 5 MB. ZapBop's own validation errors are shown as ZapBop wrote them (in Vietnamese).
- Export refuses empty songs and songs longer than 15 minutes. That is a manager-side memory cap; ZapBop's UI has none.
- Exports are written only to a new per-run folder. Project files are never modified or overwritten, and an existing output file is never replaced.
- Python runs with `-I` (isolated): the working folder and `PYTHON*` variables are not loaded. The manager's process runner already strips AI API-key variables. No package is installed.
- The bridge forces UTF-8 on stdin/stdout/stderr. ZapBop layer names are Vietnamese, and piped Windows streams otherwise fall back to the ANSI code page.

**Not exposed on purpose:** ZapBop's AI composer (`ai_composer.generate_music` / `generate_project`). It starts external Codex/Claude CLIs itself, outside the manager's routing, call budgets and approval gates. Composing new music therefore stays in the ZapBop app. The planner prompt and the `employee-music` skill both say there is no text-to-music. Live playback (`sounddevice` stream) and SFX synthesis are UI features and are not exposed either.

## Setup

Settings → Tools → select **ZapBop Music Producer**:

- **Source folder**: `D:\Projects\MusicProducer` (must contain `main.py`, `pro_engine.py`, `audio_engine.py`).
- **Python/executable**: `D:\Projects\MusicProducer\.venv\Scripts\python.exe`. If you leave it blank, the source folder's `.venv` is used. Its packages (numpy, sounddevice, soundfile, customtkinter) must already be installed.
- **Allowed workspace**: the folder that holds your saved ZapBop `.json` projects. When a task names ZapBop and the field is blank, it defaults to the task's project root.
- **Windows app (.exe)**: `D:\Projects\MusicProducer\build\ZapBop.exe` for **Open Windows app**.
- Endpoint, token and backend fields are not used: there is no backend to start and no credential to store.
- Enable the connector. Enable **Allow writing/exporting files** for `export`. Model calls and publishing are not needed.

**Check connection** runs `status` (up to 30 s) and reports "Engine imports OK" plus the numpy version. It reads no project and opens no audio device.

## Use in tasks

- **Tool task form**: choose ZapBop, pick *Export to WAV* or *Inspect project*, and browse to the saved `.json`. The file must be inside the allowed workspace.
- **Natural-language tasks**: name the tool, e.g. "dùng ZapBop xuất bài nhạc …", "use the music producer tool …". Mentions inside quotes or examples, and negated mentions, are ignored, as for the other tools.
- **Workflows**: `{"$from": "music.artifactPath"}` passes the WAV to a later step, e.g. an AI integration step that copies it into a game under WriteProject.

## Verification performed

- The bridge was run against the real ZapBop source and `.venv` in a temporary folder with ZapBop's own `demo_project()`. It produced `status` OK; `inspect` OK, including Vietnamese layer names after the UTF-8 fix; `export` → 2-channel, 16-bit, 44.1 kHz, 8.73 s WAV. A second export to the same folder was refused, as were a file outside the workspace and an invalid project (ZapBop's BPM validation message). The temporary folder was deleted.
- Checks added: `RoutingChecks` covers alias routing and a fake-runner execution test (approval gate, isolated args, run-folder output, hash, bridge error text, missing WAV). These checks and the C#/WPF changes were **not compiled or run** in this step, and the app UI was not launched.
