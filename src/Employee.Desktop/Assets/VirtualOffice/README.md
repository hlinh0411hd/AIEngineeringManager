# Virtual Office assets

`desk.png` is the generated isometric workstation used by each desk.
`office-characters.png` is the original generated six-character atlas;
`office-characters-cutout.png` removes its translucent background and is sliced
into six equal cells by `VirtualOfficeView`, in zone order. Both were generated
with the built-in image tool and remain available as fallback sprites.

`development-animation.png`, `research-animation.png`, `creative-animation.png`
and `qa-animation.png` are generated four-column, ten-row sheets consumed by
`VirtualOfficeView`. The working row depicts coding, reading, drawing and
checklist review respectively. Idle, walking, working, error and waiting rows
are selected, along with talking, receiving, delivering, reporting and happy
frames during interactions. Built-in
imagegen prompts requested one consistent chibi employee per sheet, four frames
each for idle, walking, the zone's work, talking, receiving, delivering,
reporting, happy, error and waiting, on a transparent background without text.

`manager-animation.png` is a generated four-column, ten-row sprite sheet.
The scene slices it into equal cells and currently uses idle, walking and
keyboard and interaction rows for Manager. Built-in
imagegen prompt: "A single chibi office employee in a 4 by 10 grid, four
frames each for idle, walking, keyboard work, talking, receiving, delivering,
reporting, happy, error and waiting; pixel art on a transparent background,
no furniture or text."

`tile-atlas.png` was generated with the built-in image tool: three horizontal
cells contain wood floor, stone walkway and a planter. `VirtualOfficeView` crops
the first two cells for the isometric map and the planter for obstacles. Prompt:
"pixel art isometric office floor tile atlas on transparent background: three
separate equal-sized diamond tiles in one horizontal row: warm wood floor,
lighter stone walkway, and a solid planter obstacle with leafy plant; crisp
pixel edges, no text or characters". PNGs are copied to the application output
by the desktop project. The map uses isometric projection and searches around
desk and planter cells; the artwork contains transparent padding, so its final
visual alignment still needs an interactive check.

`external-animation.png` is a generated four-column, ten-row robot sheet,
used for External Tools idle, walking, work, error and waiting frames. Built-in
imagegen prompt: "A chibi robot office employee in a regular 4 by 10 grid,
four frames each for idle, walking, typing, talking, receiving, delivering,
reporting, happy, error and waiting; pixel art, transparent background,
no labels or furniture." Interaction rows are selected in the same way as the
other characters.

`zone-decorations.png` is a transparent six-cell atlas generated with the built-in imagegen tool. Prompt: "Six isolated isometric pixel-art workstation decoration clusters in a horizontal row: planning board, code monitor, bookshelf and chart, easel, checklist, application monitor; transparent background and generous gutters." The scene trims each cell and overlays it on its corresponding workstation.

`external-animation-spaced.png` is a non-destructive built-in imagegen edit of `external-animation.png`. Prompt: "Preserve the robot, poses and four-column ten-row order; separate every frame with transparent gutters and keep antenna and feet inside each cell." The original remains available.
# Seated sprites and furniture

The WPF view loads `manager-seated.png`, `development-seated.png`, `research-seated.png`, `creative-seated.png`, `qa-seated.png`, and `external-seated.png` as four-column, six-row sheets: idle, working, waiting, error, happy, receiving/talking. Standing sheets remain in use for travel and delivery. `qa-seated-source.png` preserves the generated original; its runtime copy shifts the rows to keep frames within their cells. `chair.png` is layered behind each actor and `desk-front.png` in front.

The five new character sheets and the separate chair and desk were generated with the built-in imagegen tool. Prompts requested the existing reference character or furniture style, transparent background, and, for characters, the six states and spacing above. The external robot's working frames suggest application activity rather than showing a specific application.

# Office worker SE walk sheet

`office-worker-walk-se.png` is a Pixel Art Tool (PixelLab) sheet of the `office-worker-se.png` character walking toward the south-east (3/4 front-right). The runtime file is a csproj link to `AIEmployeeGenerated/c0c3fc8130874b1f98d4f791b4703b5c-FBA0AE198547.png` (SHA-256 FBA0AE19…8BED), so the generated binary stays where the tool wrote it. Sheet 512x384, 4 columns x 3 rows of 128x128 cells, requested 8 frames at 8 fps. Only cells 1-8 are intended walk frames; cell 9 holds an extra pose and cells 10-12 are empty. No scene consumer uses it yet: its frames must be reviewed (feet baseline is not constant) before it replaces any zone walking row.

Measured 2026-11-06 (alpha is only 0/255): lowest opaque row per cell 1-9 is 118, 118, 117, 116, 118, 118, 118, 117, 121. Opaque-pixel difference from cell 1 is 481, 989, 1221, 942, 323, 523, 864 for cells 2-8, so cell 8 does not lead back into cell 1. By eye, cells 1-2 face the viewer while 3-8 turn right, and cell 8 is a raised, leaning pose. A corrected 8-frame sheet (same character size and position in each cell, one baseline, cell 8 leading into cell 1) is needed before it is wired in.

# Office worker NE walk sheet (needs review)

