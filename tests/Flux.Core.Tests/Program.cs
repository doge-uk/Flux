using System.Text.Json;
using System.Text.Json.Nodes;
using Flux.Core;
using Flux.Core.Agent;
using Flux.Core.Routing;
using Flux.Core.Search;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Fuzzy exact match outranks prefix", TestFuzzyRanking),
    ("Known folder stays deterministic", TestKnownFolder),
    ("Close everything except routes deterministically", TestCloseEverythingExcept),
    ("Weak search matches fall back to AI", TestWeakSearchFallback),
    ("Process action requires confirmation", TestProcessPermission),
    ("Desktop folder command parses a safe name", TestCreateFolderParsing),
    ("Agent cannot auto-run disruptive tool", TestAgentSafetyGate),
    ("Release tags accept a leading v", TestReleaseTagParsing),
    ("Release comparison normalizes assembly revisions", TestReleaseComparison),
    ("Malformed release tags are rejected", TestMalformedReleaseTag),
    ("Compound process actions route to AI tools", TestCompoundProcessAction),
    ("Semantic process actions route to AI", TestSemanticProcessAction),
    ("Agent rejects unverified action claims", TestUnverifiedActionClaim),
    ("Agent streams text but buffers action requests", TestStreamingSafety)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS  {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add($"FAIL  {test.Name}: {exception.Message}");
        Console.WriteLine(failures[^1]);
    }
}

Console.WriteLine($"{tests.Length - failures.Count}/{tests.Length} tests passed.");
return failures.Count == 0 ? 0 : 1;

static Task TestFuzzyRanking()
{
    Assert(FuzzyMatcher.Score("chrome", "chrome") > FuzzyMatcher.Score("chrome", "chromebook"), "Exact match should rank first.");
    Assert(FuzzyMatcher.Score("chr", "chrome") > FuzzyMatcher.Score("chr", "my chrome tool"), "Prefix should outrank contains.");
    return Task.CompletedTask;
}

static async Task TestKnownFolder()
{
    var router = Router();
    var decision = await router.RouteAsync("my downloads");
    Assert(decision.Kind == RouteKind.Search, "Known folder should be a search result.");
    Assert(decision.Results.Single().Kind == SearchResultKind.Folder, "Result should be a folder.");
    Assert(decision.Results.Single().Action.Permission == PermissionLevel.Reversible, "Opening a folder is reversible.");
}

static async Task TestCloseEverythingExcept()
{
    var router = Router();
    var forward = await router.RouteAsync("close everything except Firefox and Discord");
    Assert(forward.Kind == RouteKind.ImmediateAction, "The standard close-except phrase should be deterministic.");
    Assert(forward.Action?.Type == FluxActionType.CloseAllExcept, "Expected a close-all-except action.");

    var reversedTypo = await router.RouteAsync("execpt firefox and chatgpt close everything");
    Assert(reversedTypo.Kind == RouteKind.ImmediateAction, "The reversed phrase with the common typo was not recognized.");
    Assert(reversedTypo.Action?.Target.Split('\n').SequenceEqual(["firefox", "chatgpt"]) == true,
        "The exclusions were not parsed into separate application names.");
}

static async Task TestWeakSearchFallback()
{
    var helpResult = new SearchResult(
        "help",
        "Help",
        @"C:\ProgramData\MyLanViewer\Help.lnk",
        SearchResultKind.Application,
        120,
        new FluxAction(FluxActionType.Launch, @"C:\ProgramData\MyLanViewer\Help.lnk"));
    var router = new DeterministicCommandRouter(new FixedApplications([helpResult]), new FakeFiles());
    var sentence = await router.RouteAsync("write me a short poem about space");
    Assert(sentence.Kind == RouteKind.Ai, "A weak Help.lnk match hijacked an unrelated sentence.");

    var exactResult = helpResult with { Title = "Firefox", Score = 1000 };
    router = new DeterministicCommandRouter(new FixedApplications([exactResult]), new FakeFiles());
    var app = await router.RouteAsync("Firefox");
    Assert(app.Kind == RouteKind.Search, "An exact application match should remain deterministic.");
}

