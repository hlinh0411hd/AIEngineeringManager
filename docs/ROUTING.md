# AI routing

Every flow that needs an AI (run, resume, approval, planning, assignment, fallback) calls one
decision: `ModelRouter.Choose` in `src/Employee.Core/Router.cs`.

## How a model is chosen

1. **The user's explicit choice** (task AI override, Switch AI) is used while it is usable.
   If it is out of usage, disabled or otherwise unusable, the step continues on another AI in
   Automatic and SemiAutomatic mode. Manual mode only runs the user's choice.
2. **Otherwise ratings decide.** The step gets a difficulty (0-100, planner-rated or estimated)
   and a work type (Coding, Debugging, UiDesign, Testing, ImageGeneration... see
   `WorkCapability`). Among enabled models, the router takes the lowest **expected usage to an
   accepted result** (below).
3. **Planner hints never decide.** They are recorded but routing stays rating-based.

The previous AI of a step is not a pin: resuming always re-decides.

## Providers: Claude and Codex first

`ManagerSettings.PrimaryProviders` (default `codex`, `claude`). While one of them can run a step,
the other AIs (qoder, antigravity, copilot, opencode, gemini...) are not considered. They take over
only when neither can: out of usage, not signed in, disabled, no write permission, or already tried
and failed in this run. A qualified local Ollama model stays eligible for Classification and
Extraction. An empty list turns the tiers off. The reason of a route says "fallback AI" when this
happened.

## Model order per work type

Each work type has an ordered list of models (`ModelRatings.PriorityFor`, built in; editable with the
ratings file, below). With usage level **Auto** the router goes down the list and takes the **first
model that is usable and meets the step's quality bar** (rating + learned offset ≥ bar). If none
meets it, the strongest usable one in the list runs. A model that is not in the catalog, is blocked,
disabled, out of usage or was sent back on this step is skipped, and the next one in the list is used.
So with no `haiku` in the Claude catalog, easy Code work goes to `sonnet`, not to another provider. A
step that is not covered (no listed model usable) uses the rating-based choice among the allowed AIs.

An entry can carry a difficulty cap: `claude/sonnet@69` is used only while the step's difficulty is 69
or less. Architecture, Graphics/Math, Performance and Debug use it, so hard work (70+) goes to Opus.

| Work type | Order (GPT-6.1 first, then GPT-6, for each Codex name) |
|---|---|
| Code | haiku → sonnet → sol → opus → astra |
| Debug | sonnet@69 → opus → astra → sol |
| Refactor | haiku → sonnet → sol → opus |
| Testing, Build/CI | haiku → terra → sonnet → sol → opus |
| UI/UX | sonnet → opus → sol → astra |
| Graphics/Math, Performance | sonnet@69 → opus → astra → sol |
| Architecture | sonnet@69 → opus → astra |
| Planning | opus → sonnet → astra |
| Review | haiku → terra → sonnet → sol → opus → astra |
| Research | sonnet → opus → sol (Claude research steps can search the web) |
| Content | haiku → sonnet → sol → opus |
| Story/Dialogue | sonnet → opus → sol |
| Game design | sonnet → opus → sol → astra |
| Translation | haiku → terra → sonnet → opus |
| Classification, Extraction | haiku → luna → terra → sonnet |
| Image generation, image reading | terra → sol → astra (only Codex has the image tool) |

The order is not used when a usage level other than Auto is set, when a model is the manual primary
for the task type, or for qualified Ollama. Switch it off with `ManagerSettings.UsePriorityLists`.

## Reviews use a different AI

A Review step looks at the steps it depends on. The AI that wrote them does not review them, if
another is usable: first a different **provider** (Claude's code is reviewed by Codex and the other way
round), else a different **model**, else the author's own model, noted in the reason. The Review order
above then picks among the allowed ones. Reviews with nothing before them, and your own explicit
choices, are not affected. Switch it off with `ManagerSettings.ReviewByOtherModel`.

## Ratings file: priority order

`model-ratings.json` (and the ratings URL) may also carry the order, with work type names as keys:

```json
{"source":"team","updated":"2026-11-17","models":[],
 "priority":{"Coding":["claude/sonnet","codex/gpt-6.1-sol","claude/opus@100"],
             "Review":["codex/gpt-6.1-terra","claude/sonnet"]}}
```

Entries are `provider/model` or `provider/model@maxDifficulty`. A work type that is not listed keeps the
built-in order; an empty array turns the order off for that type. Invalid entries reject the whole file
and the built-in values stay active. *Open ratings file* writes the current ratings and order.

## Usage level (Settings → AI & Models)

| Level | Choice among models that can do the step |
|---|---|
| **Auto** (default) | Lowest expected usage to an accepted result (below); counts send-backs |
| **Low** | Lowest-rated model whose rating for the work type meets the step's bar |
| **Medium** | The middle one of those, ranked by rating |
| **High** | The one three quarters up that ranking |
| **Highest** | The top-rated one (cheaper of equals) |

