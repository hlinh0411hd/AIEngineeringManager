# Telegram notifications and chat control

AI Employee Manager can message you on Telegram when a task stops, and you can answer in the same chat to give more work, ask for changes, finish or close the task. Sources: `src/Employee.Core/TelegramCommand.cs`, `TaskStopNotices.cs`, `TaskSummary.cs`, `src/Employee.Infrastructure/TelegramBotClient.cs`, `TelegramNotifier.cs`, `TelegramCommandService.cs`, `src/Employee.Desktop/MainWindow.Telegram.cs`.

## 1. Overview

- **Stop notifications.** When a task reaches a state that needs your attention (below), the app sends one message to one Telegram chat. It uses the same stop detection as the Windows notifications.
- **Chat control.** The app long-polls the bot for messages from that chat. A reply to a notification, or a command, acts on the task: comment and requeue, run again, mark completed, or close. `/projects` and `/new` also create new tasks from the chat.

Telegram is off until you enable it and enter a token and a chat ID. It is independent of the Windows notification switch.

## 2. Setup

1. In Telegram, open **@BotFather**, send `/newbot`, follow the prompts and copy the bot token it gives you.
2. In the app open **Settings > Notifications > Telegram**. Paste the token in **Bot token**. Once saved, the box shows a mask and "A token is saved (hidden)". Type a new token to replace it.
3. In Telegram, open your new bot and send `/start` to it.
4. Back in the app, click **Find chat ID**. It reads the newest message sent to the bot and fills **Allowed chat ID** with that chat's ID. Or type the chat ID by hand (group chats have negative IDs). If you use **Find chat ID**, check that the number is yours: it takes the latest message from anyone who wrote to the bot. The button only fills the box and does not save.
5. Tick **Enable Telegram notifications**, choose whether to keep **Notify on completed tasks** on (default on), then click **Save Telegram settings**.
6. Click **Send test message**. It saves the current form, checks the token with Telegram and sends "AI Employee Manager: Telegram test message." to the chat. The result appears in the status line, for example "Connected as @yourbot; test message sent."

Notifications and command polling both need Enabled, a saved token and a non-zero chat ID. Saving the settings restarts the poller.

## 3. What gets a notification

One message is sent when a top-level task changes into one of these states. A task is announced again only when its state, or its set of pending approval decisions, changes.

| Message title | When |
|---|---|
| Approval needed | The task has pending approval decisions |
| Approval needed to continue | State AwaitingApproval |
| Permission needed | State WaitingForPermission |
| Result ready for review | State NeedsReview |
| Task failed | State Failed |
| Task paused | State Paused |
| Task completed | State Completed, only while **Notify on completed tasks** is on |
| Wrong access - correct task permissions | State WrongAccess |
| Wrong project - correct project/folder | State WrongProject |
| Waiting for AI - see usage/auth/availability reason | State WaitingForProvider |

Not announced: Running, Queued, Saved, Skipped, Delegated, and tasks you closed yourself (including `/close`, so a close from Telegram is not echoed back). Tasks already in a stopped state when the app starts are not sent, and a Completed task seen for the first time is not sent.

A message contains:

1. **Title and short ID:** `Result ready for review · #a1b2c3d4`. The short ID is the first 8 characters of the task ID.
2. **Request excerpt:** `Task: ...`, the first 100 characters of the request on one line.
3. **Summary** (rule-based, no AI call, same text as `/status`): what is done (`Done (n/m): ...`), what is not done with the reason for each (`Not done (n): ... [State: reason]`), skipped steps, `Why it stopped`, `Tests`, and an `AI result:` excerpt of the last AI answer (up to 250 characters). For a milestone plan it also shows the current milestone and how many are accepted.
4. **Command hints:** tappable commands for that task, for example `/continue_a1b2c3d4  /done_a1b2c3d4  /redo_a1b2c3d4  /close_a1b2c3d4  /status_a1b2c3d4`. `/run_<id>` is added for Failed and Paused tasks. Completed tasks show only `/redo_<id>` and `/status_<id>`.

5. **Inline buttons:** every notification also carries a button keyboard under the message (section 4). The text command hints stay in the message too, so typed and tappable commands keep working.

The app remembers which Telegram message announced which task, so a reply to that message goes to that task.

## 4. Commands

Messages are matched in this order to find the task: the message you replied to, then a `#shortid` in the text or a `_shortid` command suffix. There is no "latest task" fallback. If no task is found the bot answers "I could not tell which task you mean."