`office-worker-walk-ne.png` is a Pixel Art Tool (PixelLab) sheet intended as the north-east (3/4 back-right) walk loop of `office-worker-se.png`. The runtime file is a csproj link to `AIEmployeeGenerated/17cde09564374fc79afe821d8852a2fd-EA4B6E512C1F.png` (SHA-256 EA4B6E51...0D10E7); the binary was not copied. Sheet 512x384, 4 columns x 3 rows of 128x128 cells, requested 8 frames at 8 fps, looping. Direction key `ne` (file suffix, as `se` for the SE sheet). Metadata is only this note and the file name; no code reads it yet.

Visual check 2026-11-06: the sheet does NOT meet the brief. Cells 1-4 show the face (front/side view), cells 5-8 turn gradually to the right and cells 7-9 show the back of the head. Cell 9 is an extra pose and cells 10-12 are empty (the tool flagged them). The tool's request settings recorded direction `E`, not `NE`. Feet baseline and step alternation were judged by eye only and are not constant. Regenerate or correct before wiring into any scene.

# Office worker SE seated typing sheet (needs review)

`office-worker-seated-typing-se.png` is a Pixel Art Tool (PixelLab) sheet: seated, waist-up, typing, head nodding, 3/4 front-right. The runtime file is a csproj link to `AIEmployeeGenerated/a82c52fdaed846ae97663439146ba8a5-6772A1C72149.png` (SHA-256 6772A1C7...A00146C77); the binary was not copied. Sheet 512x384, 4 columns x 3 rows of 128x128 cells, requested 8 frames at 8 fps. Consumer: `VirtualOfficeView.cs` (`TypingSheetFile`), used for the seated Working state of the Development actor only, frames 1-8 (row-major), `animationFrame / 4` (~7.6 fps at the 33 ms tick), nearest-neighbor scaling via the actor sprite.

The tool flagged cells 10-12 as empty or of different size; they are not used. Cell 9 is filled (review t5), but it is not used by the code.

**Status 2026-11-06 (t6): typing sheet NOT wired in; use of it is disabled.** `VirtualOfficeView.cs` has `TypingSheetEnabled = false`, so the Development actor shows the old `development-seated.png` Working row, not a standing figure. The csproj link (`a82c52fd...-6772A1C72149.png`) is unchanged and stays only as a placeholder.

Second candidate (also rejected): `AIEmployeeGenerated/2a2a9770dcc9435faf7b9a52ff4c8a8b-7C9F57014770.png`, SHA-256 `7C9F57014770A84A18D817E2C8D9979F66FEED1F320C1F85F6F516D480396F55` (matches the tool receipt; 512x384, 4x3 of 128x128, direction E, 8 frames, 8 fps). Review by eye: cells 1-4 are a standing full-body figure; cells 5-6 are a walking/standing figure with the legs cut off by stray desk fragments; cell 7 is cut at the waist without a desk; cells 8-9 are waist-up with a brown desk slab and the arms stretched forward (a reach, not typing). Head position and scale change between cells 1-4 and 5-9, and the facing varies between front and 3/4. Cell 9 is filled; cells 10-12 are empty. Not wired in. Layout (4x3, 128x128, 8 frames) is correct, so no constants changed. To retry: the pose must be seated and waist-up with a keyboard in every one of cells 1-8, cells 9-12 empty; then change only the csproj link path, this path and the SHA, and set `TypingSheetEnabled = true`.

Review t5 (2026-11-06) found that the first sheet's art does NOT meet the brief: the figure is standing and full-body (legs and shoes visible), not seated or cut off at the waist. The arms hang at the sides with no keyboard or typing motion, the facing is only roughly front or slight 3/4, and the faces vary (closed eyes, open mouth) like a talking/idle loop instead of a nod. Cells 1-9 are filled instead of 1-8 only. The sheet must be regenerated before it is shipped; the Development desk currently shows a standing figure. After regeneration only the csproj link path and the hash above need updating.


# Office worker SE seated sad sheet (needs review, NOT wired in)

`office-worker-seated-sad-se.png` is a csproj link to `AIEmployeeGenerated/94b3b42f8a564b47aee1da722bbb498b-A5529CC1D41C.png` (SHA-256 A5529CC1...9B572BB); the binary was not copied. Pixel Art Tool (PixelLab), 512x384, 4x3 cells of 128x128, requested 8 frames at 8 fps, direction E. Prompt: chibi office worker, waist-up, seated behind a desk, both hands holding the head, sad face (downturned mouth, worried brows, slight tear), 3/4 front-right, same style as office-worker-se.

Review by eye 2026-11-06: does NOT meet the brief. Cells 1-9 show a standing, full-body figure (legs and shoes visible); the face is neutral or eyes-closed, and the hands are not holding the head (hands clasped in front in cells 5-9). Cells 10-12 are empty (flagged by the tool). Consumer: `VirtualOfficeView.cs` (`SadSheetFile`, `SadSheetEnabled = false`) for the Error/Waiting state of the Development actor, frames 1-8. To enable: regenerate with a true seated waist-up pose, update the csproj link path, then set `SadSheetEnabled = true`.
