# Web Model Ratings Design (`WEB_RATINGS_DESIGN.md`)

**Date:** 2026-11-24  
**Scope:** Automatic, unauthenticated HTTPS lookup of public AI model pricing, capabilities, and benchmark scores to update **Usage rank (`Cost`, 0–100)** and per-capability **Quality (`0–100` across all 20 `WorkCapability` values)** for agent models in `AI Employee Manager`.

---

## Baseline Inspection of Existing Architecture

Inspection of the current codebase establishes the exact contracts that web-synced ratings must integrate with and preserve:

- **`src/Employee.Core/ModelRatings.cs`**:
  - Defines all 20 `WorkCapability` values (`Coding`, `Review`, `Research`, `Content`, `Classification`, `Extraction`, `Planning`, `ImageGeneration`, `UiDesign`, `Debugging`, `Refactoring`, `Testing`, `GraphicsMath`, `Performance`, `Architecture`, `GameDesign`, `CreativeWriting`, `Translation`, `ImageUnderstanding`, `BuildDevOps`).
  - Defines `RatingEntry(string Pattern, int Cost, Dictionary<string, int> Quality)` and `RatingSet(string Source, DateTimeOffset Updated, RatingEntry[] Models, Dictionary<string, string[]>? Priority = null)`.
  - `ModelRatings.Apply(RatingSet? set)` validates every pattern against `^[a-z0-9][a-z0-9._:/-]{1,80}$`, requires `Cost` in `0..100` and `Quality[capability]` in `0..100`, fills omitted capabilities from `Table` or the entry's own mean, sorts `loaded.Rows` by `pattern.Length` descending (most specific first), and swaps `loaded` atomically via `Volatile.Write`.
  - `ModelRatings.Matches(string id, string pattern)` matches `pattern` as a substring of lowercased `model.Id` provided neither boundary splits an ASCII/Unicode word (`char.IsLetter` on both sides of the boundary is rejected; digits and punctuation `-._:/~` act as boundaries).
  - `ModelRatings.Effective(ModelInfo model)` and `ModelRatings.QualityFor(string providerId, ModelInfo model, WorkCapability capability)` check `if (model.RatedByUser)` **first**, returning `(model.Quality, model.CostRank, "User rating")` before consulting `loaded.Rows` or built-in `Table`.
- **`src/Employee.Core/Domain.cs`**:
  - `ModelInfo` carries `Id`, `Kinds`, `Quality`, `CostRank`, `RatedByUser`, `Blocked`, `DiscoverySource`, `AccountAvailabilityVerified`, and computed display properties `EffectiveQuality`, `EffectiveCostRank`, and `RatingSource`.
  - `ManagerSettings` stores `Providers` (`List<ProviderSettings>`) and `RatingsSourceUrl` (currently only supporting a custom JSON file already formatted as `RatingSet`).
- **`src/Employee.Infrastructure/ModelRatingsService.cs`**:
  - Persists `model-ratings.json` inside `dataFolder` with `MaxBytes = 1_000_000` (1 MB) and a 20-second `HttpClient` streaming download for custom `RatingSet` URLs.
- **`src/Employee.Desktop/MainWindow.Performance.cs`, `MainWindow.Agents.cs`, `MainWindow.xaml`**:
  - `MainWindow.Performance.cs` loads `model-ratings.json` on startup, refreshes custom `RatingsSourceUrl` at most once per 24 hours (`RefreshRatingsIfStale`), and handles `UpdateRatingsNow`, `OpenRatingsFile`, and `ReloadRatings`.
  - In `MainWindow.xaml`, `Settings > AI & Models` (`SettingsAISection`) shows the `AI agents` table (`ProviderAgentsGrid`) and top action bar (`Detect agents & models`, `Enable usable agents`), while `Model ratings` is currently tucked inside the collapsed `AdvancedAiSettings` expander (`IsExpanded="False"`).
  - `MainWindow.Agents.cs` (`ShowAgentDetails`) opens a modal `940×660` window for any clicked agent row with a read-only `DataGrid` displaying `Id`, `AllowedTaskTypes`, `EffectiveQuality`, `EffectiveCostRank`, `RatingSource`, `Blocked`, and `VerificationDisplay`.

---

## Researched Public Web Sources (Verified Live 2026-11-24)

All sources below were queried over unauthenticated `HTTPS GET` (no API keys, cookies, or account tokens) and verified against primary endpoints and documentation:

