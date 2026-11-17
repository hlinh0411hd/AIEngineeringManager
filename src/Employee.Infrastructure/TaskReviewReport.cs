using System.Text.Json;
using Employee.Core;

namespace Employee.Infrastructure;
public sealed record TaskReviewReport(string Title, string Summary, string Result, string Changes, string Verification, string RawLog, string ProjectRoot)
{
    public static TaskReviewReport Create(WorkItem work, ProjectInfo project, IReadOnlyList<TaskLog> logs, Handoff? checkpoint)
    {
        var execution = logs.LastOrDefault(l => l.Event == "Execution");
        var latestRoute = logs.LastOrDefault(l => l.Event == "Route");
        var current = execution != null && (latestRoute == null || execution.Sequence > latestRoute.Sequence);
        var output = current ? execution!.Message[(execution.Message.IndexOf('\n') + 1)..] : "";
        if (output.Length == 0 && latestRoute == null && checkpoint?.Reason.StartsWith("After execution", StringComparison.OrdinalIgnoreCase) == true)
            output = checkpoint.Progress;
        if (work.Tool != null)
            output = logs.LastOrDefault(l => l.Event == "ToolResult")?.Message ?? work.Notes;
        var result = output.Length > 0 ? ReadableOutput(output) : "No AI response was saved for this run. Check the log for status.";
        var summary = work.State == WorkState.NeedsReview ? "AI finished its run. Review the response, inspect files and run the project before confirming completion." : $"State: {work.State}. {work.Notes}";
        var changes = "";
        var proposal = logs.LastOrDefault(l => l.Event == "EditProposal");
        if (proposal != null && (latestRoute == null || proposal.Sequence > latestRoute.Sequence))
        {
            var applied = logs.Any(l => l.Event == "EditsApplied" && l.Sequence > proposal.Sequence);
            changes += (applied ? "User applied the proposal." : "PROPOSAL NOT APPLIED. Review the contents below, then use Apply reviewed edits if you agree.") + "\n\n" + proposal.Message + "\n\n";
        }

        if (checkpoint != null)
        {
            changes += "Project-wide Git snapshot at " + checkpoint.At.ToLocalTime().ToString("g") + ". May include changes that existed before this task.\n\nFile list / Git status:\n" + (string.IsNullOrWhiteSpace(checkpoint.GitStatus) ? "Git reported no changed files." : checkpoint.GitStatus) + "\n\nDiff (length limited; new untracked files may have no diff):\n" + (string.IsNullOrWhiteSpace(checkpoint.GitDiff) ? "No diff was saved." : checkpoint.GitDiff);
        }
        else
            changes += "No checkpoint or file diff was saved. Inspect the project folder and AI response; this does not prove that no files changed.";
        var verification = "The manager has not independently verified result quality.\n\n1. Read the AI response in AI result.\n2. Compare Changed files with the project folder.\n3. Run the project to inspect the actual UI or result; use the project's run command or Build.bat when needed.\n4. If it meets the request, return to the task and select Mark reviewed complete. Otherwise, create a follow-up task with specific feedback.\n\n";
        verification += Orchestrator.EditsDirectly(work.ProviderId) && work.Access == AccessLevel.WriteProject ? (work.ProviderId switch { "claude" => "Claude", "qoder" => "Qoder", "antigravity" => "Antigravity", _ => "Codex" }) + " may have edited source files directly. Use Apply reviewed edits only if there is a separate file edit proposal.\n\n" : "";
        verification += "Checkpoint test information (if available):\n" + (checkpoint?.Tests ?? "No test information was saved.");
        verification += "\n\nTest status: " + work.Verification + "\n" + string.Join("\n\n", logs.Where(l => l.Event == "SmartTest").TakeLast(3).Select(l => l.Message));
        var raw = string.Join("\n\n", logs.Select(l => $"{l.At.ToLocalTime():g} [{l.Event}] {l.Message}"));
        return new(work.Request, summary, result, changes, verification, raw, project.Root);
    }

    public static string ReadableOutput(string output)
    {
        var messages = new List<string>();
        foreach (var line in output.Split('\n'))
        {
            try
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    continue;
                if (root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "item.completed" && root.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object && item.TryGetProperty("type", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == "agent_message" && item.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    messages.Add(text.GetString()!);
            }
            catch (JsonException)
            {
            }
        }

        return messages.Count > 0 ? string.Join("\n\n", messages) : output;
    }
}
