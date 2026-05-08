using System.IO;
using MyASR.Asr;

namespace MyASR.Tests.Asr;

public class VocabularyTests
{
    private static string AssetPath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Assets", name);

    [Test]
    public async Task Load_ParsesAll34Tokens()
    {
        var vocab = Vocabulary.Load(AssetPath("v3_vocab.txt"));
        await Assert.That(vocab.Count).IsEqualTo(34);
    }

    [Test]
    public async Task Load_BlankIndexIs33()
    {
        var vocab = Vocabulary.Load(AssetPath("v3_vocab.txt"));
        await Assert.That(vocab.BlankIndex).IsEqualTo(33);
    }

    [Test]
    public async Task Load_FirstTokenIsSpaceLikeUnderscore()
    {
        var vocab = Vocabulary.Load(AssetPath("v3_vocab.txt"));
        await Assert.That(vocab[0]).IsEqualTo("▁");
    }

    [Test]
    public async Task Decode_ConvertsTokenIdsToText_AndReplacesUnderscoreWithSpace()
    {
        var vocab = Vocabulary.Load(AssetPath("v3_vocab.txt"));
        var text = vocab.Decode([0, 16, 17, 9, 3, 6, 19]);
        await Assert.That(text).IsEqualTo(" привет");
    }

    [Test]
    public async Task Decode_IgnoresBlankToken()
    {
        var vocab = Vocabulary.Load(AssetPath("v3_vocab.txt"));
        var text = vocab.Decode([33, 16, 33, 17, 33]);
        await Assert.That(text).IsEqualTo("пр");
    }
}
