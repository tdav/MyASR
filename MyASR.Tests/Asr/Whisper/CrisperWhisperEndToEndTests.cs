using MyASR.Asr;
using MyASR.Asr.Whisper;
using MyASR.Audio;
using MyASR.Models;

namespace MyASR.Tests.Asr.Whisper;

/// <summary>
/// Runs the real ONNX model. Skipped unless both the model folder and a Russian sample exist,
/// mirroring how the GigaAM reference tests treat missing model files.
///
/// To run it:
///   set MYASR_MODELS_DIR to a root containing crisperwhisper2-small-int8/
///   place a 16 kHz mono WAV of Russian speech at MyASR.Tests/Assets/russian_sample.wav
///   place its expected transcript at MyASR.Tests/Assets/russian_sample.txt
/// </summary>
public class CrisperWhisperEndToEndTests
{
    private static readonly ModelDescriptor Model = ModelCatalog.CrisperWhisperSmallInt8;

    private static string? ModelFolder()
    {
        var root = Environment.GetEnvironmentVariable("MYASR_MODELS_DIR");
        if (string.IsNullOrWhiteSpace(root)) return null;
        var paths = new ModelPaths(root, Model);
        foreach (var f in Model.Files)
            if (!File.Exists(paths.File(f.LocalName))) return null;
        return paths.Root;
    }

    private static string SamplePath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "russian_sample.wav");

    private static string ExpectedPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "russian_sample.txt");

    private static string ReferenceWavPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "reference.wav");

    private static bool CanRun() => ModelFolder() is not null && File.Exists(SamplePath);

    [Test]
    [Skip("Требуются файлы модели и MyASR.Tests/Assets/russian_sample.wav")]
    public async Task Transcribes_RussianSpeech()
    {
        if (!CanRun()) return;

        var root = Environment.GetEnvironmentVariable("MYASR_MODELS_DIR")!;
        using var engine = WhisperOnnxEngine.Create(new ModelPaths(root, Model));

        var pcm = WavReader.ReadMono16kFloat(SamplePath);
        var result = engine.Transcribe(pcm, TimeSpan.FromSeconds(pcm.Length / 16000.0));

        await Assert.That(string.IsNullOrWhiteSpace(result.Text)).IsFalse();
        await Assert.That(ContainsCyrillic(result.Text)).IsTrue();

        if (File.Exists(ExpectedPath))
        {
            var expected = Normalise(File.ReadAllText(ExpectedPath));
            var actual = Normalise(result.Text);
            await Assert.That(WordErrorRate(expected, actual)).IsLessThan(0.30);
        }
    }

    [Test]
    [Skip("Требуются файлы модели и MyASR.Tests/Assets/russian_sample.wav")]
    public async Task IntendedMode_DoesNotEmitVocalEventMarkers()
    {
        if (!CanRun()) return;

        var root = Environment.GetEnvironmentVariable("MYASR_MODELS_DIR")!;
        using var engine = WhisperOnnxEngine.Create(new ModelPaths(root, Model));

        var pcm = WavReader.ReadMono16kFloat(SamplePath);
        var text = engine.Transcribe(pcm, TimeSpan.FromSeconds(pcm.Length / 16000.0)).Text;

        foreach (var marker in new[] { "[UM]", "[UH]", "[laughter]", "<|", "[intended_" })
            await Assert.That(text).DoesNotContain(marker);
    }

    /// <summary>
    /// Does not require a Russian sample: it runs the real encoder/decoder pipeline on the
    /// existing synthetic <c>reference.wav</c> just to prove the engine constructs, decodes without
    /// throwing, and that the shared decoder session plus a per-call KV cache produce the same
    /// output twice in a row on the same engine instance. It cannot assert a transcript because
    /// reference.wav has no speech.
    /// </summary>
    [Test]
    [Skip("Требуется каталог модели: установите MYASR_MODELS_DIR на корень с crisperwhisper2-small-int8/")]
    public async Task Transcribe_ReferenceWav_TwiceOnSameEngine_ProducesAgreeingResults()
    {
        if (ModelFolder() is null) return;

        var root = Environment.GetEnvironmentVariable("MYASR_MODELS_DIR")!;
        using var engine = WhisperOnnxEngine.Create(new ModelPaths(root, Model));

        var pcm = WavReader.ReadMono16kFloat(ReferenceWavPath);
        var duration = TimeSpan.FromSeconds(pcm.Length / 16000.0);

        var first = engine.Transcribe(pcm, duration);
        await Assert.That(first).IsNotNull();
        await Assert.That(first.Text).IsNotNull();

        var second = engine.Transcribe(pcm, duration);
        await Assert.That(second).IsNotNull();
        await Assert.That(second.Text).IsEqualTo(first.Text);
    }

    private static bool ContainsCyrillic(string s) => s.Any(c => c is >= '\u0400' and <= '\u04FF');

    private static string[] Normalise(string s) =>
        s.ToLowerInvariant()
         .Where(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c))
         .Aggregate(new System.Text.StringBuilder(), (sb, c) => sb.Append(c))
         .ToString()
         .Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Levenshtein distance over words, divided by the reference length.</summary>
    private static double WordErrorRate(string[] reference, string[] hypothesis)
    {
        if (reference.Length == 0) return hypothesis.Length == 0 ? 0 : 1;

        var previous = new int[hypothesis.Length + 1];
        var current = new int[hypothesis.Length + 1];
        for (var j = 0; j <= hypothesis.Length; j++) previous[j] = j;

        for (var i = 1; i <= reference.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= hypothesis.Length; j++)
            {
                var cost = reference[i - 1] == hypothesis[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }

        return previous[hypothesis.Length] / (double)reference.Length;
    }
}
