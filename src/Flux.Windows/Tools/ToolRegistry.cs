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
        IFileSearchService files,
        IApplicationCatalog applications)
    {
        IFluxTool[] all =
        [
            new ListProcessesTool(processes),
            new SystemStatsTool(system),
            new TerminateApplicationTool(processes),
            new CloseApplicationTool(processes),
            new CloseApplicationsTool(processes),
            new CloseApplicationsExceptTool(processes),
            new LaunchApplicationTool(applications),
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

    private static JsonObject StringArraySchema(string name, string description) => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            [name] = new JsonObject
            {
                ["type"] = "array",
                ["description"] = description,
                ["items"] = new JsonObject { ["type"] = "string" }
            }
        },
        ["required"] = new JsonArray(name),
        ["additionalProperties"] = false
    };

    private static string ReadRequiredString(ToolCall call, string propertyName)
    {
        if (!call.Arguments.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidOperationException($"Tool argument '{propertyName}' is required.");
        }

        return value.GetString()!.Trim();
    }

    private static IReadOnlyList<string> ReadStringArray(ToolCall call, string propertyName)
    {
        if (!call.Arguments.TryGetProperty(propertyName, out var value))
        {
            throw new InvalidOperationException($"Tool argument '{propertyName}' is required.");
        }

        IEnumerable<string?> values = value.ValueKind switch
        {
            JsonValueKind.Array => value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()),
            JsonValueKind.String => (value.GetString() ?? string.Empty)
                .Replace(" and ", ",", StringComparison.OrdinalIgnoreCase)
                .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            _ => Array.Empty<string>()
        };

        var result = values
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (result.Length == 0 && propertyName != "exclusions")
        {
            throw new InvalidOperationException($"Tool argument '{propertyName}' must contain at least one application name.");
        }

        return result;
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
                display_name = item.FriendlyName,
                window = item.WindowTitle,
                memory_mb = Math.Round(item.WorkingSetBytes / 1024d / 1024d, 1),
                cpu_percent = Math.Round(item.CpuPercent, 1),
                responding = item.Responding,
                user_app = item.IsUserApplication,
                process_category = item.Category.ToString(),
                visible_window = item.HasVisibleWindow,
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

    private sealed class TerminateApplicationTool(IProcessService processes) : IFluxTool
    {
        public ToolDefinition Definition { get; } = new(
            "terminate_application",
            "Force-terminate one visible, non-protected user application after Flux resolves a unique live application identity.",
            Schema(("name", "string", "Application name exactly as the user said it", true)),
            PermissionLevel.Disruptive);

        public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default)
        {
            var name = ReadRequiredString(call, "name");
            var result = await processes.TerminateApplicationAsync(name, cancellationToken);
            return result with { CallId = call.Id, Name = Definition.Name };
        }
    }

    private sealed class CloseApplicationTool(IProcessService processes) : IFluxTool
    {
        public ToolDefinition Definition { get; } = new(
            "close_application",
            "Close one visible user application by unique live identity. The name File Explorer safely closes folder windows without terminating the Windows shell. Flux verifies application exit and force-closes the exact process tree only if the app ignores a clean close.",
            Schema(("name", "string", "Application name exactly as the user said it", true)),
            PermissionLevel.Disruptive);

        public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default)
        {
            var name = ReadRequiredString(call, "name");
            var result = await processes.CloseApplicationAsync(name, cancellationToken);
            return result with { CallId = call.Id, Name = Definition.Name };
        }
    }

    private sealed class CloseApplicationsTool(IProcessService processes) : IFluxTool
    {
        public ToolDefinition Definition { get; } = new(
            "close_applications",
            "Close several specifically named visible user applications. Use this for requests such as 'close Discord and Chrome'. File Explorer safely closes folder windows without terminating the Windows shell.",
            StringArraySchema("names", "Exact application names from the user's request"),
            PermissionLevel.Disruptive);

        public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default)
        {
            var names = ReadStringArray(call, "names");
            var result = await processes.CloseApplicationsAsync(names, cancellationToken);
            return result with { CallId = call.Id, Name = Definition.Name };
        }
    }

    private sealed class CloseApplicationsExceptTool(IProcessService processes) : IFluxTool
    {
        public ToolDefinition Definition { get; } = new(
            "close_applications_except",
            "Close every eligible visible user application except the named applications. Flux discovers live applications, preserves protected Windows processes, and verifies every close.",
            StringArraySchema("exclusions", "Application names that must remain open"),
            PermissionLevel.Disruptive);

        public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default)
        {
            var exclusions = ReadStringArray(call, "exclusions");
            var result = await processes.CloseAllExceptAsync(exclusions, cancellationToken);
            return result with { CallId = call.Id, Name = Definition.Name };
        }
    }

    private sealed class LaunchApplicationTool(IApplicationCatalog applications) : IFluxTool
    {
        public ToolDefinition Definition { get; } = new(
            "launch_application",
            "Open an installed application by name after Flux resolves it against the indexed application catalog.",
            Schema(("name", "string", "Installed application name exactly as the user said it", true)),
            PermissionLevel.Reversible);

        public Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default)
        {
            var name = ReadRequiredString(call, "name");
            var matches = applications.Search(name, 5)
                .Where(result => result.Kind == SearchResultKind.Application)
                .ToArray();
            var exact = matches.Where(result =>
                string.Equals(result.Title, name, StringComparison.OrdinalIgnoreCase)).ToArray();
            var match = exact.Length == 1
                ? exact[0]
                : matches.Length > 0 && matches[0].Score >= 620 &&
                    (matches.Length == 1 || matches[0].Score - matches[1].Score >= 80)
                    ? matches[0]
                    : null;
            if (match is null || !File.Exists(match.Action.Target))
            {
                return Task.FromResult(new ToolResult(call.Id, Definition.Name, false, $"Couldn't open: {name}"));
            }

            try
            {
                var process = Process.Start(new ProcessStartInfo(match.Action.Target) { UseShellExecute = true });
                if (process is null)
                {
                    return Task.FromResult(new ToolResult(call.Id, Definition.Name, false, $"Couldn't open: {match.Title}"));
                }

                process.Dispose();
                return Task.FromResult(new ToolResult(call.Id, Definition.Name, true, $"Opened: {match.Title}"));
            }
            catch
            {
                return Task.FromResult(new ToolResult(call.Id, Definition.Name, false, $"Couldn't open: {match.Title}"));
            }
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
