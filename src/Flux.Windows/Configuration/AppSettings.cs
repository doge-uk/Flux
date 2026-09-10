namespace Flux.Windows.Configuration;

public enum AiMode
{
    Local,
    Automatic,
    Cloud,
    Disabled
}

public sealed class AppSettings
{
    public AiMode AiMode { get; set; } = AiMode.Local;
    public string LocalEndpoint { get; set; } = "http://localhost:11434";
    public string LocalModel { get; set; } = "qwen3:8b";
    public string CloudEndpoint { get; set; } = "https://api.openai.com/v1";
    public string CloudModel { get; set; } = "gpt-5.6-luna";
    public bool StartWithWindows { get; set; }
    public bool LaunchHidden { get; set; }
    public bool CheckForUpdatesOnLaunch { get; set; } = true;
    public int HistoryLimit { get; set; } = 100;
    public int HotkeyModifiers { get; set; } = 0x0001; // Alt
    public int HotkeyKey { get; set; } = 0x20; // Space
    public Dictionary<string, int> ApplicationUsage { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
