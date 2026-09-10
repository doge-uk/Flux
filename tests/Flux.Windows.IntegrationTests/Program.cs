using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flux.Core;
using Flux.Windows;
using Flux.Windows.Ai;
using Flux.Windows.Configuration;
using Flux.Windows.Search;
using Flux.Windows.SystemIntegration;
using Flux.Windows.Tools;
using Flux.Windows.Updates;

await TestUpdateIntegrityAsync();
TestProcessClassification();
TestMalformedToolCallParsing();
TestMarkdownRendering();
TestEndpointValidation();
TestSystemInstructions();
TestBackgroundProcessToolContract();
await TestFileSearchBudgetAsync();
await TestPowerShellExecutionSafetyAsync();

if (args.Contains("--warmup-only", StringComparer.OrdinalIgnoreCase))
{
    var requestedModel = Environment.GetEnvironmentVariable("FLUX_TEST_MODEL")
        ?? throw new InvalidOperationException("FLUX_TEST_MODEL is required for --warmup-only.");
    var settings = new AppSettings { AiMode = AiMode.Local, LocalModel = requestedModel };
    using var provider = new AiProviderRouter(settings);
    var stages = new List<AiModelStage>();
    var unloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    provider.ModelStatusChanged += status =>
    {
        stages.Add(status.Stage);
        if (status.Stage == AiModelStage.Unloaded)
        {
            unloaded.TrySetResult();
        }
    };
    await provider.BeginLocalModelSessionAsync();
    await Task.Delay(TimeSpan.FromSeconds(35));
    using (var ollama = new HttpClient())
    {
        var running = JsonNode.Parse(await ollama.GetStringAsync("http://localhost:11434/api/ps"));
        var stillResident = running?["models"]?.AsArray().Any(item =>
            string.Equals(item?["name"]?.GetValue<string>(), requestedModel, StringComparison.OrdinalIgnoreCase)) == true;
        Assert(stillResident, "The model left memory while the search UI session was still open.");
    }
    provider.ScheduleLocalModelUnload(TimeSpan.FromSeconds(1));
    await unloaded.Task.WaitAsync(TimeSpan.FromSeconds(15));
    Assert(stages.Contains(AiModelStage.Loading) && stages.Contains(AiModelStage.Ready) && stages.Contains(AiModelStage.Unloaded),
        "The model lifecycle did not report loading, ready and unloaded states.");
    Console.WriteLine($"PASS  Local model loaded on demand and unloaded after inactivity: {requestedModel}");
    return 0;
}

if (args.Contains("--model-only", StringComparer.OrdinalIgnoreCase))
{
    var requestedModel = Environment.GetEnvironmentVariable("FLUX_TEST_MODEL")
        ?? throw new InvalidOperationException("FLUX_TEST_MODEL is required for --model-only.");
    await TestLocalModelProtocolAsync(requestedModel);
    return 0;
}

var powershell = StartWindowProcess("powershell.exe", ignoreClose: true);
var pwsh = StartWindowProcess("pwsh.exe", ignoreClose: false);
var backgroundPwsh = StartBackgroundProcess("pwsh.exe");
try
{
    await WaitForWindowAsync(powershell);
    await WaitForWindowAsync(pwsh);

    var service = new WindowsProcessService([powershell.Id, pwsh.Id, backgroundPwsh.Id]);
    var refused = await service.CloseAllExceptAsync(["definitely-not-a-running-app"]);
    Assert(!refused.Success, "An unresolved exclusion should stop the operation.");
    Assert(!HasExited(powershell.Id) && !HasExited(pwsh.Id),
        "Flux closed an application after failing to identify an exclusion.");
    Console.WriteLine("PASS  Unresolved exclusions close nothing");

    var closeExcept = await service.CloseAllExceptAsync(["pwsh"]);
    Assert(closeExcept.Success, closeExcept.Output);
    Assert(await WaitForGoneAsync(powershell.Id), "Windows PowerShell was reported closed but is still running.");
    Assert(!HasExited(pwsh.Id), "The excluded pwsh application was closed.");
    Console.WriteLine("PASS  Close everything except preserves the exact excluded application and verifies closure");

    var closeNamed = await service.CloseApplicationAsync("pwsh");
    Assert(closeNamed.Success, closeNamed.Output);
    Assert(await WaitForGoneAsync(pwsh.Id), "pwsh was reported closed but is still running.");
    Assert(await WaitForGoneAsync(backgroundPwsh.Id), "The windowless pwsh sibling was left running.");
    Console.WriteLine("PASS  Close named application verifies visible and windowless sibling processes exited");

    var model = Environment.GetEnvironmentVariable("FLUX_TEST_MODEL");
    if (!string.IsNullOrWhiteSpace(model))
    {
        await TestLocalModelProtocolAsync(model);
    }
    return 0;
}

