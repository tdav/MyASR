using System.IO;
using MyASR.Features;

namespace MyASR.Tests.Features;

public class MelSpectrogramExtractorTests
{
    [Test]
    public async Task Extract_PulsePairFrames_MatchTorchaudio()
    {
        var samples = new float[480];
        samples[80] = 0.25f;
        samples[260] = -0.375f;
        samples[390] = 0.125f;
        var features = new MelSpectrogramExtractor().Extract(samples);

        // torchaudio 2.10 MelSpectrogram(16000, n_fft=320, hop_length=160,
        // n_mels=64, center=False), followed by clamp(1e-9, 1e9).log().
        int[] bands = [0, 3, 19, 37, 63];
        float[,] expected = {
            { -4.3839283f, -5.582696f, -3.5765378f, -2.6043508f, -1.674695f },
            { -3.7510247f, -3.6699793f, -2.3723717f, -1.7622691f, -0.7523204f },
        };
        for (var frame = 0; frame < 2; frame++)
            for (var i = 0; i < bands.Length; i++)
                await Assert.That(features[frame, bands[i]]).IsEqualTo(expected[frame, i]).Within(1e-4f);
    }

    [Test]
    public async Task Extract_ZeroSilence_ProducesSmallLogValues()
    {
        var fe = new MelSpectrogramExtractor();
        var pcm = new float[16000];                 // 1 sec silence
        var feats = fe.Extract(pcm);
        // 64 mels, T = (16000 - 320) / 160 + 1 = 99
        await Assert.That(feats.GetLength(0)).IsEqualTo(99);
        await Assert.That(feats.GetLength(1)).IsEqualTo(64);
        // log(1e-9) ≈ -20.72; expect ALL frames near that value
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
    public async Task Extract_MatchesTorchaudio_ForReferenceWav()
    {
        var wav = Path.Combine(AppContext.BaseDirectory, "Assets", "reference.wav");
        var bin = Path.Combine(AppContext.BaseDirectory, "Assets", "reference_mel.bin");
        if (!File.Exists(bin))
            Skip.Test("Generate reference_mel.bin with tools/generate_reference.py.");

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
        var maxSignalDiff = 0f;
        double totalDiff = 0;
        for (var i = 0; i < t; i++)
            for (var j = 0; j < m; j++)
            {
                var expected = br.ReadSingle();
                var error = Math.Abs(ours[i, j] - expected);
                totalDiff += error;
                maxDiff = Math.Max(maxDiff, error);
                if (expected >= Math.Log(1e-5))
                    maxSignalDiff = Math.Max(maxSignalDiff, error);
            }

        // Float32 FFT round-off dominates near spectral nulls in this chirp.
        // Bound those bins too, and keep a tighter tolerance for signal-bearing bands.
        await Assert.That(maxSignalDiff).IsLessThan(1e-3f);
        await Assert.That(maxDiff).IsLessThan(0.05f);
        await Assert.That(totalDiff / (t * m)).IsLessThan(0.002);
    }
}
