# CLI capability evidence

Inspected on 2026-11-01. No provider installation, login, model download or cloud inference was performed.

| Provider | Machine evidence | Phase 1 integration |
|---|---|---|
| Codex | `codex-cli 0.166.0-alpha.5.1`; `codex --help` and `codex exec --help` inspected; required headless/sandbox flags present | Ready for a user-authorized run; login/quota Unknown. Native process sandbox read/write; no bypass flags |
| Claude Code | No `claude` executable in PATH | Adapter implemented; live authentication/inference untested. Requires print, model, tools, MCP restriction flags |
| GitHub Copilot | VS Code `copilot.exe` launcher exists but help says the real Copilot CLI was not found | Not Ready; blocked, no auto-install. Adapter requires actual headless/model/tool-denial flags |
| Gemini | Not in PATH | Adapter implemented; live authentication/inference untested. Requires headless/model/admin-policy support; blocked if centrally installed policies would cause supplemental policy to be ignored |
| OpenCode | Not in PATH | Adapter implemented; live authentication/inference untested. Requires run/model/JSON format help and verified models subcommand; tool permissions supplied through documented environment configuration |
| Freebuff | `freebuff 0.2.35` npm launcher with downloaded `~/.config/manicode/freebuff.exe`; installed `--help` advertises only `--continue`, `--cwd`, `--trust-agents`, `-v` and `login` | Detected and model-catalogued only. Interactive terminal UI with no headless prompt, print/JSON or model-selection flag; execution is blocked rather than driven through a fragile TUI. Local settings may supply the selected model |
| Ollama | Version `0.40.13`; loopback `/api/tags` responds; installed `qwen2.5-coder:7b-instruct` | Local optional adapter detects and lists actual installed model. Real qualification attempt did not pass; no coding qualification |

Standard npm `.cmd` launchers for the four recognized npm packages are resolved to their installed Node entrypoint and executed via argument arrays. The launcher is never passed to a shell. Unknown package paths or arbitrary shell scripts are rejected. These resolution paths are fixture-tested; the absent CLIs were not installed merely to test them.

Native detection alone does not prove vendor identity or make startup hooks safe. Use trusted installed executables and configuration. Capability inspection checks actual local help before running a task; unsupported CLI versions are blocked. Auth and quota remain Unknown when no dependable no-token interface is used.

## Official references consulted

- [Codex noninteractive execution](https://learn.chatgpt.com/docs/non-interactive-mode) — compared against the actual installed exec help; the installed CLI is authoritative for supported flags.
- [Claude Code CLI reference](https://code.claude.com/docs/en/cli-reference) — print/model/tool and MCP restrictions.
- [GitHub Copilot CLI reference](https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-command-reference) — headless prompt, tool availability/denial, no-user-question and no-auto-update flags.
- [Gemini headless](https://geminicli.com/docs/cli/headless/) and [maintainer policy-engine reference](https://github.com/google-gemini/gemini-cli/blob/main/docs/reference/policy-engine.md) — tool denial policy and supplemental admin-policy behavior.
- [OpenCode CLI](https://opencode.ai/docs/cli/) and [permissions](https://opencode.ai/docs/permissions/) — run/models/format, inline permissions, plugin/LSP download controls.
- [Ollama generation API](https://docs.ollama.com/api/generate) — structured output, bounded generation and unload request.

All references describe vendor behavior, not proof of a live authenticated integration on this machine. Model ranking is user policy; no “best/current coding model” claim is inferred from these pages.