static async Task TestProcessPermission()
{
    var router = Router();
    var decision = await router.RouteAsync("kill Discord");
    Assert(decision.Kind == RouteKind.ImmediateAction, "Explicit process action should be deterministic.");
    Assert(decision.Action?.Permission == PermissionLevel.Disruptive, "Termination must be disruptive.");
}

static async Task TestCreateFolderParsing()
{
    var router = Router();
    var decision = await router.RouteAsync("make a folder called Test on my desktop");
    Assert(decision.Kind == RouteKind.ImmediateAction, "Folder creation should be deterministic.");
    Assert(decision.Action?.Type == FluxActionType.CreateFolder, "Expected create-folder action.");
    Assert(Path.GetFileName(decision.Action?.Target) == "Test", "Desktop suffix leaked into the folder name.");
    Assert(decision.Action?.Permission == PermissionLevel.Reversible, "Folder creation should be reversible level 1.");
}

static async Task TestAgentSafetyGate()
{
    var tool = new CountingTool(PermissionLevel.Disruptive);
    var registry = new FakeRegistry(tool);
    var provider = new FakeProvider();
    var agent = new FluxAgent(provider, registry, new FakeLog());
    var result = await agent.RunAsync("close it", "pid 42");
    Assert(tool.Executions == 0, "Disruptive tool executed before confirmation.");
    Assert(result.PendingActions.Count == 1, "Disruptive tool should be returned as pending.");
}

static Task TestReleaseTagParsing()
{
    Assert(ReleaseVersion.TryParseTag("v1.4.2", out var version), "Expected a valid release tag.");
    Assert(version == new Version(1, 4, 2, 0), "Release tag was not normalized.");
    return Task.CompletedTask;
}

static Task TestReleaseComparison()
{
    Assert(ReleaseVersion.Compare(new Version(1, 4, 2), new Version(1, 4, 2, 0)) == 0,
        "An omitted revision should compare as zero.");
    Assert(ReleaseVersion.Compare(new Version(1, 4, 3), new Version(1, 4, 2, 9)) > 0,
        "A newer build should compare higher.");
    return Task.CompletedTask;
}

static Task TestMalformedReleaseTag()
{
    Assert(!ReleaseVersion.TryParseTag("latest", out _), "A non-version tag must be rejected.");
    Assert(!ReleaseVersion.TryParseTag("v1", out _), "A one-part version must be rejected.");
    return Task.CompletedTask;
}

static async Task TestCompoundProcessAction()
{
    var compound = await Router().RouteAsync("close Discord and Chrome");
    Assert(compound.Kind == RouteKind.Ai, "A compound close must not be treated as one application name.");

    var single = await Router().RouteAsync("close Discord");
    Assert(single.Kind == RouteKind.ImmediateAction, "A single close should stay deterministic.");
    Assert(single.Action?.Target == "Discord", "The single application target was parsed incorrectly.");
}

static async Task TestSemanticProcessAction()
{
    var router = Router();
    foreach (var request in new[]
             {
                 "close anything that isnt productive",
                 "close all background apps",
                 "close whatever I do not need"
             })
    {
        var decision = await router.RouteAsync(request);
        Assert(decision.Kind == RouteKind.Ai, $"Semantic close request was treated as a literal app: {request}");
    }

    var explicitApp = await router.RouteAsync("close Epic Games Launcher");
    Assert(explicitApp.Kind == RouteKind.ImmediateAction, "A specific multi-word app name should stay deterministic.");
}

