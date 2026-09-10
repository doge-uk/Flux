using Flux.Core;

namespace Flux.Windows.Infrastructure;

public sealed class LogService : ILogService
{
    private readonly object _gate = new();
    private readonly string _path;

    public LogService()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Flux",
            "Logs");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, $"flux-{DateTime.Today:yyyy-MM-dd}.log");
    }

    public void Info(string message) => Write("INFO", message, null);

    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        try
        {
            lock (_gate)
            {
                File.AppendAllText(
                    _path,
                    $"{DateTimeOffset.Now:O} [{level}] {message}{(exception is null ? string.Empty : $" {exception}")}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never crash the launcher.
        }
    }
}

