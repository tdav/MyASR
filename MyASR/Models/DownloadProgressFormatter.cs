using System.Globalization;

namespace MyASR.Models;

/// <summary>Turns a <see cref="DownloadProgress"/> into the strings the download dialog shows.</summary>
public static class DownloadProgressFormatter
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static string Line(ModelDescriptor model, DownloadProgress p) =>
        $"{model.DisplayName} — {p.FileName} ({p.FileIndex + 1}/{p.FileCount}): " +
        $"{p.Downloaded / 1_000_000} / {p.Total / 1_000_000} MB";

    /// <summary>Overall percentage across the whole model, not just the current file.</summary>
    public static int Percent(DownloadProgress p)
    {
        if (p.FileCount <= 0) return 0;
        var withinFile = p.Total > 0 ? Math.Clamp(p.Downloaded / (double)p.Total, 0, 1) : 0;
        var overall = (p.FileIndex + withinFile) / p.FileCount;
        return (int)Math.Clamp(Math.Round(overall * 100, MidpointRounding.ToZero), 0, 100);
    }

    public static string Speed(DownloadProgress p)
    {
        var mbPerSec = p.BytesPerSecond / 1_000_000.0;
        return mbPerSec > 0 ? string.Format(Ru, "{0:F1} MB/s", mbPerSec) : "";
    }

    /// <summary>
    /// What the dialog shows when acquisition fails. The exception names the source that was
    /// tried; on its own that leaves the user with a path and no way to change it, so the
    /// override variable is spelled out next to it.
    /// </summary>
    public static string FailureDetail(ModelDescriptor model, Exception ex) =>
        $"{ex.Message}{Environment.NewLine}{Environment.NewLine}" +
        $"Другой источник можно задать переменной окружения " +
        $"{ModelSourceResolver.EnvVarName(model.Id)} " +
        $"(папка, UNC-путь или базовый URL).";
}