Each level only sets a target quality: usage still comes first, so the cheapest model within 3
points of the target is used (then the closest to it). "Meets the bar" uses rating + learned offset; models already sent back on this work are skipped
when others fit; if none meets the bar, the strongest is used. User choices, manual primary roles
and provider priority still come first.

## Expected usage to an accepted result

A cheap model that keeps missing costs more than a strong model that gets it right, so routing
does not just take the cheapest model that looks good enough:

```
bar         = 50 + 0.45 × difficulty  (Economy −6, Performance +6)  + 6 per send-back of this work (max 4)
quality     = rating for the work type + learned offset (below)
acceptance  = 1 / (1 + e^(−(quality − bar)/5)), clamped 5–95%
              × 0.4 if this model's result was already sent back on this work
expected    = usage / acceptance + missCost × (1 − acceptance) / acceptance
missCost    = 40 (Economy 20, Performance 80): one send-back ≈ a mid-tier try (review + redo)
```

The lowest expected value wins. Easy work stays on cheap models; work a model often misses moves
to a stronger one as evidence accumulates, and a step you send back moves off the model that missed.

**Send-backs** are recorded when you post a change request ("continue updating") on a result you
have not approved, or rerun a task whose result you had not approved. They count as a miss for the
model and work type that produced the result. Approving a finished task counts as a good result.

## No switch limits

There is no AI switch budget. When a provider fails because it is out of usage, unavailable or
not signed in, the step moves to the next usable AI; the chain ends when none is left, and the
step waits with a checkpoint. Such calls are recorded (`Attempts`, session calls) but counted in
`ProviderWaits`, never in the per-step attempt limit (`WorkAttempts`). A provider that cannot even
start (prompt too long for its command line, launch failure) is skipped for that step only.

The session AI call budget and duration still apply when the user sets them.

## No cooldowns

A provider that ran out of usage is skipped for the rest of that run only. Nothing is locked for
later runs: the next run, resume or step asks it again (`ProviderSettings.CooldownUntil` is kept
for old data and is always cleared). The reset time the CLI states ("try again at 11:14 PM",
"resets in 3 hours", `Retry-After`), else an estimate of 15 minutes for usage limits and 5 for
other unavailability, is only shown in the step's waiting reason and log.

## Escalation and work no model can do

- A step that stalls on something the model could not do moves to another model (at most twice).
  The bar rises like after a send-back, and the step only moves to a model rated at least as well
  as the one that stalled; if those drop out during the run, any usable model continues.
- Image generation needs a worker with an image tool (Codex). When none is usable the step waits
  instead of spending a call on a model that cannot produce the image. A model you pick yourself
  still runs.

## Keeping ratings current

- **Built-in table** in `ModelRatings` (public benchmarks and prices). A pattern matches a model
  ID where it is not part of a longer word: `mini` matches `gpt-5-mini`, not `gemini`.
- **Ratings file**: `model-ratings.json` in the data folder overrides it. *Settings → AI & Models →
  Model ratings → Open ratings file* writes the current table as a starting point.
- **Ratings URL**: a JSON file in the same format, refreshed automatically once a day.
- **Learning from work**: each run is stored with its work type and difficulty. See below.

Ratings file format (omitted work types keep the built-in value for the same pattern):

```json
{"source":"team","updated":"2026-11-04","models":[
  {"pattern":"gpt-6-luna","cost":5,"quality":{"Coding":85,"Debugging":75}}]}
```

## Learned offset (cannot inflate)

Judged results move a model's quality for one work type by a learned offset (`ModelLearning`):

```
for each judged result: expected = acceptance(rating, bar at that result's difficulty)
                        residual += weight × (accepted − expected)
offset = clamp(25 × Σresidual / (Σweight + 3), −12, +12)
weight = source weight × share × 0.5^(age in days / 30)
source weight: explicit good/poor rating 1, send-back 1, approval 0.5, failed run (timeout, error) 0.5
share: 1 for an explicit rating of one result, a single rerun step and failed runs; 1 / (number of
       steps) (at least 0.1) for a verdict on a whole task (approve, send back, rerun, close as not met)
```

A verdict on a whole task is one verdict. It is divided between the steps that produced it, so one weak
step does not mark every model that worked on the task as missed (or, on approval, as good). Older
feedback keeps share 1. The Performance table's counts (good/poor) still count each step once.

- **Difficulty-aware**: a result is compared with what the rating predicted at its own difficulty.
  Twenty approved easy tweaks add about +1 (they were expected), so they cannot hand hard work to a
  cheap model. Wins on hard work and misses on easy work move the offset most.
- **Capped** at ±12 rating points and acceptance never exceeds 95%, so no model becomes "certain".
- **Fading**: results lose half their weight every 30 days, so an old verdict does not lock a model
  out for good and model updates are picked up.
- **Visible and resettable**: *Settings → AI & Models → Performance* shows the learned offset per
  model and work type. *Reset learning* ignores everything judged so far (history is kept).
