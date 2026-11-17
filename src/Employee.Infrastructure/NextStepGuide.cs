using Employee.Core;

namespace Employee.Infrastructure;

// "What to do next" for a task: instead of only the error, say what it means and which button to
// press (names as they appear in the app). Null when nothing needs the user.
public static class NextStepGuide
{
    public static string? For(WorkItem goal, IReadOnlyList<WorkItem> steps, IReadOnlyList<PendingDecision> decisions, IManagerStore store)
    {
        static string Clip(string? text, int max = 220) => string.IsNullOrWhiteSpace(text) ? "" : (text.Length <= max ? text : text[..max] + "…").Trim();
        static string Name(WorkItem step) => "“" + Clip(step.Request.Split('\n')[0], 70) + "”";
        var all = steps.Append(goal).ToArray();

        var logsOf = all.ToDictionary(w => w.Id, w => store.Logs(w.Id));
        // 0. A step stopped and said why: that explains the task better than a generic review request.
        foreach (var step in steps.Where(s => s.State is WorkState.NeedsReview or WorkState.Failed or WorkState.Paused))
        {
            var gaveUp = logsOf[step.Id].LastOrDefault(l => l.Event is "StepUnfinished" or "StepIncomplete" or "Execution");
            if (gaveUp?.Event != "StepUnfinished")
                continue;
            var why = StepStatus.WhyUnfinished(gaveUp.Message);
            var stopped = why == StepStatus.Why.External || step.Notes.StartsWith("The AI cannot finish this step itself", StringComparison.Ordinal) || step.Notes.Contains("outside the AI", StringComparison.Ordinal)
                ? $"Step {Name(step)} needs something only you can give: {Clip(gaveUp.Message)}. Provide it (attach the file, answer the question, sign in), then write it in Comments and press Send & continue updating — or press Skip step."
                : $"Step {Name(step)} stopped unfinished: {Clip(gaveUp.Message)}. Write what to do differently and press Send & continue updating, or More ▾ → Change AI for this step, or Skip step.";
            return stopped;
        }

        // 1. Something waits for a decision.
        if (decisions.Count > 0)
        {
            var kind = decisions[0].Kind;
            return kind switch
            {
                "Plan" => "A plan is waiting for you. Open Steps & approvals, read it, then press ✔ Approve to start it or ✖ Reject.",
                "ToolApproval" or "ToolReview" => "A tool request is waiting for you. Open Steps & approvals, check it, then press ✔ Approve to run it or ✖ Reject.",
                "Review" or "TestReview" => "A result is waiting for your review. Check it in Comments (or More ▾ → View step result), then press ✔ Approve to accept it, ✖ Reject, or write what to change and press Send & continue updating.",
                "Access" or "TaskAccess" => "The AI asks for more access to the project. Open Steps & approvals, read why, then press ✔ Approve to grant it or ✖ Reject.",
                "MilestoneSignOff" => "A milestone passed its tester and waits for your sign-off. Run the project and check it, then press ✔ Approve and Run / resume to start the next milestone, or write what to change and press Send & continue updating: that milestone is reworked and tested again.",
                "ProviderWait" =>"The task waits for an AI to become available. Press ✔ Approve to try again now, or wait and press Run / resume later.",
                _ => $"{decisions.Count} decision(s) wait for you in Steps & approvals: select one, read the reason, then press ✔ Approve or ✖ Reject."
            };
        }

        // 2. Proposed file changes not applied yet.
        if (all.Any(w => store.Logs(w.Id) is var logs && logs.LastOrDefault(l => l.Event == "EditProposal") is { } proposal && !logs.Any(l => l.Event == "EditsApplied" && l.Sequence > proposal.Sequence)))
            return "An AI proposed file changes instead of editing directly. Open Steps & approvals, press Apply edits, check the changes in History, then confirm.";

        // 3. Access, project and provider problems on the task itself.
        if (goal.State == WorkState.WrongAccess || steps.Any(s => s.State == WorkState.WrongAccess))
            return "The task needs to edit project files but only has read access. Open the Project tab and press Save project (project tasks get write access), then press Run / resume.";
        if (goal.State == WorkState.WrongProject || steps.Any(s => s.State == WorkState.WrongProject))
            return "The task points at a project that is missing or wrong. Open the Project tab, choose the right project, press Save project, then Run / resume.";
        if (goal.State == WorkState.WaitingForProvider || steps.Any(s => s.State == WorkState.WaitingForProvider))
        {
            var reason = steps.Append(goal).Select(s => s.BlockReason).FirstOrDefault(r => !string.IsNullOrWhiteSpace(r)) ?? "";
            var until = System.Text.RegularExpressions.Regex.Match(reason, @"until ([0-9]{4}-[0-9]{2}-[0-9]{2} [0-9:]{5})").Groups[1].Value;
            return "No AI can run this step right now (usage used up or not signed in" + (until.Length > 0 ? ", earliest retry " + until : "") + "). Wait and press Run / resume, or enable/sign in another AI in Settings → AI providers, then Run / resume.";
        }

        // 4. Checks that stopped the run.
        var rootLogs = logsOf[goal.Id];
        if (rootLogs.LastOrDefault(l => l.Event is "AutoCheckStalled" or "AutoCheckPassed" or "AutoCheckFailed") is { Event: "AutoCheckStalled" } check)
            return "The project still fails its build/test check after an automatic fix (errors are in Comments). Write a hint about the cause and press Send & continue updating, or fix it yourself and press Run / resume. Errors: " + Clip(check.Message, 160);
        if (rootLogs.LastOrDefault(l => l.Event is "AcceptanceStalled" or "AcceptancePassed" or "AcceptanceCriteria" or "TaskUpdateRequested") is { Event: "AcceptanceStalled" } unaccepted)
            return "The result was tested and fixed several times but still misses its acceptance criteria: " + Clip(unaccepted.Message, 260) + " The tester's reports are in Comments. Write a hint (or say the result is fine as it is) and press Send & continue updating, or press Complete if you accept it.";
        if (rootLogs.LastOrDefault(l => l.Event is "GoalCheckStalled" or "GoalVerified" or "GoalCheck") is { Event: "GoalCheckStalled" } stalled)
            return "The goal check keeps finding the same gaps: " + Clip(stalled.Message, 180) + " Write how to close them (or that the result is fine) and press Send & continue updating, or press Complete if you accept it.";
        if (steps.FirstOrDefault(s => s.State == WorkState.Failed) is { } failed)
        {
            var reason = Clip(string.IsNullOrWhiteSpace(failed.BlockReason) ? failed.Notes : failed.BlockReason);
            var hint = failed.Tool != null || reason.Contains("tool", StringComparison.OrdinalIgnoreCase) || reason.Contains("backend", StringComparison.OrdinalIgnoreCase)
                ? "Check the tool on the Tools page (is it running, is its key/setting there?), then press Run / resume to retry it"
                : "Fix the cause if you can, then press Run / resume to retry it";
            return $"Step {Name(failed)} failed: {reason} {hint}; or press Skip step to go on without it.";
        }

        // 5. Simple states.
        if (goal.State == WorkState.Paused || steps.Any(s => s.State == WorkState.Paused))
            return "The task is paused" + (string.IsNullOrWhiteSpace(goal.BlockReason) ? "" : ": " + Clip(goal.BlockReason, 160)) + ". Press Run / resume to continue from where it stopped.";
        if (goal.State == WorkState.NeedsReview && steps.All(s => s.State is WorkState.Completed or WorkState.Skipped or WorkState.NeedsReview))
            return "All steps are done. Check the results in Comments; press Complete if you are happy, or write what to change and press Send & continue updating.";
        if (goal.State == WorkState.Queued && steps.Count == 0)
            return "The task has not started. Press Run / resume to plan and run it.";
        return null;
    }
}
