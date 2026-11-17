# Task result review — 2026-11-01

The Tasks view previously replaced streamed output with a generic NeedsReview message after a run, forcing the user to discover raw history elsewhere.

The task now displays the saved AI response directly. **Xem kết quả / file thay đổi** opens a dedicated review window with four tabs: readable AI response, saved Git status/diff and edit proposals, practical verification guidance, and complete logs. The window can open the registered project folder. Codex JSONL agent messages are extracted without presenting internal command events as the final answer. Legacy plain responses remain readable; results from a prior run are hidden after a newer route starts.

Existing tasks use their persisted logs/checkpoint; no rerun or database migration is needed. Git snapshots are labeled as project-wide and may include pre-existing changes. Missing/truncated diffs are not proof that no files changed. A successful provider exit is still not independent verification of the requested work. For Codex direct writes, Apply reviewed edits is unnecessary unless a separate structured proposal exists.

Run.bat now selects the newest Debug/Release output by application assembly timestamps rather than always choosing Release. This makes a verified Debug fix usable even when an older Release build exists, without automatically creating a Release build.

Verification: Debug build passed with zero warnings/errors, 26 executable checks passed including persisted Codex response extraction, plain-output compatibility and stale-run handling. WPF smoke run rendered the task view and the four-tab review window using isolated fixture data. No AI inference, Release build, archive or modification of production task data was performed.

To use: close the old application, launch Run.bat, select the existing task, then select Xem kết quả / file thay đổi. Inspect the result and run the actual project if appropriate before choosing Mark reviewed complete.
