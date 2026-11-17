# Large goals: milestones, acceptance and the test → fix → test loop

A request such as "research and make a survivors-like game" is now planned, built, tested and
repaired by the manager until the result meets criteria the plan wrote down, or until it is clear
that more rounds will not help. Small tasks run as before.

## What happens

1. **Recognise the goal.** "Make / build / create a game, app, tool…" (English or Vietnamese) is
   read as a new build (`TaskIntent.IsNewBuild`): Coding with WriteProject, always planned, no
   classification call.
2. **Plan with milestones and a definition of done.** The planner returns
   - `phases`: milestones, each with `acceptance` (2-6 checkable statements);
   - `acceptance`: 3-10 statements for the whole goal;
   - detailed steps for the first milestone only; a step may carry its own `acceptance`.
   For a new build it is told what is installed on the machine (node, python, dotnet, …) so it
   picks a stack that can be run and tested headlessly, to start with a Research step that writes
   `docs/DESIGN.md`, and to make the first implementation milestone leave a runnable build plus a
   self-check.
3. **Self-check after code steps.** `.aiemployee/check.json` in the project declares the headless
   check: `{"name":"Smoke","executable":"node","arguments":["tests/smoke.mjs"],"timeoutSeconds":300}`.
   The manager runs it whenever code steps finish (Unity/.NET/Node/Python projects are still
   detected from their files). A failure becomes a fix step.
4. **Milestone verification.** When a milestone's steps are done, the manager adds a tester step
   (`verify1`, `verify2`, …). The tester runs the project against the milestone's criteria and
   answers `ACCEPTANCE: {"criteria":[{"id":1,"pass":true,"blocking":true,"evidence":"…"}],…}`.
   - Blocking failures → a fix step (`accfix…`) with the tester's evidence, then a new tester round.
     A second fix is routed away from the model whose first fix did not hold.
   - Only minor failures → accepted; they are listed as known issues.
   - Still failing after the rounds allowed (3) → the run stops, the task shows
     `NOT ACCEPTED: …` with the failures, and the next-step hint says what to do.
