using System.Net;
using System.Text;
using System.Text.Json;
using Employee.Core;
using Employee.Infrastructure;

// Stop-notice titles, DPAPI token handling, the Telegram client and the notifier, all against a fake HTTP handler.
static class TelegramChecks
{
    private sealed class FakeHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Url, string Body)> Calls { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancel);
            Calls.Add((request.RequestUri!.ToString(), body));
            return respond(request, body);
        }
    }

    private sealed class SlowHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            await Task.Delay(5000, cancel);
            return new(HttpStatusCode.OK);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    public static int Run()
    {
        const string Secret = "123456:SECRET-token_value";
        // Stop notices keep the exact toast titles.
        var expected = new (WorkState State, string? Title)[]
        {
            (WorkState.WrongAccess, "Wrong access - correct task permissions"),
            (WorkState.WrongProject, "Wrong project - correct project/folder"),
            (WorkState.WaitingForProvider, "Waiting for AI - see usage/auth/availability reason"),
            (WorkState.AwaitingApproval, "Approval needed to continue"),
            (WorkState.WaitingForPermission, "Permission needed"),
            (WorkState.NeedsReview, "Result ready for review"),
            (WorkState.Failed, "Task failed"),
            (WorkState.Paused, "Task paused"),
            (WorkState.Completed, "Task completed"),
            (WorkState.Running, null),
            (WorkState.Queued, null)
        };
        foreach (var (state, title) in expected)
        {
            var work = new WorkItem { Request = "line one\r\nline two", State = state };
            var notice = TaskStopNotices.Evaluate(work, [], state == WorkState.Completed ? "Running:" : null);
            Require(notice?.Title == title, $"Title for {state}: {notice?.Title}");
            Require(notice == null || notice.RequestExcerpt == "line one  line two", "Excerpt must flatten line breaks");
        }

        var item = new WorkItem { Request = new string('x', 300), State = WorkState.NeedsReview };
        Require(TaskStopNotices.Evaluate(item, ["d1"], null)?.Title == "Approval needed", "Pending decisions title");
        Require(TaskStopNotices.Evaluate(item, [], null)!.RequestExcerpt.Length == 100, "Excerpt limit");
        Require(TaskStopNotices.Evaluate(item, [], TaskStopNotices.Signature(item, [])) == null, "Unchanged state repeated");
        Require(TaskStopNotices.Evaluate(item, ["d1"], "NeedsReview:d1,d2") == null, "Resolved decision must not reannounce");
        Require(TaskStopNotices.Evaluate(new WorkItem { State = WorkState.Completed }, [], null) == null, "First sighting of Completed");

        // The summary says what is done, what is not and why, in a few short lines.
        var summaryGoal = new WorkItem { Request = "Build the app", State = WorkState.NeedsReview, Verification = VerificationState.PartiallyTested };
        var summarySteps = new[]
        {
            new WorkItem { Request = "Add the dark theme\nwith details", State = WorkState.Completed },
            new WorkItem { Request = "Update the settings page", State = WorkState.Completed },
            new WorkItem { Request = "Fix the failing tests", State = WorkState.Failed, Notes = "Test project does not build" },
            new WorkItem { Request = "Old idea", State = WorkState.Skipped },
            new WorkItem { Request = "Tester", State = WorkState.Completed, VerifyScope = "goal" }
        };
        var summaryText = TaskSummary.Build(summaryGoal, summarySteps, null, []);
        Require(summaryText.Contains("Done (2/3): Add the dark theme; Update the settings page"), "Summary done line: " + summaryText);
        Require(summaryText.Contains("Not done (1): Fix the failing tests [Failed: Test project does not build]") || summaryText.Contains("Not done (1): Fix the failing tests [Failed"), "Summary not-done line: " + summaryText);
        Require(summaryText.Contains("Skipped (not needed): 1") && !summaryText.Contains("Tester"), "Summary skipped/tester: " + summaryText);
        Require(summaryText.Split('\n').Length <= 7, "Summary must stay short: " + summaryText);
        var summaryNotice = new StopNotice("abcdef1234567890", StopNoticeKind.NeedsReview, "Result ready for review", "Build the app", WorkState.NeedsReview) { Summary = summaryText };
        Require(TelegramNotifier.Format(summaryNotice).Contains("Not done (1)"), "Telegram message must carry the summary");
        Require(!TelegramNotifier.Format(summaryNotice with { Summary = "" }).Contains("Done ("), "No summary, no summary text");

        // The token is stored only as a DPAPI blob.
        var settings = new ManagerSettings();
        TelegramSecrets.SetToken(settings.Telegram, Secret);
        var json = JsonSerializer.Serialize(settings);
        Require(!json.Contains(Secret) && !json.Contains("SECRET-token"), "Token serialized as plaintext");
        Require(TelegramSecrets.TryGetToken(JsonSerializer.Deserialize<ManagerSettings>(json)!.Telegram, out var back, out _) && back == Secret, "Token round trip");
        Require(!TelegramSecrets.TryGetToken(new() { ProtectedBotToken = "bm90LWRwYXBp" }, out _, out var bad) && !bad.Contains("bm90"), "Undecryptable token must report an error");

        // Client: escaping, message id, getMe, getUpdates.
        var handler = new FakeHandler((r, _) => r.RequestUri!.AbsolutePath.EndsWith("/getMe") ? Json("{\"ok\":true,\"result\":{\"id\":7,\"username\":\"mybot\"}}")
            : r.RequestUri.AbsolutePath.EndsWith("/getUpdates") ? Json("{\"ok\":true,\"result\":[{\"update_id\":5,\"message\":{\"message_id\":9,\"chat\":{\"id\":42},\"text\":\"ok\",\"reply_to_message\":{\"message_id\":3}}},{\"update_id\":6,\"message\":{\"message_id\":10,\"chat\":{\"id\":42}}}]}")
            : Json("{\"ok\":true,\"result\":{\"message_id\":77}}"));
        using (var client = new TelegramBotClient(Secret, handler))
        {
            Require(client.GetMeAsync(default).Result.Username == "mybot", "getMe");
            Require(client.SendMessageAsync(42, "a <b>&</b> " + new string('z', 5000), null, default).Result == 77, "sendMessage id");
            var sent = JsonDocument.Parse(handler.Calls[^1].Body).RootElement;
            var text = sent.GetProperty("text").GetString()!;
            Require(text.StartsWith("a &lt;b&gt;&amp;&lt;/b&gt; ") && sent.GetProperty("parse_mode").GetString() == "HTML" && text.Length < 4200, "HTML escaping/truncation");
            var updates = client.GetUpdatesAsync(5, 0, default).Result;
            Require(updates.Count == 1 && updates[0].ChatId == 42 && updates[0].ReplyToMessageId == 3 && updates[0].UpdateId == 5, "getUpdates parsing");
            Require(handler.Calls[^1].Url.Contains("/bot" + Secret + "/getUpdates"), "Telegram URL shape");
        }

        // Errors never reveal the token.
        using (var client = new TelegramBotClient(Secret, new FakeHandler((_, _) => Json("{\"ok\":false,\"error_code\":401,\"description\":\"Unauthorized " + Secret + "\"}", HttpStatusCode.Unauthorized))))
        {
            var ex = Assert(() => client.SendMessageAsync(1, "x", null, default).GetAwaiter().GetResult());
            Require(ex is TelegramApiException { Code: 401 } && !ex.Message.Contains("SECRET-token"), "API error leaked token: " + ex.Message);
        }

        using (var client = new TelegramBotClient(Secret, new FakeHandler((r, _) => throw new HttpRequestException("failed " + r.RequestUri))))
        {
            var ex = Assert(() => client.GetMeAsync(default).GetAwaiter().GetResult());
            Require(ex is TelegramApiException && !ex.Message.Contains("SECRET-token") && ex.InnerException == null, "Transport error leaked token: " + ex.Message);
        }

        using (var client = new TelegramBotClient(Secret, new SlowHandler(), TimeSpan.FromMilliseconds(50)))
            Require(Assert(() => client.GetMeAsync(default).GetAwaiter().GetResult()).Message.Contains("timed out"), "Timeout");

        // Notifier.
        var folder = Path.Combine(Path.GetTempPath(), "TelegramChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var config = new TelegramSettings { Enabled = true, AllowedChatId = 42 };
        TelegramSecrets.SetToken(config, Secret);
        var messages = new List<string>();
        var ok = new FakeHandler((_, _) => Json("{\"ok\":true,\"result\":{\"message_id\":501}}"));
        var notifier = new TelegramNotifier(folder, () => config, messages.Add, token => new(token, ok));
        var review = new StopNotice("abcdef0123456789", StopNoticeKind.NeedsReview, "Result ready for review", "Do <it>", WorkState.NeedsReview);
        Require(notifier.NotifyAsync(review).Result && ok.Calls.Count == 1, "Notice not sent");
        Require(notifier.Messages.WorkIdFor(42, 501) == "abcdef0123456789" && File.Exists(Path.Combine(folder, "telegram-messages.json")), "message_id not recorded against the task");
        Require(JsonDocument.Parse(ok.Calls[0].Body).RootElement.GetProperty("text").GetString()!.Contains("#abcdef01"), "Short task id missing");
        var firstBody = JsonDocument.Parse(ok.Calls[0].Body).RootElement;
        Require(firstBody.GetProperty("chat_id").GetInt64() == 42 && firstBody.GetProperty("text").GetString()!.Contains("Do &lt;it&gt;"), "Allowed chat or excerpt missing");
        Require(firstBody.GetProperty("text").GetString()!.Contains("/done") && firstBody.GetProperty("text").GetString()!.Contains("Reply to this message"), "Command hints missing from the notice");
        foreach (var (kind, state) in new[] { (StopNoticeKind.Failed, WorkState.Failed), (StopNoticeKind.Paused, WorkState.Paused) })
        {
            var work = new WorkItem { Request = "Fix the build", State = state };
            var notice = TaskStopNotices.Evaluate(work, [], null);
            var before = ok.Calls.Count;
            Require(notice != null && notifier.NotifyAsync(notice).Result && ok.Calls.Count == before + 1, $"{state} must send exactly one message");
            var body = JsonDocument.Parse(ok.Calls[^1].Body).RootElement;
            Require(body.GetProperty("chat_id").GetInt64() == 42 && body.GetProperty("text").GetString()!.Contains("#" + work.Id[..8]) && body.GetProperty("text").GetString()!.Contains("Fix the build"), $"{state} message content");
            Require(TaskStopNotices.Evaluate(work, [], TaskStopNotices.Signature(work, [])) == null, $"Re-observing {state} must send nothing");
        }

        config.NotifyCompleted = true;
        var beforeDone = ok.Calls.Count;
        Require(notifier.NotifyAsync(review with { Kind = StopNoticeKind.Completed }).Result && ok.Calls.Count == beforeDone + 1, "Completed must send when NotifyCompleted is on");
        config.NotifyCompleted = false; var baseline = ok.Calls.Count;
        Require(!notifier.NotifyAsync(review with { Kind = StopNoticeKind.Completed }).Result && ok.Calls.Count == baseline, "Completed notice sent while switched off");
        config.Enabled = false;
        Require(!notifier.NotifyAsync(review).Result && ok.Calls.Count == baseline, "Sent while disabled");
        config.Enabled = true;
        config.AllowedChatId = 0;
        Require(!notifier.NotifyAsync(review).Result && ok.Calls.Count == baseline, "Sent while not paired");
        config.AllowedChatId = 42;
        var broken = new TelegramNotifier(folder, () => config, messages.Add, token => new(token, new FakeHandler((_, _) => throw new HttpRequestException("down " + token))), [TimeSpan.Zero, TimeSpan.Zero]);
        Require(!broken.NotifyAsync(review).Result && messages.Count == 1 && messages[0].StartsWith("Telegram notification failed") && !messages[0].Contains("SECRET-token"), "Failure must surface as status text without the token");
        // Retry: transient errors get at most 3 attempts; permanent ones exactly 1 and one clear status line.
        var zero = new[] { TimeSpan.Zero, TimeSpan.Zero };
        var flaky = 0;
        var flakyHandler = new FakeHandler((_, _) => ++flaky < 3 ? Json("{\"ok\":false,\"error_code\":502,\"description\":\"Bad Gateway\"}", HttpStatusCode.BadGateway) : Json("{\"ok\":true,\"result\":{\"message_id\":777}}"));
        var retryMessages = new List<string>();
        var retrying = new TelegramNotifier(folder, () => config, retryMessages.Add, token => new(token, flakyHandler), zero);
        Require(retrying.NotifyAsync(review).Result && flakyHandler.Calls.Count == 3 && retryMessages.Count == 0 && retrying.Messages.WorkIdFor(42, 777) == review.WorkId, "5xx must retry and succeed on attempt 3 with the message_id recorded");
        var limited = 0;
        var limitedHandler = new FakeHandler((_, _) => ++limited == 1 ? Json("{\"ok\":false,\"error_code\":429,\"description\":\"Too Many Requests\",\"parameters\":{\"retry_after\":1}}", (HttpStatusCode)429) : Json("{\"ok\":true,\"result\":{\"message_id\":778}}"));
        var limiter = new TelegramNotifier(folder, () => config, retryMessages.Add, token => new(token, limitedHandler), zero);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        Require(limiter.NotifyAsync(review).Result && limitedHandler.Calls.Count == 2 && timer.Elapsed >= TimeSpan.FromMilliseconds(900), "429 must wait retry_after then retry");
        var always500 = new FakeHandler((_, _) => Json("{\"ok\":false,\"error_code\":500,\"description\":\"boom\"}", HttpStatusCode.InternalServerError));
        var exhausted = new TelegramNotifier(folder, () => config, retryMessages.Add, token => new(token, always500), zero);
        Require(!exhausted.NotifyAsync(review).Result && always500.Calls.Count == 3 && retryMessages.Count == 1, "Transient failure must stop after 3 attempts with one status line");
        var denied = new FakeHandler((_, _) => Json("{\"ok\":false,\"error_code\":403,\"description\":\"Forbidden: bot was blocked by the user\"}", HttpStatusCode.Forbidden));
        var blocked = new TelegramNotifier(folder, () => config, retryMessages.Add, token => new(token, denied), zero);
        var beforeMap = File.ReadAllText(Path.Combine(folder, "telegram-messages.json"));
        Require(!blocked.NotifyAsync(review).Result && !blocked.NotifyAsync(review).Result && denied.Calls.Count == 2 && retryMessages.Count == 2 && retryMessages[1].Contains("send /start") && File.ReadAllText(Path.Combine(folder, "telegram-messages.json")) == beforeMap, "403 must not retry, be reported once, and record no mapping");
        var badToken = new FakeHandler((_, _) => Json("{\"ok\":false,\"error_code\":401,\"description\":\"Unauthorized\"}", HttpStatusCode.Unauthorized));
        var rejected = new TelegramNotifier(folder, () => config, retryMessages.Add, token => new(token, badToken), zero);
        Require(!rejected.NotifyAsync(review).Result && badToken.Calls.Count == 1 && retryMessages[^1] == "Telegram rejected the bot token. Enter a valid token in the Telegram settings." && !retryMessages[^1].Contains("SECRET"), "401 must not retry and give a token-free hint");
        Require(!TelegramBotClient.Redact("GET https://api.telegram.org/bot123456:ABC-def_ghi/getMe failed", "other").Contains("ABC-def"), "Redact must mask bot URL segments");
        var routed = new FakeHandler((r, _) => r.RequestUri!.AbsolutePath.EndsWith("/getMe")
            ? Json("{\"ok\":true,\"result\":{\"id\":7,\"username\":\"mybot\"}}")
            : Json("{\"ok\":true,\"result\":{\"message_id\":9}}"));
        var tester = new TelegramNotifier(folder, () => config, messages.Add, token => new(token, routed));
        var testResult = tester.TestAsync().Result;
        Require(testResult == "Connected as @mybot; test message sent." && routed.Calls.Count == 2, "Test must call getMe then sendMessage: " + testResult);
        config.AllowedChatId = 0;
        Require(tester.TestAsync().Result.StartsWith("Connected as @mybot.") && routed.Calls.Count == 3, "Test without chat id should only call getMe");
        config.AllowedChatId = 42;
        var failing = new TelegramNotifier(folder, () => config, messages.Add, token => new(token, new FakeHandler((_, _) => Json("{\"ok\":false,\"error_code\":401,\"description\":\"Unauthorized " + Secret + "\"}", HttpStatusCode.Unauthorized))));
        var failText = failing.TestAsync().Result;
        Require(failText.StartsWith("Telegram test failed") && !failText.Contains("SECRET-token"), "Test failure must redact token: " + failText);
        Phase2(Secret);
        Phase3(Secret);
        Buttons(Secret);
        Hardening(Secret);
        EndToEnd(Secret);
        Console.WriteLine("PASS Telegram: stop-notice titles and dedupe, DPAPI token never plaintext, client escaping/ids/errors/timeouts without token leaks, notifier send/skip/failure and message_id map");
        return 0;
    }

    private sealed class FakeTasks
    {
        public Dictionary<string, WorkItem> Items { get; } = [];
        public List<string> Calls { get; } = [];
        public Func<string, Exception?>? Fail { get; set; }
        public long? OffsetSeenDuringAction { get; set; }
        public Func<long>? ReadOffset { get; set; }

        private WorkItem Get(string id) => Items.TryGetValue(id, out var w) ? w : throw new InvalidOperationException("Task not found.");
        private void Enter(string call)
        {
            Calls.Add(call);
            OffsetSeenDuringAction = ReadOffset?.Invoke();
            if (Fail?.Invoke(call) is { } ex)
                throw ex;
        }

        public TelegramActions Actions => new(
            prefix => Items.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList() is [var one] ? one : null,
            id => Task.FromResult("State: " + Get(id).State),
            (id, text) =>
            {
                Enter("Comment:" + id + ":" + text);
                var work = Get(id);
                work.Comments.Add(new(text, [], DateTimeOffset.UtcNow));
                work.State = WorkState.Queued;
                return Task.FromResult("Comment added; task queued.");
            },
            id => { Enter("Complete:" + id); Get(id).State = WorkState.Completed; return Task.FromResult("Task completed."); },
            (id, reason) => { Enter("Close:" + id + ":" + reason); Get(id).State = WorkState.Cancelled; return Task.FromResult("Task closed."); },
            id => { Enter("Run:" + id); Get(id).State = WorkState.Queued; return Task.FromResult("Task queued."); });
    }

    private static TelegramMessage M(long update, long chat, int id, string text, int? reply) => new(update, chat, id, text, reply);

    private static void Phase2(string secret)
    {
        // (a) Parser.
        var p = TelegramCommand.Parse("/done");
        Require(p.Kind == TelegramCommandKind.Done && p.ShortId == null && p.Text == "", "/done");
        p = TelegramCommand.Parse("/done_a1b2c3d4");
        Require(p.Kind == TelegramCommandKind.Done && p.ShortId == "a1b2c3d4" && p.Text == "", "tappable /done_id");
        p = TelegramCommand.Parse("/redo_a1b2c3d4");
        Require(p.Kind == TelegramCommandKind.Comment && p.ShortId == "a1b2c3d4" && p.Text == "", "tappable /redo_id has no text yet");
        Require(TelegramCommand.Hints(WorkState.Failed, "a1b2c3d4").Contains("/run_a1b2c3d4") && TelegramCommand.Hints(WorkState.NeedsReview, "a1b2c3d4").Contains("/done_a1b2c3d4"), "tappable hints");
        p = TelegramCommand.Parse("/redo make it bigger");
        Require(p.Kind == TelegramCommandKind.Redo && p.Text == "make it bigger", "/redo text");
        p = TelegramCommand.Parse("/close not needed anymore");
        Require(p.Kind == TelegramCommandKind.Close && p.Text == "not needed anymore", "/close reason");
        Require(TelegramCommand.Parse("/close").Kind == TelegramCommandKind.Close && TelegramCommand.Parse("/close").Text == "", "/close without reason");
        Require(TelegramCommand.Parse("/run").Kind == TelegramCommandKind.Run, "/run");
        Require(TelegramCommand.Parse("/status").Kind == TelegramCommandKind.Status, "/status");
        Require(TelegramCommand.Parse("/help").Kind == TelegramCommandKind.Help, "/help");
        p = TelegramCommand.Parse("/continue add tests");
        Require(p.Kind == TelegramCommandKind.Continue && p.Text == "add tests", "/continue text");
        Require(TelegramCommand.Parse("/continue").Kind == TelegramCommandKind.Continue, "/continue");
        p = TelegramCommand.Parse("/done@MyBot");
        Require(p.Kind == TelegramCommandKind.Done, "@botname suffix on /done");
        p = TelegramCommand.Parse("/redo@MyBot fix X");
        Require(p.Kind == TelegramCommandKind.Redo && p.Text == "fix X", "@botname suffix with args");
        p = TelegramCommand.Parse("/redo #a1b2c3d4 make it bigger");
        Require(p.Kind == TelegramCommandKind.Redo && p.ShortId == "a1b2c3d4" && p.Text == "make it bigger", "#shortid after command: " + p);
        p = TelegramCommand.Parse("#a1b2c3d4 /done");
        Require(p.Kind == TelegramCommandKind.Done && p.ShortId == "a1b2c3d4", "#shortid before command");
        p = TelegramCommand.Parse("please also fix X");
        Require(p.Kind == TelegramCommandKind.Comment && p.Text == "please also fix X" && p.ShortId == null, "plain text is Comment");
        foreach (var word in new[] { "ok", "duyệt" })
        {
            p = TelegramCommand.Parse(word);
            Require(p.Kind == TelegramCommandKind.Comment && p.Text == word, $"'{word}' must be a Comment");
        }

        Require(TelegramCommand.Parse("/projects").Kind == TelegramCommandKind.Projects, "/projects");
        p = TelegramCommand.Parse("/new 2 fix issue #12345 now");
        Require(p.Kind == TelegramCommandKind.New && p.ShortId == null && p.Text == "2 fix issue #12345 now", "/new keeps #hex in the request: " + p);
        var projects = new List<ProjectInfo> { new("a", "Garden", "C:/g", "plants, yard"), new("b", "Garden Tools", "C:/gt"), new("c", "Web", "C:/w") };
        Require(TelegramProjects.List(projects).Contains("2. Garden Tools") && TelegramProjects.List([]).Contains("No projects"), "project list text");
        var r = TelegramProjects.Resolve(projects, "2 do it");
        Require(r.Project?.Id == "b" && r.Request == "do it", "resolve by number");
        r = TelegramProjects.Resolve(projects, "garden tools add a rake");
        Require(r.Project?.Id == "b" && r.Request == "add a rake", "longest name wins");
        r = TelegramProjects.Resolve(projects, "Garden add water");
        Require(r.Project?.Id == "a" && r.Request == "add water", "name match");
        r = TelegramProjects.Resolve(projects, "yard mow");
        Require(r.Project?.Id == "a" && r.Request == "mow", "alias match");
        Require(TelegramProjects.Resolve(projects, "9 x").Project == null && TelegramProjects.Resolve(projects, "Gardenx y").Project == null, "unknown project");
        var seen = new List<string>();
        var projectActions = new TelegramActions(_ => null, _ => Task.FromResult(""), (_, _) => Task.FromResult(""), _ => Task.FromResult(""), (_, _) => Task.FromResult(""), _ => Task.FromResult(""),
            () => Task.FromResult(TelegramProjects.List(projects)), a => { seen.Add(a); return Task.FromResult("created"); });
        Require(projectActions.ListProjects != null && projectActions.NewTask != null, "actions expose project commands");

        // Shared fixture: real WorkItems over fake actions and a fake HTTP handler.
        var folder = Path.Combine(Path.GetTempPath(), "TelegramChecks2", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var config = new ManagerSettings();
        config.Telegram.Enabled = true;
        config.Telegram.AllowedChatId = 42;
        TelegramSecrets.SetToken(config.Telegram, secret);
        var map = new TelegramMessageMap(Path.Combine(folder, "map.json"));
        var offsets = new TelegramOffsetStore(Path.Combine(folder, "offset.json"));
        var tasks = new FakeTasks { ReadOffset = offsets.Load };
        var work = new WorkItem { Request = "Add dark mode", State = WorkState.NeedsReview };
        var other = new WorkItem { Request = "Other", State = WorkState.Failed };
        tasks.Items[work.Id] = work;
        tasks.Items[other.Id] = other;
        map.Record(42, 100, work.Id);
        var sends = new FakeHandler((_, _) => Json("{\"ok\":true,\"result\":{\"message_id\":900}}"));
        var statuses = new List<string>();
        var service = new TelegramCommandService(() => config, map, offsets, tasks.Actions, statuses.Add);
        using var client = new TelegramBotClient(secret, sends);
        List<string> Replies() => sends.Calls.Select(c => JsonDocument.Parse(c.Body).RootElement.GetProperty("text").GetString()!).ToList();
        void Process(params TelegramMessage[] updates) => service.ProcessUpdatesAsync(updates, client, default).GetAwaiter().GetResult();

        // (b) Chat not allowed: no action, no reply, state unchanged, offset advances.
        Process(M(10, 999, 1, "please also fix X", 100), M(11, 999, 2, "/done", 100));
        Require(tasks.Calls.Count == 0 && sends.Calls.Count == 0, "Disallowed chat must trigger no action and no reply");
        Require(work.State == WorkState.NeedsReview && work.Comments.Count == 0, "Disallowed chat changed task state");
        Require(offsets.Load() == 12, "Offset must advance past ignored updates: " + offsets.Load());

        // (c) Reply to a mapped notification becomes a comment, requeues, and sends exactly one reply.
        Process(M(20, 42, 5, "please also fix X", 100));
        Require(tasks.Calls.SequenceEqual([$"Comment:{work.Id}:please also fix X"]), "Reply must call Comment with its text: " + string.Join("|", tasks.Calls));
        Require(work.Comments.Count == 1 && work.Comments[0].Text == "please also fix X" && work.State == WorkState.Queued, "Task must have the comment and be requeued");
        Require(sends.Calls.Count == 1 && JsonDocument.Parse(sends.Calls[0].Body).RootElement.GetProperty("chat_id").GetInt64() == 42, "Exactly one reply expected");

        // (g) Offset is saved after handling: while the action ran it still held the previous value.
        Require(tasks.OffsetSeenDuringAction == 12 && offsets.Load() == 21, $"Offset must be update_id+1 after handling (during={tasks.OffsetSeenDuringAction}, after={offsets.Load()})");

        // (d) /redo #shortid text resolves by short id without a reply mapping.
        tasks.Calls.Clear();
        Process(M(30, 42, 6, $"/redo #{other.Id[..8]} try again with tests", null));
        Require(tasks.Calls.SequenceEqual([$"Comment:{other.Id}:try again with tests"]), "/redo #shortid: " + string.Join("|", tasks.Calls));
        Require(offsets.Load() == 31, "Offset after /redo");

        // Tapped /redo_<id> with no text: no action, one force-reply prompt that maps back to the task.
        tasks.Calls.Clear();
        var sentBefore = sends.Calls.Count;
        Process(M(31, 42, 7, $"/redo_{other.Id[..8]}", null));
        Require(tasks.Calls.Count == 0 && sends.Calls.Count == sentBefore + 1 && sends.Calls[^1].Body.Contains("force_reply"), "/redo_id must prompt with force_reply, not act");

        // (e) /done on the mapped task completes it.
        tasks.Calls.Clear();
        work.State = WorkState.NeedsReview;
        Process(M(40, 42, 7, "/done", 100));
        Require(tasks.Calls.SequenceEqual([$"Complete:{work.Id}"]) && work.State == WorkState.Completed, "/done must Complete the mapped task");

        // (f) Unresolvable target: hint, no action. Action failure: reply with its message, no throw.
        tasks.Calls.Clear();
        var before = sends.Calls.Count;
        Process(M(50, 42, 8, "just some text", null), M(51, 42, 9, "/done #ffffffff", null), M(52, 42, 10, "/done", 12345));
        Require(tasks.Calls.Count == 0 && sends.Calls.Count == before + 3, "Unresolvable targets must reply and call no action");
        Require(Replies().Skip(before).All(r => r.Contains("#id") && r.Contains("which task")), "Hint text missing: " + string.Join("|", Replies().Skip(before)));
        tasks.Fail = _ => new InvalidOperationException("Task is running; stop it first.");
        before = sends.Calls.Count;
        Process(M(60, 42, 11, "/close", 100));
        Require(sends.Calls.Count == before + 1 && Replies()[^1].Contains("Task is running; stop it first."), "InvalidOperationException must become a reply: " + Replies()[^1]);
        Require(offsets.Load() == 61, "Offset after failed action");
        tasks.Fail = _ => new InvalidOperationException("boom " + secret);
        Process(M(70, 42, 12, "/run", 100));
        Require(Replies()[^1].Contains("boom") && !Replies()[^1].Contains(secret), "Action error must be redacted");
        tasks.Fail = null;

        Process(M(80, 42, 13, "/status", 100), M(81, 42, 14, "/help", null));
        Require(Replies()[^2].Contains("State:") && Replies()[^1].Contains("/done") && Replies()[^1].Contains("/continue"), "/status and /help replies");

        // /continue: without text runs the task, with text comments; neither is "Unknown command".
        tasks.Calls.Clear();
        before = sends.Calls.Count;
        work.State = WorkState.NeedsReview;
        Process(M(82, 42, 15, "/continue", 100), M(83, 42, 16, "/continue also add tests", 100));
        Require(tasks.Calls.SequenceEqual([$"Run:{work.Id}", $"Comment:{work.Id}:also add tests"]), "/continue calls: " + string.Join("|", tasks.Calls));
        Require(sends.Calls.Count == before + 2 && Replies().Skip(before).All(r => !r.Contains("Unknown command")), "/continue must send a confirmation reply");

        // (h) No reply or status text contains the token.
        Require(Replies().All(r => !r.Contains(secret) && !r.Contains("SECRET-token")), "Reply contained the bot token");
        Require(statuses.All(t => !t.Contains(secret) && !t.Contains("SECRET-token")), "Status text contained the bot token");
        Require(sends.Calls.All(c => !c.Body.Contains("SECRET-token")), "Request body contained the token");

        // A failing sendMessage must not break handling or the offset, and its status text hides the token.
        using var downClient = new TelegramBotClient(secret, new FakeHandler((_, _) => throw new HttpRequestException("down")));
        statuses.Clear();
        service.ProcessUpdatesAsync([M(90, 42, 15, "/done #" + other.Id[..8], null)], downClient, default).GetAwaiter().GetResult();
        Require(offsets.Load() == 91 && statuses.Count > 0 && statuses.All(t => !t.Contains("SECRET-token")), "Reply failure handling");
    }

    // Restart: the real polling loop saves the offset, and a fresh service over the same file asks for it instead of 0.
    private static void Phase3(string secret)
    {
        var folder = Path.Combine(Path.GetTempPath(), "TelegramChecks3", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var config = new ManagerSettings();
        config.Telegram.Enabled = true;
        config.Telegram.AllowedChatId = 42;
        TelegramSecrets.SetToken(config.Telegram, secret);
        var work = new WorkItem { Request = "r", State = WorkState.NeedsReview };
        var commentCalls = new List<string>();
        var actions = new TelegramActions(_ => work.Id, _ => Task.FromResult("s"), (id, t) => { commentCalls.Add(t); return Task.FromResult("ok"); }, _ => Task.FromResult("d"), (_, _) => Task.FromResult("c"), _ => Task.FromResult("r"));
        var offsetsAsked = new List<long>();
        var handler = new FakeHandler((r, body) =>
        {
            if (!r.RequestUri!.AbsolutePath.EndsWith("/getUpdates"))
                return Json("{\"ok\":true,\"result\":{\"message_id\":1}}");
            var offset = JsonDocument.Parse(body).RootElement.GetProperty("offset").GetInt64();
            lock (offsetsAsked) offsetsAsked.Add(offset);
            Thread.Sleep(20);
            return Json(offset == 0 ? "{\"ok\":true,\"result\":[{\"update_id\":500,\"message\":{\"message_id\":9,\"chat\":{\"id\":42},\"text\":\"more please\"}}]}" : "{\"ok\":true,\"result\":[]}");
        });
        TelegramCommandService Make() => new(() => config, new TelegramMessageMap(Path.Combine(folder, "map.json")), TelegramOffsetStore.InFolder(folder), actions, _ => { }, t => new(t, handler));
        var map = new TelegramMessageMap(Path.Combine(folder, "map.json"));
        var first = Make();
        first.Start();
        var until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until && TelegramOffsetStore.InFolder(folder).Load() != 501)
            Thread.Sleep(50);
        first.Stop();
        Thread.Sleep(300);
        Require(TelegramOffsetStore.InFolder(folder).Load() == 501, "Polling loop did not save offset 501");
        Require(commentCalls.Count == 0, "Unmapped plain text must not comment (no target): " + commentCalls.Count);
        lock (offsetsAsked) offsetsAsked.Clear();
        var second = Make();
        second.Start();
        until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until) { lock (offsetsAsked) if (offsetsAsked.Count > 0) break; Thread.Sleep(50); }
        second.Stop();
        lock (offsetsAsked) Require(offsetsAsked.Count > 0 && offsetsAsked.All(o => o == 501), "After restart getUpdates offsets: " + string.Join(",", offsetsAsked));
    }

    private static Task NoWait(TimeSpan _, CancellationToken __) => Task.Delay(1);
    private static bool WaitFor(Func<bool> condition, int seconds = 15)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until && !condition())
            Thread.Sleep(10);
        return condition();
    }

    private static string UpdatesJson(params (long Update, int Message, string Text)[] items) =>
        "{\"ok\":true,\"result\":[" + string.Join(",", items.Select(i => $"{{\"update_id\":{i.Update},\"message\":{{\"message_id\":{i.Message},\"chat\":{{\"id\":42}},\"text\":{JsonSerializer.Serialize(i.Text)}}}}}")) + "]}";

    private static void Buttons(string secret)
    {
        // Pure: buttons per state and callback_data under 64 bytes.
        static List<string> Data(WorkState state) => TelegramCommand.Buttons(state, "a1b2c3d4").SelectMany(r => r).Select(b => b.Data).ToList();
        foreach (var state in new[] { WorkState.Failed, WorkState.Paused, WorkState.NeedsReview })
        {
            var data = Data(state);
            foreach (var verb in new[] { "continue", "done", "redo", "close", "status" })
                Require(data.Contains(verb + ":a1b2c3d4"), $"{state} must show {verb}");
            Require(data.Contains("run:a1b2c3d4") == (state != WorkState.NeedsReview), $"Run visibility for {state}");
        }

        Require(Data(WorkState.Completed).SequenceEqual(["continue:a1b2c3d4", "status:a1b2c3d4"]), "Completed shows only Continue and Status");
        Require(TelegramCommand.Buttons(WorkState.Failed, "a1b2c3d4").SelectMany(r => r).All(b => Encoding.UTF8.GetByteCount(b.Data) < 64), "callback_data must stay under 64 bytes");
        Require(TelegramCommand.ParseCallback("done:a1b2c3d4") is { Kind: TelegramCommandKind.Done, Stage: TelegramCallbackStage.Action, ShortId: "a1b2c3d4" }, "parse done");
        Require(TelegramCommand.ParseCallback("yes_close:a1b2c3d4") is { Kind: TelegramCommandKind.Close, Stage: TelegramCallbackStage.Confirm }, "parse confirm close");
        Require(TelegramCommand.ParseCallback("nope:zz") == null && TelegramCommand.ParseCallback("") == null, "garbage callback_data");

        // Notifier: the sendMessage body carries the keyboard, and the text hints stay.
        var folder = Path.Combine(Path.GetTempPath(), "TelegramChecks3", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var settings = new TelegramSettings { Enabled = true, AllowedChatId = 42 };
        TelegramSecrets.SetToken(settings, secret);
        var ok = new FakeHandler((_, _) => Json("{\"ok\":true,\"result\":{\"message_id\":5}}"));
        var notifier = new TelegramNotifier(folder, () => settings, _ => { }, t => new(t, ok));
        foreach (var state in new[] { WorkState.Failed, WorkState.Paused, WorkState.Completed })
        {
            var item = new WorkItem { Request = "x", State = state };
            Require(notifier.NotifyAsync(new(item.Id, StopNoticeKind.Failed, "T", "x", state)).Result, "send " + state);
            var body = JsonDocument.Parse(ok.Calls[^1].Body).RootElement;
            var buttons = body.GetProperty("reply_markup").GetProperty("inline_keyboard").EnumerateArray().SelectMany(r => r.EnumerateArray()).Select(b => b.GetProperty("callback_data").GetString()!).ToList();
            var sid = item.Id[..8];
            Require(buttons.Contains("continue:" + sid) && buttons.Contains("status:" + sid), $"{state} body must carry buttons");
            Require(buttons.Contains("run:" + sid) == (state != WorkState.Completed), $"{state} Run button");
            Require(body.GetProperty("text").GetString()!.Contains("/status_" + sid), "text hints must remain as fallback");
            if (state == WorkState.Failed)
            {
                foreach (var verb in new[] { "continue", "done", "redo", "run", "status" })
                    Require(buttons.Contains(verb + ":" + sid), $"Failed sendMessage body must carry {verb}");
                Require(buttons.All(b => Encoding.UTF8.GetByteCount(b) <= 64), "callback_data at most 64 bytes in the sent body");
            }
        }

        // getUpdates asks for both message and callback_query.
        string? updatesBody = null;
        using (var updatesClient = new TelegramBotClient(secret, new FakeHandler((r, b) => { if (r.RequestUri!.AbsolutePath.EndsWith("/getUpdates")) updatesBody = b; return Json("{\"ok\":true,\"result\":[]}"); })))
            updatesClient.GetUpdatesAsync(0, 0, CancellationToken.None).Wait();
        var allowed = JsonDocument.Parse(updatesBody!).RootElement.GetProperty("allowed_updates").EnumerateArray().Select(e => e.GetString()).ToList();
        Require(allowed.Contains("message") && allowed.Contains("callback_query"), "getUpdates allowed_updates must contain message and callback_query");

        // Callback flow through the command service.
        var config = new ManagerSettings();
        config.Telegram.Enabled = true;
        config.Telegram.AllowedChatId = 42;
        TelegramSecrets.SetToken(config.Telegram, secret);
        var map = new TelegramMessageMap(Path.Combine(folder, "map.json"));
        var offsets = new TelegramOffsetStore(Path.Combine(folder, "offset.json"));
        var tasks = new FakeTasks();
        var work = new WorkItem { Request = "Add dark mode", State = WorkState.NeedsReview };
        tasks.Items[work.Id] = work;
        var sid8 = work.Id[..8];
        var failAnswers = false;
        var handler = new FakeHandler((r, _) =>
        {
            var method = r.RequestUri!.AbsolutePath.Split('/')[^1];
            if (failAnswers && method is "answerCallbackQuery" or "editMessageReplyMarkup")
                return Json("{\"ok\":false,\"error_code\":400,\"description\":\"bad " + secret + "\"}", HttpStatusCode.BadRequest);
            return Json("{\"ok\":true,\"result\":{\"message_id\":901}}");
        });
        var statuses = new List<string>();
        var service = new TelegramCommandService(() => config, map, offsets, tasks.Actions, statuses.Add);
        using var client = new TelegramBotClient(secret, handler);
        TelegramMessage Tap(long update, long chat, string data, string query) => new(update, chat, 77, data, null, query);
        void Process(params TelegramMessage[] updates) => service.ProcessUpdatesAsync(updates, client, default).GetAwaiter().GetResult();
        List<(string Method, JsonElement Body)> Calls() => handler.Calls.Select(c => (c.Url.Split('/')[^1], JsonDocument.Parse(c.Body).RootElement.Clone())).ToList();

        // Foreign chat: empty answer, no action, no reply, offset advances.
        Process(Tap(100, 999, "done:" + sid8, "q-foreign"), Tap(101, 999, "yes_done:" + sid8, "q-foreign2"));
        var calls = Calls();
        Require(tasks.Calls.Count == 0 && work.State == WorkState.NeedsReview, "Foreign callback must run no action");
        Require(calls.Count == 2 && calls.All(c => c.Method == "answerCallbackQuery" && !c.Body.TryGetProperty("text", out _)), "Foreign callback must get an empty answerCallbackQuery only");
        Require(offsets.Load() == 102, "Offset must advance past foreign callbacks: " + offsets.Load());

        // Done: the first tap only swaps the buttons, the confirm tap calls Complete.
        handler.Calls.Clear();
        Process(Tap(110, 42, "done:" + sid8, "q-done"));
        calls = Calls();
        Require(tasks.Calls.Count == 0, "First Done tap must not run the action");
        Require(calls.Any(c => c.Method == "editMessageReplyMarkup" && c.Body.ToString().Contains("yes_done:" + sid8) && c.Body.ToString().Contains("Confirm done") && c.Body.ToString().Contains("Cancel")), "Confirm/Cancel buttons expected");
        Require(calls.Any(c => c.Method == "answerCallbackQuery" && c.Body.GetProperty("callback_query_id").GetString() == "q-done"), "First tap must be answered");
        handler.Calls.Clear();
        Process(Tap(111, 42, "yes_done:" + sid8, "q-yes"));
        calls = Calls();
        Require(tasks.Calls.SequenceEqual(["Complete:" + work.Id]) && work.State == WorkState.Completed, "Confirm tap must call Complete");
        Require(calls.Any(c => c.Method == "editMessageReplyMarkup" && c.Body.GetProperty("reply_markup").GetProperty("inline_keyboard").GetArrayLength() == 0), "Buttons must be removed after the action");
        Require(calls.Any(c => c.Method == "answerCallbackQuery" && c.Body.GetProperty("text").GetString() == "Task completed.") && calls.Any(c => c.Method == "sendMessage"), "Answer with a one-line result plus a confirmation reply");
        Require(offsets.Load() == 112, "Offset after confirm: " + offsets.Load());

        // Cancel runs nothing.
        tasks.Calls.Clear();
        handler.Calls.Clear();
        Process(Tap(112, 42, "cancel:" + sid8, "q-cancel"));
        Require(tasks.Calls.Count == 0 && Calls().Any(c => c.Method == "answerCallbackQuery"), "Cancel must only answer");

        // Redo: force_reply prompt mapped to the task, and the answer becomes a Comment.
        work.State = WorkState.Failed;
        handler.Calls.Clear();
        Process(Tap(120, 42, "redo:" + sid8, "q-redo"));
        calls = Calls();
        var prompt = calls.Single(c => c.Method == "sendMessage").Body;
        Require(prompt.GetProperty("text").GetString()!.StartsWith("What should change? Reply to this message") && prompt.GetProperty("reply_markup").GetProperty("force_reply").GetBoolean(), "Redo must send a force_reply prompt");
        Require(calls.Any(c => c.Method == "answerCallbackQuery") && tasks.Calls.Count == 0 && map.WorkIdFor(42, 901) == work.Id, "Redo answers, runs nothing yet, and maps the prompt");
        Process(new TelegramMessage(121, 42, 902, "make the button larger", 901));
        Require(tasks.Calls.SequenceEqual(["Comment:" + work.Id + ":make the button larger"]) && work.Comments.Count == 1, "The answer to the prompt must become a Comment");

        // Other buttons go through the same dispatch.
        tasks.Calls.Clear();
        Process(Tap(130, 42, "run:" + sid8, "q-run"), Tap(131, 42, "status:" + sid8, "q-status"), Tap(132, 42, "continue:" + sid8, "q-cont"));
        Require(tasks.Calls.SequenceEqual(["Run:" + work.Id, "Run:" + work.Id]), "run/continue buttons dispatch like typed commands: " + string.Join(",", tasks.Calls));

        // answerCallbackQuery / editMessageReplyMarkup failures never stop the loop and never leak the token.
        failAnswers = true;
        statuses.Clear();
        handler.Calls.Clear();
        Process(Tap(140, 42, "done:" + sid8, "q-f1"), Tap(141, 42, "yes_done:" + sid8, "q-f2"), Tap(142, 42, "status:" + sid8, "q-f3"));
        Require(offsets.Load() == 143, "Loop must continue past answer failures: " + offsets.Load());
        Require(statuses.Count > 0 && statuses.All(t => !t.Contains("SECRET-token")), "Failures reported without the token");
        Require(Calls().Count(c => c.Method == "answerCallbackQuery") >= 3, "answerCallbackQuery is always called");

        // The client parses callback_query updates.
        var poll = new FakeHandler((r, _) => Json("{\"ok\":true,\"result\":[{\"update_id\":9,\"callback_query\":{\"id\":\"cq1\",\"from\":{\"id\":42},\"message\":{\"message_id\":55,\"chat\":{\"id\":42}},\"data\":\"done:a1b2c3d4\"}}]}"));
        using var pollClient = new TelegramBotClient(secret, poll);
        var updates = pollClient.GetUpdatesAsync(0, 0, default).Result;
        Require(updates is [{ CallbackQueryId: "cq1", ChatId: 42, MessageId: 55, Text: "done:a1b2c3d4", UpdateId: 9 }], "callback_query must be parsed");
        Require(poll.Calls[0].Body.Contains("callback_query"), "getUpdates must request callback_query");
    }

    private static void Hardening(string secret)
    {
        var folder = Path.Combine(Path.GetTempPath(), "TelegramChecks4", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var config = new TelegramSettings { Enabled = true, AllowedChatId = 42 };
        TelegramSecrets.SetToken(config, secret);
        var review = new StopNotice("0123456789abcdef", StopNoticeKind.NeedsReview, "Result ready for review", "Do it", WorkState.NeedsReview);
        const string Ok = "{\"ok\":true,\"result\":{\"message_id\":321}}";
        var everyText = new List<string>(); // test-button results produced below

        // (a) One transient failure, then success: exactly one delivery, mapped, no status, no throw, no real waiting.
        var transient = new (string Name, Func<HttpResponseMessage> First, TimeSpan? Wait)[]
        {
            ("HttpRequestException", () => throw new HttpRequestException("socket closed"), null),
            ("HTTP 500", () => Json("{\"ok\":false,\"error_code\":500,\"description\":\"Internal\"}", HttpStatusCode.InternalServerError), null),
            ("HTTP 429 retry_after=1", () => Json("{\"ok\":false,\"error_code\":429,\"description\":\"Too Many Requests\",\"parameters\":{\"retry_after\":1}}", (HttpStatusCode)429), TimeSpan.FromSeconds(1))
        };
        foreach (var (name, first, wait) in transient)
        {
            var calls = 0;
            var delivered = 0;
            var handler = new FakeHandler((_, _) => ++calls == 1 ? first() : Delivered(ref delivered));
            var statuses = new List<string>();
            var waits = new List<TimeSpan>();
            var notifier = new TelegramNotifier(folder, () => config, statuses.Add, t => new(t, handler), null, (t, _) => { waits.Add(t); return Task.CompletedTask; });
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var sent = notifier.NotifyAsync(review).GetAwaiter().GetResult();
            Require(sent && calls == 2 && delivered == 1, $"{name}: expected one retry and exactly one delivery (calls={calls}, delivered={delivered})");
            Require(notifier.Messages.WorkIdFor(42, 321) == review.WorkId, $"{name}: message_id not mapped");
            Require(statuses.Count == 0, $"{name}: transient recovery must not report a failure: " + string.Join("|", statuses));
            Require(waits.Count == 1 && (wait == null || waits[0] == wait), $"{name}: wait was {string.Join(",", waits)}");
            Require(timer.Elapsed < TimeSpan.FromSeconds(1), $"{name}: test slept for real ({timer.Elapsed})");
        }

        // (b) Permanent errors: one call, one status line, no throw.
        var permanent = new (string Name, HttpStatusCode Code, string Body)[]
        {
            ("401", HttpStatusCode.Unauthorized, "{\"ok\":false,\"error_code\":401,\"description\":\"Unauthorized\"}"),
            ("400 chat not found", HttpStatusCode.BadRequest, "{\"ok\":false,\"error_code\":400,\"description\":\"Bad Request: chat not found\"}"),
            ("400 other", HttpStatusCode.BadRequest, "{\"ok\":false,\"error_code\":400,\"description\":\"Bad Request: can't parse entities\"}")
        };
        foreach (var (name, code, body) in permanent)
        {
            var handler = new FakeHandler((_, _) => Json(body, code));
            var statuses = new List<string>();
            var notifier = new TelegramNotifier(folder, () => config, statuses.Add, t => new(t, handler), null, NoWait);
            var sent = notifier.NotifyAsync(review).GetAwaiter().GetResult();
            Require(!sent && handler.Calls.Count == 1 && statuses.Count == 1, $"{name}: calls={handler.Calls.Count}, statuses={statuses.Count}");
        }

        // (c) The poll loop survives a getUpdates exception and a failed reply, and keeps polling.
        var loopFolder = Path.Combine(Path.GetTempPath(), "TelegramChecks5", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(loopFolder);
        var loopConfig = new ManagerSettings();
        loopConfig.Telegram.Enabled = true;
        loopConfig.Telegram.AllowedChatId = 42;
        TelegramSecrets.SetToken(loopConfig.Telegram, secret);
        var polls = 0;
        var replies = 0;
        var described = new List<string>();
        var loopStatuses = new List<string>();
        var loopHandler = new FakeHandler((r, _) =>
        {
            if (r.RequestUri!.AbsolutePath.EndsWith("/getUpdates"))
            {
                var n = Interlocked.Increment(ref polls);
                if (n == 1)
                    throw new HttpRequestException("network down");
                Thread.Sleep(5);
                return Json(n == 2 ? UpdatesJson((10, 1, "/status #aaaaaaaa"), (11, 2, "/status #aaaaaaaa")) : "{\"ok\":true,\"result\":[]}");
            }

            if (Interlocked.Increment(ref replies) == 1)
                throw new HttpRequestException("reply failed");
            return Json(Ok);
        });
        var loopActions = new TelegramActions(p => p == "aaaaaaaa" ? "aaaaaaaa-1" : null, id => { lock (described) described.Add(id); return Task.FromResult("State: ok"); },
            (_, _) => Task.FromResult(""), _ => Task.FromResult(""), (_, _) => Task.FromResult(""), _ => Task.FromResult(""));
        var loopOffsets = TelegramOffsetStore.InFolder(loopFolder);
        var service = new TelegramCommandService(() => loopConfig, new TelegramMessageMap(Path.Combine(loopFolder, "map.json")), loopOffsets, loopActions, s => { lock (loopStatuses) loopStatuses.Add(s); }, t => new(t, loopHandler), NoWait);
        service.Start();
        var survived = WaitFor(() => Volatile.Read(ref polls) >= 5 && loopOffsets.Load() == 12);
        service.Stop();
        Require(survived, $"Poll loop stopped: polls={polls}, offset={loopOffsets.Load()}");
        lock (described) Require(described.Count == 2, "A failed reply must not stop later updates: handled " + described.Count);
        lock (loopStatuses) Require(loopStatuses.Any(x => x.StartsWith("Telegram polling failed")) && loopStatuses.Any(x => x.StartsWith("Telegram reply failed")), "Failures must be reported: " + string.Join("|", loopStatuses));

        // (c2) Backoff grows 5s -> 10s -> 20s -> 40s -> 60s (capped) on errors and resets after a good poll.
        var boFolder = Path.Combine(Path.GetTempPath(), "TelegramChecks5b", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(boFolder);
        var boConfig = new ManagerSettings();
        boConfig.Telegram.Enabled = true;
        boConfig.Telegram.AllowedChatId = 42;
        TelegramSecrets.SetToken(boConfig.Telegram, secret);
        var boPolls = 0;
        var boDelays = new List<double>();
        var boHandler = new FakeHandler((r, _) =>
        {
            var n = Interlocked.Increment(ref boPolls);
            // polls 1-6 fail, 7 succeeds, 8 fails again, then succeed
            if (n <= 6 || n == 8)
                throw new HttpRequestException("network down");
            Thread.Sleep(5);
            return Json("{\"ok\":true,\"result\":[]}");
        });
        var boActions = new TelegramActions(_ => null, _ => Task.FromResult(""), (_, _) => Task.FromResult(""), _ => Task.FromResult(""), (_, _) => Task.FromResult(""), _ => Task.FromResult(""));
        var boService = new TelegramCommandService(() => boConfig, new TelegramMessageMap(Path.Combine(boFolder, "map.json")), TelegramOffsetStore.InFolder(boFolder), boActions, _ => { }, t => new(t, boHandler),
            (d, _) => { lock (boDelays) boDelays.Add(d.TotalSeconds); return Task.Delay(1); });
        boService.Start();
        WaitFor(() => Volatile.Read(ref boPolls) >= 10);
        boService.Stop();
        lock (boDelays)
        {
            var seq = string.Join(",", boDelays.Take(7));
            Require(seq == "5,10,20,40,60,60,5", "Backoff sequence (expected 5,10,20,40,60,60,5 with reset after success): " + seq);
        }

        // (d) Token sweep over every error path.
        var sweepFolder = Path.Combine(Path.GetTempPath(), "TelegramChecks6", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sweepFolder);
        var sweepManager = new ManagerSettings();
        sweepManager.Telegram.Enabled = true;
        sweepManager.Telegram.AllowedChatId = 42;
        TelegramSecrets.SetToken(sweepManager.Telegram, secret);
        var escaped = Uri.EscapeDataString(secret);
        var sweepStatuses = new List<string>();
        void Say(string text) { lock (sweepStatuses) sweepStatuses.Add(text); }
        var sweepBodies = new List<string>();
        var apiLeak = "{\"ok\":false,\"error_code\":%CODE%,\"description\":\"bad " + secret + " and " + escaped + " at https://api.telegram.org/bot" + secret + "/x\"%EXTRA%}";
        string Leak(int code, string extra = "") => apiLeak.Replace("%CODE%", code.ToString()).Replace("%EXTRA%", extra);

        // send failures: transport error carrying the URL, and API errors echoing the token; transient and permanent
        foreach (var respond in new Func<HttpResponseMessage>[]
        {
            () => throw new HttpRequestException("failed https://api.telegram.org/bot" + secret + "/sendMessage " + secret),
            () => Json(Leak(500), HttpStatusCode.InternalServerError),
            () => Json(Leak(400), HttpStatusCode.BadRequest),
            () => Json(Leak(401), HttpStatusCode.Unauthorized),
            () => Json(Leak(403), HttpStatusCode.Forbidden)
        })
        {
            var sender = new TelegramNotifier(sweepFolder, () => sweepManager.Telegram, Say, t => new(t, new FakeHandler((_, _) => respond())), null, NoWait);
            Require(!sender.NotifyAsync(review).GetAwaiter().GetResult(), "Sweep send failure should not succeed");
        }

        // one successful send so telegram-messages.json exists in the swept folder
        new TelegramNotifier(sweepFolder, () => sweepManager.Telegram, Say, t => new(t, new FakeHandler((_, _) => Json(Ok))), null, NoWait).NotifyAsync(review).GetAwaiter().GetResult();

        // test-button failures
        foreach (var respond in new Func<HttpResponseMessage>[]
        {
            () => throw new HttpRequestException("https://api.telegram.org/bot" + secret + "/getMe"),
            () => Json(Leak(401), HttpStatusCode.Unauthorized)
        })
        {
            var tester = new TelegramNotifier(sweepFolder, () => sweepManager.Telegram, Say, t => new(t, new FakeHandler((_, _) => respond())), null, NoWait);
            var text = tester.TestAsync().GetAwaiter().GetResult();
            Require(text.StartsWith("Telegram test failed"), "Test button failure text: " + text);
            everyText.Add(text);
        }

        // getUpdates failures and actions whose exceptions contain the token, through the real loop and its real offset file
        var sweepPolls = 0;
        var sweepSends = 0;
        var sweepHandler = new FakeHandler((r, body) =>
        {
            if (!r.RequestUri!.AbsolutePath.EndsWith("/getUpdates"))
            {
                lock (sweepBodies) sweepBodies.Add(body);
                if (Interlocked.Increment(ref sweepSends) == 2)
                    throw new HttpRequestException("reply failed " + r.RequestUri + " " + secret);
                return Json(Ok);
            }

            Thread.Sleep(5);
            return Interlocked.Increment(ref sweepPolls) switch
            {
                1 => throw new HttpRequestException("poll failed " + r.RequestUri + " " + secret),
                2 => Json(Leak(500), HttpStatusCode.InternalServerError),
                3 => Json(Leak(429, ",\"parameters\":{\"retry_after\":1}"), (HttpStatusCode)429),
                4 => Json(UpdatesJson((20, 1, "/done #bbbbbbbb"), (21, 2, "/close #bbbbbbbb"), (22, 3, "/run #bbbbbbbb"), (23, 4, "/status #bbbbbbbb"))),
                5 => Json(Leak(401), HttpStatusCode.Unauthorized),
                _ => Json("{\"ok\":true,\"result\":[]}")
            };
        });
        var sweepActions = new TelegramActions(p => p == "bbbbbbbb" ? "bbbbbbbb-1" : null,
            _ => throw new InvalidOperationException("state lookup " + secret + " / " + escaped),
            (_, _) => Task.FromResult(""),
            _ => throw new Exception("complete failed for https://api.telegram.org/bot" + secret + "/x"),
            (_, _) => throw new InvalidOperationException("close refused: " + secret),
            _ => throw new Exception("run exploded " + secret));
        var sweepService = new TelegramCommandService(() => sweepManager, new TelegramMessageMap(Path.Combine(sweepFolder, "telegram-messages.json")), TelegramOffsetStore.InFolder(sweepFolder), sweepActions, Say, t => new(t, sweepHandler), NoWait);
        sweepService.Start();
        var swept = WaitFor(() => Volatile.Read(ref sweepPolls) >= 7 && TelegramOffsetStore.InFolder(sweepFolder).Load() == 24);
        sweepService.Stop();
        Thread.Sleep(100);
        Require(swept, $"Sweep loop did not process all updates: polls={sweepPolls}, offset={TelegramOffsetStore.InFolder(sweepFolder).Load()}");

        List<string> statusCopy;
        lock (sweepStatuses) statusCopy = [.. sweepStatuses];
        Require(statusCopy.Count >= 6, "Sweep must have produced status lines: " + statusCopy.Count);
        var replyTexts = new List<string>();
        lock (sweepBodies)
            foreach (var body in sweepBodies)
                replyTexts.Add(JsonDocument.Parse(body).RootElement.GetProperty("text").GetString()!);
        Require(replyTexts.Count >= 4 && replyTexts.Any(x => x.Contains("close refused")), "Sweep replies missing: " + string.Join("|", replyTexts));

        var files = Directory.GetFiles(sweepFolder, "*", SearchOption.AllDirectories);
        Require(files.Any(f => Path.GetFileName(f) == "telegram-state.json") && files.Any(f => Path.GetFileName(f) == "telegram-messages.json"), "Sweep folder must contain state and message files: " + string.Join(",", files.Select(Path.GetFileName)));
        foreach (var variant in new[] { secret, escaped, "SECRET-token_value" })
        {
            Require(statusCopy.All(x => !x.Contains(variant, StringComparison.OrdinalIgnoreCase)), $"Status line leaked the token ({variant}): " + string.Join("|", statusCopy.Where(x => x.Contains(variant, StringComparison.OrdinalIgnoreCase))));
            Require(replyTexts.All(x => !x.Contains(variant, StringComparison.OrdinalIgnoreCase)), $"Reply leaked the token ({variant})");
            Require(sweepBodies.All(x => !x.Contains(variant, StringComparison.OrdinalIgnoreCase)), $"Request body leaked the token ({variant})");
            Require(everyText.All(x => !x.Contains(variant, StringComparison.OrdinalIgnoreCase)), $"Test button text leaked the token ({variant})");
            foreach (var file in files)
                Require(!File.ReadAllText(file).Contains(variant, StringComparison.OrdinalIgnoreCase), $"File {Path.GetFileName(file)} contains the token ({variant})");
        }

        var raw = Encoding.UTF8.GetBytes(secret);
        foreach (var file in files)
            Require(File.ReadAllBytes(file).AsSpan().IndexOf(raw) < 0, $"File {Path.GetFileName(file)} contains raw token bytes");
        Require(!JsonSerializer.Serialize(sweepManager).Contains(secret), "Serialized settings contain the token");
    }

    // The command service driven end to end with the real TelegramTaskActions, a real temporary SqliteStore and a real
    // TaskExecutionPool (the same service the Desktop uses). Only the Telegram HTTP side is faked; no AI is invoked.
    private static void EndToEnd(string secret)
    {
        var folder = Path.Combine(Path.GetTempPath(), "TelegramChecksE2E", Guid.NewGuid().ToString("N"));
        var projectFolder = Path.Combine(folder, "game");
        Directory.CreateDirectory(projectFolder);
        using var store = new SqliteStore(Path.Combine(folder, "checks.db"));
        store.SaveProject(new("p1", "Game", projectFolder));
        var runner = new NullRunner();
        var pool = new TaskExecutionPool(store, store, new Orchestrator(store, [], runner), runner);
        var resumed = 0;
        var changed = new List<string?>();
        var tasks = new TelegramTaskActions(pool, store, run => Task.FromResult(run()), w => changed.Add(w?.Id), () => resumed++);

        var config = new ManagerSettings();
        config.Telegram.Enabled = true;
        config.Telegram.AllowedChatId = 42;
        TelegramSecrets.SetToken(config.Telegram, secret);
        var map = new TelegramMessageMap(Path.Combine(folder, "map.json"));
        var offsets = new TelegramOffsetStore(Path.Combine(folder, "offset.json"));
        var sends = new FakeHandler((_, _) => Json("{\"ok\":true,\"result\":{\"message_id\":900}}"));
        var service = new TelegramCommandService(() => config, map, offsets, tasks.ToActions(), _ => { });
        using var client = new TelegramBotClient(secret, sends);
        long update = 1000;
        string Say(string text, int? replyTo = null, long chat = 42)
        {
            var before = sends.Calls.Count;
            service.ProcessUpdatesAsync([M(update++, chat, 500 + (int)update, text, replyTo)], client, default).GetAwaiter().GetResult();
            Require(sends.Calls.Count == before + 1, $"'{text}' must get exactly one reply, got {sends.Calls.Count - before}");
            return JsonDocument.Parse(sends.Calls[^1].Body).RootElement.GetProperty("text").GetString()!;
        }

        WorkItem Goal(string request, WorkState state, string? id = null)
        {
            var session = new WorkSession { Goal = request, ProjectIds = ["p1"], State = SessionState.WaitingForUser, CallsUsed = 1, MaxCalls = 5 };
            var goal = new WorkItem { ProjectId = "p1", IsGoal = true, Request = request, State = state, Access = AccessLevel.WriteProject, SessionId = session.Id };
            if (id != null)
                goal.Id = id;
            session.RootWorkId = goal.Id;
            store.SaveSession(session);
            store.SaveWork(goal);
            return goal;
        }

        WorkItem Fresh(WorkItem goal) => store.WorkItems().Single(w => w.Id == goal.Id);

        // A reply to a mapped notice adds the comment to the real task and requeues it.
        var review = Goal("Add dark mode", WorkState.NeedsReview);
        map.Record(42, 100, review.Id);
        var reply = Say("please make the border thinner", 100);
        var commented = Fresh(review);
        Require(commented.Comments.Any(c => c.Text == "please make the border thinner" && !c.IsAssistant), "Reply must be stored as a comment on the real task");
        Require(commented.State == WorkState.Queued && commented.QueuedAt != null && pool.Steps(commented).Any(s => s.Request.Contains("thinner")), $"Reply must requeue the task (state {commented.State})");
        Require(reply == "Comment saved. The task is queued to continue." && resumed >= 1 && changed.Count >= 1, "Comment reply text/callbacks: " + reply);

        // /done moves a NeedsReview task to Completed.
        var finish = Goal("Ship it", WorkState.NeedsReview);
        reply = Say("/done #" + finish.Id[..8]);
        Require(Fresh(finish).State == WorkState.Completed && reply == "Task marked completed.", $"/done: {Fresh(finish).State} / {reply}");

        // /close sets Cancelled with the reason.
        var close = Goal("Drop it", WorkState.NeedsReview);
        reply = Say("/close #" + close.Id[..8] + " not needed anymore");
        var closed = Fresh(close);
        Require(closed.State == WorkState.Cancelled && reply == "Task closed (cancelled).", $"/close: {closed.State} / {reply}");
        Require(store.Logs(close.Id).Any(l => l.Message.Contains("not needed anymore")) || closed.Notes.Contains("not needed anymore") || closed.ProblemReason?.Contains("not needed anymore") == true, "/close must record the reason");

        // An ambiguous short id gives the 'could not tell which task' reply and changes nothing.
        var twinA = Goal("Twin one", WorkState.NeedsReview, "dead0001" + Guid.NewGuid().ToString("N")[..24]);
        var twinB = Goal("Twin two", WorkState.NeedsReview, "dead0002" + Guid.NewGuid().ToString("N")[..24]);
        Require(tasks.FindByShortId("dead") == null && tasks.FindByShortId("dead0001") == twinA.Id && tasks.FindByShortId("") == null, "Short id prefix matching");
        reply = Say("/done #dead");
        Require(reply.Contains("I could not tell which task you mean") && Fresh(twinA).State == WorkState.NeedsReview && Fresh(twinB).State == WorkState.NeedsReview, "Ambiguous id reply: " + reply);

        // /new <project> <request> creates a queued WriteProject coding task in that project.
        var knownIds = store.WorkItems().Select(w => w.Id).ToHashSet();
        reply = Say("/new 1 fix the login button");
        var created = store.WorkItems().Single(w => !knownIds.Contains(w.Id) && w.IsGoal);
        Require(created.ProjectId == "p1" && created.Request == "fix the login button" && created.Kind == WorkKind.Coding && created.Access == AccessLevel.WriteProject, "/new task fields");
        Require(created.State == WorkState.Queued && created.QueuedAt != null, $"/new task must be queued (state {created.State})");
        Require(reply == "Task #" + created.Id[..8] + " created in Game and queued. You will get a message when it stops." && changed[^1] == created.Id, "/new reply: " + reply);
        Require(Say("/projects").Contains("1. Game") && Say("/new 9 nothing").Contains("could not find that project"), "/projects and unknown project");
    }

    private static HttpResponseMessage Delivered(ref int counter)
    {
        counter++;
        return Json("{\"ok\":true,\"result\":{\"message_id\":321}}");
    }

    private static Exception Assert(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            return ex;
        }

        throw new Exception("Expected an exception");
    }
}
