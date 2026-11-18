using System.Text.RegularExpressions;

namespace Employee.Core;

public enum TelegramCommandKind { Comment, Redo, Continue, Done, Close, Run, Status, Help, Start, Projects, New, Unknown }

// One inline keyboard button. Data is Telegram callback_data (at most 64 bytes), for example "done:a1b2c3d4".
public sealed record TelegramButton(string Label, string Data);

public enum TelegramCallbackStage { Action, Confirm, Cancel }

// A parsed callback_data value. Kind uses the same values as typed commands.
public sealed record TelegramCallback(TelegramCommandKind Kind, string ShortId, TelegramCallbackStage Stage);

// ShortId is the hex digits of an optional #xxxxxxxx token (without the '#'); Text is what remains after the command and the token.
public sealed record TelegramCommand(TelegramCommandKind Kind, string? ShortId, string Text)
{
    private static readonly Regex ShortIdToken = new(@"(?<![\w#])#([0-9A-Fa-f]{4,32})(?!\w)", RegexOptions.Compiled);
    private static readonly Regex CommandHead = new(@"^/([A-Za-z]+)(?:_([0-9A-Fa-f]{4,32}))?(?:@\w+)?(?=\s|$)", RegexOptions.Compiled);

    // Pure: no I/O. Plain text (including 'ok' or 'duyệt') is a Comment so TaskIntent.IsApproval still decides downstream.
    public static TelegramCommand Parse(string? text)
    {
        var rest = (text ?? "").Trim();
        // /projects and /new work on projects, not tasks: a '#1234' inside the request text must stay part of the text.
        var lead = CommandHead.Match(rest);
        if (lead.Success)
        {
            var name = lead.Groups[1].Value.ToLowerInvariant();
            if (name == "projects")
                return new(TelegramCommandKind.Projects, null, "");
            if (name == "new")
                return new(TelegramCommandKind.New, null, rest[lead.Length..].Trim());
        }

        string? shortId = null;
        var match = ShortIdToken.Match(rest);
        if (match.Success)
        {
            shortId = match.Groups[1].Value;
            var before = rest[..match.Index].TrimEnd(' ', '\t');
            var after = rest[(match.Index + match.Length)..].TrimStart(' ', '\t');
            rest = (before.Length > 0 && after.Length > 0 && !before.EndsWith('\n') && !after.StartsWith('\n') ? before + " " + after : before + after).Trim();
        }

        var head = CommandHead.Match(rest);
        if (!head.Success)
            return new(TelegramCommandKind.Comment, shortId, rest);

        // A tappable command carries its task: /done_a1b2c3d4. An explicit #id token wins if both are present.
        if (shortId == null && head.Groups[2].Success)
            shortId = head.Groups[2].Value;
        var args = rest[head.Length..].Trim();
        return head.Groups[1].Value.ToLowerInvariant() switch
        {
            "continue" => new(TelegramCommandKind.Continue, shortId, args),
            "done" => new(TelegramCommandKind.Done, shortId, args),
            "redo" => args.Length == 0 ? new(TelegramCommandKind.Comment, shortId, "") : new(TelegramCommandKind.Redo, shortId, args),
            "close" => new(TelegramCommandKind.Close, shortId, args),
            "run" => new(TelegramCommandKind.Run, shortId, args),
            "status" => new(TelegramCommandKind.Status, shortId, args),
            "help" => new(TelegramCommandKind.Help, shortId, args),
            "start" => new(TelegramCommandKind.Start, shortId, args),
            _ => new(TelegramCommandKind.Unknown, shortId, rest)
        };
    }

    // Inline keyboard for a notification. Continue, Done, Redo, Close and Status are always shown; Run for Failed and Paused;
    // Completed tasks only get Continue and Status.
    public static IReadOnlyList<IReadOnlyList<TelegramButton>> Buttons(WorkState state, string shortId)
    {
        TelegramButton B(string label, string verb) => new(label, verb + ":" + shortId);
        if (state == WorkState.Completed)
            return [[B("Continue", "continue"), B("Status", "status")]];
        var first = new List<TelegramButton> { B("Continue", "continue"), B("Done", "done"), B("Redo", "redo") };
        var second = new List<TelegramButton> { B("Close", "close") };
        if (state is WorkState.Failed or WorkState.Paused)
            second.Add(B("Run", "run"));
        second.Add(B("Status", "status"));
        return [first, second];
    }

    // The second keyboard for Done and Close: only the confirm tap runs the action.
    public static IReadOnlyList<IReadOnlyList<TelegramButton>> ConfirmButtons(TelegramCommandKind kind, string shortId)
    {
        var verb = kind == TelegramCommandKind.Close ? "close" : "done";
        return [[new("Confirm " + verb, "yes_" + verb + ":" + shortId), new("Cancel", "cancel:" + shortId)]];
    }

