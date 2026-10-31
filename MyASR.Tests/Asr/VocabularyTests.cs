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

    [Test]
    public async Task Decode_SentencePieceBpeSubwords_ReplacesLeadingUnderscoreAndSkipsUnk()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"bpe-vocab-{Guid.NewGuid()}.txt");
        try
        {
            File.WriteAllLines(tmp,
            [
                "<unk> 0",
                ", 1",
                "? 2",
                "▁Привет 3",
                "▁как 4",
                "▁дела 5",
                "<blk> 6"
            ]);
            var vocab = Vocabulary.Load(tmp);
            var text = vocab.Decode([0, 3, 1, 4, 5, 2, 6]);
            await Assert.That(text).IsEqualTo(" Привет, как дела?");
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }
}
