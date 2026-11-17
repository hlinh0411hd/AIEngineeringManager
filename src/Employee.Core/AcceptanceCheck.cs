using System.Text.Json;
using System.Text.RegularExpressions;

namespace Employee.Core;

// One criterion as judged by the independent tester step. Blocking=false marks a cosmetic or minor
// miss that does not stop the milestone from being usable: it is reported, not fixed in a loop.
public sealed record CriterionVerdict(int Id, bool Pass, bool Blocking, string Evidence)
{
    // Could not be exercised on this machine (needs a real screen, device or person); the checkable part passed.
    public bool Unverifiable { get; init; }
}
public sealed record AcceptanceVerdict(IReadOnlyList<CriterionVerdict> Criteria, string Summary)
{
    public IReadOnlyList<CriterionVerdict> BlockingFailures => Criteria.Where(c => !c.Pass && c.Blocking).ToArray();
    public IReadOnlyList<CriterionVerdict> MinorIssues => Criteria.Where(c => !c.Pass && !c.Blocking).ToArray();
}

// "Acceptable" for a goal is what its plan wrote down as acceptance criteria, judged by a tester
// step that runs the project. The manager verifies one scope at a time: each phase when its steps
// finish, then the whole goal. Blocking failures become a fix step and the scope is verified again,
// for a bounded number of rounds.
public static class AcceptanceCheck
{
    public const int DefaultRounds = 3;
    public const int MaxCriteria = 12;
    public const string GoalScope = "goal";
    public const string Marker = "ACCEPTANCE:";

    public static string[] Clean(IEnumerable<string>? criteria) => (criteria ?? [])
        .Where(c => !string.IsNullOrWhiteSpace(c))
        .Select(c => Regex.Replace(c, @"\s+", " ").Trim())
        .Select(c => c.Length > 400 ? c[..400] : c)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(MaxCriteria)
        .ToArray();

    // The scope being worked on. While the goal is open: the current milestone, which the manager
    // moves itself (OpenPhase), never the coordinator's own count. Once the coordinator says the
    // goal is done: every milestone not yet accepted, in order, then the whole goal. Seen live: the
    // coordinator kept saying "phase 1" while planning later work (milestone 1 was tested four
    // times), then jumped to 3 and 4, and milestones 2 and 4 were never tested.
    public static string ScopeKey(WorkSession session) =>
        session.Phases.Count == 0 ? GoalScope
        : !session.GoalVerified ? "phase:" + session.PhaseIndex
        : OpenPhase(session) is int open ? "phase:" + open : GoalScope;

    // The first milestone the tester has not accepted yet, or null when all are.
    public static int? OpenPhase(WorkSession session)
    {
        for (var i = 0; i < session.Phases.Count; i++)
            if (!session.AcceptedScopes.Contains("phase:" + i))
                return i;
        return null;
    }

    public static string ScopeName(WorkSession session, string scope)
    {
        if (scope == GoalScope)
            return "the whole goal";
        var index = PhaseIndex(scope);
        return index >= 0 && index < session.Phases.Count ? $"milestone {index + 1}/{session.Phases.Count} \"{session.Phases[index].Name}\"" : scope;
    }

    private static int PhaseIndex(string scope) => scope.StartsWith("phase:", StringComparison.Ordinal) && int.TryParse(scope[6..], out var index) ? index : -1;

    // A phase without its own criteria is judged by its goal sentence; the whole goal only by what
    // the plan listed (none means: nothing to verify, as for a small edit).
    public static IReadOnlyList<string> Criteria(WorkSession session, string scope)
    {
        if (scope == GoalScope)
            return Clean(session.Acceptance);
        var index = PhaseIndex(scope);
        if (index < 0 || index >= session.Phases.Count)
            return [];
        var own = Clean(session.Phases[index].Acceptance);
        return own.Length > 0 ? own : Clean([session.Phases[index].Goal]);
    }

