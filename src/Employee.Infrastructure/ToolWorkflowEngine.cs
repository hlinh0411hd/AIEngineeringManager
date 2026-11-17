using System.Text.Json.Nodes;
using Employee.Core;

namespace Employee.Infrastructure;
public sealed class ToolWorkflowEngine(IToolWorkflowStore store, IEmployeeStore manager, ToolExecutionService tools, Func<string[], IDisposable> reserve)
{
    private CancellationTokenSource? cancellation;
    private int active;
    private bool pauseRequested;
    public bool IsBusy => Volatile.Read(ref active) != 0;

    public event Action? Changed;
    public void Save(ToolWorkflow workflow)
    {
        if (IsBusy)
            throw new InvalidOperationException("Stop the workflow before editing.");
        if (string.IsNullOrWhiteSpace(workflow.Name) || !manager.Projects().Any(p => p.Id == workflow.ProjectId) || workflow.Steps.Count is < 1 or > 50)
            throw new InvalidOperationException("Choose a registered project, name and 1–50 steps.");
        var keys = new HashSet<string>();
        foreach (var step in workflow.Steps)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(step.Key, @"^[A-Za-z0-9_-]{1,60}$") || !keys.Add(step.Key))
                throw new InvalidOperationException("Step keys must be unique simple identifiers.");
            var tool = store.Tools().Single(t => t.Id == step.ToolId);
            if (!ToolExecutionService.Capabilities(tool.Connector).Any(c => c.Id == step.Operation))
                throw new InvalidOperationException("Unsupported tool operation: " + step.Operation);
        }

        TaskDependencies.Validate(workflow.Steps.Select(s => new WorkItem { Id = s.Key, DependsOn = s.DependsOn }).ToArray());
        // Existing execution records cannot be rebound to different input/steps through a JSON edit.
        var previous = store.Workflows().FirstOrDefault(w => w.Id == workflow.Id);
        if (previous?.Steps.Any(s => s.RunId != null) == true)
            throw new InvalidOperationException("An executed workflow cannot be edited in place. Import it with a new ID and no RunId values.");
        if (workflow.Steps.Any(s => s.RunId != null))
            throw new InvalidOperationException("Imported workflows must not contain execution IDs.");
        workflow.State = ToolRunState.Draft;
        store.SaveWorkflow(workflow);
        Changed?.Invoke();
    }

    public async Task RunAsync(string id, CancellationToken token = default)
    {
        var workflow = store.Workflows().Single(w => w.Id == id);
        if (Interlocked.CompareExchange(ref active, 1, 0) != 0)
            throw new InvalidOperationException("A tool workflow is already running.");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        cancellation = cancel;
        pauseRequested = false;
        try
        {
            if (workflow.State == ToolRunState.Completed)
                throw new InvalidOperationException("Workflow already confirmed complete.");
            TaskDependencies.Validate(workflow.Steps.Select(s => new WorkItem { Id = s.Key, DependsOn = s.DependsOn }).ToArray());
            var project = manager.Projects().Single(p => p.Id == workflow.ProjectId);
            var paths = new[]
            {
                project.Root
            }.Concat(workflow.Steps.SelectMany(s =>
            {
                var p = store.Tools().Single(t => t.Id == s.ToolId);
                return new[]
                {
                    p.SourcePath,
                    p.WorkspaceRoot
                };
            })).Where(Directory.Exists).ToArray();
            using var lease = reserve(paths);
            workflow.State = ToolRunState.Running;
            store.SaveWorkflow(workflow);
            Changed?.Invoke();
            foreach (var step in Topological(workflow))
            {
                cancel.Token.ThrowIfCancellationRequested();
                if (pauseRequested)
                {
                    workflow.State = ToolRunState.Draft;
                    workflow.Notes = "Paused after the current step; resume preserves existing run IDs.";
                    return;
                }

                var runs = store.ToolRuns();
                if (step.DependsOn.Any(key =>
                {
                    var dependency = workflow.Steps.Single(s => s.Key == key);
                    return dependency.RunId == null || runs.All(r => r.Id != dependency.RunId || r.State != ToolRunState.Completed);
                }))
                    continue;
                if (step.RunId != null)
                {
                    var previous = runs.Single(r => r.Id == step.RunId);
                    if (previous.State == ToolRunState.Completed)
                        continue;
                    if (previous.State != ToolRunState.Draft)
                    {
                        workflow.Notes = "Review/approve step " + step.Key + " in the tool-run queue. Interrupted writes are never replayed.";
                        continue;
                    }
                }
                else
                {
                    var input = Resolve(step.Input, workflow, step.DependsOn).AsObject();
                    var request = tools.Create(step.ToolId, step.Operation, input.ToJsonString());
                    step.RunId = request.Id;
                    store.SaveWorkflow(workflow);
                    if (request.State == ToolRunState.WaitingApproval)
                    {
                        workflow.Notes = "Approve exact request for step " + step.Key + " in Tools, then Resume.";
                        continue;
                    }
                }

                await tools.ExecuteAsync(step.RunId!, cancel.Token, scopeReserved: true);
                var completed = store.ToolRuns().Single(r => r.Id == step.RunId);
                var capability = ToolExecutionService.Capabilities(store.Tools().Single(t => t.Id == step.ToolId).Connector).Single(c => c.Id == step.Operation);
                if (completed.State == ToolRunState.NeedsReview && workflow.AutoContinueLocal && !capability.Publishes)
                {
                    tools.Review(completed.Id);
                    completed.State = ToolRunState.Completed;
                }

                if (completed.State != ToolRunState.Completed)
                {
                    workflow.Notes = "Inspect step " + step.Key + " before continuing.";
                    continue;
                }

                store.SaveWorkflow(workflow);
                Changed?.Invoke();
            }

            var pending = workflow.Steps.Any(s => s.RunId == null || store.ToolRuns().All(r => r.Id != s.RunId || r.State != ToolRunState.Completed));
            workflow.State = pending && store.ToolRuns().Any(r => workflow.Steps.Any(s => s.RunId == r.Id) && r.State == ToolRunState.WaitingApproval) ? ToolRunState.WaitingApproval : ToolRunState.NeedsReview;
            if (!pending)
                workflow.Notes = "All tool steps finished. Inspect artifacts and confirm the final workflow result.";
        }
        catch (OperationCanceledException)
        {
            workflow.State = ToolRunState.Cancelled;
            workflow.Notes = "Stopped; execution history retained.";
        }
        catch (Exception ex)
        {
            workflow.State = ToolRunState.Failed;
            workflow.Notes = ex.Message;
        }
        finally
        {
            store.SaveWorkflow(workflow);
            cancellation = null;
            Volatile.Write(ref active, 0);
            Changed?.Invoke();
        }
    }

    private IEnumerable<ToolWorkflowStep> Topological(ToolWorkflow workflow)
    {
        var visited = new HashSet<string>();
        var ordered = new List<ToolWorkflowStep>();
        void Visit(ToolWorkflowStep step)
        {
            if (!visited.Add(step.Key))
                return;
            foreach (var dependency in step.DependsOn)
                Visit(workflow.Steps.Single(s => s.Key == dependency));
            ordered.Add(step);
        }

        foreach (var step in workflow.Steps)
            Visit(step);
        return ordered;
    }

    private JsonNode Resolve(JsonNode node, ToolWorkflow workflow, IReadOnlyList<string> dependencies)
    {
        if (node is JsonObject obj && obj.Count == 1 && obj["$from"] is JsonValue reference)
        {
            var parts = reference.GetValue<string>().Split('.');
            if (parts.Length < 2 || !dependencies.Contains(parts[0]))
                throw new InvalidOperationException("Output references must name a declared dependency: " + reference);
            var source = workflow.Steps.Single(s => s.Key == parts[0]);
            var run = store.ToolRuns().Single(r => r.Id == source.RunId && r.State == ToolRunState.Completed);
            var output = tools.ReadOutput(run);
            foreach (var part in parts.Skip(1))
                output = output[part] ?? throw new InvalidOperationException("Missing dependency output field: " + part);
            return output.DeepClone();
        }

        if (node is JsonObject map)
        {
            var result = new JsonObject();
            foreach (var item in map)
                result[item.Key] = item.Value == null ? null : Resolve(item.Value, workflow, dependencies);
            return result;
        }

        if (node is JsonArray array)
        {
            var result = new JsonArray();
            foreach (var item in array)
                result.Add(item == null ? null : Resolve(item, workflow, dependencies));
            return result;
        }

        return node.DeepClone();
    }

    public void Confirm(string id)
    {
        if (IsBusy)
            throw new InvalidOperationException("Wait for execution to finish.");
        var workflow = store.Workflows().Single(w => w.Id == id);
        if (workflow.Steps.Any(s => s.RunId == null || store.ToolRuns().All(r => r.Id != s.RunId || r.State != ToolRunState.Completed)))
            throw new InvalidOperationException("Unfinished steps remain.");
        workflow.State = ToolRunState.Completed;
        store.SaveWorkflow(workflow);
        Changed?.Invoke();
    }

    public void Stop() => cancellation?.Cancel();
    public void PauseAfterCurrent() => pauseRequested = true;
}
