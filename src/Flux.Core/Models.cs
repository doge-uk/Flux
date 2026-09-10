using System.Text.Json;
using System.Text.Json.Nodes;

namespace Flux.Core;

public enum PermissionLevel
{
    ReadOnly = 0,
    Reversible = 1,
    Disruptive = 2,
    Destructive = 3
}

public enum SearchResultKind
{
    Application,
    File,
    Folder,
    Command,
    System,
    Ai
}

public enum FluxActionType
{
    None,
    Launch,
    OpenPath,
    TerminateProcess,
    CloseAllExcept,
    RestartProcess,
    CreateFolder,
    ShowProcesses,
    ShowSystemStats,
    AskAi
}

public enum RouteKind
{
    Search,
    ImmediateAction,
    Ai
}

public sealed record FluxAction(
    FluxActionType Type,
    string Target = "",
    PermissionLevel Permission = PermissionLevel.ReadOnly,
    string? DisplayName = null,
    IReadOnlyDictionary<string, string>? Arguments = null);

public sealed record SearchResult(
    string Id,
    string Title,
    string Subtitle,
    SearchResultKind Kind,
    double Score,
    FluxAction Action);

public sealed record RouteDecision(
    RouteKind Kind,
    IReadOnlyList<SearchResult> Results,
    FluxAction? Action = null,
    string? Explanation = null)
{
    public static RouteDecision Search(IReadOnlyList<SearchResult> results) =>
        new(RouteKind.Search, results);

    public static RouteDecision Execute(FluxAction action, string? explanation = null) =>
        new(RouteKind.ImmediateAction, Array.Empty<SearchResult>(), action, explanation);

    public static RouteDecision Ai(string explanation) =>
        new(RouteKind.Ai, Array.Empty<SearchResult>(), null, explanation);
}

public sealed record ApplicationEntry(string Name, string Target, string Source, int UsageCount = 0);

public sealed record ProcessSnapshot(
    int Id,
    string Name,
    string? WindowTitle,
    long WorkingSetBytes,
    double CpuPercent,
    bool Responding,
    bool IsUserApplication,
    bool IsProtected,
    string? ExecutablePath = null,
    string? DisplayName = null)
{
    public string FriendlyName => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;
}

public sealed record SystemSnapshot(
    double CpuPercent,
    ulong TotalMemoryBytes,
    ulong AvailableMemoryBytes,
    IReadOnlyList<DriveSnapshot> Drives,
    string WindowsVersion,
    bool NetworkAvailable,
    string? BatteryStatus);

public sealed record DriveSnapshot(string Name, long TotalBytes, long AvailableBytes);

public sealed record ToolDefinition(
    string Name,
    string Description,
    JsonObject Parameters,
    PermissionLevel Permission);

public sealed record ToolCall(string Id, string Name, JsonElement Arguments);

public sealed record ToolResult(string CallId, string Name, bool Success, string Output);

public sealed record PendingToolCall(ToolCall Call, ToolDefinition Definition, string ConfirmationText);

public sealed record AiTurnRequest(
    string UserRequest,
    string Context,
    IReadOnlyList<ToolDefinition> Tools,
    string? PreviousResponseId = null,
    IReadOnlyList<ToolResult>? ToolResults = null);

public sealed record AiTurnResponse(
    string ResponseId,
    string Text,
    IReadOnlyList<ToolCall> ToolCalls);

public sealed record AiAgentResult(
    string Text,
    IReadOnlyList<PendingToolCall> PendingActions,
    IReadOnlyList<ToolResult> ExecutedTools);
