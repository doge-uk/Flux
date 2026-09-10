using System.Text.Json;

namespace Flux.Windows.Configuration;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _path;

    public SettingsService()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Flux");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "settings.json");
        Current = Load();
    }

    public AppSettings Current { get; }

    public void Save()
    {
        var temporaryPath = _path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(Current, JsonOptions));
        File.Move(temporaryPath, _path, true);
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOptions)
                    ?? new AppSettings();
            }
        }
        catch
        {
            // A malformed settings file must not prevent Flux from starting.
        }

        return new AppSettings();
    }
}