5. **Goal check.** With the milestone accepted, the coordinator reads what the steps really
   reported, the manager's own check results and the project files, then plans the next milestone
   or closes gaps. It cannot change the acceptance criteria. The manager, not the coordinator,
   decides which milestone comes next: always the first one the tester has not accepted (the
   coordinator's own "phase" number once kept repeating milestone 1 and later skipped 2 and 4).
6. **Final verification.** When the coordinator says the goal is done, every milestone not yet
   accepted is tested in order, then a tester verifies the whole-goal criteria. Then the task goes to NeedsReview with a line saying what was verified.

"Acceptable" therefore means: the project's self-check passes and an independent tester step has
shown, by running the project, that every blocking criterion of each milestone and of the goal holds.

## Not only games or code

Nothing above is specific to games. The criteria and the tester follow the kind of deliverable:

- **Software**: build, self-check and headless scripts.
- **Files made by a tool** (sprites, video, audio, exports): the tester receives the results of the
  task's tool steps and opens each file to check that it exists and has the properties the
  criteria name (size, duration, format, where it is used).
- **Documents and content** (reports, designs, post drafts): structure, required sections, lengths,
  sources.

When a criterion fails on a result that involved a connected tool, the failure goes to the
coordinator (`AcceptanceReplan`), which can plan a new tool step with a corrected request; a plain
AI fix step is used when no tool is involved or the coordinator plans nothing.

The same happens without a tester when a step that uses a tool's output stops because that output
is missing or wrong ("the animation frames and GUI assets are unavailable"): the coordinator plans
corrected tool jobs, the stopped step is replaced, and later steps wait for the new work (at most
two repairs per step).

**Splitting a tool step.** One tool call makes one asset or one motion. When a step needs more, it
is split into steps of its own (`t3.2`, `t3.3`, …, at most 6 calls), each waiting only for the
result it uses, and the steps after it wait for all of them:
- the preparation step may return `{"toolInputs":[…]}` instead of one `toolInput`, for any tool;
- a Pixel Art `create` whose prompt asks for frames ("a four-frame idle … a four-frame peck") with
  no source image is split by the manager: the base pose first, then one `animation` per motion.
  With a source image it simply becomes an `animation` of it. (`create` only ever returns one still
  picture; that is how the HonkAndBonk run got one goose instead of eight frames.)

Verification needs a project with write access. Text-only tasks without a project and read-only
tasks are not verified this way.

## Research that should change the project

"Research … and change/improve the project after it" (`TaskIntent.IsResearchDriven`) is planned as
changes to build, not as a report. The planner is told that the research step must end with an
**ADOPT** list in the design document: 4-10 concrete changes a player will notice, each naming the
researched product it comes from, including the core features of the genre the project lacks (for an
idle game: offline progress, automation, prestige layers…). The research step may not declare the
request out of scope; a change left out needs a written reason. The manager adds its own whole-goal
criterion: every ADOPT item is in the project, or has its reason next to it. (Seen live: the research
deferred offline progress itself and the run ended with a few readability tweaks.)

## Art review

When a task's image tool steps made art, every tester round gets one more criterion and one more
image: `.aiemployee/screenshots/art-review-N.png`, made by headless Edge from the project's own art
from before the task (top, plus the source image each tool call was given) and the new art (below),
scaled by whole steps with hard pixels. Art clearly worse than or off-style from the existing art is a
blocking failure. Animations and edits take the project's original art as source, never an earlier
generated output; a sheet with several poses is a `styleGuide` for a new base pose, not the image to
animate. When the Pixel Art Tool's own check still reports problems ("isolated pixels"), the image is
made once more, told what to avoid, before it is passed on. (Seen live: idle/peck frames animated
from a poor first output, frame sizes mismatched, accepted on sizes and wiring alone.)

## Browser screenshots

Before every tester round on a project with a web page (`index.html` at the root or in
`public/`, `dist/`, `www/`, `web/`), the manager serves the project read-only on a loopback port and
takes two screenshots with headless Edge (or Chrome): desktop 1280×800 and phone 390×844. They are
saved in `.aiemployee/screenshots/` and named in the tester's and fix step's request. The workers'
sandbox cannot start a browser, so this is how visual problems (a collapsed play area, clipped
text) are caught.

The phone view loads the page in a 390px frame inside a wider window, because headless browsers keep
a minimum window width of about 500px: without the frame the page is laid out wider and the
screenshot crops it, which once made a tester and two fix steps chase a defect that did not exist.
The magenta strip on the right of the phone image is outside the phone.

`Employee.Checks --snapshot <projectFolder>` takes the same screenshots by hand.

## Tools and research

- **Planner chooses tools** (Settings → Tools, on by default): the planner may use any connected
  tool that is enabled with task automation allowed (pixel art, video, music, marketing or a
  connector added later), without the user naming it. It can never choose an operation that
  publishes outside the machine. A tool the user named stays mandatory. If a planner-chosen tool step fails it is retried once, then skipped: the
  steps that need its output make a stand-in asset and the gap is reported as a known issue.
- **Research steps** (kind Research) get Claude's WebSearch/WebFetch in addition to the tools their
  access allows, and keep their kind when they write findings into the project. Other providers
  research from model knowledge.
- Workers may install project-local dependencies with the project's package manager; never global
  software.

## Milestone sign-off

In a task with two or more milestones, a milestone that passes its tester does not start the next
one by itself. The task waits (Steps & approvals shows "Approve milestone") with the milestone's
known issues and the checks only a person can do. Then:

- **Approve**, then Run / resume, or send a short comment such as "ok", "được rồi", "duyệt", "làm
  tiếp": the milestone is signed off and the next milestone is planned. A comment with a change in
  it ("ok nhưng sửa màu") is a change request, not an approval.
- **Comment** ("Send & continue updating") instead: that milestone and every later one are no
  longer accepted. The comment is planned as work in that milestone, and the milestone is tested
  again and asks for sign-off again before the run moves on.
- **Reject**: the milestone reopens without a change; comment with what to change, then run.

Milestones are planned small (up to 20, about 2-6 steps each, one kind of result to look at), and
an asset is checked in its own milestone before work that builds on it: for a visual update, sample
art first, then the rest of the art, then animations of the approved art, then GUI, then integration.

The Steps tab groups a task's steps by milestone, with each milestone's goal, status and progress.

A task can also be closed without completing it (Close as ▾): not completed, does not meet
requirements (counts as a miss for the AIs that made the result), or cancelled.

The last milestone is not signed off separately: the task's own final review covers it. Sign-off
needs milestone testing on (it follows the tester's pass). Turn it off in Settings → Tools →
"Sign off milestones" to let large tasks run straight through.

## Switches and limits

| Setting | Where | Default |
|---|---|---|
| Let the planner choose tools | Settings → Tools | on |
| Test milestones (verify / fix loop) | Settings → Tools | on |
| Sign off milestones (wait for approval between milestones) | Settings → Tools | on |
| Verification rounds per milestone | `Automation.MaxVerifyRounds` | 3 |
| Goal checks per task | fixed | max(8, 3 × milestones) |

Verification runs only for large work: a plan with milestones, or a request to build something
new. Ordinary tasks (a fix, a feature, a review) run exactly as before, even if their plan lists
criteria. A change request sent after the result was accepted ("Send & continue updating") makes
the manager test the whole result again once the update is done.

Verification needs: steps continuing automatically, one project, WriteProject. Every tester and
fix step is an AI call and counts against the task's call limit; the loop stops when calls run out.

## Limits to know

- The tester works headlessly. It cannot look at the screen or judge "fun"; criteria about feel
  must be written as measurable proxies, and the final look still needs a person.
- The tester is an AI. Its verdict needs evidence from a command it ran, but it can still be
  wrong; the task stays NeedsReview until the user approves it.
- A new project must be an existing (possibly empty) folder registered as a project.
- Events on the task: `AcceptanceCriteria`, `AcceptanceVerify`, `AcceptancePassed`,
  `AcceptanceFailed`, `AcceptanceStalled`, `AcceptanceReport`, `ToolFallback`.

## Checks and trying it

- `dotnet run --project tests/Employee.Checks -- --acceptance` — scripted AI, no usage spent.
- `Employee.Checks --live-goal <dataDir> <projectDir> <requestFile> [--settings-from <manager.db>] [--plan-only] [--minutes N] [--providers a,b]`
  runs one goal through the real services without the window, in an isolated data folder.
  **It calls real AI providers and spends their usage.**
