using MyASR;
using MyASR.Asr;
using MyASR.Models;

namespace MyASR.Tests;

public class ModelSelectionPolicyTests
{
    [Test]
    public async Task CanSwitch_OnlyWhenIdle()
    {
        await Assert.That(ModelSelectionPolicy.CanSwitch(AsrState.Idle)).IsTrue();
        await Assert.That(ModelSelectionPolicy.CanSwitch(AsrState.Recording)).IsFalse();
        await Assert.That(ModelSelectionPolicy.CanSwitch(AsrState.Decoding)).IsFalse();
    }

    [Test]
    public async Task StatusText_RegisteredHotkey_MentionsIt()
    {
        var text = ModelSelectionPolicy.StatusText(ModelCatalog.GigaAmV3, true, "Ctrl+Shift+X");
        await Assert.That(text).Contains("Ctrl+Shift+X");
        await Assert.That(text).Contains("GigaAM v3");
    }

    [Test]
    public async Task StatusText_UnregisteredHotkey_SaysUnavailable()
    {
        var text = ModelSelectionPolicy.StatusText(ModelCatalog.GigaAmV3, false, "Ctrl+Shift+X");
        await Assert.That(text).Contains("PTT недоступен");
    }

    [Test]
    public async Task StatusText_LicenceNote_IsShownWhenPresent()
    {
        var restricted = ModelCatalog.GigaAmV3 with { LicenceNote = "только для исследований" };
        var text = ModelSelectionPolicy.StatusText(restricted, true, "F9");
        await Assert.That(text).Contains("только для исследований");
    }

    [Test]
    public async Task StatusText_NoLicenceNote_OmitsTheParenthesisedNote()
    {
        var note = "только для исследований";
        var withNote = ModelSelectionPolicy.StatusText(
            ModelCatalog.GigaAmV3 with { LicenceNote = note }, true, "F9");
        var withoutNote = ModelSelectionPolicy.StatusText(ModelCatalog.GigaAmV3, true, "F9");

        await Assert.That(withNote).Contains($"({note})");
        await Assert.That(withoutNote).DoesNotContain(note);
        await Assert.That(withoutNote.Length).IsLessThan(withNote.Length);
    }

    [Test]
    public async Task StatusText_EmptyLicenceNote_IsTreatedAsAbsent()
    {
        var text = ModelSelectionPolicy.StatusText(
            ModelCatalog.GigaAmV3 with { LicenceNote = "" }, true, "F9");
        await Assert.That(text).IsEqualTo(
            ModelSelectionPolicy.StatusText(ModelCatalog.GigaAmV3, true, "F9"));
    }
}
