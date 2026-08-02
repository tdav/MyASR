using System.IO;

namespace MyASR.Models;

/// <summary>Resolves the directory that holds every model's folder.</summary>
public static class ModelRoot
{
    public static string Resolve()
    {
        var env = Environment.GetEnvironmentVariable("MYASR_MODELS_DIR");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, "MyASR", "models");
    }
}
