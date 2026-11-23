using MyASR.Asr;
using MyASR.Audio;
using MyASR.Models;

namespace MyASR.Tests.Asr;

public class RnnTGreedyDecoderTests
{
    [Test]
    [Arguments("gigaam-v3-rnnt")]
    [Arguments("gigaam-v3-e2e-rnnt-int8")]
    public async Task Decode_SyntheticChirp_ProducesNoWords(string modelId)
    {
        var model = ModelCatalog.ById(modelId);
        var paths = new ModelPaths(ModelRoot.Resolve(), model);
        if (!model.Files.All(file => File.Exists(paths.File(file.LocalName))))
            Skip.Test("Requires local GigaAM model files.");

        var wav = Path.Combine(AppContext.BaseDirectory, "Assets", "reference.wav");
        var pcm = WavReader.ReadMono16kFloat(wav);
        using var engine = GigaAmRnnTEngine.Create(paths);
        var result = engine.Transcribe(pcm, TimeSpan.FromSeconds(pcm.Length / 16000.0));

        await Assert.That(result.Text).IsEqualTo(string.Empty);
    }
}
