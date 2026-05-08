using System.Text.Json;

namespace MyASR;

public sealed class UserSettings
{
    public string? MicDeviceId { get; set; }
    public string Hotkey { get; set; } = "Pause";
    public bool GlobalHotkey { get; set; } = true;

    private static string Path =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MyASR", "settings.json");

    public static UserSettings Load()
    {
        try
        {
            if (File.Exists(Path))
                return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(Path)) ?? new();
        }
        catch { /* corrupt file → defaults */ }
        return new UserSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
