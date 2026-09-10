using System.Text.Json;

namespace Flux.Core;

public interface IApplicationCatalog
{
    Task RefreshAsync(CancellationToken cancellationToken = default);
    IReadOnlyList<SearchResult> Search(string query, int limit = 8);
}

public interface IFileSearchService
{
    Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int limit = 12, CancellationToken cancellationToken = default);
}

public interface IProcessService
{
    Task<IReadOnlyList<ProcessSnapshot>> ListAsync(CancellationToken cancellationToken = default);
    Task<ToolResult> TerminateAsync(int processId, CancellationToken cancellationToken = default);
    Task<ToolResult> CloseAsync(int processId, CancellationToken cancellationToken = default);
    bool IsProtected(string processName);
}

public interface ISystemInfoService
{
    Task<SystemSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
}

public interface ICommandRouter
{
    Task<RouteDecision> RouteAsync(string query, CancellationToken cancellationToken = default);
}

public interface IFluxTool
{
    ToolDefinition Definition { get; }
    Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default);
}

public interface IToolRegistry
{
    IReadOnlyList<ToolDefinition> Definitions { get; }
    bool TryGet(string name, out IFluxTool? tool);
}

public interface IAiProvider
{
    string Name { get; }
    bool IsConfigured { get; }
    Task<AiTurnResponse> CompleteAsync(AiTurnRequest request, CancellationToken cancellationToken = default);
}

public interface ICommandHistory
{
    IReadOnlyList<string> Items { get; }
    void Add(string command);
    void Clear();
}

public interface ILogService
{
    void Info(string message);
    void Error(string message, Exception? exception = null);
}

