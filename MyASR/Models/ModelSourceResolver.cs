namespace MyASR.Models;

/// <summary>Environment variable beats user settings beats the descriptor's default.</summary>
public sealed class ModelSourceResolver(IReadOnlyDictionary<string, string>? settingsSources)
    : IModelSourceResolver
{
    public static string EnvVarName(string modelId) =>
        "MYASR_MODEL_SOURCE_" + modelId.ToUpperInvariant().Replace('-', '_');

    public string ResolveBaseUri(ModelDescriptor model)
    {
        var env = Environment.GetEnvironmentVariable(EnvVarName(model.Id));
        if (!string.IsNullOrWhiteSpace(env)) return env;

        if (settingsSources is not null
            && settingsSources.TryGetValue(model.Id, out var configured)
            && !string.IsNullOrWhiteSpace(configured))
            return configured;

        return model.DefaultBaseUri;
    }
}
