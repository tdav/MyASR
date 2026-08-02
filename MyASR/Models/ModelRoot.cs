using System.IO;

namespace MyASR.Models;

/// <summary>Resolves the directory that holds every model's folder.</summary>
public static class ModelRoot
{
    public const string EnvVarName = "MYASR_MODELS_DIR";

    public static string Resolve() => Resolve(Environment.GetEnvironmentVariable);

    /// <summary>
    /// Reads the override through the supplied accessor instead of the process environment, so a
    /// test can exercise every branch by passing a value rather than by writing the real variable.
    /// The environment is process-global: the end-to-end gates in MyASR.Tests read
    /// <see cref="EnvVarName"/> to locate real artefacts, and a concurrent write would make them
    /// decide the model is absent and pass inert.
    /// </summary>
    public static string Resolve(Func<string, string?> environment)
    {
        var env = environment(EnvVarName);
        if (!string.IsNullOrWhiteSpace(env)) return env;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, "MyASR", "models");
    }
}
