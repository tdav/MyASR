using MyASR.Models;

namespace MyASR.Tests.Models;

public class ModelCatalogTests
{
    [Test]
    public async Task All_IsNotEmpty_AndDefaultIsGigaAm()
    {
        await Assert.That(ModelCatalog.All).IsNotEmpty();
        await Assert.That(ModelCatalog.Default.Id).IsEqualTo("gigaam-v3-rnnt");
    }

    [Test]
    public async Task Ids_AreUnique()
    {
        var ids = ModelCatalog.All.Select(m => m.Id).ToList();
        await Assert.That(ids.Distinct().Count()).IsEqualTo(ids.Count);
    }

    [Test]
    public async Task FolderNames_AreUnique()
    {
        var folders = ModelCatalog.All.Select(m => m.FolderName).ToList();
        await Assert.That(folders.Distinct().Count()).IsEqualTo(folders.Count);
    }

    [Test]
    public async Task EveryDescriptor_HasFiles_AndPositiveTotalBytes()
    {
        foreach (var m in ModelCatalog.All)
        {
            await Assert.That(m.Files).IsNotEmpty();
            await Assert.That(m.TotalBytes).IsGreaterThan(0L);
        }
    }

    [Test]
    public async Task EveryDescriptor_HasNonBlankDefaultBaseUri()
    {
        foreach (var m in ModelCatalog.All)
            await Assert.That(string.IsNullOrWhiteSpace(m.DefaultBaseUri)).IsFalse();
    }

    [Test]
    public async Task ById_UnknownOrNull_FallsBackToDefault()
    {
        await Assert.That(ModelCatalog.ById("no-such-model").Id).IsEqualTo(ModelCatalog.Default.Id);
        await Assert.That(ModelCatalog.ById(null).Id).IsEqualTo(ModelCatalog.Default.Id);
    }

    [Test]
    public async Task ById_KnownId_ReturnsThatModel()
    {
        await Assert.That(ModelCatalog.ById("gigaam-v3-rnnt").Kind).IsEqualTo(AsrEngineKind.RnnT);
    }

    [Test]
    public async Task GigaAm_ListsTheFourExpectedFiles()
    {
        var names = ModelCatalog.GigaAmV3.Files.Select(f => f.LocalName).ToList();
        await Assert.That(names).Contains(GigaAmFiles.Encoder);
        await Assert.That(names).Contains(GigaAmFiles.Decoder);
        await Assert.That(names).Contains(GigaAmFiles.Joint);
        await Assert.That(names).Contains(GigaAmFiles.Vocab);
    }
}