finally
{
    KillIfRunning(powershell);
    KillIfRunning(pwsh);
    KillIfRunning(backgroundPwsh);
}

static async Task TestPowerShellExecutionSafetyAsync()
{
    var bounded = await PowerShellCommandRunner.RunAsync(
        "[Console]::Out.Write('x' * 40000)",
        timeout: TimeSpan.FromSeconds(5));
    Assert(bounded.Success, "A successful PowerShell command was reported as failed.");
    Assert(bounded.Output.Length <= 32_800 && bounded.Output.EndsWith("[Output truncated]", StringComparison.Ordinal),
        "PowerShell output was not safely bounded.");

    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
    var stopwatch = Stopwatch.StartNew();
    try
    {
        await PowerShellCommandRunner.RunAsync(
            "Start-Sleep -Seconds 10",
            cancellation.Token,
            TimeSpan.FromSeconds(15));
        throw new InvalidOperationException("A cancelled PowerShell command completed normally.");
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
    {
    }

    Assert(stopwatch.Elapsed < TimeSpan.FromSeconds(4),
        "Cancelling PowerShell did not promptly terminate its process tree.");
    Console.WriteLine("PASS  PowerShell execution is cancellable and output-bounded");
}

static void TestSystemInstructions()
{
    var instructions = CompatibleChatProvider.SystemInstructions;
    Assert(instructions.Contains("Use lightweight Markdown", StringComparison.Ordinal),
        "The model was not told which response formatting Flux supports.");
    Assert(!instructions.Contains("DO NOT USE ANY TYPE OF FORMATTING", StringComparison.OrdinalIgnoreCase),
        "The system prompt contains contradictory formatting instructions.");
    Assert(instructions.Contains("do not claim the action succeeded", StringComparison.OrdinalIgnoreCase),
        "The system prompt no longer prevents unverified success claims.");
    Console.WriteLine("PASS  AI response instructions are concise and internally consistent");
}

static void TestBackgroundProcessToolContract()
{
    var registry = new ToolRegistry(
        new WindowsProcessService([]),
        new WindowsSystemInfoService(),
        new FileSearchService(),
        new ApplicationCatalog(new AppSettings(), new SilentLog()));
    var closeTools = registry.Definitions.Where(definition =>
        definition.Name is "terminate_application" or "close_application" or
            "close_applications" or "close_applications_except").ToArray();

    Assert(closeTools.Length == 4, "One or more application-close tools are missing.");
    Assert(closeTools.All(definition => definition.Description.Contains("background", StringComparison.OrdinalIgnoreCase)),
        "An application-close tool still tells the model it cannot resolve background applications.");
    Assert(CompatibleChatProvider.SystemInstructions.Contains("BackgroundApplication", StringComparison.Ordinal),
        "The system prompt does not explain safe background application selection.");
    Assert(CompatibleChatProvider.SystemInstructions.Contains("HelperProcess", StringComparison.Ordinal),
        "The system prompt does not prevent helpers from being selected independently.");
    Console.WriteLine("PASS  AI tools advertise safe background application shutdown");
}

static async Task TestLocalModelProtocolAsync(string model)
{
    using var provider = new CompatibleChatProvider("Ollama integration test", "http://localhost:11434", model, "ollama");
    var closeTool = new ToolDefinition(
        "close_applications",
        "Close several specifically named applications. Use this for requests such as close Discord and Chrome.",
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["names"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["type"] = "string" }
                }
            },
            ["required"] = new JsonArray("names"),
            ["additionalProperties"] = false
        },
        PermissionLevel.Disruptive);
    var action = await provider.CompleteAsync(new AiTurnRequest(
        "close Discord and Chrome",
        "Both applications are running.",
        [closeTool]));
    var call = action.ToolCalls.SingleOrDefault(tool => tool.Name == "close_applications");
    Assert(call is not null, $"{model} did not emit the expected close_applications tool call.");
    var names = call!.Arguments.GetProperty("names").EnumerateArray()
        .Select(item => item.GetString())
        .Where(item => item is not null)
        .ToArray();
    Assert(names.Contains("Discord", StringComparer.OrdinalIgnoreCase) &&
        names.Contains("Chrome", StringComparer.OrdinalIgnoreCase),
        $"{model} did not preserve both application names.");
    Console.WriteLine("PASS  Local model emits a structured compound application tool call");

    var chunks = new List<string>();
    var streamed = await provider.CompleteStreamingAsync(
        new AiTurnRequest("Reply with exactly these three words: Hello from Flux", "No tools are needed.", []),
        new InlineProgress<string>(chunks.Add));
    Assert(chunks.Count > 0, $"{model} returned no streamed text chunks.");
    Assert(streamed.Text.Contains("Hello from Flux", StringComparison.OrdinalIgnoreCase),
        $"{model} streaming response text was not reconstructed correctly: {streamed.Text}");
    Console.WriteLine("PASS  Local model streams and reconstructs response text");
}

