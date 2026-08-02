using MyASR.Models;

namespace MyASR.Tests.Models;

public class ModelPathsTests
{
    [Test]
    [NotInParallel("env-var-mutex")]
    public async Task ModelRoot_Default_PointsTo_LocalAppData_MyASR_Models()
    {
        var saved = Environment.GetEnvironmentVariable("MYASR_MODELS_DIR");
        try
        {
            Environment.SetEnvironmentVariable("MYASR_MODELS_DIR", null);
            await Assert.That(ModelRoot.Resolve()).EndsWith("MyASR\\models");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MYASR_MODELS_DIR", saved);
        }
    }

    [Test]
    [NotInParallel("env-var-mutex")]
    public async Task ModelRoot_EnvironmentOverride_TakesPrecedence()
    {
        var saved = Environment.GetEnvironmentVariable("MYASR_MODELS_DIR");
        try
        {
            Environment.SetEnvironmentVariable("MYASR_MODELS_DIR", @"C:\my-override");
            await Assert.That(ModelRoot.Resolve()).IsEqualTo(@"C:\my-override");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MYASR_MODELS_DIR", saved);
        }
    }

    [Test]
    [NotInParallel("env-var-mutex")]
    public async Task ModelRoot_BlankEnvironmentValue_IsIgnored()
    {
        var saved = Environment.GetEnvironmentVariable("MYASR_MODELS_DIR");
        try
        {
            Environment.SetEnvironmentVariable("MYASR_MODELS_DIR", "   ");
            await Assert.That(ModelRoot.Resolve()).EndsWith("MyASR\\models");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MYASR_MODELS_DIR", saved);
        }
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
