using MyASR.Asr;
using MyASR.Models;

namespace MyASR;

/// <summary>Decisions about model switching, kept out of the form so they can be tested.</summary>
public static class ModelSelectionPolicy
{
    public static bool CanSwitch(AsrState state) => state == AsrState.Idle;

    public static string StatusText(ModelDescriptor model, bool hotkeyRegistered, string hotkey)
    {
        var ptt = hotkeyRegistered ? $"PTT: {hotkey}" : "PTT недоступен — занят";
        var licence = model.LicenceNote is { Length: > 0 } note ? $" ({note})" : "";
        return $"● Idle — {model.DisplayName}{licence} [{ptt}]";
    }
}
