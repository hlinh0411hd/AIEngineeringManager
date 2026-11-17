# Visual interface design and native Codex image generation

Source update 2026-11-02; no application build/test, live image-generation test or inference performed.

## Findings

The installed Codex CLI reports `image_generation stable true` through metadata-only `features list`. This does not prove that a particular noninteractive model/account/runtime will expose or successfully execute the image tool. The earlier assumption that CLI execution inherently prevents imagegen is incorrect.

The inspected Pixel Art Tool codex.mjs already asks its Codex execution to use the actual built-in imagegen tool, forbids API-key/HTTP/API-script fallback and requests a real PNG. AI Employee Manager's ordinary coding pipeline previously lacked similarly explicit native-art instructions. A task stopped at planning/permission handling has not reached any image-generation step.

Codex/Claude/Copilot executions are not interchangeable tool environments. In this app Claude/Copilot adapters are intentionally tool-free proposal paths. The application cannot transfer the current desktop chat's tool instance into every provider process. Codex's own built-in support is the relevant native path.

## Changes

- Installed shared AI guidance skill visual-interface-design in %USERPROFILE%/.agents/skills. The existing V5 shared registry discovers it as an AI skill, not a fabricated connector. It covers matching references, purposeful composition/type/spacing, choosing real bitmap art versus native vectors, implementation in the actual UI stack, and screenshot-based critique only when running/preview is permitted.
- Codex WriteProject implementation workers receive explicit instructions to use the available $imagegen skill/actual built-in tool for requested bitmap art, save the real output in the approved project and report actual paths. This grants no new runtime permissions and does not force irrelevant image generation. Coordinators, classifiers and input-preparation steps do not generate art.
- Planner guidance allows native generation within an eligible Codex project worker, while honoring an explicitly requested Pixel Art Tool/workflow. It does not invent a tool ID named imagegen or assume other providers have OpenAI's built-in tool.
- Codex detection optionally reads features metadata and distinguishes a reported flag from verified live capability. Failure to obtain this optional metadata does not invalidate verified CLI headless flags. No feature/config is automatically enabled.
- API-key, HTTP and image_gen.py fallback remain unauthorized unless separately requested; no paid API integration was added. Built-in generation consumes general Codex usage, which manager invocation counts cannot fully measure.

## Use

After manually building/reopening the app, detection can show Codex's feature flag. For direct native art as part of a project task, choose an eligible Codex model and approved WriteProject scope, attach the design reference, and request specific assets/layout changes. Example: “Update this screen to match the reference. Use imagegen for the garden background and character art, save PNG assets in the project, then integrate them. Preserve the current controls.” An explicitly requested Pixel Art Tool still follows the existing connector route and its permissions/backend requirements.

This change does not add an automatic screenshot-capture service or certify visual parity with an interactive Codex chat. The skill instructs review when previewing is allowed and tools exist; under a no-run/no-test policy visual runtime quality remains unverified. Standalone TextOnly requests are not silently promoted to filesystem-writing Codex tasks.

## Sources and validation

- Official OpenAI image-generation guide: https://learn.chatgpt.com/docs/image-generation — supports explicit $imagegen invocation and general Codex usage accounting.
- Local installed CLI metadata, bundled imagegen/SKILL.md and D:/Projects/pixel-art-tool/codex.mjs were inspected. No reference image or generated asset was created during this update.
- Skill source/frontmatter and JSON metadata were inspected. The bundled skill validator could not start because the installed Python lacks PyYAML; no dependency was installed solely for validation.
