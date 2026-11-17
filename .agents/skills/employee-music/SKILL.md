---
name: employee-music
description: Make game audio with ZapBop Music Producer - sound effects from presets, new music composed from a description, and WAV/OGG renders of saved or composed projects - and pass the audio files to later tasks.
---

# ZapBop audio

ZapBop is a music app with its own API (`zapbop_api.py`, see ZapBop's `API.md`). The manager calls that API through its bridge; every file goes into a new manager run folder (`artifacts/tool-runs/<run id>/`) and is never written into the project by the tool. An integration step copies the files into the game afterwards.

Operations:

- `sfx`: synthesizes ONE sound effect, no AI involved. Input: `{"kind": "Coin", "name": "coin_pickup", "format": "WAV"}`. `kind` is a ZapBop preset: Jump, Laser, Explosion, Coin, Power Up, Level Up, Shoot, Hit, Hurt, Death, Blip, Confirm, Error, Magic, Teleport, Alarm, Footstep, Swoosh, or `Random` with a `seed`. Optional shaping: `pitch` (Hz), `decay` (seconds), `distortion`, `slide`, `vibrato`, `attack`, `tone` (0-1, lower is darker), `echo` (0-1). Omitted values use the preset. Pick the closest preset per cue and shape it (for example a soft UI tap: `Blip` with lower `tone`; watering: `Swoosh`; purchase success: `Confirm` or `Coin`; wrong answer: `Error`).
- `compose`: composes a NEW multi-layer song from a description with ZapBop's AI composer, saves the project (`projectFile`) and renders it. Input: `{"description": "calm looping garden music, soft guzheng and light percussion", "seconds": 60, "mode": "loop", "name": "garden_bgm", "format": "OGG"}`. Optional: `bpm`, `bars` (instead of `seconds`), `root` + `scale` (omit both to let the AI choose), `genre`, `mood`, `density`, `provider` (`Claude`, `Codex` or `Qoder`; default Claude), `model`. This uses an AI CLI and can take several minutes; it counts as a model call, so the tool needs "Allow model calls" turned on in Settings > Tools. When it is off, plan `sfx` steps for the effects and report that music composition needs that permission; do not plan `compose`. A result with `partialError` was only partly composed: say so.
- `render`: renders a saved ZapBop project, or the `projectFile` a `compose` step returned, to WAV/OGG. Input: `{"projectFile": "<path>", "mode": "once", "format": "WAV"}`. `mode`: `loop`, `once`, `stems` (one file per layer) or `variants` (one file per variant); `variant` picks one variant.
- `inspect` / `export`: validate and summarize, or render to WAV, a saved ZapBop Professional project (`.json` with `"format": "zapbop-pro"`) inside the approved workspace.
- `status`: checks that ZapBop's engine and API import. Takes `{}`.

Formats: `WAV` (16-bit, 44.1 kHz) or `OGG`. For Unity, OGG suits long music and WAV suits short effects.

Planning: one tool step per sound or song (one call per output). For several cues, the preparation step returns `{"toolInputs":[{...},{...}]}` with one entry per sound; the manager makes one tool step per entry. A missing saved project is not a blocker: make new audio with `sfx` and `compose`. Use `inspect`/`export`/`render` of an existing project only when the user's project already has ZapBop files to reuse.

For input preparation, return only `{"toolInput":{...}}` or `{"toolInputs":[...]}`. Never invent a `projectFile` path; for `render` use a real saved file or the `projectFile` returned by a `compose` step.

Results carry `artifactPath` and `artifactSha256` (and `artifactPaths` / `artifactSha256s` for stems or variants). Pass them to integration steps through declared dependencies. A rendered file proves ZapBop made it, not that it sounds right: say that nobody listened to it. If ZapBop's source, its Python environment or its API module is missing, report the exact missing piece. Do not install packages or change ZapBop's files.
