using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Flux.Core;
using Flux.Windows.Ai;
using Flux.Windows.Configuration;
using Flux.Windows.SystemIntegration;
using Flux.Windows.Updates;

await TestUpdateIntegrityAsync();

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
try
{
    await WaitForWindowAsync(powershell);
    await WaitForWindowAsync(pwsh);

    var service = new WindowsProcessService([powershell.Id, pwsh.Id]);
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
    Console.WriteLine("PASS  Close named application verifies the process exited");

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