static async Task TestUnverifiedActionClaim()
{
    var registry = new FakeRegistry(new CountingTool(PermissionLevel.Disruptive));
    var agent = new FluxAgent(new TextOnlyProvider("Closed: Discord"), registry, new FakeLog());
    var result = await agent.RunAsync("close Discord", "Discord is running");
    Assert(result.Text == "No action was performed.", "The agent exposed an unverified success claim.");
}

static async Task TestStreamingSafety()
{
    var registry = new FakeRegistry(new CountingTool(PermissionLevel.Disruptive));
    var provider = new StreamingProvider();
    var agent = new FluxAgent(provider, registry, new FakeLog());
    var chunks = new List<string>();
    await agent.RunAsync("explain uptime", "none", new InlineProgress<string>(chunks.Add));
    Assert(chunks.SequenceEqual(["hello", " world"]), "Normal AI text did not stream through the agent.");

    chunks.Clear();
    await agent.RunAsync("could you close Discord", "Discord is running", new InlineProgress<string>(chunks.Add));
    Assert(chunks.Count == 0, "An action response streamed before tool verification.");
}

static DeterministicCommandRouter Router() => new(new FakeApplications(), new FakeFiles());

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed class FakeApplications : IApplicationCatalog
{
    public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public IReadOnlyList<SearchResult> Search(string query, int limit = 8) => Array.Empty<SearchResult>();
}

sealed class FixedApplications(IReadOnlyList<SearchResult> results) : IApplicationCatalog
{
    public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public IReadOnlyList<SearchResult> Search(string query, int limit = 8) => results.Take(limit).ToArray();
}

sealed class FakeFiles : IFileSearchService
{
    public Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int limit = 12, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SearchResult>>(Array.Empty<SearchResult>());
}

sealed class FakeProvider : IAiProvider
{
    public string Name => "fake";
    public bool IsConfigured => true;
    public Task<AiTurnResponse> CompleteAsync(AiTurnRequest request, CancellationToken cancellationToken = default)
    {
        using var document = JsonDocument.Parse("{\"pid\":42}");
        return Task.FromResult(new AiTurnResponse("response", "", [new ToolCall("call", "test_tool", document.RootElement.Clone())]));
    }
}

sealed class TextOnlyProvider(string text) : IAiProvider
{
    public string Name => "text-only";
    public bool IsConfigured => true;
    public Task<AiTurnResponse> CompleteAsync(AiTurnRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new AiTurnResponse("response", text, Array.Empty<ToolCall>()));
}

sealed class StreamingProvider : IStreamingAiProvider
{
    public string Name => "streaming";
    public bool IsConfigured => true;
    public Task<AiTurnResponse> CompleteAsync(AiTurnRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new AiTurnResponse("response", "Unverified action claim", Array.Empty<ToolCall>()));

    public Task<AiTurnResponse> CompleteStreamingAsync(
        AiTurnRequest request,
        IProgress<string> textProgress,
        CancellationToken cancellationToken = default)
    {
        textProgress.Report("hello");
        textProgress.Report(" world");
        return Task.FromResult(new AiTurnResponse("response", "hello world", Array.Empty<ToolCall>()));
    }
}

sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

sealed class CountingTool(PermissionLevel permission) : IFluxTool
{
    public int Executions { get; private set; }
    public ToolDefinition Definition { get; } = new("test_tool", "test", new JsonObject { ["type"] = "object" }, permission);
    public Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default)
    {
        Executions++;
        return Task.FromResult(new ToolResult(call.Id, call.Name, true, "done"));
    }
}

sealed class FakeRegistry(IFluxTool tool) : IToolRegistry
{
    public IReadOnlyList<ToolDefinition> Definitions => [tool.Definition];
    public bool TryGet(string name, out IFluxTool? result)
    {
        result = string.Equals(name, tool.Definition.Name, StringComparison.OrdinalIgnoreCase) ? tool : null;
        return result is not null;
    }
}

sealed class FakeLog : ILogService
{
    public void Info(string message) { }
    public void Error(string message, Exception? exception = null) { }
}
