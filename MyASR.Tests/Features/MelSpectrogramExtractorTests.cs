using System.IO;
using MyASR.Features;

namespace MyASR.Tests.Features;

public class MelSpectrogramExtractorTests
{
    [Test]
    public async Task Extract_ZeroSilence_ProducesSmallLogValues()
    {
        var fe = new MelSpectrogramExtractor();
        var pcm = new float[16000];                 // 1 sec silence
        var feats = fe.Extract(pcm);
        // 64 mels, T = (16000 - 320) / 160 + 1 = 99
        await Assert.That(feats.GetLength(0)).IsEqualTo(99);
        await Assert.That(feats.GetLength(1)).IsEqualTo(64);
        // log(0 + 1e-9) ≈ -20.72; expect ALL frames near that value
        for (var t = 0; t < feats.GetLength(0); t++)
            for (var m = 0; m < feats.GetLength(1); m++)
                await Assert.That(feats[t, m]).IsLessThan(-15f);
    }

    [Test]
    public async Task Extract_PureSine440Hz_HasPeakInLowMelBins()
    {
        var fe = new MelSpectrogramExtractor();
        var pcm = new float[16000];
        for (var i = 0; i < pcm.Length; i++)
            pcm[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * i / 16000.0));
        var feats = fe.Extract(pcm);
        var midFrame = feats.GetLength(0) / 2;
        var argmax = 0;
        var max = float.MinValue;
        for (var m = 0; m < 64; m++)
            if (feats[midFrame, m] > max) { max = feats[midFrame, m]; argmax = m; }
        // 440 Hz sits in the lowest ~16 mel bins on a 0-8000 Hz htk scale
        await Assert.That(argmax).IsLessThan(15);
    }

    [Test]
    public async Task Extract_MatchesGigaamPython_ForReferenceWav()
    {
        var wav = Path.Combine(AppContext.BaseDirectory, "Assets", "reference.wav");
        var bin = Path.Combine(AppContext.BaseDirectory, "Assets", "reference_mel.bin");
        if (!File.Exists(wav) || !File.Exists(bin))
        {
            await Assert.That(true).IsTrue(); // skip — golden artefacts absent
            return;
        }

        var pcm = MyASR.Audio.WavReader.ReadMono16kFloat(wav);
        var fe  = new MelSpectrogramExtractor();
        var ours = fe.Extract(pcm);

        using var fs = File.OpenRead(bin);
        var br = new BinaryReader(fs);
        var t  = br.ReadUInt32();
        var m  = br.ReadUInt32();
        await Assert.That(t).IsEqualTo((uint)ours.GetLength(0));
        await Assert.That(m).IsEqualTo((uint)ours.GetLength(1));

        var maxDiff = 0f;
        for (var i = 0; i < t; i++)
            for (var j = 0; j < m; j++)
            {
                var refIj = br.ReadSingle();
                maxDiff = Math.Max(maxDiff, Math.Abs(ours[i, j] - refIj));
            }
        await Assert.That(maxDiff).IsLessThan(1e-3f);
    }
}
