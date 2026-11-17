---
name: employee-marketing
description: Prepare marketing project/media workflows, drafts and export packages through the connected Marketing backend; publishing requires separate explicit approval.
---

# Connected Marketing backend

Operate only through the manager's `marketing` connector. The user configures the loopback endpoint and session token in Settings; never request credentials in task text, read account token files or replace backend authentication. Use existing project/media/job IDs from user input or declared `projects`, `jobs`, `job.status` results. Do not invent IDs.

**Every post in this backend is built around a real video** (it is a video-first tool for game marketing). Text-only posts cannot be stored: a request without a video in the project's Media Library ends with `ACTION_REQUIRED` ("Media Library chưa có video thật..."). So: first get a video (a TrailerForge export, a recorded gameplay clip the user gave), `media.import` it, then create content from it. When no video exists and none can be made, keep the drafts as files in the project and say that the marketing tool needs a video, instead of calling it.

Input preparation returns only `{"toolInput":{...}}` for ONE operation:

- status/projects/jobs/platforms/queue: `{}`. `queue` lists every platform post ("target") with its id, platform, status and content; `platforms` lists what the backend supports.
- project.create: `{"name":"requested project","description":"...","language":"vi","project_type":"game|app|tool|other","short_description":"...","features":["..."],"links":["https://..."]}`. Only `name` is required.
- media.import: `{"project_id":"known ID","path":{"$from":"video.artifactPath"}}`; declare the video dependency.
- content.create: the backend's own AI writes the posts around the project's imported video. `"content_mode":"upload"` uses one imported video as is: `{"project_id":"known ID","content_mode":"upload","media_id":"known ID","platforms":["facebook","x"],"review":true,"publish_action":"draft"}`. `"autocut"` lets its AI pick clips from that video (optional `clip_count`, `clip_duration`, `aspect`). `"create"` edits a new video from the Media Library with TrailerForge from a `prompt`; it also needs imported footage and the TrailerForge editor configured in the backend. Platforms: youtube, facebook, x, tiktok, bluesky. Always `"publish_action":"draft"` unless the user asked to schedule or post. The manager waits for the task and returns it with the jobs it created in `task.result.jobs`.
- task.status: `{"taskId":"known ID"}`.
- job.create: `{"project_id":"known ID","media_id":"known ID","platforms":["youtube"],"export_mode":"reference"}` (a job always needs an imported video).
- job.status/job.run: `{"jobId":"known ID","body":{}}`. A job's `targets` array holds the per-platform posts and their ids.
- content.set: `{"jobId":"known ID","body":{"content":"requested draft"}}`.
- target.content: `{"targetId":"known ID","body":{"fields":{"caption":"new text"}}}`; use the field names the target already has.
- review: `{"jobId":"known ID","body":{"gate":"content","decision":"approve","revision":1}}`; obtain the actual revision/gate from backend state, never assume the example revision.
- export: `{"jobId":"known ID"}`.
- schedule (whole job) / target.schedule (one post): `{"jobId":"known ID","body":{"scheduled_at":"2026-12-01T09:00:00+07:00"}}` or `{"targetId":"known ID","body":{"scheduled_at":"..."}}`. The time must be in the future and carry a UTC offset; `null` clears the schedule.
- target.publish (older name: youtube.publish): `{"targetId":"known ID","body":{"confirmed":true}}`, one platform post at a time, only when the original user request authorizes publishing. The manager additionally requires explicit per-request approval and publishing permission, and never plans this by itself. The post goes to the account connected in the backend; the backend's review gates still apply.

Read existing state before creating duplicate projects/jobs. Pass IDs via declared output references and keep draft generation separate from publishing. Reuse the same approved media; status/polling should not use a new AI call. job.run can invoke backend AI and uses the manager call budget, whose count cannot measure all backend internal calls. Review outputs/links before completion; do not claim a scheduled/exported package is already published.
