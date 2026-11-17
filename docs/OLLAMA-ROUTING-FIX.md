# Ollama task-intent routing correction

Root cause observed in read-only production task history: requests to change software were automatically assigned Classification / TextOnly. A subsequent local classification result was mistaken for completion of the original goal. Ollama was not editing project files; its configured qualification supports classification only.

Changes:
- Internal assignment has an explicit classifier role. Ollama receives the original user task as JSON and a distinct kind+access contract instead of classifying nested classification instructions.
- Automatic Classification/Extraction assignments are rejected unless the user's actual request explicitly asks for that data operation. Invalid local assignment can use the existing bounded permitted-cloud fallback, or stop visibly.
- More explicit software/UI changes are recognized deterministically, including 'sửa tool'. Checking the Ollama integration routes to project review rather than another text classification.
- Both router and local adapter reject implementation requests in the text-processing path; the adapter also rejects unsupported work kinds. No local coding qualification or project-write permission is invented.
- Incorrectly finished, automatically classified text-only Ollama goals can be repaired atomically. Original output/history/call counts/deadline are retained; only the access grant explicitly recorded at task creation is restored. Old text results become historical coordinator records, not successful implementation samples. User-confirmed Completed goals, actual code worker results and legitimate data-classification requests are untouched. Repair pauses work and requires explicit Run / resume, never launches AI.
- --repair-routing takes a SQLite backup under Data/backups, acquires the normal instance lease and writes routing-repair-report.json. Explicit resume also applies eligible repairs.

Application-only Debug compilation; no test suite, benchmark, UI smoke or model inference is run for this fix. State repair is a requested real data correction, not an execution of coding tasks. Runtime AI coding results remain unverified.
