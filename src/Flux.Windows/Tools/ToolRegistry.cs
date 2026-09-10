using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flux.Core;

namespace Flux.Windows.Tools;

public sealed class ToolRegistry : IToolRegistry
{
    private readonly IReadOnlyDictionary<string, IFluxTool> _tools;

    public ToolRegistry(
        IProcessService processes,
        ISystemInfoService system,
        IFileSearchService files)
    {
        IFluxTool[] all =
        [
            new ListProcessesTool(processes),
            new SystemStatsTool(system),
            new TerminateProcessTool(processes),
            new CloseApplicationTool(processes),
            new LaunchApplicationTool(),
            new OpenPathTool(),
            new SearchFilesTool(files),
            new CreateFolderTool(),
            new PowerShellTool()
        ];

        _tools = all.ToDictionary(tool => tool.Definition.Name, StringComparer.OrdinalIgnoreCase);
        Definitions = all.Select(tool => tool.Definition).ToArray();
    }

    public IReadOnlyList<ToolDefinition> Definitions { get; }

    public bool TryGet(string name, out IFluxTool? tool) => _tools.TryGetValue(name, out tool);

    private static JsonObject Schema(params (string Name, string Type, string Description, bool Required)[] properties)
    {
        var props = new JsonObject();
        var required = new JsonArray();
        foreach (var property in properties)
        {
            props[property.Name] = new JsonObject
            {
                ["type"] = property.Type,
                ["description"] = property.Description
            };
            if (property.Required)
            {
                required.Add(property.Name);
            }
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["required"] = required,
            ["additionalProperties"] = false
        };
    }

    private sealed class ListProcessesTool(IProcessService processes) : IFluxTool
    {
        public ToolDefinition Definition { get; } = new(
            "list_processes",
            "List running processes with CPU, memory, window, responsiveness and protection status.",
            Schema(), PermissionLevel.ReadOnly);

        public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default)
        {
            var items = await processes.ListAsync(cancellationToken);
            var compact = items.OrderByDescending(item => item.WorkingSetBytes).Take(80).Select(item => new
            {
                pid = item.Id,
                name = item.Name,
                window = item.WindowTitle,
                memory_mb = Math.Round(item.WorkingSetBytes / 1024d / 1024d, 1),
                cpu_percent = Math.Round(item.CpuPercent, 1),
                responding = item.Responding,
                user_app = item.IsUserApplication,
                protected_process = item.IsProtected
            });
            return new ToolResult(call.Id, Definition.Name, true, JsonSerializer.Serialize(compact));
        }
    }

    private sealed class SystemStatsTool(ISystemInfoService system) : IFluxTool
    {
        public ToolDefinition Definition { get; } = new(
            "get_system_stats",
            "Inspect CPU, RAM, disks, network, battery and Windows version.",
            Schema(), PermissionLevel.ReadOnly);

        public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default)
        {
            var snapshot = await system.GetSnapshotAsync(cancellationToken);
            return new ToolResult(call.Id, Definition.Name, true, JsonSerializer.Serialize(snapshot));
        }
    }

    private sealed class TerminateProcessTool(IProcessService processes) : IFluxTool
    {
        public ToolDefinition Definition { get; } = new(
            "terminate_process",
            "Force-terminate one non-protected process by PID. Unsaved work may be lost.",
            Schema(("pid", "integer", "Exact process ID from list_processes", true)),
            PermissionLevel.Disruptive);

        public Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default) =>
            processes.TerminateAsync(call.Arguments.GetProperty("pid").GetInt32(), cancellationToken);
    }

    private sealed class CloseApplicationTool(IProcessService processes) : IFluxTool
    {
        public ToolDefinition Definition { get; } = new(
            "close_application",
            "Ask one non-protected desktop application to close cleanly by PID.",
            Schema(("pid", "integer", "Exact process ID from list_processes", true)),
            PermissionLevel.Disruptive);

        public Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default) =>
            processes.CloseAsync(call.Arguments.GetProperty("pid").GetInt32(), cancellationToken);
    }

    private sealed class LaunchApplicationTool : IFluxTool
    {
        public ToolDefinition Definition { get; } = new(
            "launch_application",
            "Launch an application at an exact path already found by Flux.",
            Schema(("path", "string", "Full executable or shortcut path", true)),
            PermissionLevel.Reversible);

        public Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default)
        {
            var path = call.Arguments.GetProperty("path").GetString() ?? string.Empty;
            if (!File.Exists(path))
            {
                return Task.FromResult(new ToolResult(call.Id, Definition.Name, false, "Application path does not exist."));
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return Task.FromResult(new ToolResult(call.Id, Definition.Name, true, $"Launched {Path.GetFileNameWithoutExtension(path)}."));
        }
    }

    private sealed class OpenPathTool : IFluxTool
    {
        public ToolDefinition Definition { get; } = new(
            "open_path",
            "Open an existing file or folder with the Windows shell.",
            Schema(("path", "string", "Full existing file or folder path", true)),
            PermissionLevel.Reversible);

        public Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default)
        {
            var path = call.Arguments.GetProperty("path").GetString() ?? string.Empty;
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return Task.FromResult(new ToolResult(call.Id, Definition.Name, false, "Path does not exist."));
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return Task.FromResult(new ToolResult(call.Id, Definition.Name, true, $"Opened {path}."));
        }
    }

    private sealed class SearchFilesTool(IFileSearchService files) : IFluxTool
    {
        public ToolDefinition Definition { get; } = new(
            "search_files",
            "Search user folders by file or folder name.",
            Schema(("query", "string", "Name or partial name to find", true)),
            PermissionLevel.ReadOnly);

        public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default)
        {
            var query = call.Arguments.GetProperty("query").GetString() ?? string.Empty;
            var results = await files.SearchAsync(query, 20, cancellationToken);
            return new ToolResult(call.Id, Definition.Name, true, JsonSerializer.Serialize(results.Select(result => result.Subtitle)));
        }
    }

    private sealed class CreateFolderTool : IFluxTool
    {
        public ToolDefinition Definition { get; } = new(
            "create_folder",
            "Create a folder at an exact full path.",
            Schema(("path", "string", "Full folder path", true)),
            PermissionLevel.Reversible);

        public Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default)
        {
            var path = call.Arguments.GetProperty("path").GetString() ?? string.Empty;
            if (!Path.IsPathFullyQualified(path))
            {
                return Task.FromResult(new ToolResult(call.Id, Definition.Name, false, "A fully qualified path is required."));
            }

            var userProfile = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(userProfile, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new ToolResult(call.Id, Definition.Name, false, "Flux only allows automatic folder creation inside the current user profile."));
            }

            Directory.CreateDirectory(fullPath);
            return Task.FromResult(new ToolResult(call.Id, Definition.Name, true, $"Created {fullPath}."));
        }
    }

    private sealed class PowerShellTool : IFluxTool
    {
        public ToolDefinition Definition { get; } = new(
            "run_powershell",
            "Run an arbitrary PowerShell command. This is unrestricted and always needs explicit confirmation.",
            Schema(("command", "string", "Exact PowerShell command", true)),
            PermissionLevel.Destructive);

        public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default)
        {
            var command = call.Arguments.GetProperty("command").GetString() ?? string.Empty;
            var startInfo = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(command);
            using var process = Process.Start(startInfo)!;
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var output = (await outputTask) + (await errorTask);
            return new ToolResult(call.Id, Definition.Name, process.ExitCode == 0, output.Trim());
        }
    }
}
