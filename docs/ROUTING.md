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
weight = source weight × 0.5^(age in days / 30)
source weight: explicit good/poor rating 1, send-back 1, approval 0.5, failed run (timeout, error) 0.5
```

- **Difficulty-aware**: a result is compared with what the rating predicted at its own difficulty.
  Twenty approved easy tweaks add about +1 (they were expected), so they cannot hand hard work to a
  cheap model. Wins on hard work and misses on easy work move the offset most.
- **Capped** at ±12 rating points and acceptance never exceeds 95%, so no model becomes "certain".
- **Fading**: results lose half their weight every 30 days, so an old verdict does not lock a model
  out for good and model updates are picked up.
- **Visible and resettable**: *Settings → AI & Models → Performance* shows the learned offset per
  model and work type. *Reset learning* ignores everything judged so far (history is kept).
