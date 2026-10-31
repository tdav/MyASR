using System.Text.Json;
using MyASR;
using MyASR.Audio;
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
        var json = JsonSerializer.Serialize(s, MyAsrJsonContext.Default.UserSettings);
        var back = JsonSerializer.Deserialize(json, MyAsrJsonContext.Default.UserSettings)!;
        await Assert.That(back.ModelId).IsEqualTo("crisperwhisper2-small-int8");
    }

    [Test]
    public async Task ModelSources_RoundTripThroughJson()
    {
        var s = new UserSettings();
        s.ModelSources["some-model"] = @"D:\models\some-model";
        var json = JsonSerializer.Serialize(s, MyAsrJsonContext.Default.UserSettings);
        var back = JsonSerializer.Deserialize(json, MyAsrJsonContext.Default.UserSettings)!;
        await Assert.That(back.ModelSources["some-model"]).IsEqualTo(@"D:\models\some-model");
    }

    [Test]
    public async Task OldSettingsFile_WithoutModelFields_DeserializesToDefaults()
    {
        const string legacy = """{"MicDeviceId":"abc","Hotkey":"Ctrl+Shift+X","GlobalHotkey":true}""";
        var s = JsonSerializer.Deserialize(legacy, MyAsrJsonContext.Default.UserSettings)!;
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

    [Test]
    public async Task VocabDictionary_DeserializesViaSourceGeneratedContext()
    {
        const string json = """{"hello":1,"world":2}""";
        var dict = JsonSerializer.Deserialize(json, MyAsrJsonContext.Default.DictionaryStringInt32)!;
        await Assert.That(dict["hello"]).IsEqualTo(1);
        await Assert.That(dict["world"]).IsEqualTo(2);
    }

    [Test]
    public async Task ToString_OnModelDescriptorAndDeviceInfo_ReturnsDisplayNamesForAotComboBoxes()
    {
        var dev = new DeviceInfo("dev-1", "USB Microphone");
        await Assert.That(dev.ToString()).IsEqualTo("USB Microphone");
        await Assert.That(ModelCatalog.Default.ToString()).IsEqualTo(ModelCatalog.Default.DisplayName);
    }
}
