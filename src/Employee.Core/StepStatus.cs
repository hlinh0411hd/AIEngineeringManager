using System.Text.RegularExpressions;

namespace Employee.Core;

// Workers end their answer with a status line saying whether the step's request is fully done.
// A step that reports unfinished work is run again (within its attempt limit) to finish it, instead
// of being counted as done because the CLI exited cleanly.
public static class StepStatus
{
    public const string Instruction = "STEP STATUS: end your final answer with one line, either \"STEP STATUS: COMPLETE\" when everything this step's Request asks for is done, or \"STEP STATUS: INCOMPLETE: <what is still missing>\" when part of it is not done yet, or \"STEP STATUS: BLOCKED: <reason>\" when a tool, access or input the step needs is not available to you, so running this step again cannot finish it. Skipped requirements, placeholders, stubs and temporary stand-ins make a step incomplete. Things this run cannot do are not missing work: launching or visually checking the app, checks only the user can make, verification you were told not to run, and changes outside this step's request; mention those in your answer, not in the status line. Leave the line out only when you were told to return ONLY JSON.";

    // Extra runs in a row that report the same missing work before the step stops and waits for
    // the user: the model is not getting further, so more runs would only spend usage.
    public const int MaxStalls = 2;

    public const string Continuing = "StepIncomplete";
    public const string GaveUp = "StepUnfinished";

    // Case-sensitive on purpose: only the exact marker counts, not prose about a status. The text may
    // sit inside a JSON string (Codex event stream), so it stops at a quote, backslash or line end.
    private static readonly Regex Line = new(@"STEP STATUS:\s*(COMPLETE|INCOMPLETE|BLOCKED)\b[\s:\-–—]*([^\r\n""\\]*)", RegexOptions.CultureInvariant);

    // What the worker says is still missing, or null when it reported the step complete or gave no status.
    public static string? Remaining(string output)
    {
        var last = Line.Matches(output ?? "").LastOrDefault();
        if (last == null || last.Groups[1].Value != "INCOMPLETE")
            return null;
        var missing = last.Groups[2].Value.Trim();
        return missing.Length == 0 ? "The AI did not say what is missing." : missing[..Math.Min(missing.Length, 1500)];
    }

    public enum Why { Capability, External, MoreWork, Unknown }

    // Why work is still missing, from the worker's words. External: something outside the model
    // (access, a missing file or input, a decision, a tool or the app) — a stronger model would not
    // help. Capability: the model could not do it (errors it could not fix, failing tests, a bug it
    // could not find) — a stronger model may. MoreWork: there is simply more to do.
    public static Why WhyUnfinished(string? missing)
    {
        if (string.IsNullOrWhiteSpace(missing))
            return Why.Unknown;
        var text = missing.ToLowerInvariant();
        bool Any(params string[] words) => words.Any(text.Contains);
        if (Any("permission", "access", "denied", "read-only", "readonly", "not allowed", "sandbox", "credential", "api key", "token", "login", "sign in", "not installed", "no internet", "network",
                "usage limit", "quota", "rate limit", "approval", "decision", "the user", "user must", "user needs", "ask the user", "waiting for", "depends on", "dependency",
                "missing file", "missing asset", "missing input", "not provided", "no such file", "cannot find the file", "file not found", "not found in the project",
                "cannot run the app", "cannot launch", "cannot open unity", "unity editor", "outside this step", "out of scope",
                "quyền", "chưa có", "thiếu file", "thiếu ảnh", "không tìm thấy", "cần người dùng", "chờ", "phê duyệt", "đăng nhập"))
            return Why.External;
        if (Any("error", "fail", "failing", "bug", "crash", "exception", "does not work", "doesn't work", "not working", "broken", "could not fix", "couldn't fix", "unable to", "could not figure", "unclear how",
                "incorrect", "wrong", "still shows", "still has", "regression", "compile", "build error", "test",
                "lỗi", "không sửa được", "vẫn bị", "chưa chạy được", "sai", "không hoạt động"))
            return Why.Capability;
        if (Any("remaining", "rest of", "still need to", "not yet", "more files", "other files", "next", "continue", "too long", "truncated", "output limit", "time", "còn lại", "chưa làm", "tiếp tục"))
            return Why.MoreWork;
        return Why.Unknown;
    }

    // The work is done and only its check could not run here (seen live: "The managed command policy
    // rejected the requested C# compiler invocation, so compilation remains unverified" stopped a task
    // whose code changes were made). Missing work is never this.
    public static bool OnlyVerificationMissing(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return false;
        var text = reason.ToLowerInvariant();
        bool Any(params string[] words) => words.Any(text.Contains);
        var check = Any("compil", "build", "unverified", "not verified", "verif", "test", "biên dịch", "chưa xác minh", "chưa kiểm", "kiểm thử");
        var couldNotRun = Any("rejected", "policy", "sandbox", "not available", "unavailable", "cannot run", "can't run", "could not run", "couldn't run", "not allowed", "no unity", "license", "not installed",
            "không chạy được", "không thể chạy", "bị chặn", "không có unity");
        var workMissing = Any("not implemented", "not done", "still missing", "todo", "not yet changed", "did not edit", "no changes were made", "no files were changed", "remaining work", "still need to",
            "chưa làm", "chưa sửa", "chưa implement", "chưa thay đổi", "còn thiếu");
        return check && couldNotRun && !workMissing;
    }

    // True when a new report still names almost all the missing work of the previous one: the extra
    // run made no visible progress, so running again is unlikely to help.
    public static bool SameGap(string? previous, string current)
    {
        if (previous == null)
            return false;
        static HashSet<string> Words(string text) => Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}]{3,}").Select(m => m.Value).ToHashSet();
        var a = Words(previous);
        var b = Words(current);
        if (a.Count == 0 || b.Count == 0)
            return true;
        // Progress means items from the previous report are gone; rewording alone is not progress.
        return a.Count(b.Contains) / (double)a.Count >= 0.8;
    }

    // Why the worker cannot finish this step itself (a missing tool, access or input), or null.
    public static string? Blocker(string output)
    {
        var last = Line.Matches(output ?? "").LastOrDefault();
        if (last == null || last.Groups[1].Value != "BLOCKED")
            return null;
        var reason = last.Groups[2].Value.Trim();
        return reason.Length == 0 ? "The AI did not say what it needs." : reason[..Math.Min(reason.Length, 1500)];
    }

    // The unfinished-work report from the step's latest run, if that run ended with one of the given events.
    public static string? Latest(IEnumerable<TaskLog> logs, string evt)
    {
        var last = logs.LastOrDefault(l => l.Event == "Route" || l.Event == evt);
        return last?.Event == evt ? last.Message : null;
    }
}
