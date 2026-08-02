using MyASR.Models;

namespace MyASR.Tests.Models;

public class ModelSourceResolverTests
{
    private static readonly ModelDescriptor Model = ModelCatalog.GigaAmV3;

    [Test]
    public async Task EnvVarName_UppercasesAndReplacesDashes()
    {
        await Assert.That(ModelSourceResolver.EnvVarName("crisperwhisper2-small-int8"))
            .IsEqualTo("MYASR_MODEL_SOURCE_CRISPERWHISPER2_SMALL_INT8");
    }

    [Test]
    public async Task NoOverrides_ReturnsDescriptorDefault()
    {
        var resolver = new ModelSourceResolver(null);
        await Assert.That(resolver.ResolveBaseUri(Model)).IsEqualTo(Model.DefaultBaseUri);
    }

    [Test]
    public async Task SettingsOverride_BeatsDefault()
    {
        var resolver = new ModelSourceResolver(new Dictionary<string, string>
        {
            [Model.Id] = @"D:\models\gigaam"
        });
        await Assert.That(resolver.ResolveBaseUri(Model)).IsEqualTo(@"D:\models\gigaam");
    }

    [Test]
    public async Task BlankSettingsValue_IsIgnored()
    {
        var resolver = new ModelSourceResolver(new Dictionary<string, string> { [Model.Id] = "  " });
        await Assert.That(resolver.ResolveBaseUri(Model)).IsEqualTo(Model.DefaultBaseUri);
    }

    [Test]
    [NotInParallel("env-var-mutex")]
    public async Task EnvironmentOverride_BeatsSettings()
    {
        var name = ModelSourceResolver.EnvVarName(Model.Id);
        var saved = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, "https://example.test/models/");
            var resolver = new ModelSourceResolver(new Dictionary<string, string>
            {
                [Model.Id] = @"D:\models\gigaam"
            });
            await Assert.That(resolver.ResolveBaseUri(Model)).IsEqualTo("https://example.test/models/");
        }
        finally { Environment.SetEnvironmentVariable(name, saved); }
    }

    [Test]
    public async Task SettingsForAnotherModel_DoNotLeak()
    {
        var resolver = new ModelSourceResolver(new Dictionary<string, string> { ["some-other-id"] = @"D:\x" });
        await Assert.That(resolver.ResolveBaseUri(Model)).IsEqualTo(Model.DefaultBaseUri);
    }
}
