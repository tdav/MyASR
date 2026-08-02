using MyASR.Asr;
using MyASR.Features;
using MyASR.Models;

namespace MyASR.Tests.Asr;

public class RnnTGreedyDecoderTests
{
    private static bool ModelsPresent()
    {
        var p = new ModelPaths(ModelRoot.Resolve(), ModelCatalog.Default);
        return File.Exists(p.File(GigaAmFiles.Encoder)) && File.Exists(p.File(GigaAmFiles.Decoder))
            && File.Exists(p.File(GigaAmFiles.Joint))   && File.Exists(p.File(GigaAmFiles.Vocab));
    }

    [Test]
    public async Task Decode_ReferenceWav_ProducesNonEmptyText()
    {
        if (!ModelsPresent())
        {
            await Assert.That(true).IsTrue(); // skip when models absent
            return;
        }
        var wav = Path.Combine(AppContext.BaseDirectory, "Assets", "reference.wav");
        if (!File.Exists(wav)) { await Assert.That(true).IsTrue(); return; }

        var pcm   = MyASR.Audio.WavReader.ReadMono16kFloat(wav);
        var mel   = new MelSpectrogramExtractor().Extract(pcm);
        var paths = new ModelPaths(ModelRoot.Resolve(), ModelCatalog.Default);
        using var dec = new RnnTGreedyDecoder(
            paths.File(GigaAmFiles.Encoder),
            paths.File(GigaAmFiles.Decoder),
            paths.File(GigaAmFiles.Joint),
            Vocabulary.Load(paths.File(GigaAmFiles.Vocab)));
        var res = dec.Decode(mel, TimeSpan.FromSeconds(pcm.Length / 16000.0));

        await Assert.That(res.Text).IsNotEmpty();
    }
}
