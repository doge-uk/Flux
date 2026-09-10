using Microsoft.Win32;
using Flux.Core;
using Flux.Core.Search;
using Flux.Windows.Configuration;
using Flux.Windows.Infrastructure;

namespace Flux.Windows.Search;

public sealed class ApplicationCatalog(AppSettings settings, ILogService log) : IApplicationCatalog
{
    private readonly object _gate = new();
    private IReadOnlyList<ApplicationEntry> _entries = Array.Empty<ApplicationEntry>();

    public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var entries = new Dictionary<string, ApplicationEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in StartMenuRoots())
        {
            AddShortcuts(root, entries, cancellationToken);
        }

        AddRegisteredApplications(Registry.CurrentUser, entries);
        AddRegisteredApplications(Registry.LocalMachine, entries);

        foreach (var known in KnownExecutables())
        {
            var path = ResolveOnPath(known.Executable);
            if (path is not null)
            {
                Add(entries, new ApplicationEntry(known.Name, path, "PATH"));
            }
        }

        lock (_gate)
        {
            _entries = entries.Values.OrderBy(entry => entry.Name).ToArray();
        }

        log.Info($"Application catalog refreshed with {_entries.Count} entries.");
    }, cancellationToken);

    public IReadOnlyList<SearchResult> Search(string query, int limit = 8)
    {
        IReadOnlyList<ApplicationEntry> snapshot;
        lock (_gate)
        {
            snapshot = _entries;
        }

        return snapshot
            .Select(entry =>
            {
                var score = FuzzyMatcher.Score(query, entry.Name);
                settings.ApplicationUsage.TryGetValue(entry.Target, out var usage);
                score += Math.Min(120, usage * 8);
                return (entry, score);
            })
            .Where(item => item.score > 0)
            .OrderByDescending(item => item.score)
            .ThenBy(item => item.entry.Name)
            .Take(limit)
            .Select(item => new SearchResult(
                $"app:{item.entry.Target}",
                item.entry.Name,
                item.entry.Target,
                SearchResultKind.Application,
                item.score,
                new FluxAction(FluxActionType.Launch, item.entry.Target, PermissionLevel.Reversible, $"Launch {item.entry.Name}")))
            .ToArray();
    }

    private static IEnumerable<string> StartMenuRoots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
    }

    private static void AddShortcuts(
        string root,
        IDictionary<string, ApplicationEntry> entries,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        try
        {
            foreach (var path in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileNameWithoutExtension(path);
                if (!name.Contains("uninstall", StringComparison.OrdinalIgnoreCase))
                {
                    Add(entries, new ApplicationEntry(name, path, "Start Menu"));
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void AddRegisteredApplications(RegistryKey hive, IDictionary<string, ApplicationEntry> entries)
    {
        const string appPaths = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
        try
        {
            using var root = hive.OpenSubKey(appPaths);
            if (root is null)
            {
                return;
            }

            foreach (var subKeyName in root.GetSubKeyNames())
            {
                using var subKey = root.OpenSubKey(subKeyName);
                var path = subKey?.GetValue(null) as string;
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path.Trim('"')))
                {
                    var name = Path.GetFileNameWithoutExtension(subKeyName);
                    Add(entries, new ApplicationEntry(name, path!.Trim('"'), "Registered application"));
                }
            }
        }
        catch
        {
        }
    }

    private static IEnumerable<(string Name, string Executable)> KnownExecutables()
    {
        yield return ("Windows Terminal", "wt.exe");
        yield return ("PowerShell", "pwsh.exe");
        yield return ("Command Prompt", "cmd.exe");
        yield return ("Notepad", "notepad.exe");
        yield return ("File Explorer", "explorer.exe");
    }

    private static string? ResolveOnPath(string executable)
    {
        var candidate = Path.Combine(Environment.SystemDirectory, executable);
        if (File.Exists(candidate))
        {
            return candidate;
        }

        foreach (var path in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';'))
        {
            try
            {
                candidate = Path.Combine(path.Trim(), executable);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private static void Add(IDictionary<string, ApplicationEntry> entries, ApplicationEntry entry)
    {
        var key = $"{entry.Name}|{entry.Target}";
        entries.TryAdd(key, entry);
    }
}
