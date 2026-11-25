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

// Planners write a tool step either with the stored names (toolId, skillId) or with the short ones the
// planner prompt also shows (id, skill). Both are read; the stored form is written.
[System.Text.Json.Serialization.JsonConverter(typeof(ToolTaskSpecJsonConverter))]
public sealed record ToolTaskSpec(string ToolId, string SkillId, string Operation, JsonObject? Input = null, string? InputFrom = null);

public sealed class ToolTaskSpecJsonConverter : System.Text.Json.Serialization.JsonConverter<ToolTaskSpec>
{
    public override ToolTaskSpec? Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
    {
        if (reader.TokenType == System.Text.Json.JsonTokenType.Null)
            return null;
        using var document = System.Text.Json.JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
            return null;
        string? Text(params string[] names)
        {
            foreach (var property in root.EnumerateObject())
                if (names.Any(name => string.Equals(name, property.Name, StringComparison.OrdinalIgnoreCase)) && property.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                    return property.Value.GetString();
            return null;
        }
        JsonObject? input = null;
        foreach (var property in root.EnumerateObject())
            if (string.Equals(property.Name, "input", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
                input = JsonNode.Parse(property.Value.GetRawText()) as JsonObject;
        return new ToolTaskSpec(Text("ToolId", "toolId", "id")!, Text("SkillId", "skillId", "skill")!, Text("Operation", "operation")!, input, Text("InputFrom", "inputFrom"));
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, ToolTaskSpec value, System.Text.Json.JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        WriteText(writer, "ToolId", value.ToolId);
        WriteText(writer, "SkillId", value.SkillId);
        WriteText(writer, "Operation", value.Operation);
        // Every property is written, null included, exactly as the default serializer does: the tool binding
        // hash of a saved step is computed from this JSON, and a changed shape would refuse to resume it.
        if (value.Input is { } input)
        {
            writer.WritePropertyName("Input");
            input.WriteTo(writer);
        }
        else
            writer.WriteNull("Input");
        WriteText(writer, "InputFrom", value.InputFrom);
        writer.WriteEndObject();
    }

    private static void WriteText(System.Text.Json.Utf8JsonWriter writer, string name, string? text)
    {
        if (text == null)
            writer.WriteNull(name);
        else
            writer.WriteString(name, text);
    }
}
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
