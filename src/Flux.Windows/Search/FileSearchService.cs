using Flux.Core;
using Flux.Core.Search;

namespace Flux.Windows.Search;

public sealed class FileSearchService : IFileSearchService
{
    private static readonly string[] IgnoredDirectories =
    [
        "$Recycle.Bin", ".git", ".svn", "node_modules", "bin", "obj", "AppData"
    ];

    public Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query,
        int limit = 12,
        CancellationToken cancellationToken = default) => Task.Run<IReadOnlyList<SearchResult>>(() =>
    {
        if (query.Length < 2)
        {
            return Array.Empty<SearchResult>();
        }

        var results = new PriorityQueue<SearchResult, double>();
        foreach (var root in SearchRoots().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            SearchRoot(root, query, limit, results, cancellationToken);
        }

        return results.UnorderedItems
            .Select(item => item.Element)
            .OrderByDescending(item => item.Score)
            .Take(limit)
            .ToArray();
    }, cancellationToken);

    private static void SearchRoot(
        string root,
        string query,
        int limit,
        PriorityQueue<SearchResult, double> results,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
        var visited = 0;

        while (pending.Count > 0 && visited < 50_000)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, depth) = pending.Pop();

            try
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    visited++;
                    var isDirectory = Directory.Exists(path);
                    var name = Path.GetFileName(path);
                    var score = FuzzyMatcher.Score(query, name);

                    if (score > 0)
                    {
                        var kind = isDirectory ? SearchResultKind.Folder : SearchResultKind.File;
                        var result = new SearchResult(
                            $"path:{path}",
                            name,
                            path,
                            kind,
                            score - (depth * 3),
                            new FluxAction(FluxActionType.OpenPath, path, PermissionLevel.Reversible, $"Open {name}"));
                        results.Enqueue(result, result.Score);
                        if (results.Count > limit * 3)
                        {
                            results.Dequeue();
                        }
                    }

                    if (isDirectory && depth < 6 && !IgnoredDirectories.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        pending.Push((path, depth + 1));
                    }
                }
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PathTooLongException)
            {
            }
        }
    }

    private static IEnumerable<string> SearchRoots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        yield return Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
    }
}

