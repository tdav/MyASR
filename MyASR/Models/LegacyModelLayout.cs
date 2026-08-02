using System.IO;

namespace MyASR.Models;

/// <summary>
/// Before per-model folders existed, GigaAM's files lived directly in the models root.
/// Moving them avoids a 928 MB re-download on upgrade. Safe to call on every startup.
/// </summary>
public static class LegacyModelLayout
{
    /// <returns>How many files were moved.</returns>
    public static int Migrate(string root, ModelDescriptor model)
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

            Directory.CreateDirectory(target);
            File.Move(flat, dest);
            moved++;
        }

        return moved;
    }
}