static async Task TestUpdateIntegrityAsync()
{
    var bytes = Encoding.UTF8.GetBytes("flux-update-integrity-test");
    var expected = Convert.ToHexString(SHA256.HashData(bytes));
    Assert(UpdateIntegrity.TryParseGitHubDigest("sha256:" + expected.ToLowerInvariant(), out var parsed) && parsed == expected,
        "A valid GitHub SHA-256 digest was not accepted.");
    Assert(!UpdateIntegrity.TryParseGitHubDigest("sha256:not-a-hash", out _),
        "An invalid GitHub digest was accepted.");

    var path = Path.Combine(Path.GetTempPath(), $"flux-update-{Guid.NewGuid():N}.test");
    try
    {
        await File.WriteAllBytesAsync(path, bytes);
        Assert(await UpdateIntegrity.VerifyFileAsync(path, expected), "The expected update hash did not verify.");
        Assert(!await UpdateIntegrity.VerifyFileAsync(path, new string('0', 64)), "A mismatched update hash was accepted.");
        Console.WriteLine("PASS  Update packages require a valid matching SHA-256 digest");
    }
    finally
    {
        File.Delete(path);
    }
}

static Process StartWindowProcess(string executable, bool ignoreClose)
{
    var script = "$f=[Windows.Forms.Form]::new();" +
        "$f.Text='Flux isolated integration test';" +
        "$f.ShowInTaskbar=$true;" +
        "$f.StartPosition='Manual';" +
        "$f.Location=[Drawing.Point]::new(-30000,-30000);" +
        (ignoreClose ? "$f.add_FormClosing({$args[1].Cancel=$true});" : string.Empty) +
        "[Windows.Forms.Application]::Run($f)";
    var startInfo = new ProcessStartInfo(executable) { UseShellExecute = false };
    startInfo.ArgumentList.Add("-NoProfile");
    startInfo.ArgumentList.Add("-Sta");
    startInfo.ArgumentList.Add("-Command");
    startInfo.ArgumentList.Add("Add-Type -AssemblyName System.Windows.Forms,System.Drawing;" + script);
    return Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {executable}.");
}

