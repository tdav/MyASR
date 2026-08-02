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

    /// <summary>
    /// Status text for when no model is loaded at all — e.g. a failed switch whose revert to
    /// the previous model also failed. Recognition is unavailable until the app is restarted.
    /// </summary>
    public static string NoModelLoadedStatusText() =>
        "⚠ Модель не загружена — распознавание недоступно. " +
        "Проверьте файлы модели и свободное место на диске, затем перезапустите приложение.";

    /// <summary>Index of the model with the given id in <paramref name="models"/>, or -1 if absent.</summary>
    public static int IndexOf(IReadOnlyList<ModelDescriptor> models, string modelId)
    {
        for (var i = 0; i < models.Count; i++)
            if (models[i].Id == modelId) return i;
        return -1;
    }
}