| You send | Effect |
|---|---|
| Reply to a notification with plain text | Adds your text as a comment and queues the task to continue. If the task is busy or closed, the bot answers with the reason. |
| `ok` or `duyệt` as a reply | Plain text like any other comment, but it is read as approval, so it signs off a milestone waiting for your approval. |
| `/continue [text]` | With text: same as a plain reply. Without text: queues the task to run again. |
| `/redo <text>` | Asks for changes: adds the text as a comment and queues the task. |
| `/redo` or `/redo_<id>` with no text | The bot sends a force-reply prompt "What should change?". Your reply to that prompt becomes the comment for that task. |
| `/done` | Marks the task completed ("complete anyway"). |
| `/close [reason]` | Closes the task as Cancelled. Without a reason it records "Closed from Telegram". |
| `/run` | Queues the task to run. |
| `/status` | Shows the state, request (200 characters) and the summary with the AI result. |
| `/projects` | Lists the registered projects, numbered. Needs no task. |
| `/new <project> <request>` | Creates a task in a project and queues it. The project is its number, name or alias (longest match wins). Needs no task. |
| `/help`, `/start` | Show the command list. Any unknown command gets "Unknown command." and the list. |

### Inline buttons

Each notification has inline buttons that do the same as the typed commands. Which buttons appear depends on the task state:

| Task state | Buttons |
|---|---|
| Completed | Continue, Status |
| Failed or Paused | Continue, Done, Redo / Close, Run, Status |
| Any other notified state | Continue, Done, Redo / Close, Status |

- **Continue** queues the task to run again (same as `/continue` without text). **Status** shows the state and summary (same as `/status`). **Run** (Failed and Paused only) queues the task (same as `/run`).
- **Confirm step for Done and Close.** The first tap on **Done** or **Close** runs nothing. It replaces the buttons with **Confirm done** (or **Confirm close**) and **Cancel**, and the button popup says to tap Confirm. Only the Confirm tap runs the action (complete anyway, or close as Cancelled with the reason "Closed from Telegram"); the buttons are then removed from the message. **Cancel** changes nothing and puts the standard buttons back, but without **Run**, even for a Failed or Paused task. Use `/run_<id>` or `/run` for that task, or wait for the next notification.
- **Redo opens a reply prompt.** Tapping **Redo** sends a force-reply message "What should change? Reply to this message with the details (#id)." Your reply to that prompt is added as a comment on that task and the task is queued, like `/redo <text>`. Nothing changes until you reply.
- **Feedback.** Every tap is answered with a short popup. Continue, Status, Run and Confirm also send the result as a reply in the chat. A button for a task that no longer exists answers "Task not found." and a button the app does not recognise answers "Unknown button."
- **Typed commands still work.** Buttons are an addition. Replies, `/done`, `/redo <text>`, `/close [reason]`, the tappable `/command_<id>` forms and the rest of the table above behave as described. Typed `/done` and `/close` have no confirm step.
- **Old messages.** A button tap looks the task up by the short ID in the button, so buttons on older notifications keep working while the task still exists.

Notes:

- **Tappable forms.** `/done_a1b2c3d4`, `/continue_...`, `/redo_...`, `/close_...`, `/run_...` and `/status_...` carry the task ID, so one tap works without a reply. You can also write `#a1b2c3d4` anywhere in the text, for example `/done #a1b2c3d4` or `#a1b2c3d4 make the button larger`. An explicit `#id` wins over a `_id` suffix.
- **Short ID matching.** A short ID is 4 to 32 hex characters and must match exactly one task, otherwise it counts as not found. Only top-level tasks and goals can be targeted, not their child steps.
- **Bot name suffix.** `/done@yourbot` works. The name after `@` is ignored.
- **Commands are case-insensitive** for the command word.
- **`/new` defaults.** Coding task with **Write project** access, automatic AI assignment, queued at once. The request is limited to 12000 characters. Whoever can write in the chat can therefore start tasks that edit the project files.
- **Errors.** When an action is refused (for example the task is running, or already finished), the bot replies with the app's message. An unexpected failure replies "That action failed. See the app for details." The bot answers each command as a reply to your message.

## 5. Security and limits