static void TestProcessClassification()
{
    var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    var discord = Path.Combine(local, "Discord", "app-1.0.0", "Discord.exe");

    Assert(ProcessClassifier.Classify("Discord", discord, true, false, false) == ProcessCategory.BackgroundApplication,
        "A user-installed background application was not recognized.");
    Assert(ProcessClassifier.Classify("DiscordUpdate", Path.Combine(local, "Discord", "Update.exe"), true, false, false) == ProcessCategory.LauncherOrUpdater,
        "An application updater was not separated from the application.");
    Assert(ProcessClassifier.Classify("node", Path.Combine(local, "Example", "runtimes", "node.exe"), true, false, false) == ProcessCategory.HelperProcess,
        "A runtime host was not classified as a helper.");
    Assert(ProcessClassifier.Classify("svchost", Path.Combine(windows, "System32", "svchost.exe"), true, false, true) == ProcessCategory.WindowsProcess,
        "A protected Windows process was not classified as Windows-owned.");
    Assert(ProcessClassifier.Classify("steamwebhelper", @"C:\Program Files (x86)\Steam\bin\steamwebhelper.exe", true, true, false) == ProcessCategory.HelperProcess,
        "A helper with a visible utility window was promoted to a user application.");
    Assert(ProcessClassifier.Classify("steam", @"C:\Program Files (x86)\Steam\steam.exe", true, false, false) == ProcessCategory.BackgroundApplication,
        "A tray-only application installed under Program Files was not recognized.");
    Assert(ProcessClassifier.Classify("nvcontainer", @"C:\Program Files\NVIDIA Corporation\NvContainer\nvcontainer.exe", true, false, false) == ProcessCategory.HelperProcess,
        "A background container was promoted to an application.");
    Assert(ProcessClassifier.Classify("ExampleService", @"C:\Program Files\Example\ExampleService.exe", false, false, false, null, true) == ProcessCategory.Service,
        "A session-zero service was not recognized.");
    Assert(ProcessClassifier.Classify("Discord", discord, false, false, false) == ProcessCategory.OtherSession,
        "A process in another session was treated as the current user's application.");
    var windowsAppsExecutable = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "WindowsApps", "Example.App_1.0_x64__publisher", "app", "Example.exe");
    var windowsAppsFamily = WindowsProcessService.TryGetApplicationFamilyRoot(windowsAppsExecutable);
    Assert(windowsAppsFamily is not null && windowsAppsFamily.EndsWith(
            Path.Combine("WindowsApps", "Example.App_1.0_x64__publisher"), StringComparison.OrdinalIgnoreCase),
        "WindowsApps packages were grouped at an unsafe shared-container boundary.");
    var commonFilesExecutable = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "Common Files", "Example Vendor", "Agent.exe");
    var commonFilesFamily = WindowsProcessService.TryGetApplicationFamilyRoot(commonFilesExecutable);
    Assert(commonFilesFamily is not null && commonFilesFamily.EndsWith(
            Path.Combine("Common Files", "Example Vendor"), StringComparison.OrdinalIgnoreCase),
        "Common Files applications were grouped at an unsafe shared-container boundary.");
    Console.WriteLine("PASS  Process classifier separates apps, helpers, updaters and Windows processes");
}

static void TestMalformedToolCallParsing()
{
    using var document = JsonDocument.Parse("""
        {
          "tool_calls": [
            { "id": "broken-arguments", "function": { "name": "close_application", "arguments": "{" } },
            { "id": "missing-name", "function": { "arguments": "{}" } },
            null
          ]
        }
        """);
    var calls = CompatibleChatProvider.ParseToolCalls(document.RootElement);
    Assert(calls.Count == 1 && calls[0].Name == "close_application",
        "A malformed tool call corrupted the complete tool response.");
    Assert(calls[0].Arguments.ValueKind == JsonValueKind.Object && !calls[0].Arguments.EnumerateObject().Any(),
        "Malformed tool arguments were not replaced with a safe empty object.");
    Console.WriteLine("PASS  Malformed model tool calls degrade safely without faking execution");
}

