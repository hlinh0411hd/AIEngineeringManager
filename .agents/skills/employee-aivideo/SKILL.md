---
name: employee-aivideo
description: Make videos with AIVideoStudio - a new MP4 from a topic, a script or the project's own images and clips with its AI video generator - check its setup, or inspect and export a saved AIVideoStudio project, and pass the video to later tasks.
---

# AIVideoStudio video

The manager runs AIVideoStudio's own code through its bridge (`connectors/aivideostudio_bridge.py`), without opening the app. Every generated video goes to a new manager run folder (`artifacts/tool-runs/<run id>/`); nothing is written into the project by the tool. An integration step copies the MP4 where it belongs.

Operations:

- `status`: input `{}`. Checks the source, Python, Pillow and FFmpeg; starts no job and writes nothing. Reports `missing` modules.
- `generate`: makes a new MP4 with AIVideoStudio's AI video generator. It counts as a model call (the script is written by the AI CLI configured in AIVideoStudio). Input fields:
  - `mode`: `text` (from `topic`, plus an optional `brief`), `script` (from your `script`, kept word for word), or `assets` (edits the images and clips in `asset_folder`, a folder inside the project).
  - `title`, `duration` (5-600 seconds), `format` (`1080p`, `720p`, `vertical`, `square`), `language` (`vi`, `en`, `ja`, `ko`, `zh`, `es`, `fr`, `de`, `pt`, `id`, `th`, `ru`), `style` (`explainer`, `story`, `promo`, `trailer`, `short`, `highlight`).
  - Voice: `voice` (`auto`, `edge`, `piper`, `sapi`, `none`), `voice_id`, `voice_rate`; subtitles: `subtitle_style` (`clean`, `boxed`, `impact`, `none`); `music_mode`, `music_volume`, `media_pref`, `still_motion`.
  - `media`: free sources to switch on for this video, for example `{"commons": true}`.
  - Example: `{"mode": "text", "topic": "A 30-second promo for my game", "duration": 30, "format": "1080p", "style": "promo", "language": "en"}`.
- `inspect` / `export`: read or render a saved AIVideoStudio project (`projectFolder` containing `project.json`) inside the approved workspace.

Paid services: AIVideoStudio asks before any paid call; the manager always refuses, so it uses free providers and the budget is 0. Without Pexels/Pixabay keys, `text` and `script` videos fall back to plain backgrounds (see `scenesWithFallback` and `unavailable`).

For input preparation return only `{"toolInput":{...}}`, or `{"toolInputs":[...]}` for several videos. Never invent paths: `asset_folder` and `projectFolder` must be real folders in the project.

Results carry `artifactPath`, `artifactSha256`, `artifactBytes`, `projectFolder`, `jobId`, `title`, `subtitlesPath`, `sourcesPath`, `scenes`, `scenesWithMedia`, `scenesWithFallback`, `unavailable`, `warnings` and `costUsd`. A rendered MP4 proves AIVideoStudio made it, not that it looks right: say that nobody watched it. If a Python module (Pillow, FFmpeg) is missing, report the exact missing piece; do not install software.
