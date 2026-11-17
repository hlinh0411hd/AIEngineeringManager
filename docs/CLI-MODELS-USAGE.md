# CLI discovery, model catalog and usage diagnostics

Source update: 2026-11-02. No application build, test suite, model inference, installer or database changes performed during this work.

## Behavior

- A failed execution stores a diagnostic in Execution and ProviderUsageLimit/ProviderUnavailable logs, including provider/model, failure class, CLI evidence and manager backoff. Task list reason and parent task inherit the wait reason. Quota exhaustion, rate limiting and missing authentication have distinct wording. Manager backoff is never advertised as the provider quota reset time. Existing waiting tasks can recover the last recorded RateLimit diagnosis at next startup if their reason is empty.
- Provider waits create ProviderWait decisions rather than generic Approval decisions. Existing permission/project checks and manual/semi-automatic approval gates remain authoritative.
- Standard npm launchers may point to native package EXEs or JavaScript. The resolver follows only the known provider package path and invokes EXE/Node directly. It also checks persisted PATH, user .local/bin, .bun/bin and WinGet links; a VS Code copilot-shim is deferred behind actual native/npm CLI candidates.
- Detection refreshes model metadata for all installed adapters. Codex reads CODEX_HOME/models_cache.json (default ~/.codex); hidden entries are omitted. Claude aliases and Copilot choices are extracted only if advertised by installed --help. OpenCode uses its supported models command; Ollama uses installed runtime metadata. Freebuff reads the selected `freebuffModel` from local CLI settings, falling back to its recorded default `freebuffModelDefaultMigration`; its interactive TUI exposes no noninteractive list or model-selection flag. Unsupported listing is explicitly explained and CLI default/manual ID stays available. Gemini IDs are not invented.
- Catalog source and last refresh status are visible in Settings > AI & Models. CLI-help aliases/cache entries do not prove account entitlement, funding or current availability. Refresh preserves existing model blocks, allowed kinds and ranking preferences. No model availability check consumes inference usage.

## Findings on this machine

- Claude Code 2.1.316 is installed through an npm launcher pointing to @anthropic-ai/claude-code/bin/claude.exe. Its actual --help advertises the required headless flags. Its auth status --json reported loggedIn=false and authMethod=none. No login was performed. Optional auth metadata failures do not invalidate verified headless capabilities.
- GitHub Copilot Desktop 1.1.34 is installed as LocalAppData/Programs/GitHub Copilot/github.exe. The VS Code copilot-shim --help reports: "GitHub Copilot CLI was not found." No separate supported CLI executable was located in the inspected standard locations. The desktop GUI is not an executable CLI replacement; it is not launched or installed by this change.
- A local Codex models_cache.json exists. Only public model metadata was inspected; cache identity/credentials were not printed. Cache may be stale or refer to another CLI login.

## Using the updated source

Build manually with Build.bat when ready, reopen the app, then use Settings > AI & Models > Detect CLI & refresh model catalog. Select a provider to see the catalog/status. For Claude, log in through its own CLI before execution. For Copilot, configure a real standalone CLI executable if installed elsewhere; the current shim explicitly reports it unavailable. Existing running app binaries are not replaced by source edits.

## Official references

- [Claude CLI reference](https://code.claude.com/docs/en/cli-reference): authentication status and CLI options.
- [Copilot model selection](https://docs.github.com/en/copilot/how-tos/copilot-cli/use-copilot-cli/choose-a-model): interactive /model, --model and account/organization availability.
- [Copilot command reference](https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-command-reference).

Limits: source reviewed only; no compilation or runtime verification of these changes. Direct installed CLI help/auth metadata and local cache/file inspection were read-only. No software installation, provider enablement, API calls for generation or changes to user data/configuration were performed.

## Copilot SDK runtime discovery correction (2026-11-02)

A follow-up inspection found an existing CLI at LocalAppData/github-copilot-sdk/cli/copilot-sdk-runtime-*/copilot.exe. The earlier inspection missed this SDK cache location, so the prior standalone-CLI finding was incomplete. Direct --help succeeded and advertised every flag required by the manager. The initial restricted invocation could not create LocalAppData/copilot/pkg; the permitted metadata invocation initialized the executable's own bundled cache and returned help. No new software was downloaded or installed, and no AI prompt/authentication flow was run.

Default Copilot resolution now checks complete SDK runtime bundle roots after native PATH/npm candidates and before falling back to the VS Code shim. Explicitly configured executable paths remain authoritative. Versions are not hardcoded; the newest existing executable by modification time is selected, and DetectAsync still validates installed help before allowing execution. This updates source only; no build/tests or application configuration changes were performed. Rebuild manually, clear a manually configured shim path if present, then refresh CLI detection.
