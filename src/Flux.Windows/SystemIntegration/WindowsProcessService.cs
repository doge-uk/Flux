using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Flux.Core;

namespace Flux.Windows.SystemIntegration;

public sealed class WindowsProcessService : IProcessService
{
    private const uint WmClose = 0x0010;
    private static readonly TimeSpan GracefulCloseTimeout = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan TerminationTimeout = TimeSpan.FromSeconds(5);
    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "smss", "csrss", "wininit", "services", "lsass",
        "winlogon", "svchost", "dwm", "fontdrvhost", "Memory Compression", "sihost",
        "taskhostw", "ShellExperienceHost", "StartMenuExperienceHost", "SecurityHealthService",
        "MsMpEng", "explorer", "Flux", "audiodg", "conhost", "dllhost", "LogonUI",
        "WmiPrvSE", "SearchHost", "SearchIndexer", "RuntimeBroker", "spoolsv", "ctfmon"
    };

    private readonly HashSet<int>? _allowedProcessIds;
    private readonly int _currentSessionId;

    public WindowsProcessService(IEnumerable<int>? allowedProcessIds = null)
    {
        _allowedProcessIds = allowedProcessIds?.ToHashSet();
        using var current = Process.GetCurrentProcess();
        _currentSessionId = current.SessionId;
    }

    public bool IsProtected(string processName) =>
        ProtectedNames.Contains(Path.GetFileNameWithoutExtension(processName));

    public async Task<IReadOnlyList<ProcessSnapshot>> ListAsync(CancellationToken cancellationToken = default)
    {
        var first = CaptureCpuTimes();
        var started = Stopwatch.GetTimestamp();
        await Task.Delay(250, cancellationToken);
        var elapsed = Math.Max(1, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return CaptureSnapshots(first, elapsed, cancellationToken);
    }

    public async Task<ToolResult> TerminateAsync(int processId, CancellationToken cancellationToken = default)
    {
        Process? process = null;
        try
        {
            if (!TryOpenEligibleProcess(processId, out process, out var failureName))
            {
                return Failed(processId, "terminate_application", "terminate", failureName);
            }

            var targetProcess = process!;
            using (targetProcess)
            {
                var name = GetDisplayName(targetProcess);
                targetProcess.Kill(entireProcessTree: true);
                var exited = await WaitForExitAsync(targetProcess, TerminationTimeout, cancellationToken);
                return exited && IsProcessGone(processId)
                    ? Succeeded(processId, "terminate_application", "Terminated", name)
                    : Failed(processId, "terminate_application", "terminate", name);
            }
        }
        catch (OperationCanceledException)
        {
            process?.Dispose();
            throw;
        }
        catch
        {
            process?.Dispose();
            return Failed(processId, "terminate_application", "terminate", ProcessNameOrId(processId));
        }
    }

    public async Task<ToolResult> CloseAsync(int processId, CancellationToken cancellationToken = default)
    {
        Process? process = null;
        try
        {
            if (!TryOpenEligibleProcess(processId, out process, out var failureName))
            {
                return Failed(processId, "close_application", "close", failureName);
            }

            var targetProcess = process!;
            using (targetProcess)
            {
                var name = GetDisplayName(targetProcess);
                var closeRequested = targetProcess.CloseMainWindow();
                if (closeRequested && await WaitForExitAsync(targetProcess, GracefulCloseTimeout, cancellationToken) && IsProcessGone(processId))
                {
                    return Succeeded(processId, "close_application", "Closed", name);
                }

                if (!targetProcess.HasExited)
                {
                    targetProcess.Kill(entireProcessTree: true);
                }

                var exited = targetProcess.HasExited || await WaitForExitAsync(targetProcess, TerminationTimeout, cancellationToken);
                return exited && IsProcessGone(processId)
                    ? Succeeded(processId, "close_application", "Closed", name)
                    : Failed(processId, "close_application", "close", name);
            }
        }
        catch (OperationCanceledException)
        {
            process?.Dispose();
            throw;
        }
        catch
        {
            process?.Dispose();
            return Failed(processId, "close_application", "close", ProcessNameOrId(processId));
        }
    }

    public async Task<ToolResult> TerminateApplicationAsync(
        string applicationName,
        CancellationToken cancellationToken = default)
    {
        var target = ResolveGroups([applicationName], out _).SingleOrDefault();
        if (target is null)
        {
            return Failed(applicationName, "terminate_application", "terminate", applicationName.Trim());
        }

        var results = new List<ToolResult>();
        foreach (var process in target.Processes)
        {
            results.Add(await TerminateAsync(process.Id, cancellationToken));
        }

        var remains = CaptureApplicationGroups(cancellationToken)
            .Any(group => group.IdentityKey.Equals(target.IdentityKey, StringComparison.OrdinalIgnoreCase));
        return results.All(result => result.Success) && !remains
            ? Succeeded(applicationName, "terminate_application", "Terminated", target.DisplayName)
            : Failed(applicationName, "terminate_application", "terminate", target.DisplayName);
    }

    public async Task<ToolResult> CloseApplicationAsync(
        string applicationName,
        CancellationToken cancellationToken = default)
    {
        var result = await CloseApplicationsAsync([applicationName], cancellationToken);
        return result with { Name = "close_application", CallId = applicationName };
    }

    public async Task<ToolResult> CloseApplicationsAsync(
        IReadOnlyCollection<string> applicationNames,
        CancellationToken cancellationToken = default)
    {
        var explorerRequested = applicationNames.Any(IsExplorerAlias);
        var groups = ResolveGroups(applicationNames.Where(name => !IsExplorerAlias(name)), out var unresolved);
        var results = new List<ToolResult>();
        if (groups.Count > 0 || unresolved.Count > 0)
        {
            results.Add(await CloseGroupsAsync("close_applications", groups, unresolved, cancellationToken));
        }
        if (explorerRequested)
        {
            results.Add(await CloseExplorerWindowsAsync("close_applications", cancellationToken));
        }

        return CombineCloseResults("close_applications", results);
    }

    public async Task<ToolResult> CloseAllExceptAsync(
        IReadOnlyCollection<string> exclusions,
        CancellationToken cancellationToken = default)
    {
        var groups = CaptureApplicationGroups(cancellationToken);
        var normalizedExclusions = exclusions
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var unresolved = normalizedExclusions
            .Where(exclusion => !IsExplorerAlias(exclusion) && !groups.Any(group => IsExclusionMatch(exclusion, group)))
            .ToArray();
        if (unresolved.Length > 0)
        {
            return new ToolResult(
                "close_applications_except",
                "close_applications_except",
                false,
                $"Couldn't safely identify: {string.Join(", ", unresolved)}{Environment.NewLine}Nothing was closed.");
        }
        var targets = groups
            .Where(group => !normalizedExclusions.Any(exclusion => IsExclusionMatch(exclusion, group)))
            .ToArray();
        var results = new List<ToolResult>
        {
            await CloseGroupsAsync("close_applications_except", targets, [], cancellationToken)
        };
        if (!normalizedExclusions.Any(IsExplorerAlias))
        {
            results.Add(await CloseExplorerWindowsAsync("close_applications_except", cancellationToken));
        }

        return CombineCloseResults("close_applications_except", results);
    }

    private async Task<ToolResult> CloseExplorerWindowsAsync(
        string toolName,
        CancellationToken cancellationToken)
    {
        var windows = CaptureExplorerWindows();
        if (windows.Count == 0)
        {
            return new ToolResult(toolName, toolName, true, "Nothing to close.");
        }

        foreach (var window in windows)
        {
            _ = PostMessage(window, WmClose, IntPtr.Zero, IntPtr.Zero);
        }

        var deadline = Stopwatch.GetTimestamp() + (long)(GracefulCloseTimeout.TotalSeconds * Stopwatch.Frequency);
        IReadOnlyList<IntPtr> remaining;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            remaining = windows.Where(IsWindow).ToArray();
            if (remaining.Count == 0 || Stopwatch.GetTimestamp() >= deadline)
            {
                break;
            }
            await Task.Delay(50, cancellationToken);
        }
        while (true);

        var closedCount = windows.Count - remaining.Count;
        var lines = new List<string>();
        if (closedCount == 1)
        {
            lines.Add("Closed: File Explorer");
        }
        else if (closedCount > 1)
        {
            lines.Add($"Closed {closedCount} File Explorer windows");
        }
        if (remaining.Count > 0)
        {
            lines.Add($"Couldn't close {remaining.Count} File Explorer window{(remaining.Count == 1 ? string.Empty : "s")}");
        }

        return new ToolResult(toolName, toolName, remaining.Count == 0, string.Join(Environment.NewLine, lines));
    }

    private IReadOnlyList<IntPtr> CaptureExplorerWindows()
    {
        var windows = new List<IntPtr>();
        _ = EnumWindows((window, parameter) =>
        {
            var className = new StringBuilder(128);
            if (GetClassName(window, className, className.Capacity) == 0 ||
                className.ToString() is not ("CabinetWClass" or "ExploreWClass"))
            {
                return true;
            }

            _ = GetWindowThreadProcessId(window, out var processId);
            try
            {
                using var process = Process.GetProcessById((int)processId);
                if (process.SessionId == _currentSessionId &&
                    string.Equals(process.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase) &&
                    (_allowedProcessIds is null || _allowedProcessIds.Contains(process.Id)))
                {
                    windows.Add(window);
                }
            }
            catch
            {
            }

            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private static ToolResult CombineCloseResults(string toolName, IReadOnlyCollection<ToolResult> results)
    {
        if (results.Count == 0)
        {
            return new ToolResult(toolName, toolName, true, "Nothing to close.");
        }

        var output = results
            .Select(result => result.Output)
            .Where(text => !string.IsNullOrWhiteSpace(text) && !text.Equals("Nothing to close.", StringComparison.Ordinal))
            .ToArray();
        return new ToolResult(
            toolName,
            toolName,
            results.All(result => result.Success),
            output.Length == 0 ? "Nothing to close." : string.Join(Environment.NewLine, output));
    }

    private async Task<ToolResult> CloseGroupsAsync(
        string toolName,
        IReadOnlyCollection<ApplicationGroup> groups,
        IReadOnlyCollection<string> initiallyFailed,
        CancellationToken cancellationToken)
    {
        if (groups.Count == 0 && initiallyFailed.Count == 0)
        {
            return new ToolResult(toolName, toolName, true, "Nothing to close.");
        }

        var attempts = await Task.WhenAll(groups.Select(async group =>
        {
            var results = new List<ToolResult>();
            foreach (var process in group.Processes)
            {
                results.Add(await CloseAsync(process.Id, cancellationToken));
            }
            return (Group: group, AllSucceeded: results.All(result => result.Success));
        }));

        var remainingKeys = CaptureApplicationGroups(cancellationToken)
            .Select(group => group.IdentityKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var closed = attempts
            .Where(item => item.AllSucceeded && !remainingKeys.Contains(item.Group.IdentityKey))
            .Select(item => item.Group.DisplayName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var failed = initiallyFailed
            .Concat(attempts
                .Where(item => !item.AllSucceeded || remainingKeys.Contains(item.Group.IdentityKey))
                .Select(item => item.Group.DisplayName))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        return new ToolResult(
            toolName,
            toolName,
            failed.Length == 0,
            FormatSummary(closed, failed));
    }

    private IReadOnlyList<ApplicationGroup> ResolveGroups(
        IEnumerable<string> requestedNames,
        out IReadOnlyList<string> unresolved)
    {
        var available = CaptureApplicationGroups(CancellationToken.None);
        var resolved = new Dictionary<string, ApplicationGroup>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();
        foreach (var requestedName in requestedNames.Where(item => !string.IsNullOrWhiteSpace(item)))
        {
            var matches = available.Where(group => IsStrongNameMatch(requestedName, group)).ToArray();
            if (matches.Length == 1)
            {
                resolved.TryAdd(matches[0].IdentityKey, matches[0]);
            }
            else
            {
                failures.Add(requestedName.Trim());
            }
        }

        unresolved = failures;
        return resolved.Values.ToArray();
    }

    private bool TryOpenEligibleProcess(int processId, out Process? process, out string failureName)
    {
        process = null;
        failureName = ProcessNameOrId(processId);
        if (_allowedProcessIds is not null && !_allowedProcessIds.Contains(processId))
        {
            return false;
        }

        try
        {
            process = Process.GetProcessById(processId);
            failureName = GetDisplayName(process);
            if (process.Id == Environment.ProcessId || process.HasExited || process.SessionId != _currentSessionId ||
                process.SessionId == 0 || IsProtected(process.ProcessName) || process.MainWindowHandle == IntPtr.Zero)
            {
                process.Dispose();
                process = null;
                return false;
            }

            return true;
        }
        catch
        {
            process?.Dispose();
            process = null;
            return false;
        }
    }

    private IReadOnlyList<ProcessSnapshot> CaptureSnapshots(
        IReadOnlyDictionary<int, TimeSpan>? previousCpuTimes,
        double elapsedMilliseconds,
        CancellationToken cancellationToken)
    {
        var processorCount = Math.Max(1, Environment.ProcessorCount);
        var results = new List<ProcessSnapshot>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_allowedProcessIds is not null && !_allowedProcessIds.Contains(process.Id))
                {
                    continue;
                }

                try
                {
                    var name = process.ProcessName;
                    var protectedProcess = IsProtected(name) || process.Id == Environment.ProcessId;
                    var inCurrentSession = process.SessionId == _currentSessionId && process.SessionId != 0;
                    var hasWindow = process.MainWindowHandle != IntPtr.Zero;
                    var cpu = 0d;
                    if (previousCpuTimes is not null && previousCpuTimes.TryGetValue(process.Id, out var previous))
                    {
                        var delta = (process.TotalProcessorTime - previous).TotalMilliseconds;
                        cpu = Math.Clamp(delta / elapsedMilliseconds / processorCount * 100d, 0d, 100d);
                    }

                    var title = process.MainWindowTitle;
                    var path = TryGetExecutablePath(process);
                    results.Add(new ProcessSnapshot(
                        process.Id,
                        name,
                        string.IsNullOrWhiteSpace(title) ? null : title,
                        process.WorkingSet64,
                        cpu,
                        process.Responding,
                        hasWindow && inCurrentSession && !protectedProcess,
                        protectedProcess,
                        path,
                        GetDisplayName(process, path)));
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            }
        }

        return results;
    }

    private IReadOnlyList<ApplicationGroup> CaptureApplicationGroups(CancellationToken cancellationToken) =>
        CaptureSnapshots(null, 1, cancellationToken)
            .Where(process => process.IsUserApplication && !process.IsProtected)
            .GroupBy(
                process => string.IsNullOrWhiteSpace(process.ExecutablePath)
                    ? $"name:{process.Name}"
                    : $"path:{process.ExecutablePath}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => new ApplicationGroup(
                group.Key,
                ChooseDisplayName(group),
                group.OrderBy(process => process.Id).ToArray()))
            .ToArray();

    private Dictionary<int, TimeSpan> CaptureCpuTimes()
    {
        var result = new Dictionary<int, TimeSpan>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (_allowedProcessIds is not null && !_allowedProcessIds.Contains(process.Id))
                {
                    continue;
                }

                try
                {
                    result[process.Id] = process.TotalProcessorTime;
                }
                catch
                {
                }
            }
        }

        return result;
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (process.HasExited)
        {
            return true;
        }

        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCancellation.Token);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return process.HasExited;
        }
    }

    private static bool IsProcessGone(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static string? TryGetExecutablePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    private static string GetDisplayName(Process process, string? executablePath = null)
    {
        try
        {
            executablePath ??= TryGetExecutablePath(process);
            var description = executablePath is null ? null : FileVersionInfo.GetVersionInfo(executablePath).FileDescription;
            return string.IsNullOrWhiteSpace(description) ? process.ProcessName : description.Trim();
        }
        catch
        {
            return process.ProcessName;
        }
    }

    private static string ProcessNameOrId(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return GetDisplayName(process);
        }
        catch
        {
            return $"PID {processId}";
        }
    }

    private static string ChooseDisplayName(IEnumerable<ProcessSnapshot> processes) =>
        processes.Select(process => process.FriendlyName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .OrderBy(name => name.Length)
            .FirstOrDefault() ?? processes.First().Name;

    private static bool IsStrongNameMatch(string requestedName, ApplicationGroup group)
    {
        var requestedWords = NormalizeWords(requestedName);
        var requestedCompact = Compact(requestedWords);
        if (requestedCompact.Length < 2)
        {
            return false;
        }

        return group.Processes
            .SelectMany(process => new[] { process.Name, process.FriendlyName })
            .Append(group.DisplayName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Any(alias =>
            {
                var aliasWords = NormalizeWords(alias);
                return Compact(aliasWords).Equals(requestedCompact, StringComparison.OrdinalIgnoreCase) ||
                    aliasWords.Equals(requestedWords, StringComparison.OrdinalIgnoreCase) ||
                    aliasWords.StartsWith(requestedWords + " ", StringComparison.OrdinalIgnoreCase) ||
                    aliasWords.EndsWith(" " + requestedWords, StringComparison.OrdinalIgnoreCase);
            });
    }

    private static bool IsExclusionMatch(string requestedName, ApplicationGroup group)
    {
        if (IsStrongNameMatch(requestedName, group))
        {
            return true;
        }

        var requested = Compact(NormalizeWords(requestedName));
        return requested == "chatgpt" && group.Processes.Any(process =>
            string.Equals(Compact(NormalizeWords(process.Name)), "codex", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Compact(NormalizeWords(process.FriendlyName)), "codex", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeWords(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousWasSpace = true;
        foreach (var character in Path.GetFileNameWithoutExtension(value.Trim()))
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
                previousWasSpace = false;
            }
            else if (!previousWasSpace)
            {
                builder.Append(' ');
                previousWasSpace = true;
            }
        }

        return builder.ToString().Trim();
    }

    private static string Compact(string value) => value.Replace(" ", string.Empty, StringComparison.Ordinal);

    private static bool IsExplorerAlias(string value) =>
        Compact(NormalizeWords(value)) is
            "explorer" or "fileexplorer" or "windowsexplorer" or
            "explorerwindows" or "fileexplorerwindows" or "folderwindows";

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int maximumCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    private static ToolResult Succeeded(object callId, string toolName, string verb, string name) =>
        new(callId.ToString() ?? toolName, toolName, true, $"{verb}: {name}");

    private static ToolResult Failed(object callId, string toolName, string verb, string name) =>
        new(callId.ToString() ?? toolName, toolName, false, $"Couldn't {verb}: {name}");

    private static string FormatSummary(IReadOnlyList<string> succeeded, IReadOnlyList<string> failed)
    {
        var lines = new List<string>();
        if (succeeded.Count == 1)
        {
            lines.Add($"Closed: {succeeded[0]}");
        }
        else if (succeeded.Count > 1)
        {
            lines.Add($"Closed {succeeded.Count} apps:");
            lines.AddRange(succeeded);
        }

        if (failed.Count == 1)
        {
            lines.Add($"Couldn't close: {failed[0]}");
        }
        else if (failed.Count > 1)
        {
            lines.Add($"Couldn't close {failed.Count} apps:");
            lines.AddRange(failed);
        }

        return lines.Count == 0 ? "Nothing to close." : string.Join(Environment.NewLine, lines);
    }

    private sealed record ApplicationGroup(string IdentityKey, string DisplayName, IReadOnlyList<ProcessSnapshot> Processes);
}
