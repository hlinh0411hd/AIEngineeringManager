---
name: employee-video
description: Make videos with TrailerForge - a new MP4 from a topic, a script or the project's own images and clips with its AI video generator - or inspect and export a saved TrailerForge project, and pass the video to later tasks.
---

# TrailerForge video

The manager runs TrailerForge's own code through its bridge, without opening the app. Every video goes to a new manager run folder (`artifacts/tool-runs/<run id>/`); nothing is written into the project by the tool. An integration step copies the MP4 where it belongs.

Operations:

- `generate`: makes a new MP4 with TrailerForge's AI video generator. It counts as a model call (the script is written by the AI CLI configured in TrailerForge). Input fields:
  - `mode`: `text` (from `topic`, plus an optional `brief`), `script` (from your `script`, kept word for word), or `assets` (edits the images and clips in `asset_folder`, a folder inside the project).
  - `title`, `duration` (5-600 seconds), `format` (`1080p`, `720p`, `vertical` for Shorts/Reels/TikTok, `square`), `language` (`vi`, `en`, `ja`, `ko`, `zh`, `es`, `fr`, `de`, `pt`, `id`, `th`, `ru`), `style` (`explainer`, `story`, `promo` for games and products, `trailer`, `short`, `highlight`).
  - Voice: `voice` (`auto`, `edge` free online voices, `piper`, `sapi`, `none`), `voice_id`, `voice_rate`; subtitles: `subtitle_style` (`clean`, `boxed`, `impact`, `none`).
  - `media`: free sources to switch on for this video, for example `{"commons": true}` (Wikimedia Commons) or `{"ai_image": true}` (images made by the image AI configured in TrailerForge).
  - Example: `{"mode": "assets", "asset_folder": "<project>/Marketing/Screenshots", "title": "Honk and Bonk", "duration": 30, "format": "vertical", "style": "promo", "language": "en"}`.
- `inspect` / `export`: read or render a saved TrailerForge project (`projectFolder` containing `project.json`) inside the approved workspace.

Media: stock footage (Pexels, Pixabay) needs API keys entered in TrailerForge itself; without them a `text` or `script` video gets plain coloured backgrounds (the result lists `scenesWithFallback` and `unavailable`). For a game, app or product video prefer `assets` with a folder of real screenshots, key art or gameplay clips from the project. Use finished images, not sprite sheets or animation strips (a sheet is shown as one still picture). When the right images do not exist yet, plan the steps that make them (screenshots, key art with the pixel art tool) before the video.

Paid services: TrailerForge asks before any paid call; the manager always refuses, so it uses free providers. Results report `costUsd` (normally 0).

For input preparation return only `{"toolInput":{...}}`, or `{"toolInputs":[...]}` for several videos (one call each, for example a 16:9 trailer and a 9:16 short). Never invent paths: `asset_folder` and `projectFolder` must be real folders in the project.

Results carry `artifactPath`, `artifactSha256`, `artifactBytes`, `projectFolder` (the TrailerForge project made for it, which opens in TrailerForge for manual edits), `title`, `scenes`, `scenesWithMedia`, `scenesWithFallback`, `unavailable` and `warnings`. Pass them to integration or marketing steps through declared dependencies. A rendered MP4 proves TrailerForge made it, not that it looks right: say that nobody watched it, and report scenes that fell back to plain backgrounds. If TrailerForge's source, its Python environment or a library is missing, report the exact missing piece; do not install software.
