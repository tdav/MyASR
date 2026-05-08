using MyASR.Models;

namespace MyASR.Tests.Models;

public class ModelPathsTests
{
    [Test]
    public async Task Default_PointsTo_LocalAppData_MyASR_Models()
    {
        Environment.SetEnvironmentVariable("MYASR_MODELS_DIR", null);
        var paths = ModelPaths.Default();
        await Assert.That(paths.Root).EndsWith("MyASR\\models");
    }

    [Test]
    public async Task EnvironmentOverride_TakesPrecedence()
    {
        Environment.SetEnvironmentVariable("MYASR_MODELS_DIR", @"C:\my-override");
        try
        {
            var paths = ModelPaths.Default();
            await Assert.That(paths.Root).IsEqualTo(@"C:\my-override");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MYASR_MODELS_DIR", null);
        }
    }

    [Test]
    public async Task EncoderPath_UsesCorrectFileName()
    {
        var paths = new ModelPaths(@"C:\m");
        await Assert.That(paths.Encoder).IsEqualTo(@"C:\m\v3_rnnt_encoder.onnx");
    }

    [Test]
    public async Task DecoderJointVocab_PathsAreCorrect()
    {
        var paths = new ModelPaths(@"C:\m");
        await Assert.That(paths.Decoder).IsEqualTo(@"C:\m\v3_rnnt_decoder.onnx");
        await Assert.That(paths.Joint).IsEqualTo(@"C:\m\v3_rnnt_joint.onnx");
        await Assert.That(paths.Vocab).IsEqualTo(@"C:\m\v3_vocab.txt");
    }
}
