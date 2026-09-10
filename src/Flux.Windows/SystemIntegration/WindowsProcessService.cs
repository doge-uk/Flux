using System.Diagnostics;
using Flux.Core;

namespace Flux.Windows.SystemIntegration;

public sealed class WindowsProcessService : IProcessService
{
    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "smss", "csrss", "wininit", "services", "lsass",
        "winlogon", "svchost", "dwm", "fontdrvhost", "Memory Compression", "sihost",
        "taskhostw", "ShellExperienceHost", "StartMenuExperienceHost", "SecurityHealthService",
        "MsMpEng", "explorer", "Flux", "audiodg", "conhost", "dllhost", "LogonUI",
        "WmiPrvSE", "SearchHost", "SearchIndexer", "RuntimeBroker", "spoolsv", "ctfmon"
    };

    public bool IsProtected(string processName) =>
        ProtectedNames.Contains(Path.GetFileNameWithoutExtension(processName));

    public async Task<IReadOnlyList<ProcessSnapshot>> ListAsync(CancellationToken cancellationToken = default)
    {
        var first = CaptureCpuTimes();
        var started = Stopwatch.GetTimestamp();
        await Task.Delay(250, cancellationToken);
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var processorCount = Math.Max(1, Environment.ProcessorCount);
        var results = new List<ProcessSnapshot>();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var name = process.ProcessName;
                    var cpu = 0d;
                    if (first.TryGetValue(process.Id, out var previous))
                    {
                        var delta = (process.TotalProcessorTime - previous).TotalMilliseconds;
                        cpu = Math.Clamp(delta / elapsed / processorCount * 100d, 0d, 100d);
                    }

                    var title = process.MainWindowTitle;
                    results.Add(new ProcessSnapshot(
                        process.Id,
                        name,
                        string.IsNullOrWhiteSpace(title) ? null : title,
                        process.WorkingSet64,
                        cpu,
                        process.Responding,
                        process.MainWindowHandle != IntPtr.Zero,
                        IsProtected(name)));
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            }
        }

        return results;
    }

    public Task<ToolResult> TerminateAsync(int processId, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (IsProtected(process.ProcessName) || process.SessionId == 0)
            {
                return new ToolResult(processId.ToString(), "terminate_process", false, $"Refused: {process.ProcessName} is protected.");
            }

            var name = process.ProcessName;
            process.Kill(true);
            process.WaitForExit(5000);
            return new ToolResult(processId.ToString(), "terminate_process", true, $"Terminated {name} (PID {processId}).");
        }
        catch (Exception exception)
        {
            return new ToolResult(processId.ToString(), "terminate_process", false, exception.Message);
        }
    }, cancellationToken);

    public Task<ToolResult> CloseAsync(int processId, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (IsProtected(process.ProcessName) || process.SessionId == 0)
            {
                return new ToolResult(processId.ToString(), "close_application", false, $"Refused: {process.ProcessName} is protected.");
            }

            var name = process.ProcessName;
            if (process.MainWindowHandle == IntPtr.Zero || !process.CloseMainWindow())
            {
                return new ToolResult(processId.ToString(), "close_application", false, $"{name} has no closable window.");
            }

            return new ToolResult(processId.ToString(), "close_application", true, $"Asked {name} (PID {processId}) to close.");
        }
        catch (Exception exception)
        {
            return new ToolResult(processId.ToString(), "close_application", false, exception.Message);
        }
    }, cancellationToken);

    private static Dictionary<int, TimeSpan> CaptureCpuTimes()
    {
        var result = new Dictionary<int, TimeSpan>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
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
}
