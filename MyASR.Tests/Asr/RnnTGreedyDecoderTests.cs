using MyASR.Asr;
using MyASR.Features;
using MyASR.Models;

namespace MyASR.Tests.Asr;

public class RnnTGreedyDecoderTests
{
    private static bool ModelsPresent(ModelDescriptor model)
    {
        var p = new ModelPaths(ModelRoot.Resolve(), model);
        return File.Exists(p.File(GigaAmFiles.Encoder)) && File.Exists(p.File(GigaAmFiles.Decoder))
            && File.Exists(p.File(GigaAmFiles.Joint))   && File.Exists(p.File(GigaAmFiles.Vocab));
    }

    [Test]
    public async Task Decode_ReferenceWav_ProducesNonEmptyText()
    {
        if (!ModelsPresent(ModelCatalog.GigaAmV3))
        {
            await Assert.That(true).IsTrue(); // skip when models absent
            return;
        }
        var wav = Path.Combine(AppContext.BaseDirectory, "Assets", "reference.wav");
        if (!File.Exists(wav)) { await Assert.That(true).IsTrue(); return; }

        var pcm   = MyASR.Audio.WavReader.ReadMono16kFloat(wav);
        var mel   = new MelSpectrogramExtractor().Extract(pcm);
        var paths = new ModelPaths(ModelRoot.Resolve(), ModelCatalog.GigaAmV3);
        using var dec = new RnnTGreedyDecoder(
            paths.File(GigaAmFiles.Encoder),
            paths.File(GigaAmFiles.Decoder),
            paths.File(GigaAmFiles.Joint),
            Vocabulary.Load(paths.File(GigaAmFiles.Vocab)));
        var res = dec.Decode(mel, TimeSpan.FromSeconds(pcm.Length / 16000.0));

        await Assert.That(res.Text).IsNotEmpty();
    }

    [Test]
    public async Task Decode_PureToneReferenceWav_WithE2eInt8Model_RunsCleanlyWithoutHallucination()
    {
        if (!ModelsPresent(ModelCatalog.GigaAmV3E2eInt8))
        {
            await Assert.That(true).IsTrue();
            return;
        }
        var wav = Path.Combine(AppContext.BaseDirectory, "Assets", "reference.wav");
        if (!File.Exists(wav)) { await Assert.That(true).IsTrue(); return; }

        var pcm   = MyASR.Audio.WavReader.ReadMono16kFloat(wav);
        var mel   = new MelSpectrogramExtractor().Extract(pcm);
        var paths = new ModelPaths(ModelRoot.Resolve(), ModelCatalog.GigaAmV3E2eInt8);
        using var dec = new RnnTGreedyDecoder(
            paths.File(GigaAmFiles.Encoder),
            paths.File(GigaAmFiles.Decoder),
            paths.File(GigaAmFiles.Joint),
            Vocabulary.Load(paths.File(GigaAmFiles.Vocab)));
        var res = dec.Decode(mel, TimeSpan.FromSeconds(pcm.Length / 16000.0));

        // Assets/reference.wav is a synthetic 200 Hz pure sine tone; E2E RNN-T suppresses
        // non-speech tones and returns an empty transcript without throwing.
        await Assert.That(res.Text).IsEqualTo(string.Empty);
    }
}