- **Token storage.** The bot token is encrypted with Windows DPAPI for the current Windows user before it is saved in the app settings. The plain token is never shown again, never logged, and is only sent to `api.telegram.org`. Error text that reaches the status line has the token (and anything shaped like a bot URL) replaced by `***`.
- **Other users and machines.** A token blob copied to another Windows user or another machine cannot be decrypted. The app then shows "Telegram bot token cannot be decrypted; enter it again." Enter the token again there.
- **One chat only.** Only messages from the single **Allowed chat ID** are handled. Every other chat is ignored silently: no action and no reply. While the chat ID is empty (0), all incoming messages are ignored. This also applies to button taps: a tap is accepted only when it comes from the paired chat. A tap from any other chat, or while no chat is paired, runs no action and changes no buttons. It only gets an empty answer so Telegram stops the loading indicator.
- **The chat ID is the only authentication.** There is no password or second factor. Anyone who can write in that chat (for example every member of a group chat ID) can comment on, finish, close, run and create tasks. Use a private chat with the bot, and do not add other people to it.
- **Data leaves the machine.** Messages travel through Telegram's servers. Task titles, request excerpts, summaries and AI result excerpts are sent there, and what you type back goes the other way. Do not enable it for projects whose content must stay local.
- **Message length.** Text is cut at 4000 characters (3999 plus "…"), below Telegram's 4096 limit.
- **Only while the app is open.** Polling and sending run inside the desktop app. Nothing is sent or received while it is closed. Messages you send meanwhile wait at Telegram and are handled after the app starts again (Telegram keeps undelivered updates for a limited time).
- **State files in the data folder.**
  - `telegram-state.json` holds the next `getUpdates` offset, so handled messages are not processed twice after a restart.
  - `telegram-messages.json` maps Telegram message IDs to task IDs for replies. It keeps the newest 1000 entries.
  - Neither file contains the token. Deleting `telegram-messages.json` only means older notifications can no longer be answered by reply (use `#id` or a tappable command instead).
- **Redirects** from Telegram's API are not followed.

## 6. Network errors and retries

**Sending notifications.** Messages are sent one at a time, in order, on a background task, so the UI and task processing never wait for the network or fail because of it.

- **Retried (up to 3 attempts):** network errors, timeouts, HTTP 5xx and 429. The wait is 2 s before the second attempt and 5 s before the third. For 429 the `retry_after` value from Telegram is used instead, between 1 and 60 seconds.
- **Not retried:** 400 "chat not found", 401, 403 and 404 and any other 4xx. They fail at once.
- **Reporting:** failures show in the status line, never as a dialog. After three failed attempts you get one status line, such as "Telegram notification failed: ...". A permanent problem is reported once, and again only after a send has worked in between:
  - 401 or 404: "Telegram rejected the bot token. Enter a valid token in the Telegram settings."
  - 403 or chat not found: "Bot cannot message this chat; send /start to the bot, then check the allowed chat ID."
- A message that finally fails is dropped, not queued for later. The message-to-task mapping is written only after a successful send.
- Each request times out after 30 seconds.

**Receiving commands.**

- The app long-polls `getUpdates` (25 s wait, 30 s extra for the HTTP request). On an error it waits 5 s, then doubles the wait up to 60 s. A successful poll resets the wait.
- **401 or 404:** the wait goes straight to 60 s and the status line says "Telegram rejected the bot token; command polling is slowed down. Enter a valid token in the Telegram settings." This is shown once until a poll succeeds.
- **429:** the loop waits for `retry_after` (at least 1 s).
- A failing command or reply never stops the loop. The offset is saved after every message, so a message whose handling failed is not retried. Send the command again.
- While Telegram is disabled, or the token or chat ID is missing, the loop idles and checks the settings every 3 seconds.

## 7. Troubleshooting

- **"Telegram rejected the bot token" or a 401/404 error.** The token is wrong, was revoked, or has extra characters. Copy it again from @BotFather (`/mybots`, choose the bot, API Token), paste it in **Bot token**, then **Save Telegram settings**.
- **"Bot cannot message this chat", 403, or "chat not found".** You have not sent `/start` to the bot from that chat, you blocked the bot, or the chat ID is wrong. Open the bot, unblock it if needed, send `/start`, click **Find chat ID** and save. Groups need the bot added to the group, and the ID is negative.
- **Test works but no notifications arrive.** Check **Enable Telegram notifications** is ticked and saved, and that the state is one from section 3. Completed tasks need **Notify on completed tasks**. Tasks already stopped when the app started are not sent.
- **Notifications arrive but the bot ignores your replies.** The app must be open and the chat ID must be the one you are writing from. Messages from any other chat get no answer at all. Reply to the notification message itself, or include `#shortid`. Another program polling the same bot token with `getUpdates` (or a webhook set on the bot) conflicts with it.
- **"I could not tell which task you mean."** You wrote a command without replying to a notification and without an ID. Reply to the notification, or use the tappable `/command_<id>` form.
- **"Find chat ID" says no messages found.** Send `/start` to your bot first, then click it again. It reads only the newest pending message.
- **"Telegram bot token cannot be decrypted; enter it again."** The settings came from another Windows user or machine. Enter the token again.
