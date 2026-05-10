using Microsoft.Win32;

namespace MyASR;

/// <summary>Manages the Windows autostart registry entry for MyASR.</summary>
internal static class StartupManager
{
    private const string RegistryKey  = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName    = "MyASR";

    /// <summary>Returns <c>true</c> if the app is registered to run at Windows startup.</summary>
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryKey, writable: false);
        return key?.GetValue(ValueName) is string path
            && path.Equals(ExecutablePath(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Adds or removes the autostart registry entry.</summary>
    public static void SetEnabled(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryKey, writable: true)
            ?? throw new InvalidOperationException("Cannot open Run registry key.");

        if (enable)
            key.SetValue(ValueName, ExecutablePath());
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    private static string ExecutablePath() =>
        $"\"{Environment.ProcessPath ?? Application.ExecutablePath}\"";
}
