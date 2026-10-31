using System.IO;
using System.Text.Json;
using MyASR.Models;

namespace MyASR;

public sealed class UserSettings
{
    public string? MicDeviceId { get; set; }
    public string Hotkey { get; set; } = "Ctrl+Shift+X";
    public bool GlobalHotkey { get; set; } = true;

    /// <summary>Id of the selected <see cref="ModelDescriptor"/>.</summary>
    public string ModelId { get; set; } = ModelCatalog.Default.Id;

    /// <summary>Per-model base URI or folder override, keyed by model id.</summary>
    public Dictionary<string, string> ModelSources { get; set; } = [];

    private static string Path =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MyASR", "settings.json");

    public static UserSettings Load()
    {
        try
        {
            if (File.Exists(Path))
                return JsonSerializer.Deserialize(File.ReadAllText(Path), MyAsrJsonContext.Default.UserSettings) ?? new();
        }
        catch { /* corrupt file → defaults */ }
        return new UserSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, JsonSerializer.Serialize(this, MyAsrJsonContext.Default.UserSettings));
    }
}
