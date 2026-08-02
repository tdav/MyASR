using MyASR.Audio;
using MyASR.Features;

namespace MyASR.Tests.Features;

public class WhisperFeatureExtractorTests
{
    private const int Frames = 3000;
    private const int Mels = 80;

    private static (int frames, int mels, float[] data) LoadReferenceFeatures()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "crisperwhisper_features.bin");
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);
        var t = (int)br.ReadUInt32();
        var m = (int)br.ReadUInt32();
        var data = new float[t * m];
        for (var i = 0; i < data.Length; i++) data[i] = br.ReadSingle();
        return (t, m, data);
    }

    private static float[] LoadReferenceWav()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "reference.wav");
        return WavReader.ReadMono16kFloat(path);
    }

    [Test]
    public async Task HannWindowPeriodic_DiffersFromSymmetric_AndStartsAtZero()
    {
        var periodic = PowerSpectrum.HannWindowPeriodic(8);
        var symmetric = PowerSpectrum.HanningWindow(8);

        await Assert.That(periodic[0]).IsEqualTo(0f);
        await Assert.That(periodic[7]).IsNotEqualTo(symmetric[7]);
        await Assert.That(symmetric[7]).IsEqualTo(0f).Within(1e-6f);
    }

    [Test]
    public async Task Output_IsAlways3000By80_ForShortInput()
    {
        var feats = new WhisperFeatureExtractor().Extract(new float[1600]);
        await Assert.That(feats.GetLength(0)).IsEqualTo(Frames);
        await Assert.That(feats.GetLength(1)).IsEqualTo(Mels);
    }

    [Test]
    public async Task Output_IsAlways3000By80_ForExactlyThirtySeconds()
    {
        var feats = new WhisperFeatureExtractor().Extract(new float[480_000]);
        await Assert.That(feats.GetLength(0)).IsEqualTo(Frames);
        await Assert.That(feats.GetLength(1)).IsEqualTo(Mels);
    }

    [Test]
    public async Task Output_IsAlways3000By80_ForOverlongInput()
    {
        var feats = new WhisperFeatureExtractor().Extract(new float[600_000]);
        await Assert.That(feats.GetLength(0)).IsEqualTo(Frames);
        await Assert.That(feats.GetLength(1)).IsEqualTo(Mels);
    }

    [Test]
    public async Task DynamicRange_IsExactlyTwo_BecauseOfTheWhisperClamp()
    {
        var feats = new WhisperFeatureExtractor().Extract(LoadReferenceWav());
        var max = float.MinValue;
        var min = float.MaxValue;
        for (var t = 0; t < Frames; t++)
            for (var m = 0; m < Mels; m++)
            {
                max = Math.Max(max, feats[t, m]);
                min = Math.Min(min, feats[t, m]);
            }

        // log10 values are clamped to a span of 8 decades and then divided by 4, so the
        // spread is 2.0 regardless of how loud the input was. The absolute peak is not a
        // fixed number — it is (maxLog + 4) / 4, and maxLog depends on the signal.
        await Assert.That(max - min).IsEqualTo(2.0f).Within(1e-4f);
    }

    [Test]
    public async Task Matches_TransformersReferenceFeatures()
    {
        var (t, m, expected) = LoadReferenceFeatures();
        await Assert.That(t).IsEqualTo(Frames);
        await Assert.That(m).IsEqualTo(Mels);

        var actual = new WhisperFeatureExtractor().Extract(LoadReferenceWav());

        var worst = 0.0;
        for (var i = 0; i < Frames; i++)
            for (var j = 0; j < Mels; j++)
                worst = Math.Max(worst, Math.Abs(actual[i, j] - expected[i * Mels + j]));

        await Assert.That(worst).IsLessThan(1e-3);
    }

    [Test]
    public async Task SilentInput_ProducesFiniteValues()
    {
        var feats = new WhisperFeatureExtractor().Extract(new float[16_000]);
        for (var t = 0; t < Frames; t += 250)
            for (var m = 0; m < Mels; m += 20)
                await Assert.That(float.IsFinite(feats[t, m])).IsTrue();
    }
}
