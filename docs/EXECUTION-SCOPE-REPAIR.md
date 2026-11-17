# Provider allocation and implementation access correction

2026-11-02 — source-only changes. The current task database was inspected read-only to identify the failure. No application build, tests, inference, task execution or database repair was run during implementation.

## Observed failure

The two blocked reruns in the user's screenshot are **Review / WriteProject**, with zero worker steps/calls. Their notes explicitly reject Codex because the model catalog allows Coding, not Review. The earlier compatible-role fallback accepted Review only with ReadProject, so manually granting Write made that route ineligible. Their source task had previously been classified Review/ReadProject, executed a read-only worker, and then had its access changed. Rerun copied the consumed `AutoAssign=false` and stale Review kind into each fresh goal.

Other inspected examples include “các nút nó bị sát nhau và đè lên nhau” and the task/project auto-assignment behavior. These are implementation requests expressed as defect/expected-behavior descriptions; their old records had read-only work/results.

## Source changes

- Review with an **already approved** ReadProject or WriteProject can reuse a permitted cloud Coding model in nonmanual routing when no dedicated Review model exists. All enable/ready/model-block/access/write/cooldown gates remain. Research/planning compatibility stays read-only; Ollama is excluded. Manual mode still requires catalog-compatible selection.
- Optional persisted `GrantedAccess` and `AutomaticIntent` distinguish the user's permission ceiling/automatic-intent choice from the AI's assigned access and the consumed `AutoAssign` flag. New task creation, explicit access/classification changes and reruns preserve this distinction. A classifier choosing Read no longer shrinks the entire session's grant.
- On Run/resume, legacy automatic goals and pending implementation workers are normalized within an existing grant. Historical permission edits/classification corrections take precedence over Created logs. Missing provenance falls back to current access. A permission change made by the user to Read is never silently promoted back to Write. Explicit read-only task wording is also respected.
- Rerun recovers the existing user grant and reclassifies an automatically assigned source instead of copying stale classifier output as a manual choice. It creates a new run as before; old execution history stays intact.
- Existing intermediate/completed/skipped results are not automatically reopened or replayed. For old read-only results already marked finished, use an explicit fresh rerun after reviewing existing files/checkpoints.
- Generated/imported plans normalize actual implementation steps to Coding/WriteProject only within a WriteProject ceiling. Read-only inspection/research steps stay read-only. Pending plans are normalized again when approved. Before each worker, the engine rechecks the step's access; a central execution guard blocks edit requests with insufficient access before any AI call, instead of allowing a read-only response to look like successful implementation.
- Recognition covers task/project/access/run changes, automatic project assignment behavior, UI overlap/clipping reports and “build bug…sửa đi”. A reported inability such as “không sửa được” is not treated as a read-only instruction. Lower-priority learned reviewed/manual-creation examples cannot override a clear implementation rule with a stale read-only label; an explicit user correction remains authoritative.

Completed task states, provider/model permissions, manual route pins, deadlines, attempt/switch usage and project roots are retained by automatic scope normalization. A selected but incompatible pin can still require the user to choose Automatic or another eligible model; missing/auth/cooldown/budget problems are not bypassed. Unknown natural-language intent remains a classification limitation.

After the user builds/reopens the updated application, Run/resume performs normalization for unfinished tasks. The existing executable and current database were left untouched by the source sync. Source review is not runtime verification.
