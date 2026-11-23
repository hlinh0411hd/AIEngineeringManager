# CLI capability evidence

Inspected on 2026-11-22; live headless runs of the installed CLIs on 2026-11-23. No provider installation or login was performed beyond the user-approved status/list commands named below.

| Provider | Machine evidence | Phase 1 integration |
|---|---|---|
| Codex | `codex-cli 0.166.0-alpha.5.1`; `codex --help` and `codex exec --help` inspected; required headless/sandbox flags present | Ready for a user-authorized run; login/quota Unknown. Native process sandbox read/write; no bypass flags |
| Claude Code | No `claude` executable in PATH | Adapter implemented; live authentication/inference untested. Requires print, model, tools, MCP restriction flags |
| GitHub Copilot | Standalone `copilot.exe` in `%LOCALAPPDATA%\GitHubCopilotCLI`; live run answered correctly in 12 s | Ready; `--available-tools=` (empty list) is the accepted way to run tool-free — `--deny-tool *` is rejected as an invalid rule. `--silent` keeps stats/resume lines out of the answer |
| Gemini | Not in PATH | Adapter implemented; live authentication/inference untested. Requires headless/model/admin-policy support; blocked if centrally installed policies would cause supplemental policy to be ignored |
| OpenCode | `opencode 1.19.5` from npm; one OpenCode API credential; live run reached the provider but the account is out of free usage/credits | Adapter verified as far as billing allows: `run --format json` plus `--print-logs --log-level ERROR` so provider rejections reach stderr. A quota/auth/model rejection now aborts the run in ~2 s and routes to another provider instead of hanging |
| Kilo Code | `kilo` npm launcher; `kilo run --help` shows `-m/--model`, `--format json`, `--auto` and `--dir`; `kilo models` lists 407 Gateway model IDs on this machine | Ready for a user-authorized run; `run --format json --dir <root>` for read steps, `--auto` only for WriteProject. The Gateway catalog is not account-filtered, so models stay unverified until one run succeeds |
| Cline | `cline --help` shows positional prompt, `--json`, `--plan`, `--auto-approve <boolean>`, `-c/--cwd` and `-m/--model`; no noninteractive model listing | Ready; plan mode for read steps, `--auto-approve true` only for WriteProject. `--json` emits one object per line and the final answer in `run_result.text` |
| clixad | `clixad --help` shows `-p "<task>"` headless read-only, `models` (prices plus free-turns-left), `wallet` and `model <id>`; `wallet` reported 0 credits and 15 catalog models on this machine; live run answered on a free-turn model | Ready read-only; WriteProject is refused. Models with `locked` tier or no free turns on a zero balance are hidden. A run with no routed model switches to a model that still has free turns and restores the saved choice afterwards, because the saved default may be out of allowance |
| Devin CLI | `devin --help` shows `-p/--print [prompt]`, `--permission-mode auto\|accept-edits`, `--respect-workspace-trust`, `--sandbox`, `--model` and `--verbosity`; `auth status` and `models` inspected | Ready; `--print <prompt>` headless, permission auto for read steps and accept-edits for WriteProject. Free plan shows only models tagged Free, paid models are hidden |
| Freebuff | `freebuff 0.2.35` npm launcher with downloaded `~/.config/manicode/freebuff.exe`; installed `--help` advertises only `--continue`, `--cwd`, `--trust-agents`, `-v` and `login` | Detected and model-catalogued only. Interactive terminal UI with no headless prompt, print/JSON or model-selection flag; execution is blocked rather than driven through a fragile TUI. Local settings may supply the selected model |
| Qoder | `qodercli 1.1.85` (bundled with Qoder Desktop); every `--print` run exits with `sdk_worker_removed`; the hidden `--sdk` NDJSON daemon answers initialize/authenticate/open_session and a live run replied in ~2.7 s | Ready through the daemon: `qodercli --sdk` with an auth payload file (`{"type":"qodercli"}`, path in `QODER_SDK_AUTH_PAYLOAD_FILE`, deleted after start) borrows the local login. `daemon.initialize` then `daemon.authenticate` (required — without it every session reports `Session model "auto" is unavailable in ModelProvider`), `daemon.open_session` with the same `--print/--permission-mode/--cwd/--model` worker args, prompt as a user frame, answer from the `result` frame; the daemon and payload are removed when the step ends. Model list comes from the daemon catalog (`daemon.get_models`, live) |
| Ollama | Version `0.40.13`; loopback `/api/tags` responds; installed `qwen2.5-coder:7b-instruct` | Local optional adapter detects and lists actual installed model. Real qualification attempt did not pass; no coding qualification |

Standard npm `.cmd` launchers for the four recognized npm packages are resolved to their installed Node entrypoint and executed via argument arrays. The launcher is never passed to a shell. Unknown package paths or arbitrary shell scripts are rejected. These resolution paths are fixture-tested; the absent CLIs were not installed merely to test them.

A CLI that starts a background helper for a headless step is asked to stop it when the step ends: Cline's hub (`hub status` / `hub stop`) and Devin's agent daemon (avoided with `--process-model in-process`, otherwise `daemon status` / `daemon stop`). A helper that was already running before the step belongs to the user's own session and is left untouched.

Native detection alone does not prove vendor identity or make startup hooks safe. Use trusted installed executables and configuration. Capability inspection checks actual local help before running a task; unsupported CLI versions are blocked. Auth and quota remain Unknown when no dependable no-token interface is used.

## Official references consulted

- [Codex noninteractive execution](https://learn.chatgpt.com/docs/non-interactive-mode) — compared against the actual installed exec help; the installed CLI is authoritative for supported flags.
- [Claude Code CLI reference](https://code.claude.com/docs/en/cli-reference) — print/model/tool and MCP restrictions.
- [GitHub Copilot CLI reference](https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-command-reference) — headless prompt, tool availability/denial, no-user-question and no-auto-update flags.
- [Gemini headless](https://geminicli.com/docs/cli/headless/) and [maintainer policy-engine reference](https://github.com/google-gemini/gemini-cli/blob/main/docs/reference/policy-engine.md) — tool denial policy and supplemental admin-policy behavior.
- [OpenCode CLI](https://opencode.ai/docs/cli/) and [permissions](https://opencode.ai/docs/permissions/) — run/models/format, inline permissions, plugin/LSP download controls.
- [Kilo CLI reference](https://kilo.ai/docs/code-with-ai/platforms/cli-reference) — `run` positional prompt, `--model`, `--format json`, `--dir` and `--auto`; compared against the installed `kilo run --help`.
- [Cline CLI reference](https://docs.cline.bot/cli/cli-reference) — prompt/plan/auto-approve/cwd/model flags and the `--json` line format (`run_result.text`).
- [Ollama generation API](https://docs.ollama.com/api/generate) — structured output, bounded generation and unload request.

All references describe vendor behavior, not proof of a live authenticated integration on this machine. Model ranking is user policy; no “best/current coding model” claim is inferred from these pages.