    // Maps callback_data back to a command. Returns null for anything unrecognised.
    public static TelegramCallback? ParseCallback(string? data)
    {
        var parts = (data ?? "").Trim().Split(':', 2);
        if (parts.Length != 2 || parts[1].Length == 0 || !parts[1].All(Uri.IsHexDigit))
            return null;
        var id = parts[1];
        return parts[0].ToLowerInvariant() switch
        {
            "continue" => new(TelegramCommandKind.Continue, id, TelegramCallbackStage.Action),
            "done" => new(TelegramCommandKind.Done, id, TelegramCallbackStage.Action),
            "redo" => new(TelegramCommandKind.Redo, id, TelegramCallbackStage.Action),
            "close" => new(TelegramCommandKind.Close, id, TelegramCallbackStage.Action),
            "run" => new(TelegramCommandKind.Run, id, TelegramCallbackStage.Action),
            "status" => new(TelegramCommandKind.Status, id, TelegramCallbackStage.Action),
            "yes_done" => new(TelegramCommandKind.Done, id, TelegramCallbackStage.Confirm),
            "yes_close" => new(TelegramCommandKind.Close, id, TelegramCallbackStage.Confirm),
            "cancel" => new(TelegramCommandKind.Unknown, id, TelegramCallbackStage.Cancel),
            _ => null
        };
    }

    // The footer shown under a notification: what the user can reply or type next.
    // With a shortId the commands are tappable in Telegram (/done_a1b2c3d4): a tap runs it for that task without a reply.
    public static string Hints(WorkState state, string? shortId = null)
    {
        if (!string.IsNullOrEmpty(shortId))
        {
            if (state == WorkState.Completed)
                return "Reply to continue this task, or tap:\n/redo_" + shortId + " (then reply with the changes)  /status_" + shortId;
            var run = state is WorkState.Failed or WorkState.Paused ? "  /run_" + shortId : "";
            return "Reply to this message to add changes, or tap a command:\n/continue_" + shortId + "  /done_" + shortId + "  /redo_" + shortId + " (then reply with the changes)  /close_" + shortId + run + "  /status_" + shortId;
        }

        if (state == WorkState.Completed)
            return "Reply to continue this task.";
        var commands = state is WorkState.Failed or WorkState.Paused
            ? "/continue [text]  /done  /redo <text>  /close [reason]  /run  /status"
            : "/continue [text]  /done  /redo <text>  /close [reason]  /status";
        return "Reply to this message to add changes or more work.\n" + commands;
    }
}

// Pure helpers for /projects and /new: numbering, matching and the reply text, so they can be tested without the Desktop.
public static class TelegramProjects
{
    public const int MaxRequestLength = 12000;

    public static string List(IReadOnlyList<ProjectInfo> projects)
    {
        if (projects.Count == 0)
            return "No projects are registered yet. Add one in the app first.";
        var lines = projects.Select((p, i) => (i + 1) + ". " + p.Name + (string.IsNullOrWhiteSpace(p.Technology) || p.Technology == "Unknown" ? "" : " (" + p.Technology + ")"));
        return "Projects:\n" + string.Join("\n", lines) + "\n\nCreate a task: /new <number or name> <request>\nExample: /new 1 fix the login button";
    }

    // args is everything after /new. The project is a 1-based number, or a name/alias at the start (longest match wins).
    public static (ProjectInfo? Project, string Request) Resolve(IReadOnlyList<ProjectInfo> projects, string args)
    {
        args = args.Trim();
        var space = args.IndexOfAny([' ', '\t', '\r', '\n']);
        var first = space < 0 ? args : args[..space];
        if (int.TryParse(first, out var number))
            return number >= 1 && number <= projects.Count ? (projects[number - 1], Rest(args, first.Length)) : (null, "");
        ProjectInfo? best = null;
        var bestLength = 0;
        foreach (var project in projects)
        {
            var names = new[] { project.Name }.Concat(project.Aliases.Split([',', ';', '|'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
            foreach (var name in names)
            {
                if (name.Length > bestLength && args.StartsWith(name, StringComparison.OrdinalIgnoreCase) && (args.Length == name.Length || char.IsWhiteSpace(args[name.Length])))
                {
                    best = project;
                    bestLength = name.Length;
                }
            }
        }

        return best == null ? (null, "") : (best, Rest(args, bestLength));
    }

    private static string Rest(string args, int length) => args[length..].Trim();
}
