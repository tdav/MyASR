using MyASR.Models;

namespace MyASR.Tests.Models;

public class CrisperWhisperCatalogTests
{
    [Test]
    public async Task Catalogue_ContainsBothPrecisions()
    {
        var ids = ModelCatalog.All.Select(m => m.Id).ToList();
        await Assert.That(ids).Contains("crisperwhisper2-small-int8");
        await Assert.That(ids).Contains("crisperwhisper2-small-fp32");
    }

    [Test]
    public async Task BothEntries_UseTheWhisperEngine()
    {
        foreach (var id in new[] { "crisperwhisper2-small-int8", "crisperwhisper2-small-fp32" })
            await Assert.That(ModelCatalog.ById(id).Kind).IsEqualTo(AsrEngineKind.Whisper);
    }

    [Test]
    public async Task BothEntries_CarryTheNonCommercialLicenceNote()
    {
        foreach (var id in new[] { "crisperwhisper2-small-int8", "crisperwhisper2-small-fp32" })
        {
            var note = ModelCatalog.ById(id).LicenceNote;
            await Assert.That(string.IsNullOrWhiteSpace(note)).IsFalse();
            await Assert.That(note!).Contains("некоммерч");
        }
    }

    [Test]
    public async Task BothEntries_ListEveryFileTheEngineOpens()
    {
        foreach (var id in new[] { "crisperwhisper2-small-int8", "crisperwhisper2-small-fp32" })
        {
            var names = ModelCatalog.ById(id).Files.Select(f => f.LocalName).ToList();
            await Assert.That(names).Contains(CrisperWhisperFiles.Encoder);
            await Assert.That(names).Contains(CrisperWhisperFiles.DecoderMerged);
            await Assert.That(names).Contains(CrisperWhisperFiles.Vocab);
            await Assert.That(names).Contains(CrisperWhisperFiles.AddedTokens);
            await Assert.That(names).Contains(CrisperWhisperFiles.GenerationConfig);
        }
    }

    [Test]
    public async Task Int8_IsSmallerThanFp32()
    {
        var int8 = ModelCatalog.ById("crisperwhisper2-small-int8").TotalBytes;
        var fp32 = ModelCatalog.ById("crisperwhisper2-small-fp32").TotalBytes;
        await Assert.That(int8).IsLessThan(fp32);
    }

    [Test]
    public async Task Default_IsStillGigaAm_BecauseRussianQualityIsUnverified()
    {
        await Assert.That(ModelCatalog.Default.Kind).IsEqualTo(AsrEngineKind.RnnT);
        await Assert.That(ModelCatalog.Default.Id).IsEqualTo("gigaam-v3-e2e-rnnt-int8");
    }

    [Test]
    public async Task EachEntry_HasItsOwnFolder()
    {
        var folders = ModelCatalog.All.Select(m => m.FolderName).ToList();
        await Assert.That(folders.Distinct().Count()).IsEqualTo(folders.Count);
    }
}
