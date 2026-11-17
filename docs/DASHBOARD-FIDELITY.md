# Dashboard artwork and reference-layout update

2026-11-02. Source/assets only. No build, test suite, application startup or native window visual check was performed. XML/handler/resource-path inspection is not compilation or runtime validation; pixel-identical rendering is not claimed.

## Implemented visual changes

- Reference-proportioned 262 px sidebar, 88 px top region and scalable 1,388 × 804 dashboard composition. Hero, three middle cards, two bottom cards and two right-side cards retain their relative positions instead of wrapping vertically.
- Newly generated decorative robot/ribbon hero, with real WPF heading and actionable tiles overlaid. The entire interface is not flattened into an image.
- Shared vector navigation/action/category icons; polished gradient branding/workspace avatar, task/approval badges driven by current records, segmented Task/Autonomous/Away controls, compact notifications and separate titlebar window controls.
- Four horizontal summary tiles, rounded progress bars and a native vector donut showing daily manager calls relative to the user-set limit. Provider legend counts come from recorded model runs; tool/internal call accounting may differ from manager reservations. Neither is vendor quota.
- Reworked active-task card, project list with illustrative garden/cat/pixel/video vector thumbnails, activity timeline and provider status rows with separators.
- Actual CPU-manager/RAM metadata and explicitly queried VRAM. Missing measurements, exact models, authentication or quota are not replaced with mock screenshot values. Provider symbols are app-owned generic badges, not claims of official vendor logos. Project thumbnails are decorative illustrations, not captured project output.
- Existing Settings category tabs, task inputs/operations, routes, approvals and event handlers remain connected. Updates is accessible via Settings → Cập nhật rather than an extra visible sidebar item.

## Final assets and source

- Raster hero: `D:\Projects\AIEmployeeManager\src\Employee.Desktop\Assets\dashboard-hero.png` (2,172 × 724 PNG, bundled WPF Resource).
- Shared vector artwork: `src/Employee.Desktop/UiArt.cs`.
- Dashboard composition: `src/Employee.Desktop/DashboardView.cs`.
- Sidebar/header artwork and badges: `src/Employee.Desktop/MainWindow.Chrome.cs`, MainWindow.xaml, App.xaml and the existing notification handler.

The raster was generated using the built-in **image_gen** tool under the **imagegen** skill, with the user's attached dashboard as a visual reference. No paid API/CLI fallback was used. Its default original is retained at `C:\Users\Protech\.codex\generated_images\01a19626-99bb-7cc0-a9a1-a88fbfa47225\exec-3b457e76-6b82-4b0d-a508-55d3774db918.png`; the application uses its own bundled copy.

## Final image-generation prompt

Use case: stylized-concept. Create ONLY the decorative hero-banner artwork for a Windows AI Employee Manager application, using the attached screenshot as style reference, NOT an entire interface. Output wide landscape 3:1 banner. Dark midnight navy purple #121125 background; flowing glossy blue/violet silk-like light ribbons across the RIGHT TWO THIRDS. In the upper-right quadrant a polished cute small white/lavender 3D robot with round helmet, violet glowing headphone earcups, black face with two smiling closed crescent eyes and a small smile, holding a glowing blue-purple laptop with a simple </> code symbol. Robot approximately 25% of total canvas width, completely visible including hands/laptop, leave 8% margin at the right. LEFT HALF must remain dark, quiet and mostly empty for real UI text to be overlaid in code. No other text, no buttons, no cards, no logos, no screenshot borders. Closely match the reference robot and ribbon colors, soft cinematic lighting, premium desktop dashboard artwork. No UI text baked into the image.

Built-in output uses an opaque background, as intended for the hero. The generated image was visually inspected before integration. New source/assets are backed up/hash-checked during sync; database and currently built executables are unchanged. Build.bat remains a user-invoked action and creates no ZIP as part of these edits.
