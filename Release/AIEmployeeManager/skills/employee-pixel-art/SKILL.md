---
name: employee-pixel-art
description: Create, edit or animate game pixel art through the connected Pixel Art Tool; prepare API inputs and hand off generated PNG assets for game integration.
---

# Pixel Art Tool

Use the manager's `pixel` connector. Do not launch another AI CLI or call an image generator yourself. Inspect the selected game's art conventions before choosing sizes, palette, camera angle, transparent background and animation timing. Ask for an indispensable style/subject detail only if it cannot be inferred from the project and request.

## Input preparation

When preparing a tool step, return only `{"toolInput":{...}}`; do not execute the request. Use the already inspected project/dependency results instead of rereading unrelated files.

- `status` takes `{}` and checks Codex availability without generating.
- `generate` create: `{"action":"create","prompt":"specific requested subject and style","outputWidth":128,"outputHeight":128}`. Width/height are integers 16â€“512; choose the actual game size, not this example by default. Prompt is at most 4,000 characters.
- `generate` animation: `{"action":"animation","image":{"$imageFrom":"base"},"prompt":"specific looping action","frames":8,"direction":"S","fps":8}`. Source PNG is required; frames are 4, 8 or 12. Directions: N, NE, E, SE, S, SW, W, NW. The backend uses four columns and frames/4 rows. Preserve character identity, camera, consistent frame size and transparent padding.
- Editing supports action `edit`, `skin` or `direction` with an existing `image`. To restyle an image after a sample, send the sample as `styleGuide` (for example `{"$attachmentPng":"sample.png"}`); the tool keeps the pose of `image` and takes only style from `styleGuide`. Set `lockPalette` false when the colours should change to the sample's. The manager completes every request before sending it: missing or out-of-range fields get the tool's defaults or the nearest valid value (frames, fps, direction, sizes, switches, edit context), a missing prompt uses the step's request, and a missing source image is the image an earlier Pixel step in the chain produced, else the only attached PNG. Give only what matters for the result: action, image or reference, prompt, and any size/frames/fps the user asked for. Avoid `refine`/`detect` through generate; they produce text rather than the PNG this adapter expects.
- Reference a completed, declared pixel dependency as `{"$imageFrom":"stepKey"}`. For an existing PNG in the approved project use `{"$projectPng":"Assets/path.png"}` as the image value. The manager encodes bytes; never generate base64 in AI text. Project PNG references require at least ReadProject on this step. A user-uploaded PNG may be referenced with `{"$attachmentPng":"exact filename.png"}`; the manager reads the original task attachment. A reference on create is a style reference, not the subject to copy.

Animations, edits and restyles take the project's own original art as source (`{"$projectPng":"Assets/…/original.png"}`), never an earlier generated output or a result image posted on the task, unless that output was accepted as final. When the original is an atlas or sheet with several poses, do not animate the whole sheet: `create` a base pose of the one character with the sheet as `styleGuide`, then animate that pose. The new art must match the existing art's scale, outline, palette and style: the manager's tester compares them side by side.

One call makes one asset or one motion. When a step needs several (a character, two GUI icons, idle and peck animations), return `{"toolInputs":[...]}` in run order instead of one `toolInput`: the manager makes the first call in this step and splits the rest into steps keyed `<stepKey>.2`, `.3`, …; a later call animates an earlier one's image with `{"$imageFrom":"<stepKey>"}` (first call) or `{"$imageFrom":"<stepKey>.2"}`. A `create` whose prompt asks for frames and has no source image is split by the manager itself: base pose first, then one `animation` per motion named in the prompt.

Example flow: AI inspect/prepare create -> Pixel generate base -> AI prepare animation -> Pixel animate using base -> AI integrate. Both the preparation step and animation tool step must declare any base dependency whose output they use. InputFrom is the preparation key; image references retain the original plan key.

## Result and integration

The tool returns artifactPath, artifactSha256 and result metadata including columns/rows. Reuse these results; never assume an output path before completion. Check the PNG and sheet dimensions, frame order, transparency and looping in the game when permitted. The integration AI may copy approved artifacts and update import/slicing/animation code only with WriteProject access. Record frame timing and destination paths. Generation success is not proof of visual quality; report remaining review clearly. Do not mark tests passed without running them under the task's testing policy.

The backend uses its own Codex session and can consume usage. Reuse a suitable existing image, edit only the requested detail and avoid repeated generation without a concrete defect. Tool availability/login errors are blockers, not permission to substitute another generator.
