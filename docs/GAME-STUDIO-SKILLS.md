# Reviewed Game Studios skills for Codex and V5

Source inspected locally: D:/Learn/Claude-Code-Game-Studios, release 1.2.0, commit 05ad7c8a5b8aa2aa86bbd3439ae7cf7295cac418. The repository is a Markdown/YAML/Bash Claude Code studio template, not an implemented game or an established engine stack (src contains CLAUDE.md). It advertises 74 skills, 49 roles and provider-specific hooks. No upstream script, hook, agent, configuration installer or engine command was executed.

## Selected and adapted

| Shared skill | Upstream workflow sources | Suggested AI task kinds |
|---|---|---|
| game-architecture | create-architecture | Planning, Research, Review |
| game-art-spec | art-bible, asset-spec, asset-audit | Planning, Content, Research, Review |
| game-balance-check | balance-check | Review, Research, Content |
| game-code-review | code-review, perf-profile | Review, Research |
| game-design-brief | quick-design, scope-check | Planning, Content, Research, Review |
| game-feature-delivery | dev-story | Coding |
| game-localization | localize | Coding, Content, Review |
| game-ui-flow | ux-design, ux-review | Coding, Content, Review, Planning |

The adaptations preserve concrete game-domain procedures: bounded acceptance criteria, state/lifecycle handling, art/frame/pivot/import specifications, module ownership and data flows, evidence-backed review/performance limits, target-based balance arithmetic, UI states/input/focus and localization/Unicode/placeholders. They are concise self-contained skills, not copies of the full studio orchestration. Upstream MIT notices are included as LICENSE.txt in each folder. Exact upstream SKILL.md hashes are recorded in tool-skill.json.

## One authoritative installation

Shared root: `%USERPROFILE%/.agents/skills`. This is the user/global discovery location for Codex. Each of the eight new folders contains SKILL.md, tool-skill.json and LICENSE.txt. Existing global or project skills are not replaced. The manager's three existing connector skills remain project-scoped in its .agents/skills folder.

V5 now reads its project skill folder plus the shared user folder. It does not copy the eight shared skills into every project or into .codex/skills. In a standalone app installation on a different machine, install the shared skills on that machine as well; the manager does not silently download them. Codex skill discovery is expected on a subsequent turn/session; actual host runtime selection was not exercised during this installation.

## App behavior

- `Connector: ai` plus an empty Operations list marks guidance for an AI worker, never a callable tool. Normal connector skills still require implemented operation IDs and existing permissions.
- For a game project/request the planner sees bounded descriptions, kinds and IDs. It can assign a game skill to an AI step and use employee-pixel-art on a later tool preparation step. An AI-guidance skill cannot grant a tool action or bypass its exact-input approval.
- Simple AI workers can select one matching guidance skill deterministically by task kind, game domain and keyword score. Coding falls back to game-feature-delivery only within detected game context. Coordinators/classifiers do not automatically load full game bodies. These shared AI keywords do not force an additional mixed planning call.
- Game context uses explicit game intent, project technology, actual Godot/Unity/Unreal markers or relevant web-game dependencies. It does not infer an engine from a directory name or assume every C# project is Unity.
- Selected bodies are loaded lazily with pinned hashes. All eight bodies are not appended to every prompt. Existing five-field hashes for the three connector skills are preserved, so this registry extension does not invalidate existing tool plans just because optional AI metadata was added.
- Settings > Tools shows loaded AI/connector skills and provenance. The existing V5 setting controls automatic selection; an explicitly selected skill remains part of its approved task. Provider/model routing and capability/usage checks still belong to the manager, not to a skill.

Example: adding a game character can use game-design-brief or game-art-spec for a bounded specification, employee-pixel-art for actual connector inputs/generation, then game-feature-delivery for integration and game-code-review for review. Not every task needs all of these steps.

## Deliberately excluded

The original team-* orchestration, fixed Opus/Sonnet selections, Claude Agent/AskUserQuestion tools, dynamic Bash frontmatter, hooks, .claude settings modifications and mandatory per-file questions were not imported. They conflict with the actual Codex/V5 execution boundary or add unnecessary usage/configuration coupling. Studio-only config/session/TR/ADR gates, self-improving skills, release/publishing/checklist automation and broad optional scaffolding were also left out. An existing game need not adopt 49 roles or 74 commands to use these eight skills.

No image/audio/video generator, engine, account integration or dependency was installed. Game assets are not generated by these guidance skills alone. Tests/builds remain subject to the current user's policy; missing runtime evidence is not a green verdict.

## Verification boundary

Source, license, dependency patterns, metadata and body size were inspected. JSON descriptors and skill frontmatter were read directly. No app build/tests, game execution, AI generation, model benchmark or end-to-end Codex/V5 invocation was performed. All eight bundled skill-validator attempts were blocked before validation because the installed Python lacks PyYAML (ModuleNotFoundError: yaml). No dependency was installed solely for validation. No claim of successful runtime discovery or behavioral testing is made.