    public static string VerifyRequest(WorkSession session, string scope, IReadOnlyList<string> criteria, int round, string? previousFailures)
    {
        var list = string.Join("\n", criteria.Select((c, i) => $"{i + 1}. {c}"));
        return $"ACCEPTANCE VERIFICATION of {ScopeName(session, scope)} (round {round}). You are the independent tester, not the author: assume nothing works until you have seen it work.\n" +
            "Criteria:\n" + list + "\n" +
            (criteria.Select((c, i) => (c, i)).Where(x => session.HumanChecks.Contains(x.c)).Select(x => x.i + 1).ToArray() is { Length: > 0 } handed
                ? "An earlier tester already handed criteria " + string.Join(", ", handed) + " to the person because they cannot be exercised on this machine. Re-check only their checkable part; if it still passes, answer unverifiable=true again, not a failure.\n" : "") +
            (string.IsNullOrWhiteSpace(previousFailures) ? "" : "The previous round failed on:\n" + previousFailures + "\nA fix step ran since then; test those points again with extra care, and re-test the rest.\n") +
            "How to test: check the real deliverable for every criterion, whatever kind it is.\n" +
            "- Software: run its build and its self-check command (.aiemployee/check.json or the project's test command) and read the output; start the program or load its entry point headlessly and drive it with a small script (simulate input, advance the loop, read state) where the criterion is about behaviour.\n" +
            "- Files made by a tool or generator (images, sprite sheets, video, audio, exports, data): the results of this task's tool steps are listed below when there are any. Open each file: it exists at the reported path, is not empty, has the expected format and the measurable properties the criterion names (dimensions, frame count, duration, sample rate, transparency: read them with a script or an installed program such as ffprobe), and is really used where the task says it is. A tool step that reported success is a claim until you have opened its output.\n" +
            "- Documents and content (reports, designs, drafts, posts, translations): open them and check structure, required sections, lengths, links and sources, and that the content answers the request rather than restating it.\n" +
            "- Results that live in a connected tool (a saved project, a scheduled item): confirm from the tool step's result and the files it wrote; do not call the tool yourself.\n" +
            "Reading alone is evidence only when a criterion cannot be exercised on this machine; then say so and what you checked instead.\n" +
            "You may add or extend test scripts under tests/ (or the project's test folder) and the self-check, so the next round can rerun them. Do NOT change the deliverable (code, data, assets, documents) to make a criterion pass, and do not weaken or reinterpret a criterion: a failure is a useful result. Do not install global software.\n" +
            "Answer with a short report, then exactly one line starting with " + Marker + " followed by JSON on the same line:\n" +
            Marker + " {\"criteria\":[{\"id\":1,\"pass\":true,\"blocking\":true,\"evidence\":\"what you ran and what you saw\"}],\"summary\":\"one sentence\"}\n" +
            "One entry per criterion, with the criterion's number as id. pass=true only with evidence from this run. For a failed criterion set blocking=false only when it is cosmetic or minor and the result is still usable for its purpose; everything that breaks or misses the requested behaviour is blocking=true. Evidence: at most 300 characters each, naming the command or file.\n" +
            "UNVERIFIABLE HERE: when a criterion cannot be exercised on this machine at all (it needs a real browser window, a phone, a speaker, a person's eyes or judgement) AND everything that can be checked here passed (the code, the files, a headless run of the same logic), answer pass=false, \"unverifiable\":true, and put in evidence what you checked and what a person must look at. Never use it for something you could have run, or when the checkable part fails: then it is a normal failure.\n" +
            "End with STEP STATUS: COMPLETE when every criterion has a verdict (also when some fail).";
    }

    public static string FixRequest(WorkSession session, string scope, IReadOnlyList<string> criteria, AcceptanceVerdict verdict, int round)
    {
        var failures = Describe(criteria, verdict.BlockingFailures);
        return $"ACCEPTANCE FIX for {ScopeName(session, scope)} (after verification round {round}). An independent tester checked the result and these criteria FAILED:\n" + failures + "\n" +
            "For each one: reproduce the failure with the tester's command or script first, find the real cause, fix the deliverable itself (code, data, document or asset; when a tool-made file is wrong and you cannot correct it directly, say exactly what the tool must be asked to produce instead), then run the same check again and confirm it now passes. Do not remove features, weaken the criterion, special-case the test or disable checks to get a pass. Keep everything that already passed working, and keep the project's self-check command green. " +
            "A tester will verify all criteria again after this step." +
            (verdict.MinorIssues.Count > 0 ? "\nMinor issues also seen (fix them only if cheap):\n" + Describe(criteria, verdict.MinorIssues) : "");
    }

