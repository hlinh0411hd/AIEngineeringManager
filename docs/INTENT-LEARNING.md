# Task intent rules and confirmed-example memory

2026-11-02. Source-only implementation; no build, tests, runtime UI check, benchmark, inference or database modification was performed during this edit.

## Recognition

The deterministic classifier now normalizes Vietnamese accents, case, punctuation and whitespace, then matches phrase boundaries instead of raw substrings. It includes additional change verbs and vocabulary for UI controls, lists/filtering/sorting, APIs, storage/schema, authentication, game interactions, sprites/animation, audio/subtitles, project behavior, code review/debugging, content, research, classification and extraction. Explicit read-only wording constrains write access. Standalone text deliverables are distinguished from project changes. These remain heuristic rules, not a guarantee of understanding every request or negation/quoted-text construction.

Automatic assignment order:

1. An exact confirmed example from the same project, respecting explicit read-only wording.
2. Deterministic rules.
3. Conservative matching to similar confirmed examples when the rules cannot decide: at least two distinct phrases from two source tasks, word-set Dice overlap >= 0.90, one unanimous kind/access, matching numeric/negation signatures and read-only/text-deliverable markers. The score is not a calibrated confidence probability; word order is not semantic understanding.
4. Existing on-demand AI classifier. Up to three related confirmed examples (each <=400 characters, overlap >=0.45) may be included as reference data if the original request plus attachment context is under 3,500 characters. Example retrieval adds no AI call; reference text consumes tokens only if classification is already needed. Existing context/permission/provider gates still apply.

A learned label never automatically raises the task's approved access, enables a provider, qualifies an Ollama model for coding, resets budgets, or bypasses manual routing. A recognized label above the access ceiling waits for explicit correction instead of spending another classification call.

## Learning sources and controls

In task details choose **Sửa loại / dạy nhận diện**. Select the correct kind and access, then **Lưu mẫu đúng**. By default this saves only an example. The optional checkbox applies the correction to an untouched task, with an explicit confirmation of its new access. Tasks with an executed worker or any plan must be handled through a new task instead; results/history are retained. Corrected tasks wait for explicit Run/resume. Provider/model pins and permissions continue to apply.

The dialog lists the current project's memory. **Quên mẫu đã chọn** removes a stored example without deleting tasks. **Học từ task đã duyệt** reads at most 200 completed project goals with an explicit UserReview event and fully completed workers; it does not import raw AI predictions or skipped/unfinished steps. The displayed import count is processed eligible tasks, not necessarily newly inserted unique phrases.

New manually classified tasks and tasks explicitly marked reviewed complete also save examples. Priority is explicit user correction > manual creation > reviewed completion, so a later automatic-reviewed label cannot overwrite a stronger correction. Same normalized phrase is merged, and at most 500 examples are retained per project, preferring stronger/recent records. Intermediate AI classifications and keyword guesses alone are never training labels. Learning from reviewed completion assumes that the user's approval also accepts the task's category; use correction/forget if that assumption is wrong.

Memory is an additive SQLite table in the selected data directory. It moves with the existing database backup/data-folder mechanisms. Request text is local and unencrypted like task history; only bounded related examples may be sent to a selected classifier. Examples persist after ordinary task retention cleanup and are removed when their project is deleted. No existing task history is imported automatically by this source edit.

This is deterministic retrieval and example-guided prompting, **not fine-tuning Ollama weights**. Ambiguous or conflicting near-matches still go through classification. UI layout, migration and runtime behavior remain unverified because the user requested no build/tests.
