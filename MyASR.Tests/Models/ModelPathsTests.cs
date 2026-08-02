using MyASR.Models;

namespace MyASR.Tests.Models;

public class ModelPathsTests
{
    /// <summary>
    /// A stand-in for the process environment in which only the models-root variable is set.
    /// These tests hand it to <see cref="ModelRoot.Resolve(Func{string, string?})"/> rather than
    /// writing the real variable: it is process-global, and the end-to-end gates in
    /// <see cref="MyASR.Tests.Asr.Whisper.CrisperWhisperEndToEndTests"/> read it concurrently to
    /// find real artefacts. A write here would make those gates go inert and pass without testing
    /// anything.
    /// </summary>
    private static Func<string, string?> EnvWith(string? modelsDir) =>
        name => name == ModelRoot.EnvVarName ? modelsDir : null;

    [Test]
    public async Task ModelRoot_EnvVarName_IsTheDocumentedOne()
    {
        await Assert.That(ModelRoot.EnvVarName).IsEqualTo("MYASR_MODELS_DIR");
    }

    [Test]
    public async Task ModelRoot_Default_PointsTo_LocalAppData_MyASR_Models()
    {
        await Assert.That(ModelRoot.Resolve(EnvWith(null))).EndsWith("MyASR\\models");
    }

    [Test]
    public async Task ModelRoot_EnvironmentOverride_TakesPrecedence()
    {
        await Assert.That(ModelRoot.Resolve(EnvWith(@"C:\my-override"))).IsEqualTo(@"C:\my-override");
    }

    [Test]
    public async Task ModelRoot_BlankEnvironmentValue_IsIgnored()
    {
        await Assert.That(ModelRoot.Resolve(EnvWith("   "))).EndsWith("MyASR\\models");
    }

    [Test]
    public async Task Root_IsRootPlusModelFolder()
    {
        var paths = new ModelPaths(@"C:\m", ModelCatalog.GigaAmV3);
        await Assert.That(paths.Root).IsEqualTo(@"C:\m\gigaam-v3");
    }

    [Test]
    public async Task File_CombinesModelRootWithLocalName()
    {
        var paths = new ModelPaths(@"C:\m", ModelCatalog.GigaAmV3);
        await Assert.That(paths.File(GigaAmFiles.Encoder)).IsEqualTo(@"C:\m\gigaam-v3\v3_rnnt_encoder.onnx");
        await Assert.That(paths.File(GigaAmFiles.Vocab)).IsEqualTo(@"C:\m\gigaam-v3\v3_vocab.txt");
    }

    [Test]
    public async Task Model_IsExposedForDownstreamConsumers()
    {
        var paths = new ModelPaths(@"C:\m", ModelCatalog.GigaAmV3);
        await Assert.That(paths.Model.Id).IsEqualTo("gigaam-v3-rnnt");
    }

    [Test]
    public async Task TwoModels_DoNotShareAFolder()
    {
        var other = ModelCatalog.GigaAmV3 with { Id = "other", FolderName = "other-model" };
        var a = new ModelPaths(@"C:\m", ModelCatalog.GigaAmV3);
        var b = new ModelPaths(@"C:\m", other);
        await Assert.That(a.Root).IsNotEqualTo(b.Root);
    }
}
