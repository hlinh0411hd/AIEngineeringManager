using System.Reflection;
using Employee.Core;
using Employee.Infrastructure;

static class ToolRecoveryChecks
{
    public static int Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ToolRecoveryChecks", Guid.NewGuid().ToString("N"));
        using var store = new SqliteStore(Path.Combine(folder, "checks.db"));
        var project = new ProjectInfo("p", "fixture", folder);
        store.SaveProject(project);
        var orchestrator = new Orchestrator(store, [], new ProcessRunner());
        var build = typeof(Orchestrator).GetMethod("BuildContext", BindingFlags.NonPublic | BindingFlags.Instance)!;
        int count = 0;
        void Assert(bool ok, string message)
        {
            count++;
            if (!ok)
                throw new Exception(message);
        }

        foreach (var access in new[]
        {
            AccessLevel.TextOnly,
            AccessLevel.ReadProject,
            AccessLevel.WriteProject
        }

        )
            foreach (var provider in new[]
            {
                "codex",
                "claude"
            }

            )
                foreach (bool coordinator in new[]
                {
                    false,
                    true
                }

                )
                {
                    var work = new WorkItem
                    {
                        ProjectId = project.Id,
                        Request = "Review or implement approved work",
                        Access = access,
                        IsCoordinator = coordinator
                    };
                    var context = (string)build.Invoke(orchestrator, [work, project, 50000, provider])!;
                    Assert(context.Contains("TOOL SELECTION AND RECOVERY"), "Recovery policy absent from worker context");
                    Assert(!context.Contains("Stop when blocked by permissions."), "Overbroad stop instruction retained");
                    Assert(context.Contains("Stop the denied action") && context.Contains("If the underlying operation is denied"), "Real refusals must remain authoritative");
                    Assert(context.Contains("Continue independent authorized steps") && context.Contains("PSSecurityException"), "Helper failure must not block unrelated work");
                    Assert(context.Contains("Never change execution policy") && context.Contains("encoded command"), "Policy/launcher bypass must stay forbidden");
                    Assert(context.Contains("Do not install tools") && context.Contains("paid APIs"), "Recovery cannot expand dependencies/spending");
                    Assert(context.Contains("explicit user requirement to stop the entire task"), "Explicit stop instruction lost");
                    bool implementation = provider == "codex" && access == AccessLevel.WriteProject && !coordinator;
                    Assert(context.Contains("ART DELIVERY CONTRACT (implementation)") == implementation, "Art application must stay inside implementation write scope");
                    if (implementation)
                    {
                        Assert(context.Contains("actual scene, prefab, resource, stylesheet or code consumer"), "Must wire actual consumers");
                        Assert(context.Contains("Apply each completed asset before starting the next"), "Do not orphan successful generations");
                        Assert(context.Contains("do not stop for an invented per-image confirmation"), "Artificial integration gate returned");
                        Assert(context.Contains("Do not extend budgets, ignore user cancellation, bypass permissions"), "Real interruption/security limits lost");
                        Assert(context.Contains("unless the user requests generation-only"), "Generation-only requests must remain possible");
                    }

                    if (coordinator)
                        Assert(context.Contains("COORDINATOR ROLE"), "Coordinator role lost");
                    if (provider == "codex" && access == AccessLevel.WriteProject && !coordinator)
                        Assert(context.Contains("ART EXECUTION"), "Native art route lost");
                }

        var guidance = typeof(Orchestrator).Assembly.GetType("Employee.Infrastructure.WorkerToolGuidance")!;
        var planning = (string)guidance.GetField("MediaPlanning", BindingFlags.Static | BindingFlags.Public)!.GetRawConstantValue()!;
        Assert(planning.Contains("one deliverable") && planning.Contains("existing write grant"), "Plan must deliver art plus application under existing grant");
        Assert(planning.Contains("Respect configured approval/continuation controls"), "Plan must preserve configured gates");
        Assert(planning.Contains("Preparation and coordinator steps remain read-only"), "Planner must not generate/import");
        Console.WriteLine($"PASS {count} tool recovery prompt checks across roles, access levels and providers; no AI, image generation, shell-policy change or project task executed.");
        return 0;
    }
}
