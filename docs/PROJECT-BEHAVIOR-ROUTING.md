# Project behavior assignment correction

2026-11-02 — source-only change; no build, tests or model calls performed.

The request “phần luyện nghe trong Chinese Garden mình muốn nghe xong đánh tiếng Trung luôn chứ không phải pinyin” describes changing a registered project's answer-input behavior. It was assigned Content/TextOnly, leaving no eligible Codex route. This was an intent/access mismatch rather than a missing provider.

Changes:

- Deterministic intent detection combines project name/alias/folder context, an existing feature, a desired change and an interaction. Name matching ignores spacing/punctuation. It chooses Coding/WriteProject only within the existing approved access ceiling; read-only restrictions remain respected. Standalone caption/post/translation deliverables are excluded from this additional rule.
- Both cloud and local classification prompts receive project metadata as data and distinguish feature behavior changes from standalone content. The Ollama worker guard also checks project-aware implementation intent.
- On Run/resume, historical automatic text assignments can be repaired when no worker has executed and no plan exists. Repair requires the original Created permission log to authorize the corrected access. Untouched direct steps, if any, are corrected atomically with the goal/session; pending decisions are invalidated. Manual assignments, completed/running work, accepted plans, prior usage, deadlines, attempts and model/provider permissions are preserved.
- Existing repair behavior for historical Ollama classification/extraction results remains available.

No current database records were changed during implementation. After rebuilding/reopening the app, choose Run/resume on the blocked task. Existing provider/model availability and project-write permissions still apply. Ambiguous wording outside these narrow rules continues through the optional classifier; this is not a guarantee that all natural-language requests will be understood correctly.
