using System.Text.Json;
using Flux.Core;
using Flux.Windows.Configuration;

namespace Flux.Windows.Infrastructure;

public sealed class CommandHistory : ICommandHistory
{
    private readonly string _path;
    private readonly AppSettings _settings;
    private readonly List<string> _items;

    public CommandHistory(AppSettings settings)
    {
        _settings = settings;
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Flux");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "history.json");
        _items = Load();
    }

    public IReadOnlyList<string> Items => _items;

    public void Add(string command)
    {
        command = command.Trim();
        if (command.Length == 0)
        {
            return;
        }

        _items.RemoveAll(item => string.Equals(item, command, StringComparison.OrdinalIgnoreCase));
        _items.Insert(0, command);
        if (_items.Count > Math.Clamp(_settings.HistoryLimit, 0, 1000))
        {
            _items.RemoveRange(_settings.HistoryLimit, _items.Count - _settings.HistoryLimit);
        }

        Save();
    }

    public void Clear()
    {
        _items.Clear();
        Save();
    }

    private List<string> Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_path)) ?? []
                : [];
        }
        catch
        {
            return [];
        }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_items));
        }
        catch
        {
            // History is convenience data; ignore disk failures.
        }
    }
}

