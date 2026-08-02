namespace MyASR.Models;

/// <summary>Environment variable beats user settings beats the descriptor's default.</summary>
public sealed class ModelSourceResolver : IModelSourceResolver
{
    private readonly IReadOnlyDictionary<string, string>? settingsSources;
    private readonly Func<string, string?> environment;

    /// <param name="environment">
    /// Reads environment overrides; defaults to the real process environment. Injected for the
    /// same reason as <see cref="ModelRoot.Resolve(Func{string, string?})"/>: the environment is
    /// process-global, so a test must be able to supply an override without writing one.
    /// </param>
    public ModelSourceResolver(
        IReadOnlyDictionary<string, string>? settingsSources,
        Func<string, string?>? environment = null)
    {
        this.settingsSources = settingsSources;
        this.environment = environment ?? Environment.GetEnvironmentVariable;
    }

    public static string EnvVarName(string modelId) =>
        "MYASR_MODEL_SOURCE_" + modelId.ToUpperInvariant().Replace('-', '_');

    public string ResolveBaseUri(ModelDescriptor model)
    {
        var env = environment(EnvVarName(model.Id));
        if (!string.IsNullOrWhiteSpace(env)) return env;

        if (settingsSources is not null
            && settingsSources.TryGetValue(model.Id, out var configured)
            && !string.IsNullOrWhiteSpace(configured))
            return configured;

        return model.DefaultBaseUri;
    }
}
