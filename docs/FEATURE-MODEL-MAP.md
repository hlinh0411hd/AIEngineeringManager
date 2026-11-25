# Feature → model map (primary, then fallbacks)

Best first. **Primary** = first entry; each next entry takes over only when the one before cannot run
(out of usage, not signed in, turned off, no write permission, failed on this step).
IDs are the catalog IDs on this machine (2026-11-24). Source of the claude/codex part: the built-in order in
[ROUTING.md](ROUTING.md). The rest is ordered by model size/tier, this machine's run history (`model_runs`)
and each CLI's capability; it is a starting order, the router's learned ratings adjust it.

Legend: ✍️ edits project files · 👁 reads project, no edits · 💬 text only.
Short names: `agy` = antigravity (Google account), `kilo/…` IDs are written without the leading `kilo/`.

## 1. What each CLI can do and which of its models are worth using

| CLI | Can do | Models, best → weakest (only ones worth routing to) | Skip |
|---|---|---|---|
| **agy (Google AI Pro)** ✍️👁💬 | all; plan/accept-edits modes | `gemini-4-carbon-medium` ≈ `gemini-4-neon-medium` (Carbon = deepest reasoning, plan/review/architecture; Neon = same level on coding agents, ~2x faster, use for code) → `gemini-4-neon-medium` → `claude-opus-5-5-medium` → `claude-sonnet-5-5-medium` → `gemini-4-argon-medium` → `gemini-4-hydrogen-medium` (fast/cheap) → `gemini-3.8-flash-medium` → `gemini-3.8-flash-medium` → `gemini-3.8-flash-low`; `gpt-oss-120b-medium` last (OpenAI open-weights, the only ChatGPT-family model in agy; GPT-6 is not offered there) | `gemini-pro-orchestrator-*` (multi-agent wrappers, not single-step models) |
| **kilo** ✍️👁 (free ids) | edits with `--auto` | `nvidia/nemotron-3-ultra-550b-a55b:free` → `kilo-auto/free` → `openrouter/free` → `recursive/flint-v1.0:free` → `inclusionai/ling-3.2-flash:free` → `baidu/ernie-5.5-flash-preview:free` → `particle-ai/particle-1:free` → `dots-studio/dots-3-note-preview:free` → `nvidia/nemotron-3.5-super-vl:free` → `nvidia/nemotron-3.5-lightning:free` → `nvidia/nemotron-3-nano-omni-30b-a3b-reasoning:free` | `liquid/lfm-3-1.7b:free` (1.7B, classification only) |
| **kilo** (paid ids, need Kilo credits) | same | `openai/gpt-6.2-astra-pro` → `anthropic/claude-opus-5.5` → `google/gemini-4-neon` → `openai/gpt-6.2-sol` → `anthropic/claude-sonnet-5.5` → `moonshotai/kimi-k3.1-pro` → `qwen/qwen4-max` → `z-ai/glm-5.3` → `deepseek/deepseek-v4-pro` → `qwen/qwen3-coder-next` (code) | `*-fast`/`*-pro` variants only when you want speed/extra depth |
| **cline** ✍️👁 (free ids) | edits with `--auto-approve` | `nvidia/nemotron-3-ultra-550b-a55b:free` → `google/gemma-4-31b-it:free` → `google/gemma-4-26b-a4b-it:free` → `recursive/flint-v1.0:free` → `inclusionai/ling-3.2-flash:free` → `baidu/ernie-5.5-flash-preview:free` → `particle-ai/particle-1:free` → `nvidia/nemotron-3.5-super-vl:free` → `dots-studio/dots-3-note-preview:free` | `liquid/lfm-3-1.7b:free`; every `:batch` id (async batch API, not interactive) |
| **cline** (paid ids, need Cline credits) | same | same ranking as kilo paid, without the `kilo/` prefix | `:batch` |
| **opencode** ✍️👁 (free ids) | edits (fixed today) | `qwen4-max-free` → `deepseek-v4.1-flash-free` → `big-pickle` → `flint-40b-free` → `tidepool-alpha-free` → `iceman-free` → `beam-free` → `hy4-flash-free` → `mimo-v2.6-flash-free` → `nemotron-3.5-super-vl-free` → `nemotron-3.5-lightning-free` | — |
| **opencode** (paid ids, Zen credits; account was out of credits) | same | `gpt-6.2-astra` → `claude-opus-5-5` → `gemini-4-neon` → `gpt-6.2-sol` → `claude-sonnet-5-5` → `kimi-k3` → `qwen3.8-max` → `glm-5.3` → `deepseek-v4-pro` → `gpt-5.3-codex` / `kimi-k2.7-code` (code) | `agy/*` ids (duplicate of agy) |
| **devin** ✍️👁 | edits (`accept-edits`) | Free plan exposes only `swe-2-flash-medium` (coding-tuned, small) | 1,306 paid ids are hidden on Free |
| **qoder** ✍️👁💬 | edits (borrows local login) | By real results: `GLM-5.3` (17/26 ok) → `DeepSeek-Flash` (32/42) → `GLM-5.3-Flash` (27/47) → `MiniMax-M3.1-Flash` (6/15) | `Qwen3.8-Max`, `Qwen4-Max`, `Kimi-*`, `DeepSeek-V4-Pro`, `GLM-5.4`, `Auto/Ultimate/Fast/Efficient/Sonus/Cantus`: 0 successes in 8-9 tries each |
| **clixad** 👁💬 (read-only) | no edits | `gpt-6-luna` → `gemini-3.8-flash` → `deepseek-v4.1-flash` → `claude-haiku-5.5` → `glm-5.3-flash` → `gemini-3.5-flash-lite` → `gpt-5-nano` (only while free turns remain; wallet is 0) | `kimi-k2.5` (locked) |
| **responses-api / fosta** 💬 | text only, no tools | `gpt-6.2-astra` → `claude-opus-5-5` → `gpt-6.2-sol` → `claude-sonnet-5` → `claude-fable-5-1` → `gpt-6-luna` (15/17 ok) | — |
| **ollama** 💬 | local text | `qwen3.5:4b` (79/80 ok) → `qwen2.5-coder:7b-instruct` (failed coding qualification) | — |
| **copilot** 💬 | text only | `gpt-5.6-luna-free-auto`, `gpt-6-luna` | all: 0 of 12 runs succeeded |
| freebuff | none (interactive only) | — | not runnable |

