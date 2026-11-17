using System.Text.Json.Nodes;

namespace Employee.Core;
public enum ToolRunState
{
    Draft,
    WaitingApproval,
    Running,
    NeedsReview,
    Completed,
    Failed,
    Interrupted,
    Cancelled
}

public sealed class ConnectedTool
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Connector { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string WorkspaceRoot { get; set; } = "";
    public string Executable { get; set; } = "";
    public string DesktopExecutable { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string ProtectedToken { get; set; } = "";
    public bool Enabled { get; set; }
    public bool AllowWrites { get; set; }
    public bool AllowModelCalls { get; set; }
    public bool AllowTaskAutomation { get; set; }
    public bool AllowPublishing { get; set; }
    public bool AutoStartBackend { get; set; } = true;
    public string BackendExecutable { get; set; } = "";
    public string[] BackendArguments { get; set; } = [];
    public string BackendWorkingDirectory { get; set; } = "";
    public string BackendDataRoot { get; set; } = "";
    public int BackendStartupTimeoutSeconds { get; set; } = 90;
    public int TimeoutSeconds { get; set; } = 600;
    public string Version { get; set; } = "Unknown";
    public string Status { get; set; } = "Not checked";
    public DateTimeOffset? CheckedAt { get; set; }
}

public sealed record ToolTaskSpec(string ToolId, string SkillId, string Operation, JsonObject? Input = null, string? InputFrom = null);
// What the tool task form submits: the task request, the tool and its settings as chosen in the
// form, and the files they refer to.
public sealed record ToolTaskForm(string ProjectId, string ToolId, string Operation, JsonObject Input, IReadOnlyList<WorkAttachment> Attachments, string Request, JsonObject? FirstInput = null);
public sealed record ToolCapability(string Id, string Description, bool Writes = false, bool UsesAI = false, bool Publishes = false);
public sealed class ToolRun
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ToolId { get; set; } = "";
    public string Operation { get; set; } = "";
    public string Input { get; set; } = "{}";
    public string? PayloadFile { get; set; }
    public string? PayloadSha256 { get; set; }
    public string ApprovedHash { get; set; } = "";
    public ToolRunState State { get; set; } = ToolRunState.WaitingApproval;
    public string Output { get; set; } = "";
    public string? OutputFile { get; set; }
    public string? OutputSha256 { get; set; }
    public string ConnectionLog { get; set; } = "";
    public string Error { get; set; } = "";
    public bool ModelCallReserved { get; set; }
    public string? RemoteId { get; set; }
    public string? ProjectAssetPath { get; set; }
    public string? ProjectAssetSha256 { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ToolWorkflow
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public List<ToolWorkflowStep> Steps { get; set; } = [];
    public ToolRunState State { get; set; } = ToolRunState.Draft;
    public string Notes { get; set; } = "";
    public bool AutoContinueLocal { get; set; }
}

public sealed class ToolWorkflowStep
{
    public string Key { get; set; } = "";
    public string ToolId { get; set; } = "";
    public string Operation { get; set; } = "";
    public JsonObject Input { get; set; } = new();
    public List<string> DependsOn { get; set; } = [];
    public string? RunId { get; set; }
}

public interface IToolWorkflowStore
{
    IReadOnlyList<ConnectedTool> Tools();
    void SaveTool(ConnectedTool tool);
    IReadOnlyList<ToolRun> ToolRuns();
    void SaveToolRun(ToolRun run);
    IReadOnlyList<ToolWorkflow> Workflows();
    void SaveWorkflow(ToolWorkflow workflow);
}
