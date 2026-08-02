using MyASR.Models;

namespace MyASR.Tests.Models;

public class ModelSourceResolverTests
{
    private static readonly ModelDescriptor Model = ModelCatalog.GigaAmV3;

    /// <summary>An environment with no MyASR overrides in it.</summary>
    private static readonly Func<string, string?> NoEnvironmentOverrides = _ => null;

    /// <summary>
    /// An environment in which this model's source variable — and nothing else — holds
    /// <paramref name="baseUri"/>. Passed to the resolver instead of being written to the real
    /// process environment, which every parallel test shares.
    /// </summary>
    private static Func<string, string?> EnvironmentWithSourceFor(ModelDescriptor model, string baseUri) =>
        name => name == ModelSourceResolver.EnvVarName(model.Id) ? baseUri : null;

    [Test]
    public async Task EnvVarName_UppercasesAndReplacesDashes()
    {
        await Assert.That(ModelSourceResolver.EnvVarName("crisperwhisper2-small-int8"))
            .IsEqualTo("MYASR_MODEL_SOURCE_CRISPERWHISPER2_SMALL_INT8");
    }

    [Test]
    public async Task NoOverrides_ReturnsDescriptorDefault()
    {
        var resolver = new ModelSourceResolver(null, NoEnvironmentOverrides);
        await Assert.That(resolver.ResolveBaseUri(Model)).IsEqualTo(Model.DefaultBaseUri);
    }

    [Test]
    public async Task SettingsOverride_BeatsDefault()
    {
        var resolver = new ModelSourceResolver(new Dictionary<string, string>
        {
            [Model.Id] = @"D:\models\gigaam"
        }, NoEnvironmentOverrides);
        await Assert.That(resolver.ResolveBaseUri(Model)).IsEqualTo(@"D:\models\gigaam");
    }

    [Test]
    public async Task BlankSettingsValue_IsIgnored()
    {
        var resolver = new ModelSourceResolver(
            new Dictionary<string, string> { [Model.Id] = "  " }, NoEnvironmentOverrides);
        await Assert.That(resolver.ResolveBaseUri(Model)).IsEqualTo(Model.DefaultBaseUri);
    }

    [Test]
    public async Task EnvironmentOverride_BeatsSettings()
    {
        var resolver = new ModelSourceResolver(
            new Dictionary<string, string> { [Model.Id] = @"D:\models\gigaam" },
            EnvironmentWithSourceFor(Model, "https://example.test/models/"));
        await Assert.That(resolver.ResolveBaseUri(Model)).IsEqualTo("https://example.test/models/");
    }

    [Test]
    public async Task EnvironmentOverride_ForAnotherModel_DoesNotLeak()
    {
        var other = Model with { Id = "some-other-id" };
        var resolver = new ModelSourceResolver(
            null, EnvironmentWithSourceFor(other, "https://example.test/models/"));
        await Assert.That(resolver.ResolveBaseUri(Model)).IsEqualTo(Model.DefaultBaseUri);
    }

    [Test]
    public async Task SettingsForAnotherModel_DoNotLeak()
    {
        var resolver = new ModelSourceResolver(
            new Dictionary<string, string> { ["some-other-id"] = @"D:\x" }, NoEnvironmentOverrides);
        await Assert.That(resolver.ResolveBaseUri(Model)).IsEqualTo(Model.DefaultBaseUri);
    }
}