Gemini 4 order checked on the web (2026-11-24): **Carbon** = heaviest, deepest reasoning (most thinking tokens);
**Neon** = adapted from Carbon, matches or beats it on coding-agent benchmarks at about twice the speed;
**Argon** = efficient, largely superseded by Neon; **Hydrogen** = fastest/cheapest. Claude Opus/Sonnet 5.5 in agy
need a paid (non-trial) Google AI Pro/Ultra plan; run `agy models` to confirm yours (it listed them). The search
results are secondary sources, not Google docs; treat the exact ranking as likely, not guaranteed.

## 2. Feature → model chain

Chain = claude/codex first (`PrimaryProviders`, when turned on) → agy (Gemini Pro) → free CLIs.
`provider:model`; each line is best → weakest.

| Feature | Chain |
|---|---|
| **Planning** 👁 | claude:opus → codex:gpt-6-astra → agy:gemini-4-carbon-medium → agy:claude-opus-5-5-medium → responses-api:gpt-6.2-astra → kilo:nemotron-3-ultra:free → opencode:qwen4-max-free |
| **Architecture** 👁 | claude:sonnet (≤69) / opus → codex:astra → agy:gemini-4-carbon-medium → agy:claude-opus-5-5-medium → kilo:nemotron-3-ultra:free → opencode:qwen4-max-free → cline:nemotron-3-ultra:free |
| **Coding** ✍️ | claude:sonnet → codex:gpt-6.1-sol → claude:opus → agy:gemini-4-neon-medium → agy:claude-sonnet-5-5-medium → kilo:nemotron-3-ultra:free → opencode:qwen4-max-free → cline:nemotron-3-ultra:free → kilo:kilo-auto/free → devin:swe-2-flash-medium → qoder:GLM-5.3 |
| **Debug** ✍️ | claude:sonnet (≤69) / opus → codex:astra → codex:sol → agy:gemini-4-carbon-medium → agy:gemini-4-neon-medium → agy:claude-opus-5-5-medium → kilo:nemotron-3-ultra:free → opencode:qwen4-max-free → devin:swe-2-flash-medium |
| **Refactor** ✍️ | claude:sonnet → codex:sol → claude:opus → agy:gemini-4-carbon-medium → agy:claude-sonnet-5-5-medium → opencode:qwen4-max-free → kilo:nemotron-3-ultra:free → cline:flint-v1.0:free → devin:swe-2-flash-medium |
| **UI / UX** ✍️ | claude:sonnet → claude:opus → codex:sol → agy:gemini-4-neon-medium → agy:claude-sonnet-5-5-medium → opencode:qwen4-max-free → kilo:nemotron-3-ultra:free → cline:gemma-4-31b-it:free |
| **Graphics/Math, Performance** ✍️ | claude:sonnet (≤69) / opus → codex:astra → codex:sol → agy:gemini-4-carbon-medium → agy:gemini-4-neon-medium → agy:claude-opus-5-5-medium → kilo:nemotron-3-ultra:free → opencode:qwen4-max-free |
| **Testing, Build/CI** ✍️ | codex:gpt-6.1-terra → claude:sonnet → codex:sol → agy:gemini-4-argon-medium → agy:gemini-3.8-flash-medium → opencode:deepseek-v4.1-flash-free → kilo:kilo-auto/free → cline:ling-3.2-flash:free → devin:swe-2-flash-medium |
| **Code review** 👁 (different model than the author) | codex:terra ⇄ claude:sonnet → codex:sol → agy:gemini-4-carbon-medium → agy:claude-sonnet-5-5-medium → kilo:nemotron-3-ultra:free → opencode:qwen4-max-free → cline:gemma-4-31b-it:free → clixad:gpt-6-luna |
| **Research** 👁 (web) | claude:sonnet (WebSearch/WebFetch) → claude:opus → codex:sol → agy:gemini-4-neon-medium → agy:gemini-3.8-flash-medium → kilo:nemotron-3-ultra:free → clixad:gemini-3.8-flash |
| **Content, Story/Dialogue, Game design** 💬 | claude:sonnet → claude:opus → codex:sol → agy:gemini-4-neon-medium → responses-api:claude-sonnet-5 → qoder:GLM-5.3 → kilo:ling-3.2-flash:free → opencode:deepseek-v4.1-flash-free |
| **Translation** 💬 | claude:haiku → codex:terra → claude:sonnet → agy:gemini-3.8-flash-medium → responses-api:gpt-6-luna → clixad:deepseek-v4.1-flash → qoder:DeepSeek-Flash → ollama:qwen3.5:4b |
| **Classification, Extraction** 💬 | claude:haiku → codex:luna → codex:terra → agy:gemini-3.8-flash-low → ollama:qwen3.5:4b → responses-api:gpt-6-luna → clixad:gemini-3.5-flash-lite → qoder:DeepSeek-Flash |
| **Image generation / reading** | codex:terra → codex:sol → codex:astra (only Codex has the image tool; agy Gemini models can read images but not generate through this tool) |

## 3. Notes

- `codex` and `claude` are **turned off** in Settings on this machine, so today every task starts at agy/free.
- The manager's antigravity catalog only held 7 models (a generic probe) while `agy models` lists 26,
  including all Gemini 4 and Claude 5.5 entries above. Fixed in code: `agy models` is now the source of the
  list (account-aware), picked up on the next provider scan (Settings → detect providers).
- kilo/cline/opencode: ids without `free`/`:free` are billed to that service's credits. The catalogs are not
  filtered per account, so a paid id stays "unverified" until one run succeeds.
- To make the router follow section 2, put it in `model-ratings.json` → `priority` (entries `provider/model`,
  `@N` = max difficulty), see [ROUTING.md](ROUTING.md).

