using System.Text.Json;
using Employee.Core;

namespace Employee.Infrastructure;
public sealed partial class SqliteStore : IToolWorkflowStore
{
    public IReadOnlyList<ConnectedTool> Tools() => Read("SELECT data FROM connected_tools", Decode<ConnectedTool>);
    public void SaveTool(ConnectedTool tool) => Execute("INSERT INTO connected_tools VALUES($id,$data) ON CONFLICT(id) DO UPDATE SET data=$data", ("$id", tool.Id), ("$data", JsonSerializer.Serialize(tool)));
    public IReadOnlyList<ToolRun> ToolRuns() => Read("SELECT data FROM tool_runs ORDER BY rowid DESC", Decode<ToolRun>);
    public void SaveToolRun(ToolRun run) => Execute("INSERT INTO tool_runs VALUES($id,$data) ON CONFLICT(id) DO UPDATE SET data=$data", ("$id", run.Id), ("$data", JsonSerializer.Serialize(run)));
    public IReadOnlyList<ToolWorkflow> Workflows() => Read("SELECT data FROM tool_workflows", Decode<ToolWorkflow>);
    public void SaveWorkflow(ToolWorkflow workflow) => Execute("INSERT INTO tool_workflows VALUES($id,$data) ON CONFLICT(id) DO UPDATE SET data=$data", ("$id", workflow.Id), ("$data", JsonSerializer.Serialize(workflow)));
    public void RecoverToolRuns()
    {
        foreach (var run in ToolRuns().Where(r => r.State == ToolRunState.Running))
        {
            run.State = ToolRunState.Interrupted;
            run.Error = "Execution interrupted. Inspect the tool before creating another request; remote writes are never retried blindly.";
            SaveToolRun(run);
        }

        foreach (var workflow in Workflows().Where(w => w.State == ToolRunState.Running))
        {
            workflow.State = ToolRunState.Interrupted;
            SaveWorkflow(workflow);
        }
    }
}
