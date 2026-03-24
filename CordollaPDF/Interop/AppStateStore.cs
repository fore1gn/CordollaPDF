using System.IO;
using System.Text.Json;

namespace CordollaPDF.Interop;

internal sealed class AppStateStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _stateFilePath;

    public AppStateStore()
    {
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CordollaPDF");
        _stateFilePath = Path.Combine(appDataPath, "app-state.json");
    }

    public AppState Load()
    {
        try
        {
            if (!File.Exists(_stateFilePath))
            {
                return new AppState();
            }

            var json = File.ReadAllText(_stateFilePath);
            return JsonSerializer.Deserialize<AppState>(json, SerializerOptions) ?? new AppState();
        }
        catch
        {
            return new AppState();
        }
    }

    public void Save(AppState state)
    {
        try
        {
            var directory = Path.GetDirectoryName(_stateFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(state, SerializerOptions);
            File.WriteAllText(_stateFilePath, json);
        }
        catch
        {
        }
    }
}

internal sealed class AppState
{
    public bool IsSidebarCollapsed { get; set; }

    public bool IsMaximized { get; set; }
}