static void TestMarkdownRendering()
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            var target = new System.Windows.Controls.TextBlock();
            MarkdownTextRenderer.Render(target, "## Result\n- **Closed:** `Discord`\n> Verified");
            var visibleText = ReadInlineText(target.Inlines);
            Assert(visibleText.Contains("Result", StringComparison.Ordinal) &&
                visibleText.Contains("Closed:", StringComparison.Ordinal) &&
                visibleText.Contains("Discord", StringComparison.Ordinal) &&
                visibleText.Contains("Verified", StringComparison.Ordinal),
                "Formatted response content was lost while creating WPF inlines.");
            Assert(!visibleText.Contains("**", StringComparison.Ordinal) &&
                !visibleText.Contains("##", StringComparison.Ordinal) &&
                !visibleText.Contains('`'),
                "Markdown control characters leaked into the rendered response.");
        }
        catch (Exception exception)
        {
            failure = exception;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (failure is not null)
    {
        throw failure;
    }
    Console.WriteLine("PASS  Markdown responses render as native WPF formatting");
}

static string ReadInlineText(System.Windows.Documents.InlineCollection inlines)
{
    var builder = new StringBuilder();
    foreach (var inline in inlines)
    {
        switch (inline)
        {
            case System.Windows.Documents.Run run:
                builder.Append(run.Text);
                break;
            case System.Windows.Documents.LineBreak:
                builder.AppendLine();
                break;
            case System.Windows.Documents.Span span:
                builder.Append(ReadInlineText(span.Inlines));
                break;
        }
    }
    return builder.ToString();
}

static void TestEndpointValidation()
{
    Assert(EndpointValidator.IsHttpEndpoint("http://localhost:11434"),
        "A valid Ollama endpoint was rejected.");
    Assert(EndpointValidator.IsHttpEndpoint("https://models.example.test/v1"),
        "A valid HTTPS endpoint was rejected.");
    Assert(!EndpointValidator.IsHttpEndpoint("file:///C:/models") &&
        !EndpointValidator.IsHttpEndpoint("localhost:11434") &&
        !EndpointValidator.IsHttpEndpoint(string.Empty),
        "A non-HTTP or malformed model endpoint was accepted.");
    Console.WriteLine("PASS  Model endpoints are restricted to valid HTTP or HTTPS URLs");
}

static async Task TestFileSearchBudgetAsync()
{
    var search = new FileSearchService();
    var started = Stopwatch.StartNew();
    _ = await search.SearchAsync($"flux-no-match-{Guid.NewGuid():N}", 8);
    started.Stop();
    Assert(started.Elapsed < TimeSpan.FromSeconds(1),
        $"File search exceeded its interactive latency budget: {started.Elapsed.TotalMilliseconds:N0} ms.");
    Console.WriteLine($"PASS  File search remains bounded ({started.Elapsed.TotalMilliseconds:N0} ms)");
}

static Process StartBackgroundProcess(string executable)
{
    var startInfo = new ProcessStartInfo(executable)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        WindowStyle = ProcessWindowStyle.Hidden
    };
    startInfo.ArgumentList.Add("-NoProfile");
    startInfo.ArgumentList.Add("-NonInteractive");
    startInfo.ArgumentList.Add("-Command");
    startInfo.ArgumentList.Add("Start-Sleep -Seconds 60");
    return Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {executable}.");
}

static async Task WaitForWindowAsync(Process process)
{
    var deadline = DateTime.UtcNow.AddSeconds(10);
    while (DateTime.UtcNow < deadline)
    {
        process.Refresh();
        if (!process.HasExited && process.MainWindowHandle != IntPtr.Zero)
        {
            return;
        }
        await Task.Delay(100);
    }
    throw new InvalidOperationException($"PID {process.Id} did not create a window.");
}

static async Task<bool> WaitForGoneAsync(int processId)
{
    for (var attempt = 0; attempt < 30; attempt++)
    {
        if (HasExited(processId))
        {
            return true;
        }
        await Task.Delay(100);
    }
    return HasExited(processId);
}

static bool HasExited(int processId)
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

static void KillIfRunning(Process process)
{
    try
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(3000);
        }
    }
    catch
    {
    }
    finally
    {
        process.Dispose();
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

sealed class SilentLog : ILogService
{
    public void Info(string message)
    {
    }

    public void Error(string message, Exception? exception = null)
    {
    }
}
