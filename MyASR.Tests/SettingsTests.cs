using System.Text.Json;
using MyASR;
using MyASR.Models;

namespace MyASR.Tests;

public class SettingsTests
{
    [Test]
    public async Task Defaults_SelectTheCatalogueDefaultModel()
    {
        var s = new UserSettings();
        await Assert.That(s.ModelId).IsEqualTo(ModelCatalog.Default.Id);
        await Assert.That(s.ModelSources).IsEmpty();
    }

    [Test]
    public async Task ModelId_RoundTripsThroughJson()
    {
        var s = new UserSettings { ModelId = "crisperwhisper2-small-int8" };
        var back = JsonSerializer.Deserialize<UserSettings>(JsonSerializer.Serialize(s))!;
        await Assert.That(back.ModelId).IsEqualTo("crisperwhisper2-small-int8");
    }

    [Test]
    public async Task ModelSources_RoundTripThroughJson()
    {
        var s = new UserSettings();
        s.ModelSources["some-model"] = @"D:\models\some-model";
        var back = JsonSerializer.Deserialize<UserSettings>(JsonSerializer.Serialize(s))!;
        await Assert.That(back.ModelSources["some-model"]).IsEqualTo(@"D:\models\some-model");
    }

    [Test]
    public async Task OldSettingsFile_WithoutModelFields_DeserializesToDefaults()
    {
        const string legacy = """{"MicDeviceId":"abc","Hotkey":"Ctrl+Shift+X","GlobalHotkey":true}""";
        var s = JsonSerializer.Deserialize<UserSettings>(legacy)!;
        await Assert.That(s.ModelId).IsEqualTo(ModelCatalog.Default.Id);
        await Assert.That(s.ModelSources).IsEmpty();
        await Assert.That(s.MicDeviceId).IsEqualTo("abc");
    }

    [Test]
    public async Task UnknownModelId_ResolvesToTheDefaultDescriptor()
    {
        var s = new UserSettings { ModelId = "was-removed-in-a-later-release" };
        await Assert.That(ModelCatalog.ById(s.ModelId).Id).IsEqualTo(ModelCatalog.Default.Id);
    }
}