| # | Source | Primary Unauthenticated HTTPS Endpoint(s) | Live Size (2026-11-24) | Role in Synthesis |
|---|---|---|---|---|
| 1 | **OpenRouter Public Models API** | `https://openrouter.ai/api/v1/models` ([Docs](https://openrouter.ai/docs/api-reference/list-available-models)) | `919,924` bytes (~0.92 MB, 474 models) | Token pricing, `:free`/`:batch` variants, modalities, reasoning effort support, embedded Artificial Analysis & Design Arena scores |
| 2 | **LiteLLM Model Pricing & Capability Registry** | `https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json` ([Repo](https://github.com/BerriAI/litellm)) | `5,165,975` bytes (~5.17 MB raw / ~450 KB gzip) | Broadest cross-provider token/reasoning/image pricing, context windows, `mode` filter, `supports_vision`/`supports_reasoning`/`supports_function_calling`, `deprecation_date` & `successor_model` |
| 3 | **SWE-bench Public Leaderboard** | `https://raw.githubusercontent.com/swe-bench/swe-bench.github.io/master/data/leaderboards.json` ([Site](https://www.swebench.com/) · [Repo](https://github.com/swe-bench/swe-bench.github.io)) | `4,091,442` bytes (~4.09 MB raw / ~380 KB gzip) | Ground-truth repository software engineering (`Verified`, `Multilingual`, `Test`, `Multimodal`, `Lite` `% resolved`) |
| 4A | **LMSYS / LMArena Public Leaderboard Dataset** | `https://datasets-server.huggingface.co/rows?dataset=lmarena-ai%2Fleaderboard-dataset&config={text\|webdev\|vision\|agent\|text_to_image}&split=latest&offset=0&length=100` ([Dataset](https://huggingface.co/datasets/lmarena-ai/leaderboard-dataset) · [Arena-Hard CSV](https://huggingface.co/spaces/lmarena-ai/arena-leaderboard/resolve/main/arena_hard_auto_leaderboard_v0.1.csv)) | `2–35` KB per config (`split=latest`) | Human-preference Elo & agent task-outcome scores across `text`, `webdev` (UI/frontend), `vision`, `agent`, and `text_to_image` |
| 4B | **LiveBench Contamination-Free Benchmark** | `https://livebench.ai/data/20261106/categories.json` & `https://livebench.ai/data/20261106/table.csv` (v2.0) and `https://livebench.ai/data/2026-06-25/categories.json` & `table.csv` (v1) ([Site](https://livebench.ai/) · [Repo](https://github.com/LiveBench/LiveBench)) | `2–95` KB per file | Contamination-free 0–100 subtask scores across `Coding`, `Long Agentic` / `Agentic Coding`, `Reasoning`, `Mathematics`, `Research`, `Design`, `Data Analysis`, `Language`, `IF` |

> **Why LMArena uses Hugging Face `datasets-server` (`split=latest`) instead of community git-history dumps:**  
> Live probing confirmed that `arena.ai` has no direct unauthenticated JSON REST endpoint, `api.wulong.dev` timed out, `oolong-tea-2026/arena-ai-leaderboards` (`https://github.com/oolong-tea-2026/arena-ai-leaderboards`) had an empty `leaderboards: {}` object in `data/2026-11-23/_index.json`, and `nakasyou/lmarena-history` (`https://raw.githubusercontent.com/nakasyou/lmarena-history/main/output/scores.json`) is an `18.55 MB` multi-year historical file. By contrast, LMArena's official Hugging Face dataset (`lmarena-ai/leaderboard-dataset`) serves clean, ~20 KB JSON pages for `split=latest` updated through `2026-11-23` via `datasets-server.huggingface.co`.

---

## 1. Exact JSON/CSV Fields Read & HTTP Safeguards

### 1.1 Exact Fields Extracted per Source

#### Source 1: OpenRouter (`https://openrouter.ai/api/v1/models`)
Root JSON object: `{ "data": [ ... ] }`. All other fields (`description`, `links`, `per_request_limits`, `default_parameters`) are ignored.

- **Identity & lifecycle:**
  - `data[].id` (`string`, e.g. `"google/gemini-4-neon"`, `"nvidia/nemotron-3-ultra-550b-a55b:free"`, `"google/gemini-4-neon:batch"`)
  - `data[].canonical_slug` (`string?`, e.g. `"google/gemini-4-neon-20261118"`)
  - `data[].name` (`string`, display label only, sanitized plain text)
  - `data[].expiration_date` (`string?`, ISO date when scheduled for retirement)
  - `data[].endpoints` (`string[]?`, used to detect batch-only `["/api/v1/batches"]` entries)
- **Context & architecture:**
  - `data[].context_length` (`int?`, total context tokens)
  - `data[].top_provider.max_completion_tokens` (`int?`)
  - `data[].architecture.modality` (`string?`, e.g. `"text+image->text"`)
  - `data[].architecture.input_modalities` (`string[]?`, checking `"text"`, `"image"`, `"file"`, `"audio"`, `"video"`)
  - `data[].architecture.output_modalities` (`string[]?`, checking `"text"`, `"image"`)
- **Token & request pricing (decimal strings in USD per single token, parsed with `CultureInfo.InvariantCulture`):**
  - `data[].pricing.prompt` (`string?` → `decimal`, USD per input token)
  - `data[].pricing.completion` (`string?` → `decimal`, USD per output token)
  - `data[].pricing.internal_reasoning` (`string?` → `decimal?`, USD per internal reasoning token)
  - `data[].pricing.image` / `data[].pricing.image_output` (`string?` → `decimal?`)
  - `data[].pricing.request` (`string?` → `decimal?`, flat USD per request; `> 0` disqualifies `:free` classification)
- **Reasoning & tool parameters:**
  - `data[].supported_parameters` (`string[]?`, checking `"reasoning"`, `"reasoning_effort"`, `"include_reasoning"`, `"tools"`, `"tool_choice"`, `"structured_outputs"`, `"response_format"`)
  - `data[].reasoning.mandatory` (`bool?`)
  - `data[].reasoning.default_enabled` (`bool?`)
  - `data[].reasoning.supported_efforts` (`string[]?`, e.g. `["xhigh", "high", "medium", "low"]`)
  - `data[].reasoning.default_effort` (`string?`, e.g. `"medium"`)
- **Embedded benchmarks (when present on OpenRouter model records):**
  - `data[].benchmarks.artificial_analysis`: nullable `double` fields `intelligence_index`, `coding_index`, `agentic_index`, `engineering_index`, `multilingual_index`, `strategy_and_ops_index`, `decision_index` (0–100 scale).
  - `data[].benchmarks.design_arena[]`: objects with `arena` (`"models"` | `"agents"`), `category` (`"codecategories"`, `"gamedev"`, `"uicomponent"`, `"website"`, `"3d"`, `"pdf"`), `elo` (`double`), `win_rate` (`double`).

#### Source 2: LiteLLM Registry (`https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json`)
Root JSON object: dictionary mapping `"<model_key>"` → metadata object. Immediately skip key `"sample_spec"`.

- **Identity & lifecycle:**
  - Property key `<model_key>` (`string`, e.g. `"gpt-4o"`, `"anthropic/claude-opus-4-5"`, `"azure/gpt-realtime-2.1-2026-07-07"`)
  - `litellm_provider` (`string?`, e.g. `"openai"`, `"anthropic"`, `"gemini"`, `"ollama"`, `"openrouter"`)
  - `mode` (`string?`: accept `"chat"`, `"responses"`, `"completion"`, `"image_generation"`; skip `"embedding"`, `"moderation"`, `"rerank"`, `"audio_transcription"`, `"audio_speech"`, `"realtime"`)
  - `deprecation_date` (`string?`, `"YYYY-MM-DD"`)
  - `successor_model` (`string?`)
- **Pricing (numeric `double` in USD per single token):**
  - `input_cost_per_token` (`double?`)
  - `output_cost_per_token` (`double?`)
  - `output_cost_per_reasoning_token` (`double?`)
  - `output_cost_per_image` (`double?`)
- **Context & capability flags:**
  - `max_input_tokens` (`int?`), `max_output_tokens` (`int?`), `max_tokens` (`int?`)
  - `supports_vision` (`bool?`), `supports_reasoning` (`bool?`), `supports_function_calling` (`bool?`), `supports_tool_choice` (`bool?`), `supports_response_schema` (`bool?`)
  - `supported_modalities` (`string[]?`), `supported_output_modalities` (`string[]?`)

#### Source 3: SWE-bench Leaderboard (`https://raw.githubusercontent.com/swe-bench/swe-bench.github.io/master/data/leaderboards.json`)
Root JSON object: `{ "leaderboards": [ { "name": "...", "results": [ ... ] } ] }`.

- `leaderboards[].name` (`string`: `"Verified"`, `"Multilingual"`, `"Test"`, `"Multimodal"`, `"Lite"`).
- Within `leaderboards[].results[]`:
  - `resolved` (`double`, `0.0–100.0` issue resolution percentage)
  - `tags` (`string[]?`, extracting entries prefixed `"Model: "` such as `"Model: claude-opus-4-5"` or `"Model: gemini-3-flash"`; entries listing multiple distinct base `"Model: "` tags are skipped to avoid crediting multi-model ensembles to a single model)
  - `model_display` (`string?`), `name` (`string?`), `folder` (`string?`) — fallback model slug extraction when `tags` has no `"Model: "` tag
  - `reasoning_effort` (`string?`, e.g. `"high"`, `"medium"`)
  - `date` (`string?`, `"YYYY-MM-DD"`), `checked` (`bool?`), `warning` (`string?` — entries with leakage/disqualification warnings are ignored)
  - **`per_instance_details` is NEVER materialized**: parser calls `Utf8JsonReader.Skip()` as soon as property `"per_instance_details"` is seen, avoiding ~85% of DOM allocations across the 4.09 MB payload.

#### Source 4: LMSYS Chatbot Arena (`lmarena-ai/leaderboard-dataset`) & LiveBench (`livebench.ai`)
- **LMArena (`datasets-server.huggingface.co/rows` for `config` in `text`, `webdev`, `vision`, `agent`, `text_to_image`, `split=latest`):**
  - `rows[].row.model_name` (`string`, e.g. `"Gemini 4 Carbon (High)"`, `"Claude Fable 5.2 (xHigh)"`)
  - `rows[].row.organization` (`string?`)
  - `rows[].row.category` (`string`, read only `"overall"` rows)
  - `rows[].row.rating` (`double?`, Bradley-Terry / Elo score on `text`, `webdev`, `vision`, `text_to_image`)
  - `rows[].row.score` (`double?`, `0.0–1.0` task outcome score on `agent`)
  - `rows[].row.vote_count` / `rows[].row.session_count` (`double?`, require `>= 100` to filter high-variance early entries)
  - `rows[].row.leaderboard_publish_date` (`string?`, `"YYYY-MM-DD"`)
  - Optional secondary fallback CSV (`arena_hard_auto_leaderboard_v0.1.csv`): columns `model`, `score` (`0–100`), `date`.
- **LiveBench (`categories.json` + `table.csv`):**
  - Release discovery: fetch `https://livebench.ai/` HTML (`< 2 KB`) → main bundle URL or pinned release list (`20261106` for LiveBench 2.0; `2026-06-25` for LiveBench v1). If bundle discovery fails, query the pinned `20261106` and `2026-06-25` URLs directly.
  - `categories.json`: dictionary `{ "<CategoryName>": ["<subtask_1>", "<subtask_2>", ...] }` mapping categories (`Coding`, `Long Agentic` / `Agentic Coding`, `Reasoning`, `Mathematics`, `Research`, `Design`, `Data Analysis`, `Language`, `IF`) to CSV column headers.
  - `table.csv`: header row + one row per model; read `model` (`string`) and `double` (`0–100`) subtask columns, computing each category score as the arithmetic mean of non-empty subtask columns belonging to that category.

### 1.2 HTTP Timeout, Size, Security & Failure Safeguards

Because `ModelRatingsService` currently caps custom `model-ratings.json` downloads at `1,000,000` bytes (`1 MB`), whereas live LiteLLM (`5.17 MB`) and SWE-bench (`4.09 MB`) exceed `1 MB` uncompressed, dedicated web-fetch safeguards are enforced in `WebModelRatingsFetcher`:

| Safeguard | Specification |
|---|---|
| **Transport & TLS** | `HTTPS` (`Uri.UriSchemeHttps`) strictly required for every request and every redirect target. Plain `http://`, `file://`, credentials in `UserInfo`, cookies (`UseCookies = false`), and `Authorization` headers are forbidden. |
| **Redirect & SSRF Guard** | Custom `SocketsHttpHandler` / redirect validator with `MaxAutomaticRedirections = 3`; rejects redirects to non-HTTPS schemes, `localhost`, loopback (`127.0.0.0/8`, `::1`), link-local (`169.254.0.0/16`), or RFC 1918 private ranges (`10/8`, `172.16/12`, `192.168/16`), and allowlists only the configured public hostnames (`openrouter.ai`, `raw.githubusercontent.com`, `datasets-server.huggingface.co`, `huggingface.co`, `livebench.ai`). |
| **Compression** | `AutomaticDecompression = DecompressionMethods.GZip \| DecompressionMethods.Deflate \| DecompressionMethods.Brotli` (reduces total wire transfer across all feeds from ~10.4 MB to ~1.2 MB). |
| **Timeouts** | `ConnectTimeout = 8s`; per-source request timeout = **`15s`** (`CancellationTokenSource` per source); total multi-source sync wall-clock timeout = **`35s`** (`SemaphoreSlim(4)` concurrent requests). UI cancellation (`Closing` / Stop) cancels the linked token immediately. |
| **Streaming Byte Caps** | Read via `HttpCompletionOption.ResponseHeadersRead` in `32 KB` chunks, checking both `Content-Length` header (if present) and cumulative **decompressed** bytes read before buffering/parsing:<br>• OpenRouter `/api/v1/models`: **`4,000,000` bytes (4 MB)**<br>• LiteLLM `model_prices_and_context_window.json`: **`10,000,000` bytes (10 MB)**<br>• SWE-bench `leaderboards.json`: **`10,000,000` bytes (10 MB)** (with `Utf8JsonReader.Skip()` on `per_instance_details`)<br>• LMArena HF `rows` & LiveBench `table.csv`/`categories.json`: **`2,000,000` bytes (2 MB)** per file<br>• Output `dataFolder/model-ratings.json`: **`<= 1,000,000` bytes (1 MB)** (typically `< 150 KB`, preserving `ModelRatingsService.MaxBytes`). |
| **JSON Depth & Untrusted Input** | `JsonReaderOptions { MaxDepth = 16, CommentHandling = JsonCommentHandling.Skip }`. All strings from web feeds are treated as untrusted data: never rendered as XAML/HTML, never passed to `Process.Start` or shells, and model patterns are strictly regex-validated before `ModelRatings.Apply`. |
| **Partial-Failure Degradation** | Feeds are independent. As long as **at least one pricing/capability registry** (OpenRouter or LiteLLM) succeeds, synthesis completes using all reachable benchmark feeds and reports skipped feeds in `RatingsStatus` (e.g., `"SWE-bench timed out; used OpenRouter + LiteLLM + LMArena"`). If all pricing registries fail, existing `model-ratings.json` and in-memory `ModelRatings` remain untouched. |
| **External-Provider Policy** | Respects user privacy expectations: no prompt, project name, file path, or local Ollama model name is ever sent in query strings—all feeds are static bulk catalogs/leaderboards filtered locally in memory. |

---

## 2. Model ID Normalization, Matching & Pattern Validation Rules

Agent catalogs in `EmployeeManager` come from heterogeneous CLIs and gateways (`opencode`, `kilo`, `cline`, `copilot`, `antigravity`, `clixad`, `devin`, `freebuff`, `claude`, `codex`, `qoder`, `responses-api`, `ollama`). Normalization separates **(A) canonical lookup key extraction** (for joining across web feeds) from **(B) `RatingEntry.Pattern` emission** (for runtime lookup by `ModelRatings.Find(model.Id)`).

### 2.1 Five-Step Canonical Normalization Pipeline

Given a catalog `ModelInfo.Id` (or a web source model identifier):

1. **Trim, Reject Blank, and Lowercase:**
   - Trim whitespace; if empty (`""`, CLI default), skip web rating generation so `ModelRatings.Effective` continues to report `"CLI default; exact model unknown"`.
   - Convert to lowercase invariant (`raw = id.Trim().ToLowerInvariant()`).
2. **Extract Variant & Tier Qualifiers (Before Stripping):**
   - **Free-tier flag (`isFree`):** `true` if `raw` ends with `:free` or `-free`, equals or ends with `/free` (e.g. `kilo-auto/free`), OR `model.DiscoverySource` indicates a zero-cost plan (`"clixad: free turns left today"`, `"devin: free model"`).
   - **Batch flag (`isBatch`):** `true` if `raw` ends with `:batch`.
   - **Ollama / local quantization tag (`localParamTag`):** if `providerId == "ollama"` or `raw` matches `:(?<bSize>\d+(?:\.\d+)?)b(?:-[a-z0-9_]+)?$` (e.g. `qwen3.5:4b`, `llama3.3:70b-instruct-q4_k_m`), record parameter count `bSize` (`4`, `70`) and strip quantization suffix (`-q4_k_m`, `-fp16`) for benchmark lookup.
   - Strip trailing colon transport tags (`:free`, `:batch`, `:online`, `:nitro`, `:exacto`, `:extended`) from the working lookup string. If `isFree` came from a `-free` suffix (e.g. `qwen4-max-free`, `deepseek-v4.1-flash-free`, `beam-free`), strip trailing `-free` for canonical base lookup.
3. **Strip Gateway & Provider Prefixes:**
   - Split on `/`. Iteratively drop leading segments matching known CLI gateways or vendor namespaces—including tilde-prefixed gateway namespaces such as `~anthropic` in `kilo/~anthropic/claude-fable-latest`—while keeping at least one model segment (and keeping two segments for router aliases whose final token is generic `free` or `auto`, such as `kilo-auto/free`):
     - **Gateways:** `opencode`, `kilo`, `cline`, `openrouter`, `azure`, `azure_ai`, `vertex_ai`, `vertex_ai-language-models`, `bedrock`, `bedrock_converse`, `together_ai`, `fireworks_ai`, `groq`, `perplexity`, `deepinfra`, `hosted_vllm`, `ollama`, `ollama_chat`.
     - **Vendors (with optional leading `~`):** `openai`, `anthropic`, `google`, `deepseek`, `deepseek-ai`, `meta-llama`, `meta`, `mistralai`, `nvidia`, `qwen`, `zai-org`, `z-ai`, `x-ai`, `moonshotai`, `inclusionai`, `thinkingmachines`, `radixark`, `recursive`, `minimax`, `cohere`, `microsoft`.
4. **Extract Reasoning-Effort & Mode Suffixes:**
   - Match trailing effort suffixes on model slugs or parenthesized leaderboard labels:
     - Slug suffix: `-(?<effort>xhigh|high|medium|low)$` (e.g., `gemini-4-neon-xhigh`, `gemini-4-neon-low`, `swe-2-flash-medium`, `gpt-oss-120b-medium`).
     - Arena/SWE display suffix: `\s*\((?<effort>xhigh|high|medium|low|max|thinking)\)$` (e.g., `"Gemini 4 Carbon (High)"` → slug `gemini-4-carbon`, effort `high`; `"Claude Fable 5.2 (xHigh)"` → slug `claude-fable-5.2`, effort `xhigh`).
   - Record `effort ∈ { low, medium, high, xhigh }` (`max` maps to `xhigh`) and strip the suffix to form the **family slug**.
   - Also recognize non-effort variant suffixes: `-decisions` (`gpt-6-luna-decisions` → base `gpt-6-luna`), `-thinking` (`claude-opus-4-6-thinking` → base `claude-opus-4-6` with mandatory reasoning), and `-latest` (`claude-fable-latest` → prefix match against newest non-deprecated `claude-fable-*` release).
5. **Canonicalize Dates, Instruction Tags & Version Punctuation (Lookup Key Only):**
   - Strip trailing release dates: `-(?:20\d{2}-\d{2}-\d{2}|20\d{6}|\d{4})$` (e.g., `glm-5.3-flash-2026-09-05` → `glm-5.3-flash`, `gpt-4o-mini-2024-07-18` → `gpt-4o-mini`, `inkling-2-mini-20261120` → `inkling-2-mini`).
   - Strip trailing `-it` / `-instruct` when matching against leaderboard names (`gemma-4-31b-it` ↔ `gemma-4-31b`).
   - Build a punctuation-neutral **join key** by normalizing version separators between digits (`(?<=\d)[.-](?=\d)` → `.`), so `claude-haiku-4-5` and `claude-haiku-4.5`, or `claude-fable-5-2` and `claude-fable-5.2`, resolve to the exact same canonical record (`claude-haiku-4.5`, `claude-fable-5.2`).

### 2.2 Emitting Valid `RatingEntry.Pattern` Strings (`^[a-z0-9][a-z0-9._:/-]{1,80}$`)

`ModelRatings.Apply` (`src/Employee.Core/ModelRatings.cs:335`) throws `InvalidOperationException` and rejects the **entire** ratings file if even one `Pattern` violates:

```regex
^[a-z0-9][a-z0-9._:/-]{1,80}$
```

To guarantee 100% compatibility with `ModelRatings.Apply` and `ModelRatings.Matches(id, pattern)`:

1. **Sanitize Disallowed Characters (`~`, `+`, spaces, uppercase):**
   - Raw catalog IDs from `CliModelCatalog.KiloModels` can contain `~` or `+` (e.g. `kilo/~anthropic/claude-fable-latest`) and up to 160 characters, while `Qoder` IDs contain uppercase (`GLM-5.3`).
   - Emitted patterns use the **lowercased post-prefix-strip model identifier** (preserving `:free`, `-free`, `:batch`, Ollama `:4b`, and `-low/-medium/-high/-xhigh` suffixes so variants never collide!).
   - Because `ModelRatings.Matches(id, pattern)` checks substring boundaries using `char.IsLetter`, pattern `"claude-fable-latest"` matches `"kilo/~anthropic/claude-fable-latest"` (`'~'` and `'/'` are non-letters!).
2. **Enforce Length `2..81` Characters:**
   - `^[a-z0-9][a-z0-9._:/-]{1,80}$` requires total length between **2** and **81** characters, starting with `[a-z0-9]`.
   - If a candidate pattern still exceeds 81 characters after stripping vendor/gateway prefixes, drop leading `/`- or `-`-delimited segments from the left until length `<= 81` and the first character is `[a-z0-9]`.
   - Run `Regex.IsMatch(pattern, @"^[a-z0-9][a-z0-9._:/-]{1,80}$")` as a hard pre-filter before constructing `RatingEntry`: any candidate that still fails is logged to diagnostics and skipped rather than poisoning `RatingSet`.
3. **Specificity Ordering:**
   - `ModelRatings.Apply` orders `loaded.Rows` by `pattern.Length` descending. Because `"nemotron-3-ultra-550b-a55b:free"` (len 31) is longer than `"nemotron-3-ultra-550b-a55b"` (len 26), and `"gemini-4-neon-xhigh"` (len 19) is longer than `"gemini-4-neon"` (len 13), variant-specific patterns always match before base family patterns without ambiguity.

### 2.3 Worked Normalization Examples on Real EmployeeManager Catalog IDs

| Provider | Catalog `ModelInfo.Id` | Canonical Lookup Key | Qualifiers | Emitted `RatingEntry.Pattern` (Validated) |
|---|---|---|---|---|
| `opencode` | `opencode/claude-fable-5-1` | `claude-fable-5.1` | paid, default effort | `claude-fable-5-1` |
| `opencode` | `opencode/beam-free` | `beam` | `isFree = true` | `opencode/beam-free` (or `beam-free`) |
| `kilo` | `kilo/~anthropic/claude-fable-latest` | `claude-fable` (latest) | `~` stripped | `claude-fable-latest` |
| `kilo` | `kilo/nvidia/nemotron-3-ultra-550b-a55b:free` | `nemotron-3-ultra-550b-a55b` | `isFree = true` | `nemotron-3-ultra-550b-a55b:free` |
| `cline` | `anthropic/claude-fable-5.2:batch` | `claude-fable-5.2` | `isBatch = true` | `claude-fable-5.2:batch` |
| `cline` | `google/gemma-4-31b-it:free` | `gemma-4-31b` | `isFree = true` | `gemma-4-31b-it:free` |
| `copilot` | `gpt-4o-mini-2024-07-18` | `gpt-4o-mini` | date stripped | `gpt-4o-mini-2024-07-18` |
| `freebuff` | `glm-5.3-flash-2026-09-05` | `glm-5.3-flash` | date stripped | `glm-5.3-flash-2026-09-05` |
| `devin` | `swe-2-flash-medium` | `swe-2-flash` | `effort = medium` (`isFree` if Free plan) | `swe-2-flash-medium` |
| `antigravity` | `gemini-4-neon-xhigh` | `gemini-4-neon` | `effort = xhigh` | `gemini-4-neon-xhigh` |
| `antigravity` | `gemini-4-neon-low` | `gemini-4-neon` | `effort = low` | `gemini-4-neon-low` |
| `ollama` | `qwen3.5:4b` | `qwen3.5` (`4B`) | `isLocal = true` | `qwen3.5:4b` |

---

## 3. Cost & Quality Mapping Formulas and User-Override Precedence

### 3.1 Mapping `$/1M` Input/Output Tokens & Free/Local Tiers to `Cost` (`0–100` Usage Rank)

In `EmployeeManager`, `Cost` (`CostRank`) represents **relative usage consumption (`0–100`)** fed into `ModelRouter.ExpectedCost(usage, acceptance, preference)`. Subscription CLI quotas track underlying API token pricing closely.

#### Step 1: Compute Per-1M Token Rates (`USD / 1M tokens`)
From OpenRouter `pricing.{prompt, completion, internal_reasoning}` (preferred when available for the exact variant) or LiteLLM `{input_cost_per_token, output_cost_per_token, output_cost_per_reasoning_token}`:

$$P_{\text{in}} = 10^6 \times c_{\text{input}}, \qquad P_{\text{out\_base}} = 10^6 \times c_{\text{output}}$$

If mandatory reasoning has a distinct higher token price $c_{\text{reason}} > c_{\text{output}}$, use $P_{\text{out\_eff}} = 0.5 \cdot P_{\text{out\_base}} + 0.5 \cdot (10^6 \cdot c_{\text{reason}})$; otherwise $P_{\text{out\_eff}} = P_{\text{out\_base}}$.

#### Step 2: Apply Reasoning-Effort & Batch Multipliers
Reasoning-effort suffixes scale output/thinking token volume per step:

$$m_{\text{effort}} = \begin{cases}
0.70 & \text{if effort} = \text{low} \\
1.00 & \text{if effort} = \text{medium or unspecified} \\
1.45 & \text{if effort} = \text{high} \\
2.10 & \text{if effort} = \text{xhigh}
\end{cases}$$

Agentic steps in `EmployeeManager` send ~3× as many prompt/context tokens as completion tokens (`3:1` input-to-output ratio):

$$P_{\text{blend}} = b_{\text{batch}} \times \frac{3 \cdot P_{\text{in}} + m_{\text{effort}} \cdot P_{\text{out\_eff}}}{4}$$

where $b_{\text{batch}} = 1.0$ normally, or $b_{\text{batch}} = 0.5$ when `isBatch == true` and only standard non-batch token prices were found.

#### Step 3: Tier Rules & Power-Law Mapping to `0–100`

$$\text{Cost} = \begin{cases}
0 & \text{if local model (} \text{providerId} = \text{"ollama"} \text{ or } \text{litellm\_provider} = \text{"ollama"} \text{)} \\
2 & \text{if } \text{isFree} = \text{true or } (P_{\text{in}} = 0 \land P_{\text{out\_base}} = 0 \land c_{\text{request}} = 0) \\
\text{clamp}\!\left(\text{round}\!\left(2 + 6.0 \times P_{\text{blend}}^{0.70}\right),\, 3,\, 100\right) & \text{for paid cloud models}
\end{cases}$$

**Calibration check against `ModelRatings.Table` (`src/Employee.Core/ModelRatings.cs:120-157`):**
- **Local Ollama (`qwen3.5:4b`):** `Cost = 0` (matches `Router.cs:118`).
- **Free cloud (`:free`, `-free`, `kilo-auto/free`):** `Cost = 2` (matches built-in `nemotron-3-ultra = 2`, `qwen4-max = 2`, `gemma-4-31b = 2`; ensures local Ollama at `0` wins eligible TextOnly tasks while `:free` models at `2` beat paid models `>= 3` on `difficulty <= 35` rungs).
- **Ultra-cheap / Luna / Haiku (`$0.15/1M` in, `$0.60/1M` out → $P_{\text{blend}} = 0.26$):** $2 + 6.0(0.26^{0.70}) = 4.3 \rightarrow \mathbf{4}$ (built-in `luna = 4`, `haiku = 4`).
- **Flash (`$0.30/1M` in, `$1.50/1M` out → $P_{\text{blend}} = 0.60$):** $2 + 6.0(0.60^{0.70}) = 6.2 \rightarrow \mathbf{6}$ (built-in `haiku-4 = 6`, `flash = 8`).
- **Mini / Hydrogen (`$0.80/1M` in, `$3.60/1M` out → $P_{\text{blend}} = 1.50$):** $2 + 6.0(1.50^{0.70}) = 10.0 \rightarrow \mathbf{10}$ (built-in `mini = 10`, `gemini-4-hydrogen = 10`).
- **Mid-tier Sonnet / Gemini 4 Neon (`$3.00/1M` in, `$15.00/1M` out → $P_{\text{blend}} = 6.00$):**
  - `-low`: $P_{\text{blend}} = 4.88 \rightarrow \mathbf{20}$
  - `-medium` / base: $P_{\text{blend}} = 6.00 \rightarrow \mathbf{23}$ (built-in `sonnet = 25`, `gpt-6-sol = 25`, `gemini-4-argon = 22`)
  - `-high`: $P_{\text{blend}} = 7.69 \rightarrow \mathbf{27}$
  - `-xhigh`: $P_{\text{blend}} = 10.13 \rightarrow \mathbf{32}$ (built-in `gemini-4-neon = 35`)
- **Upper Flagship Opus (`$10/1M` in, `$50/1M` out → $P_{\text{blend}} = 20.0$):** $2 + 6.0(20^{0.70}) = 50.8 \rightarrow \mathbf{51}$ (built-in `opus = 50`).
- **Frontier Pro / Fable / Astra (`$25/1M` in, `$125/1M` out → $P_{\text{blend}} = 50.0$):** $2 + 6.0(50^{0.70}) = 94.8 \rightarrow \mathbf{95}$ (built-in `fable = 95`, `gpt-6-astra = 95`).

---

### 3.2 Mapping Benchmark, Reasoning & Modality Signals to `0–100` Quality Across All 20 `WorkCapability` Values

`ModelRatings.RequiredQuality(difficulty, preference)` sets the router's acceptance bar at `50 + 0.45 × difficulty` (`59` at difficulty 20, `73` at 50, `86` at 80, `95` at 100). Raw benchmark numbers (e.g., SWE-bench `79.2%` or Artificial Analysis `61.4` or Elo `1538`) are therefore normalized onto EmployeeManager's `[45, 98]` routing quality scale before computing per-capability scores.

#### Stage A: Normalized Signal Anchors
Each available benchmark metric for the canonical model is mapped onto `[45, 98]` and combined by weighted average over **present** signals (renormalizing weights when a source has no entry for that model):

1. **Coding & Repo Engineering Signal ($S_{\text{code}}$):**
   - SWE-bench `Verified` `% resolved` ($r_v$): $N_{\text{swe}} = \text{clamp}(52 + 0.55 \cdot r_v,\, 45,\, 98)$ *(weight `0.40`; if `Verified` absent, `Multilingual` or `Lite` uses $\text{clamp}(48 + 0.55 \cdot r,\, 45,\, 94)$ at weight `0.25`)*.
   - LiveBench `Coding` & `Long Agentic`/`Agentic Coding` mean ($l_c$): $N_{\text{lb\_code}} = \text{clamp}(54 + 0.58 \cdot l_c,\, 45,\, 97)$ *(weight `0.25`)*.
   - OpenRouter `artificial_analysis.coding_index` (else `engineering_index`, $a_c$): $N_{\text{aa\_code}} = \text{clamp}(50 + 0.62 \cdot a_c,\, 45,\, 97)$ *(weight `0.20`)*.
   - LMArena `webdev` Elo ($E_w$): $N_{\text{webdev}} = \text{clamp}(50 + 0.058 \cdot (E_w - 1050),\, 45,\, 97)$, or OpenRouter `design_arena[category="codecategories"].elo` ($E_{dc}$): $\text{clamp}(50 + 0.092 \cdot (E_{dc} - 980),\, 45,\, 96)$ *(weight `0.15`)*.
2. **Reasoning, Architecture & Planning Signal ($S_{\text{reason}}$):**
   - OpenRouter `artificial_analysis.intelligence_index` ($a_i$): $N_{\text{aa\_int}} = \text{clamp}(50 + 0.65 \cdot a_i,\, 45,\, 97)$ *(weight `0.30`)*.
   - LiveBench mean of `Reasoning`, `Mathematics`, `Data Analysis` ($l_r$): $N_{\text{lb\_rsn}} = \text{clamp}(52 + 0.58 \cdot l_r,\, 45,\, 97)$ *(weight `0.30`)*.
   - LMArena `text` Elo ($E_t$, `vote_count >= 100`): $N_{\text{arena\_txt}} = \text{clamp}(48 + 0.088 \cdot (E_t - 1020),\, 45,\, 97)$ *(weight `0.20`)*.
   - LMArena `agent` score ($s_a \in [0, 1]$, `session_count >= 100`): $N_{\text{arena\_agt}} = \text{clamp}(58 + 135 \cdot s_a,\, 50,\, 97)$ *(weight `0.20`)*.
3. **Language, Writing & Instruction Signal ($S_{\text{lang}}$):**
   - Weighted mean of $N_{\text{arena\_txt}}$ (`0.45`), LiveBench mean of `Language` / `IF` / `Research` mapped via $\text{clamp}(52 + 0.56 \cdot l_l,\, 45,\, 96)$ (`0.35`), and $N_{\text{aa\_int}}$ (`0.20`).
   - Multilingual sub-signal $S_{\text{multi}}$ blends OpenRouter `multilingual_index` ($\text{clamp}(50 + 0.62 \cdot a_m,\, 45,\, 96)$, weight `0.40`), SWE-bench `Multilingual` (`0.20`), and $S_{\text{lang}}$ (`0.40`).
4. **Design & Modality Sub-Signals ($S_{\text{ui}}, S_{\text{gamedev}}, S_{\text{3d}}, M_{\text{img\_in}}, M_{\text{img\_out}}, B_{\text{ctx}}, B_{\text{struct}}$):**
   - **UI signal ($S_{\text{ui}}$):** Weighted mean of Design Arena `uicomponent`/`website` Elo ($\text{clamp}(50 + 0.092 \cdot (E_{\text{ui}} - 980),\, 45,\, 96)$), LMArena `webdev` $N_{\text{webdev}}$, and LiveBench `Design` ($\text{clamp}(52 + 0.58 \cdot l_d,\, 45,\, 96)$); falls back to $0.65 \cdot S_{\text{code}} + 0.35 \cdot S_{\text{lang}}$ when no design benchmark exists.
   - **GameDev signal ($S_{\text{gamedev}}$):** Blends `0.40 ×` Design Arena `gamedev` Elo ($\text{clamp}(50 + 0.092 \cdot (E_{\text{gd}} - 980),\, 45,\, 96)$) with `0.60 ×` $(0.55 \cdot S_{\text{reason}} + 0.45 \cdot S_{\text{lang}})$ when present; otherwise $0.55 \cdot S_{\text{reason}} + 0.45 \cdot S_{\text{lang}}$.
   - **3D/Graphics signal ($S_{\text{3d}}$):** Design Arena `3d` Elo mapped via $\text{clamp}(50 + 0.092 \cdot (E_{\text{3d}} - 980),\, 45,\, 96)$ when present.
   - **Image input gate ($M_{\text{img\_in}} \in \{0, 1\}$):** `1` iff OpenRouter `architecture.input_modalities` contains `"image"` OR LiteLLM `supports_vision == true` OR LiteLLM `supported_modalities` contains `"image"` OR model appears on LMArena `vision` / SWE-bench `Multimodal`; else `0`.
   - **Image output gate ($M_{\text{img\_out}} \in \{0, 1\}$):** `1` iff OpenRouter `architecture.output_modalities` contains `"image"` OR LiteLLM `mode == "image_generation"` OR LiteLLM `supported_output_modalities` contains `"image"` OR model appears on LMArena `text_to_image`; else `0`.
   - **Context window modifier ($B_{\text{ctx}}$):** `+2` if `context >= 500_000`; `+1` if `128_000 <= context < 500_000`; `0` if `32_000 <= context < 128_000`; `-3` if `context < 32_000`.
   - **Structured/tool modifier ($B_{\text{struct}}$):** `+2` if structured JSON schema (`structured_outputs` / `supports_response_schema`) or function calling (`tools` / `supports_function_calling`) is supported; else `0`.
5. **Cold-Start Fallback (Models Priced in OpenRouter/LiteLLM With Zero Public Benchmarks Yet):**
   - If built-in `ModelRatings.Table` matches the canonical family slug, use that row's `Quality` array as the prior (updating modality gates $M_{\text{img\_in}}, M_{\text{img\_out}}$ from live metadata).
   - Otherwise, compute a conservative prior $S_{\text{code}} = S_{\text{reason}} = S_{\text{lang}} = \text{clamp}(\text{round}(62 + 7.5 \cdot \ln(1 + P_{\text{blend}}) + 3 \cdot \mathbb{I}[\text{reasoning}]),\, 54,\, 86)$ (for Ollama `:<bSize>b`, $\text{clamp}(\text{round}(46 + 8.0 \cdot \ln(1 + \text{bSize})),\, 50,\, 82)$), capped at `86` so unbenchmarked models cannot displace proven frontier models.

#### Stage B: Exact Formulas for All 20 `WorkCapability` Values

Routine work types (`Classification`, `Extraction`, `Testing`, `BuildDevOps`, `Translation`) intentionally apply upward linear compression (`a + b × S`) matching `ModelRatings.cs:111-112` so cheap/free models win routine steps instead of escalating needlessly to expensive models:

| # | `WorkCapability` | Quality Formula (`0–100`, clamped to `[1, 99]` when supported, `0` when unsupported) | Effort Delta (`low / med / high / xhigh`) |
|---|---|---|---|
| 0 | `Coding` | $\text{round}(0.85 \cdot S_{\text{code}} + 0.15 \cdot S_{\text{reason}})$ | `-5 / 0 / +2 / +4` |
| 1 | `Review` | $\text{round}(0.55 \cdot S_{\text{code}} + 0.35 \cdot S_{\text{reason}} + 0.10 \cdot S_{\text{lang}} + 0.5 \cdot B_{\text{ctx}})$ | `-5 / 0 / +2 / +4` |
| 2 | `Research` | $\text{round}(0.55 \cdot S_{\text{reason}} + 0.45 \cdot S_{\text{lang}} + B_{\text{ctx}})$ | `-4 / 0 / +2 / +3` |
| 3 | `Content` | $\text{round}(0.75 \cdot S_{\text{lang}} + 0.25 \cdot S_{\text{reason}})$ | `-2 / 0 / +1 / +1` |
| 4 | `Classification` | $\text{round}(28 + 0.68 \cdot (0.50 \cdot S_{\text{lang}} + 0.50 \cdot S_{\text{reason}}) + B_{\text{struct}})$ | `-2 / 0 / +1 / +1` |
| 5 | `Extraction` | $\text{round}(28 + 0.68 \cdot (0.45 \cdot S_{\text{lang}} + 0.55 \cdot S_{\text{reason}}) + B_{\text{struct}})$ | `-2 / 0 / +1 / +1` |
| 6 | `Planning` | $\text{round}(0.75 \cdot S_{\text{reason}} + 0.25 \cdot S_{\text{code}} + 0.5 \cdot B_{\text{ctx}})$ | `-5 / 0 / +2 / +4` |
| 7 | `ImageGeneration` | $0 \text{ if } M_{\text{img\_out}} = 0;\text{ else } \text{round}(N_{\text{arena\_t2i}} \text{ or } (0.50 \cdot S_{\text{ui}} + 0.50 \cdot S_{\text{lang}}))$ | `0 / 0 / 0 / 0` |
| 8 | `UiDesign` | $\text{round}(S_{\text{ui}})$ | `-2 / 0 / +1 / +2` |
| 9 | `Debugging` | $\text{round}(0.65 \cdot S_{\text{code}} + 0.35 \cdot S_{\text{reason}})$ | `-5 / 0 / +2 / +4` |
| 10 | `Refactoring` | $\text{round}(0.75 \cdot S_{\text{code}} + 0.25 \cdot S_{\text{reason}} + 0.5 \cdot B_{\text{ctx}})$ | `-3 / 0 / +1 / +2` |
| 11 | `Testing` | $\text{round}(20 + 0.78 \cdot S_{\text{code}})$ | `-2 / 0 / +1 / +1` |
| 12 | `GraphicsMath` | $\text{round}(\text{if } S_{\text{3d}} \text{ present: } 0.55 \cdot S_{\text{reason}} + 0.25 \cdot S_{\text{code}} + 0.20 \cdot S_{\text{3d}} \text{ else } 0.65 \cdot S_{\text{reason}} + 0.35 \cdot S_{\text{code}})$ | `-5 / 0 / +2 / +4` |
| 13 | `Performance` | $\text{round}(0.55 \cdot S_{\text{code}} + 0.45 \cdot S_{\text{reason}})$ | `-5 / 0 / +2 / +4` |
| 14 | `Architecture` | $\text{round}(0.70 \cdot S_{\text{reason}} + 0.30 \cdot S_{\text{code}} + B_{\text{ctx}})$ | `-5 / 0 / +2 / +4` |
| 15 | `GameDesign` | $\text{round}(S_{\text{gamedev}})$ | `-3 / 0 / +1 / +2` |
| 16 | `CreativeWriting` | $\text{round}(0.80 \cdot S_{\text{lang}} + 0.20 \cdot S_{\text{reason}})$ | `-2 / 0 / +1 / +1` |
| 17 | `Translation` | $\text{round}(24 + 0.73 \cdot S_{\text{multi}})$ | `-2 / 0 / +1 / +1` |
| 18 | `ImageUnderstanding` | $0 \text{ if } M_{\text{img\_in}} = 0;\text{ else } \text{round}(\text{if } N_{\text{arena\_vis}}: 0.60 \cdot N_{\text{arena\_vis}} + 0.40 \cdot S_{\text{reason}} \text{ else } 0.50 \cdot S_{\text{reason}} + 0.50 \cdot S_{\text{lang}})$ | `-2 / 0 / +1 / +2` |
| 19 | `BuildDevOps` | $\text{round}(18 + 0.80 \cdot (0.75 \cdot S_{\text{code}} + 0.25 \cdot S_{\text{reason}}))$ | `-2 / 0 / +1 / +1` |

*(Note: At runtime, `WorkCapabilities.ProviderSupports(providerId, capability)` in `ModelRatings.cs:73` continues to enforce whether a given CLI adapter actually forwards image attachments or exposes an image generation tool.)*

---

### 3.3 Preserving `RatedByUser = true` Overrides & Existing Customizations

User overrides remain strictly authoritative across every layer:

1. **Per-Model User Rating (`ModelInfo.RatedByUser == true`):**
   - Web sync writes **only** to `dataFolder/model-ratings.json` (`RatingSet`) and calls `ModelRatings.Apply(set)`. It **never** overwrites `ModelInfo.Quality`, `ModelInfo.CostRank`, or `ModelInfo.RatedByUser` inside `settings.Providers`.
   - Because `ModelRatings.Effective(model)` (`ModelRatings.cs:373`) and `ModelRatings.QualityFor(providerId, model, capability)` (`ModelRatings.cs:385`) check `if (model.RatedByUser)` **before** reading `loaded.Rows`, any model manually rated by the user in `Settings > AI & Models` keeps its exact user `Quality`, `CostRank`, and `"User rating"` label across unlimited web updates.
   - After `ModelRatings.Apply(set)`, `ModelCatalogMerge.AssignAutomaticRoles(settings, health, store)` refreshes `AutomaticPrimaryKinds` while leaving `PrimaryKinds`, `ManualRoleKinds`, `Blocked`, and `RatedByUser` untouched.
2. **Existing `model-ratings.json` Priority Lists & Custom Patterns:**
   - When `dataFolder/model-ratings.json` already exists on disk before a web sync, `ModelRatingsService.UpdateFromWebAsync` preserves any non-empty `Priority` dictionary (`Dictionary<string, string[]>`) defined in that file so user-edited work-type priority rungs are not lost.
3. **Measured Performance Learning (`ModelLearning.Offset`):**
   - Historical approvals, send-backs, and Good/Poor verdicts stored in SQLite (`ModelOutcomes`) remain untouched and continue applying their difficulty-aware `±12` offset on top of the updated web ratings.

---

## 4. UI Placement in `Settings > AI & Models` and `Agent Details`

### 4.1 `Settings > AI & Models` (`src/Employee.Desktop/MainWindow.xaml`, `MainWindow.Performance.cs`, `MainWindow.xaml.cs`)

1. **Top-Level Action Bar (Immediate One-Click Access, Above `ProviderAgentsGrid`):**
   - In `SettingsAISection` (`MainWindow.xaml` lines 830–836), add **`Update ratings from web`** (`x:Name="UpdateWebRatingsButton"`, `Click="UpdateRatingsFromWeb"`) directly beside `Detect agents & models` and `Enable usable agents`:
     - `[Detect agents & models]` · `[Enable usable agents]` · **`[Update ratings from web]`**
   - Add a visible summary line `x:Name="WebRatingsBanner"` right below `CliToolApiStatus` (above `ProviderAgentsGrid`) so the user sees the active rating state without expanding `Advanced`:
     - Example: `"Model ratings: Web benchmarks (2026-11-24) · matched 34/37 catalog models across 8 agents (2 user-rated kept)."`
   - While fetching, disable `UpdateWebRatingsButton` (and prevent re-entry via `detecting` / `updatingRatings` guard), show progress in `Status.Text` (`"Fetching public pricing & benchmarks from OpenRouter, LiteLLM, SWE-bench, LMArena, LiveBench…"`), and show a completion toast via `ShowToast("Model ratings updated", ...)`.
2. **Advanced `Model ratings` Subsection (`AdvancedAiSettings` Expander, `MainWindow.xaml` lines 913–921):**
   - Keep existing `RatingsUrl`, `Update ratings now` (custom JSON URL), `Open ratings file`, and `Reload ratings file` intact.
   - Add **`Update from public web sources`** button, a checkbox `x:Name="AutoRefreshWebRatings"` (`"Refresh web ratings automatically once a day"`, persisted as `bool AutoUpdateWebRatings` on `ManagerSettings`, default `false` so no unsolicited external web traffic occurs unless clicked or enabled), and per-source diagnostics in `RatingsStatus` (e.g., `"OpenRouter: 474 models · LiteLLM: 2,840 models · SWE-bench: 323 entries · LMArena: 5 boards · LiveBench: 2026-11-06"`).
3. **Advanced `Model catalog` Editor (`MainWindow.xaml` lines 1011–1029):**
   - Beside `Save selected model preferences` (`line 1017`), add **`Reset to automatic rating`** (`x:Name="ResetModelRatingButton"`, `Click="ResetSelectedModelRating"`, enabled when `ModelGrid.SelectedItem is ModelInfo { RatedByUser: true }`), clearing `RatedByUser = false` so a user can return a manually overridden model back to web/built-in ratings.

### 4.2 `Agent Details` Modal Dialog (`src/Employee.Desktop/MainWindow.Agents.cs` — `ShowAgentDetails`)

In `ShowAgentDetails(ProviderAgentRow row)` (`MainWindow.Agents.cs:106-246`):

1. **Footer Action Button (`actions` `WrapPanel`, alongside `Save`, `Verify now`, `Advanced settings…`, `Close`):**
   - Add **`Update ratings from web`** button (`Padding = new Thickness(12, 4, 12, 4)`, `Margin = new Thickness(0, 0, 6, 0)`).
   - Clicking it runs `UpdateRatingsFromWebAsync()` **without closing the dialog**, then refreshes `models.ItemsSource = settings.Providers.Single(p => p.Id == row.Id).Models.ToArray()`, updates the top `summary` `TextBlock` with how many of this agent's models matched web benchmarks vs user overrides, and refreshes the background `ProviderAgentsGrid` and `ModelGrid`.
2. **`models` `DataGrid` Columns & Tooltips (`MainWindow.Agents.cs:227-242`):**
   - Keep `Model ID`, `Task types`, `Quality` (`EffectiveQuality`), `Usage` (`EffectiveCostRank`), `Rating` (`RatingSource`), `Blocked`, and `Source / verification` (`VerificationDisplay`).
   - Attach a row/cell tooltip on `Quality` and `Rating` displaying the model's per-capability web breakdown (e.g., `"Code 92 · Debug 93 · Arch 90 · Plan 90 · UI/UX 94 · Game 91 · Test 92 · Vision 89 · ImageGen 0 | Usage rank 23 ($3.00/$15.00 per 1M)"`) and any deprecation warning.

---

## ADOPT

The following **5 concrete, user-visible changes** derived directly from the live schemas of OpenRouter, LiteLLM, SWE-bench, LMArena, and LiveBench will be built into `EmployeeManager`:

1. **One-Click Multi-Source Web Ratings Sync in `Settings > AI & Models` and `Agent Details`**
   - **Web source origin:** Combines OpenRouter (`/api/v1/models`), LiteLLM (`model_prices_and_context_window.json`), SWE-bench (`leaderboards.json`), LMArena (`datasets-server` `split=latest`), and LiveBench (`categories.json` + `table.csv`) behind bounded HTTPS streaming readers.
   - **Integration in `EmployeeManager`:** Add `WebModelRatingsFetcher` in `Employee.Infrastructure` and `ModelRatingsService.UpdateFromWebAsync(settings, cancellationToken)`, wire the top-level **`Update ratings from web`** button + `WebRatingsBanner` in `MainWindow.xaml` (`Settings > AI & Models`) and in `MainWindow.Agents.cs` (`ShowAgentDetails`), persist the synthesized `RatingSet` to `dataFolder/model-ratings.json`, re-run `ModelCatalogMerge.AssignAutomaticRoles`, preserve all `RatedByUser = true` models, and add a **`Reset to automatic rating`** button in `Settings > AI & Models` for opting user-rated models back into web sync.

2. **Automatic Reasoning-Effort Tier Differentiation (`-low`, `-medium`, `-high`, `-xhigh`)**
   - **Web source origin:** OpenRouter's `reasoning.supported_efforts` (`["xhigh", "high", "medium", "low"]`) and `pricing.internal_reasoning`, LiteLLM's `output_cost_per_reasoning_token`, SWE-bench's `reasoning_effort`, and LMArena's `(High)` / `(xHigh)` effort-split entries show material cost and reasoning-score separation across effort levels that `ModelRatings.Table` currently collapses into one row (e.g., `gemini-4-neon-low` through `gemini-4-neon-xhigh` all currently get `Cost = 35, Coding = 92`).
   - **Integration in `EmployeeManager`:** Emit distinct validated `RatingEntry` patterns per effort suffix (`-low`, `-medium`, `-high`, `-xhigh`) with output-token cost multipliers (`0.70× / 1.00× / 1.45× / 2.10×`) and reasoning-capability deltas (`-5 / 0 / +2 / +4` on `Planning`, `Architecture`, `Debugging`, `GraphicsMath`, `Performance`, `Coding`, `Review`), visibly differentiating `Quality` and `Usage` in `Agent Details` and `ModelGrid` so `ModelRouter` automatically picks `-low`/`-medium` for routine tasks and reserves `-high`/`-xhigh` for high-difficulty steps.

3. **20-Capability Specialized Scoring & Per-Capability Breakdown Tooltip (Game Design, UI/UX, Repo Debugging, Multilingual)**
   - **Web source origin:** SWE-bench `Verified` & `Multilingual` (`resolved`), OpenRouter `benchmarks.design_arena` (`gamedev`, `uicomponent`, `website`, `3d`, `codecategories`) & `artificial_analysis` (`coding_index`, `intelligence_index`, `engineering_index`, `multilingual_index`), LMArena (`webdev`, `agent`, `text`), and LiveBench 2.0 (`Coding`, `Long Agentic`, `Reasoning`, `Design`, `Research`).
   - **Integration in `EmployeeManager`:** Compute individualized `0–100` `Quality` dictionaries across all 20 `WorkCapability` enum values (keeping upward compression on `Testing`, `BuildDevOps`, `Translation`, `Classification`, and `Extraction`), expose `ModelRatings.DescribeCapabilities(providerId, model)` in `Employee.Core`, and bind it to row tooltips in `Agent Details` (`MainWindow.Agents.cs`) and `ModelGrid` (`MainWindow.xaml`) so users can inspect exact capability scores per model.

4. **Live Multimodal Gate & Non-Chat Endpoint Filtering (`ImageUnderstanding` / `ImageGeneration` / `:batch`)**
   - **Web source origin:** OpenRouter `architecture.input_modalities` / `output_modalities` / `endpoints` and LiteLLM `supports_vision` / `supported_output_modalities` / `mode` expose exact vision/image-gen support (for example, live OpenRouter reports `input_modalities: ["image", ...]` on `google/gemini-4-neon`, whereas built-in `ModelRatings.Table` currently hardcodes `ImageUnderstanding = 0`), while flagging non-chat modes (`embedding`, `rerank`, `moderation`, `realtime`, batch-only `["/api/v1/batches"]`).
   - **Integration in `EmployeeManager`:** Set `ImageUnderstanding` (`0` vs calibrated vision score from LMArena `vision` + `supports_vision`) and `ImageGeneration` (`0` vs calibrated score from `output_modalities` + LMArena `text_to_image`) directly from live modality metadata, price `:batch` catalog models accurately at batch rates, and skip non-chat `mode` entries during web rating synthesis.

5. **Free-Tier Usage Calibration (`:free` / `-free` → `Usage = 2`) & Model Deprecation Warnings**
   - **Web source origin:** OpenRouter zero-price `:free` models (`pricing.prompt == "0"` and `pricing.completion == "0"`), gateway `-free`/`/free` variants, OpenRouter `expiration_date`, and LiteLLM `deprecation_date` + `successor_model` fields.
   - **Integration in `EmployeeManager`:** Emit dedicated `:free` and `-free` patterns with `Cost = 2` alongside the base model's benchmark-derived `Quality` (preventing paid base-model prices from inflating `:free` gateway variants and powering `ModelRouter`'s `difficulty <= 35` free-tier priority rungs), and surface deprecation notices (`"Deprecated YYYY-MM-DD → <successor_model>"`) in `RatingSource` / `Agent Details` so users can retire expiring models before runs fail with `ModelUnavailable`.
