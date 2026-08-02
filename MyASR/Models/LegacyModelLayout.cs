using System.IO;
using Microsoft.Extensions.Logging;

namespace MyASR.Models;

/// <summary>
/// Before per-model folders existed, GigaAM's files lived directly in the models root.
/// Moving them avoids a 928 MB re-download on upgrade. Safe to call on every startup.
/// </summary>
public static class LegacyModelLayout
{
    /// <summary>
    /// Moves each flat legacy file into the model's subfolder. This is a best-effort
    /// migration: a per-file failure (sharing violation from antivirus scanning a freshly
    /// written file, another process holding a handle, a read-only file, etc.) is logged and
    /// skipped rather than thrown, so it can never abort application startup.
    /// </summary>
    /// <returns>How many files were actually moved.</returns>
    public static int Migrate(string root, ModelDescriptor model, ILogger? logger = null)
    {
        if (!Directory.Exists(root)) return 0;

        var target = Path.Combine(root, model.FolderName);
        var moved = 0;

        foreach (var f in model.Files)
        {
            var flat = Path.Combine(root, f.LocalName);
            var dest = Path.Combine(target, f.LocalName);

            // A file already in place wins; the stale flat copy is left alone rather than deleted.
            if (!File.Exists(flat) || File.Exists(dest)) continue;

            try
            {
                Directory.CreateDirectory(target);
                File.Move(flat, dest);
                moved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger?.LogWarning(ex,
                    "Could not migrate legacy model file {Flat} to {Dest}; leaving it in place.",
                    flat, dest);
            }
        }

        return moved;
    }
}
