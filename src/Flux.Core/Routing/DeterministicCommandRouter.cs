using System.Text.RegularExpressions;

namespace Flux.Core.Routing;

public sealed partial class DeterministicCommandRouter(
    IApplicationCatalog applications,
    IFileSearchService files) : ICommandRouter
{
    private static readonly Dictionary<string, Environment.SpecialFolder> KnownFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["desktop"] = Environment.SpecialFolder.DesktopDirectory,
        ["my desktop"] = Environment.SpecialFolder.DesktopDirectory,
        ["documents"] = Environment.SpecialFolder.MyDocuments,
        ["my documents"] = Environment.SpecialFolder.MyDocuments,
        ["music"] = Environment.SpecialFolder.MyMusic,
        ["my music"] = Environment.SpecialFolder.MyMusic,
        ["pictures"] = Environment.SpecialFolder.MyPictures,
        ["my pictures"] = Environment.SpecialFolder.MyPictures,
        ["videos"] = Environment.SpecialFolder.MyVideos,
        ["my videos"] = Environment.SpecialFolder.MyVideos,
        ["downloads"] = (Environment.SpecialFolder)(-1),
        ["my downloads"] = (Environment.SpecialFolder)(-1)
    };

    public async Task<RouteDecision> RouteAsync(string query, CancellationToken cancellationToken = default)
    {
        query = query.Trim();
        if (query.Length == 0)
        {
            return RouteDecision.Search(Array.Empty<SearchResult>());
        }

        if (TryKnownFolder(query, out var folderResult))
        {
            return RouteDecision.Search([folderResult]);
        }

        var closeAllExcept = CloseAllExceptForwardRegex().Match(query);
        if (!closeAllExcept.Success)
        {
            closeAllExcept = CloseAllExceptReverseRegex().Match(query);
        }
        if (closeAllExcept.Success)
        {
            var exclusions = SplitApplicationNames(closeAllExcept.Groups["names"].Value);
            if (exclusions.Length > 0)
            {
                return RouteDecision.Execute(
                    new FluxAction(
                        FluxActionType.CloseAllExcept,
                        string.Join('\n', exclusions),
                        PermissionLevel.Disruptive,
                        $"Close everything except {string.Join(", ", exclusions)}"),
                    $"Keep open: {string.Join(", ", exclusions)}");
            }
        }

        var createFolder = CreateFolderRegex().Match(query);
        if (createFolder.Success)
        {
            var name = createFolder.Groups["name"].Value.Trim(' ', '\'', '"');
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var target = Path.Combine(desktop, name);
            return RouteDecision.Execute(
                new FluxAction(FluxActionType.CreateFolder, target, PermissionLevel.Reversible, $"Create folder “{name}”"),
                $"Create {target}");
        }

        if (ComplexActionRegex().IsMatch(query))
        {
            return RouteDecision.Ai("This request needs interpretation or a multi-step plan.");
        }

        var processAction = ProcessActionRegex().Match(query);
        if (processAction.Success)
        {
            var verb = processAction.Groups["verb"].Value.ToLowerInvariant();
            var name = processAction.Groups["name"].Value.Trim();
            if (CompoundProcessTargetsRegex().IsMatch(name))
            {
                return RouteDecision.Ai("This request targets multiple applications.");
            }

            var type = verb == "restart" ? FluxActionType.RestartProcess : FluxActionType.TerminateProcess;
            return RouteDecision.Execute(
                new FluxAction(type, name, PermissionLevel.Disruptive, $"{verb} {name}",
                    new Dictionary<string, string> { ["verb"] = verb }),
                "Process actions require confirmation.");
        }

        if (RamRegex().IsMatch(query) || CpuRegex().IsMatch(query))
        {
            return RouteDecision.Execute(new FluxAction(
                FluxActionType.ShowProcesses,
                RamRegex().IsMatch(query) ? "memory" : "cpu",
                PermissionLevel.ReadOnly,
                "Inspect running processes"));
        }

        if (SystemStatsRegex().IsMatch(query))
        {
            return RouteDecision.Execute(new FluxAction(
                FluxActionType.ShowSystemStats,
                Permission: PermissionLevel.ReadOnly,
                DisplayName: "Inspect this PC"));
        }

        var launchQuery = StripLaunchVerb(query);
        var appResults = applications.Search(launchQuery, 8);
        var fileResults = await files.SearchAsync(launchQuery, 8, cancellationToken);
        var combined = appResults.Concat(fileResults)
            .OrderByDescending(result => result.Score)
            .Take(10)
            .ToArray();

        var looksLikeQuestion = QuestionRegex().IsMatch(query);
        var looksLikeComplexAction = ComplexActionRegex().IsMatch(query);
        var strongestScore = combined.FirstOrDefault()?.Score ?? 0;
        var wordCount = launchQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var simpleSearch = wordCount <= 3 && !looksLikeQuestion && !looksLikeComplexAction;
        var minimumSearchScore = simpleSearch ? 330 : 620;

        if (combined.Length > 0 && strongestScore >= minimumSearchScore)
        {
            return RouteDecision.Search(combined);
        }

        return RouteDecision.Ai("This request needs interpretation or a multi-step plan.");
    }

    private static string StripLaunchVerb(string query) =>
        LaunchVerbRegex().Replace(query, string.Empty).Trim();

    private static bool TryKnownFolder(string query, out SearchResult result)
    {
        var normalized = OpenFolderVerbRegex().Replace(query, string.Empty).Trim();
        if (!KnownFolders.TryGetValue(normalized, out var specialFolder))
        {
            result = null!;
            return false;
        }

        var path = specialFolder == (Environment.SpecialFolder)(-1)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
            : Environment.GetFolderPath(specialFolder);

        result = new SearchResult(
            $"folder:{path}",
            CultureTitle(normalized),
            path,
            SearchResultKind.Folder,
            1000,
            new FluxAction(FluxActionType.OpenPath, path, PermissionLevel.Reversible, $"Open {normalized}"));
        return true;
    }

    private static string CultureTitle(string value) =>
        System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(value.Replace("my ", string.Empty));

    private static string[] SplitApplicationNames(string value) =>
        ApplicationSeparatorRegex().Split(value.Trim())
            .Select(item => item.Trim(' ', '\'', '"'))
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    [GeneratedRegex("^(?:open|launch|start|run)\\s+", RegexOptions.IgnoreCase)]
    private static partial Regex LaunchVerbRegex();

    [GeneratedRegex("^(?:open|show|go to)\\s+(?:my\\s+)?", RegexOptions.IgnoreCase)]
    private static partial Regex OpenFolderVerbRegex();

    [GeneratedRegex("^(?:make|create)\\s+(?:a\\s+)?folder\\s+(?:called|named)\\s+(?<name>.+?)(?=\\s+on\\s+(?:my\\s+)?desktop$|$)(?:\\s+on\\s+(?:my\\s+)?desktop)?$", RegexOptions.IgnoreCase)]
    private static partial Regex CreateFolderRegex();

    [GeneratedRegex("^(?<verb>close|kill|terminate|restart)\\s+(?<name>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ProcessActionRegex();

    [GeneratedRegex("^\\s*(?:please\\s+)?(?:close|quit|exit)\\s+(?:everything|all(?:\\s+apps?)?)\\s+(?:except|execpt|excluding)\\s+(?<names>.+?)\\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex CloseAllExceptForwardRegex();

    [GeneratedRegex("^\\s*(?:except|execpt|excluding)\\s+(?<names>.+?)\\s+(?:please\\s+)?(?:close|quit|exit)\\s+(?:everything|all(?:\\s+apps?)?)\\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex CloseAllExceptReverseRegex();

    [GeneratedRegex("\\s*(?:,|;|&|\\band\\b)\\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ApplicationSeparatorRegex();

    [GeneratedRegex("(?:,|\\s+(?:and|then)\\s+)", RegexOptions.IgnoreCase)]
    private static partial Regex CompoundProcessTargetsRegex();

    [GeneratedRegex("(?:what(?:'s| is).*(?:ram|memory)|using (?:my )?(?:ram|memory)|memory usage)", RegexOptions.IgnoreCase)]
    private static partial Regex RamRegex();

    [GeneratedRegex("(?:what(?:'s| is).*cpu|using (?:my )?cpu|cpu usage)", RegexOptions.IgnoreCase)]
    private static partial Regex CpuRegex();

    [GeneratedRegex("(?:system info|system stats|about this pc|disk usage|battery|network status)", RegexOptions.IgnoreCase)]
    private static partial Regex SystemStatsRegex();

    [GeneratedRegex("^(?:why|what|how|which|who|when|where|can you|could you|should|is |are )", RegexOptions.IgnoreCase)]
    private static partial Regex QuestionRegex();

    [GeneratedRegex("(?:everything except|i don'?t need|make my pc|and then|after that|frozen application|start playing)", RegexOptions.IgnoreCase)]
    private static partial Regex ComplexActionRegex();
}
