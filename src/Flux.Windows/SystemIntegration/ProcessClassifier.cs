using Flux.Core;

namespace Flux.Windows.SystemIntegration;

public static class ProcessClassifier
{
    private static readonly HashSet<string> WindowsUserApplications = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "control", "mspaint", "mmc", "notepad", "powershell", "regedit", "taskmgr", "write"
    };

    private static readonly HashSet<string> RuntimeHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "conhost", "dotnet", "java", "javaw", "node", "node_repl", "python", "pythonw", "wscript", "cscript"
    };

    private static readonly string[] LauncherTerms =
    [
        "bootstrap", "installer", "launcher", "setup", "squirrel", "update", "updater"
    ];

    private static readonly string[] HelperTerms =
    [
        "agent", "broker", "container", "crash", "daemon", "helper", "host", "renderer", "runtime", "webview"
    ];

    private static readonly string[] ServiceTerms =
    [
        "service", "telemetry"
    ];

    public static ProcessCategory Classify(
        string processName,
        string? executablePath,
        bool isCurrentSession,
        bool hasVisibleWindow,
        bool isProtected,
        IReadOnlySet<string>? visibleExecutablePaths = null,
        bool isServiceSession = false)
    {
        var name = Path.GetFileNameWithoutExtension(processName);
        if (isProtected)
        {
            return ProcessCategory.WindowsProcess;
        }
        if (!isCurrentSession)
        {
            return isServiceSession ? ProcessCategory.Service : ProcessCategory.OtherSession;
        }
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return hasVisibleWindow ? ProcessCategory.UserApplication : ProcessCategory.Unknown;
        }

        var fullPath = SafeFullPath(executablePath);
        if (IsWindowsOwnedPath(fullPath) && !WindowsUserApplications.Contains(name))
        {
            return ProcessCategory.WindowsProcess;
        }
        if (IsProgramFilesWindowsComponent(fullPath))
        {
            return ProcessCategory.WindowsProcess;
        }
        if (IsWindowsComponentPackage(fullPath, name))
        {
            return ProcessCategory.WindowsProcess;
        }
        if (HasRoleTerm(name, LauncherTerms))
        {
            return ProcessCategory.LauncherOrUpdater;
        }
        if (HasRoleTerm(name, HelperTerms))
        {
            return ProcessCategory.HelperProcess;
        }
        if (HasRoleTerm(name, ServiceTerms))
        {
            return ProcessCategory.Service;
        }
        if (hasVisibleWindow)
        {
            return ProcessCategory.UserApplication;
        }
        if (visibleExecutablePaths?.Contains(fullPath) == true)
        {
            return ProcessCategory.HelperProcess;
        }
        if (RuntimeHosts.Contains(name) || IsRuntimePath(fullPath))
        {
            return ProcessCategory.HelperProcess;
        }
        if (IsUserApplicationInstallPath(fullPath) || IsTraditionalApplicationInstallPath(fullPath))
        {
            return ProcessCategory.BackgroundApplication;
        }

        return ProcessCategory.Unknown;
    }

    private static bool HasRoleTerm(string name, IEnumerable<string> terms) => terms.Any(term =>
    {
        var index = name.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            var atStart = index == 0;
            var remainder = name.AsSpan(index + term.Length);
            var atEnd = remainder.Length == 0 || remainder.ToString().All(char.IsDigit);
            if (atStart || atEnd)
            {
                return true;
            }
            index = name.IndexOf(term, index + 1, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    });

    private static string SafeFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }

    private static bool IsWindowsOwnedPath(string path)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return IsWithin(path, windows);
    }

    private static bool IsWindowsComponentPackage(string path, string name)
    {
        if (!path.Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return name.EndsWith("Host", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("Service", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("\\MicrosoftWindows.Client.", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("\\Microsoft.Windows.", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProgramFilesWindowsComponent(string path)
    {
        var components = new[]
        {
            "Windows Defender", "Windows Mail", "Windows Media Player", "Windows Multimedia Platform",
            "Windows NT", "Windows Photo Viewer", "Windows Portable Devices", "Windows Security"
        };
        var programFilesRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };
        return programFilesRoots.Where(root => !string.IsNullOrWhiteSpace(root)).Any(root =>
            components.Any(component => IsWithin(path, Path.Combine(root, component))));
    }

    private static bool IsRuntimePath(string path) =>
        path.Contains("\\runtimes\\", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("\\sdk\\", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("\\.dotnet\\", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("\\.sandbox-bin\\", StringComparison.OrdinalIgnoreCase);

    private static bool IsUserApplicationInstallPath(string path)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!IsWithin(path, local) && !IsWithin(path, roaming))
        {
            return false;
        }

        return !path.Contains("\\Temp\\", StringComparison.OrdinalIgnoreCase) &&
            !path.Contains("\\Microsoft\\Windows\\", StringComparison.OrdinalIgnoreCase) &&
            !IsRuntimePath(path);
    }

    private static bool IsTraditionalApplicationInstallPath(string path)
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };
        if (!roots.Where(root => !string.IsNullOrWhiteSpace(root)).Any(root => IsWithin(path, root)))
        {
            return false;
        }

        return !path.Contains("\\Common Files\\", StringComparison.OrdinalIgnoreCase) &&
            !IsRuntimePath(path);
    }

    private static bool IsWithin(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }
}