    public static string Describe(IReadOnlyList<string> criteria, IEnumerable<CriterionVerdict> verdicts) => string.Join("\n", verdicts.Select(v =>
        $"- [{v.Id}] {(v.Id >= 1 && v.Id <= criteria.Count ? criteria[v.Id - 1] : "(unknown criterion)")} — {(string.IsNullOrWhiteSpace(v.Evidence) ? "no evidence given" : v.Evidence)}"));

    private sealed record RawCriterion(int? Id, bool? Pass, bool? Blocking, string? Evidence, bool? Unverifiable = null);
    private sealed record RawVerdict(RawCriterion[]? Criteria, string? Summary);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, AllowTrailingCommas = true };

    // The tester's verdict from its answer, or null when it gave none that can be read. A criterion
    // the tester left out counts as failed: unverified is not accepted.
    public static AcceptanceVerdict? Parse(string? output, int criteriaCount)
    {
        if (string.IsNullOrEmpty(output) || criteriaCount <= 0)
            return null;
        var at = output.LastIndexOf(Marker, StringComparison.Ordinal);
        while (at >= 0)
        {
            var json = Balanced(output, at + Marker.Length);
            if (json != null)
            {
                try
                {
                    var raw = JsonSerializer.Deserialize<RawVerdict>(json, Json);
                    if (raw?.Criteria is { Length: > 0 } items)
                    {
                        var byId = new Dictionary<int, CriterionVerdict>();
                        for (var i = 0; i < items.Length; i++)
                        {
                            var id = items[i].Id ?? i + 1;
                            if (id < 1 || id > criteriaCount || items[i].Pass == null)
                                continue;
                            var evidence = (items[i].Evidence ?? "").Trim();
                            // Unverifiable here: not a pass, but a fix step cannot change the machine, so it
                            // goes to the person instead of the fix loop.
                            var unverifiable = items[i].Pass == false && items[i].Unverifiable == true;
                            byId[id] = new(id, items[i].Pass!.Value, !unverifiable && (items[i].Blocking ?? true), evidence.Length > 600 ? evidence[..600] : evidence) { Unverifiable = unverifiable };
                        }
                        if (byId.Count > 0)
                            return new(Enumerable.Range(1, criteriaCount).Select(id => byId.TryGetValue(id, out var v) ? v : new(id, false, true, "The tester gave no verdict for this criterion.")).ToArray(), (raw.Summary ?? "").Trim());
                    }
                }
                catch (JsonException)
                {
                }
            }
            at = at == 0 ? -1 : output.LastIndexOf(Marker, at - 1, StringComparison.Ordinal);
        }
        return null;
    }

    // The JSON object that starts at the first '{' after the marker (same line or the next ones).
    private static string? Balanced(string text, int from)
    {
        var start = text.IndexOf('{', from);
        if (start < 0 || text[from..start].Trim().Trim('`').Trim().Length > 12)
            return null;
        int depth = 0;
        bool inString = false, escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0)
                return text[start..(i + 1)];
        }
        return null;
    }

    // One line per verified scope, for the task's final notes.
    public static string Summary(WorkSession session)
    {
        if (!session.VerifyAcceptance || (session.AcceptedScopes.Count == 0 && session.AcceptanceStalled == null))
            return "";
        var parts = new List<string>();
        if (session.AcceptedScopes.Count > 0)
            parts.Add("Acceptance verified by an independent tester: " + string.Join(", ", session.AcceptedScopes.Select(scope => ScopeName(session, scope))) + ".");
        if (session.AcceptanceStalled != null)
            parts.Add("NOT ACCEPTED: " + session.AcceptanceStalled);
        if (session.KnownIssues.Count > 0)
            parts.Add("Known minor issues: " + string.Join("; ", session.KnownIssues.TakeLast(6)));
        return " " + string.Join(" ", parts);
    }
}
