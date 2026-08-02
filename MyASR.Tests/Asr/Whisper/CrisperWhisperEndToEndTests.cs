using System.Text.Json;
using MyASR.Asr;
using MyASR.Asr.Whisper;
using MyASR.Audio;
using MyASR.Models;

namespace MyASR.Tests.Asr.Whisper;

/// <summary>
/// Runs the real ONNX model. Gated at runtime exactly the way
/// <see cref="MyASR.Tests.Asr.RnnTGreedyDecoderTests"/> gates GigaAM: each test checks its
/// preconditions and, if they are missing, records a trivially-true assertion and returns rather
/// than being marked skipped, so the test is genuinely exercised whenever the artefacts are
/// present and stays inert (not merely "skipped") otherwise.
///
/// The artefacts are looked for under <see cref="ModelRoot.Resolve()"/> — MYASR_MODELS_DIR when
/// set, otherwise %LOCALAPPDATA%\MyASR\models — in that root's crisperwhisper2-small-int8/ folder.
///
/// To run the tests that need a Russian sample
/// (<see cref="Transcribes_RussianSpeech"/>, <see cref="IntendedMode_DoesNotEmitVocalEventMarkers"/>):
///   put the exported model in crisperwhisper2-small-int8/ under that root
///   place a 16 kHz mono WAV of Russian speech at MyASR.Tests/Assets/russian_sample.wav
///   place its expected transcript at MyASR.Tests/Assets/russian_sample.txt
///
/// <see cref="Transcribe_ReferenceWav_TwiceOnSameEngine_ProducesAgreeingResults"/> and
/// <see cref="Decode_CyrillicProbe_MatchesReferenceText"/> need only the model — they use the
/// existing synthetic reference.wav or the model's own vocabulary files, not a Russian sample.
/// </summary>
public class CrisperWhisperEndToEndTests
{
    private static readonly ModelDescriptor Model = ModelCatalog.CrisperWhisperSmallInt8;

    /// <summary>
    /// The models root, sampled once. Nothing in the suite writes MYASR_MODELS_DIR — the tests
    /// that cover <see cref="ModelRoot"/> pass it a stand-in accessor instead — so this stays the
    /// value the run started with, and these gates cannot be tricked into the inert branch by a
    /// concurrent write.
    /// </summary>
    private static readonly string Root = ModelRoot.Resolve();

    /// <summary>The model's paths when every artefact it declares is on disk, otherwise null.</summary>
    private static ModelPaths? ModelFiles()
    {
        var paths = new ModelPaths(Root, Model);
        foreach (var f in Model.Files)
            if (!File.Exists(paths.File(f.LocalName))) return null;
        return paths;
    }

    private static string SamplePath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "russian_sample.wav");

    private static string ExpectedPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "russian_sample.txt");

    private static string ReferenceWavPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "reference.wav");

    [Test]
    public async Task Transcribes_RussianSpeech()
    {
        if (ModelFiles() is not { } paths || !File.Exists(SamplePath))
        {
            await Assert.That(true).IsTrue(); // модель и/или russian_sample.wav отсутствуют — тест неактивен
            return;
        }

        using var engine = WhisperOnnxEngine.Create(paths);

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
    public async Task IntendedMode_DoesNotEmitVocalEventMarkers()
    {
        if (ModelFiles() is not { } paths || !File.Exists(SamplePath))
        {
            await Assert.That(true).IsTrue(); // модель и/или russian_sample.wav отсутствуют — тест неактивен
            return;
        }

        using var engine = WhisperOnnxEngine.Create(paths);

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
    public async Task Transcribe_ReferenceWav_TwiceOnSameEngine_ProducesAgreeingResults()
    {
        if (ModelFiles() is not { } paths)
        {
            await Assert.That(true).IsTrue(); // каталог модели отсутствует — тест неактивен
            return;
        }

        using var engine = WhisperOnnxEngine.Create(paths);

        var pcm = WavReader.ReadMono16kFloat(ReferenceWavPath);
        var duration = TimeSpan.FromSeconds(pcm.Length / 16000.0);

        var first = engine.Transcribe(pcm, duration);
        await Assert.That(first).IsNotNull();
        await Assert.That(first.Text).IsNotNull();

        var second = engine.Transcribe(pcm, duration);
        await Assert.That(second).IsNotNull();
        await Assert.That(second.Text).IsEqualTo(first.Text);
    }

    /// <summary>
    /// Loads the real 51896-entry <c>vocab.json</c>/<c>added_tokens.json</c> from the model folder
    /// (no ONNX session needed) and decodes the <c>cyrillic_probe.ids</c> captured from the
    /// reference tokenizer in <c>crisperwhisper_reference.json</c>, asserting the result equals
    /// <c>cyrillic_probe.text</c>. This is the byte-level BPE round-trip against real data that the
    /// design spec promised — <see cref="MyASR.Tests.Asr.Whisper.WhisperTokenizerTests"/> only ever
    /// exercises toy vocabularies the tests build themselves.
    /// </summary>
    [Test]
    public async Task Decode_CyrillicProbe_MatchesReferenceText()
    {
        if (ModelFiles() is not { } paths)
        {
            await Assert.That(true).IsTrue(); // каталог модели отсутствует — тест неактивен
            return;
        }

        var tokenizer = WhisperTokenizer.Load(
            paths.File(CrisperWhisperFiles.Vocab), paths.File(CrisperWhisperFiles.AddedTokens));

        var probe = LoadCyrillicProbe();
        var decoded = tokenizer.Decode(probe.ids);

        await Assert.That(decoded).IsEqualTo(probe.text);
    }

    private sealed record CyrillicProbe(string text, int[] ids);
    private sealed record ReferenceWithProbe(CyrillicProbe cyrillic_probe);

    private static CyrillicProbe LoadCyrillicProbe()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "crisperwhisper_reference.json");
        var reference = JsonSerializer.Deserialize<ReferenceWithProbe>(File.ReadAllText(path))!;
        return reference.cyrillic_probe;
    }

    private static bool ContainsCyrillic(string s) => s.Any(c => c is >= 'Ѐ' and <= 'ӿ');

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
