using System.Diagnostics;
using System.Text;

namespace Flux.Windows.Tools;

internal sealed record PowerShellExecutionResult(bool Success, string Output);

internal static class PowerShellCommandRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private const int MaxCapturedCharacters = 32_768;

    public static async Task<PowerShellExecutionResult> RunAsync(
        string command,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return new PowerShellExecutionResult(false, "No PowerShell command was provided.");
        }

        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("PowerShell could not be started.");
        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        executionCancellation.CancelAfter(timeout ?? DefaultTimeout);

        var outputTask = ReadBoundedAsync(process.StandardOutput, executionCancellation.Token);
        var errorTask = ReadBoundedAsync(process.StandardError, executionCancellation.Token);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(executionCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            TryKill(process);
            await WaitAfterKillAsync(process);
        }

        var standardOutput = await outputTask;
        var standardError = await errorTask;
        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        var output = CombineOutput(standardOutput, standardError);
        if (timedOut)
        {
            var timeoutMessage = $"PowerShell command timed out after {(timeout ?? DefaultTimeout).TotalSeconds:0} seconds.";
            output = string.IsNullOrWhiteSpace(output) ? timeoutMessage : timeoutMessage + Environment.NewLine + output;
            return new PowerShellExecutionResult(false, output);
        }

        return new PowerShellExecutionResult(process.ExitCode == 0, output);
    }

    private static async Task<CapturedText> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[2_048];
        var captured = new StringBuilder(Math.Min(MaxCapturedCharacters, 4_096));
        var truncated = false;
        try
        {
            while (true)
            {
                var count = await reader.ReadAsync(buffer, cancellationToken);
                if (count == 0)
                {
                    break;
                }

                var remaining = MaxCapturedCharacters - captured.Length;
                if (remaining > 0)
                {
                    captured.Append(buffer, 0, Math.Min(count, remaining));
                }
                truncated |= count > remaining;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }

        return new CapturedText(captured.ToString(), truncated);
    }

    private static string CombineOutput(CapturedText standardOutput, CapturedText standardError)
    {
        var parts = new[] { standardOutput.Text.Trim(), standardError.Text.Trim() }
            .Where(value => !string.IsNullOrWhiteSpace(value));
        var output = string.Join(Environment.NewLine, parts);
        if (standardOutput.Truncated || standardError.Truncated)
        {
            output += (output.Length == 0 ? string.Empty : Environment.NewLine) + "[Output truncated]";
        }
        return output;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static async Task WaitAfterKillAsync(Process process)
    {
        try
        {
            using var cleanupCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await process.WaitForExitAsync(cleanupCancellation.Token);
        }
        catch (Exception exception) when (exception is InvalidOperationException or OperationCanceledException)
        {
        }
    }

    private sealed record CapturedText(string Text, bool Truncated);
}
