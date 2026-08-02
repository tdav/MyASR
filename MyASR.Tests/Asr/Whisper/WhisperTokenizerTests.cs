using System.Text;
using System.Text.Json;
using MyASR.Asr.Whisper;

namespace MyASR.Tests.Asr.Whisper;

public class WhisperTokenizerTests
{
    /// <summary>
    /// Builds a miniature byte-level BPE vocabulary on the fly so the tokenizer can be tested
    /// without shipping the model's 51896-entry vocab.json.
    /// </summary>
    private static string WriteVocab(string dir, IEnumerable<string> tokens)
    {
        var map = new Dictionary<string, int>();
        var i = 0;
        foreach (var t in tokens) map[t] = i++;
        var path = Path.Combine(dir, "vocab.json");
        File.WriteAllText(path, JsonSerializer.Serialize(map));
        return path;
    }

    private static string WriteAdded(string dir, Dictionary<string, int> added)
    {
        var path = Path.Combine(dir, "added_tokens.json");
        File.WriteAllText(path, JsonSerializer.Serialize(added));
        return path;
    }

    /// <summary>Encodes raw UTF-8 bytes the way GPT-2 byte-level BPE stores them in vocab.json.</summary>
    private static string AsBpeToken(string text)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(text))
            sb.Append(ByteLevelBpe.ByteToUnicode[b]);
        return sb.ToString();
    }

    private static string NewTempDir()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "MyASR-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(tmp);
        return tmp;
    }

    [Test]
    public async Task ByteMaps_AreInverses_AndCoverAll256Bytes()
    {
        await Assert.That(ByteLevelBpe.ByteToUnicode.Count).IsEqualTo(256);
        for (var b = 0; b < 256; b++)
        {
            var c = ByteLevelBpe.ByteToUnicode[(byte)b];
            await Assert.That(ByteLevelBpe.UnicodeToByte[c]).IsEqualTo((byte)b);
        }
    }

    [Test]
    public async Task Decode_ReassemblesCyrillicFromMultiTokenUtf8()
    {
        var dir = NewTempDir();
        try
        {
            // "привет" split across two tokens, each holding raw UTF-8 bytes.
            var vocab = WriteVocab(dir, [AsBpeToken("при"), AsBpeToken("вет")]);
            var added = WriteAdded(dir, []);
            var tok = WhisperTokenizer.Load(vocab, added);

            await Assert.That(tok.Decode([0, 1])).IsEqualTo("привет");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public async Task Decode_SplitsMidCodepoint_StillYieldsValidText()
    {
        var dir = NewTempDir();
        try
        {
            // Split the two bytes of 'п' (0xD0 0xBF) across two tokens.
            var first = ByteLevelBpe.ByteToUnicode[0xD0].ToString();
            var second = ByteLevelBpe.ByteToUnicode[0xBF].ToString();
            var vocab = WriteVocab(dir, [first, second]);
            var tok = WhisperTokenizer.Load(vocab, WriteAdded(dir, []));

            await Assert.That(tok.Decode([0, 1])).IsEqualTo("п");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public async Task Decode_SkipsAngleSpecialTokens()
    {
        var dir = NewTempDir();
        try
        {
            var vocab = WriteVocab(dir, [AsBpeToken("да")]);
            var added = WriteAdded(dir, new Dictionary<string, int>
            {
                ["<|startoftranscript|>"] = 1,
                ["<|ru|>"] = 2,
                ["<|endoftext|>"] = 3
            });
            var tok = WhisperTokenizer.Load(vocab, added);

            await Assert.That(tok.Decode([1, 2, 0, 3])).IsEqualTo("да");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public async Task Decode_SkipsBracketVocalEventTokens()
    {
        var dir = NewTempDir();
        try
        {
            var vocab = WriteVocab(dir, [AsBpeToken("да")]);
            var added = WriteAdded(dir, new Dictionary<string, int>
            {
                ["[UM]"] = 1,
                ["[laughter]"] = 2,
                ["[intended_3]"] = 3
            });
            var tok = WhisperTokenizer.Load(vocab, added);

            await Assert.That(tok.Decode([1, 0, 2, 3])).IsEqualTo("да");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public async Task Decode_UnknownId_IsIgnoredRatherThanThrowing()
    {
        var dir = NewTempDir();
        try
        {
            var tok = WhisperTokenizer.Load(WriteVocab(dir, [AsBpeToken("да")]), WriteAdded(dir, []));
            await Assert.That(tok.Decode([0, 9999])).IsEqualTo("да");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public async Task IdOf_FindsAddedTokens_AndTryIdOfReportsMisses()
    {
        var dir = NewTempDir();
        try
        {
            var tok = WhisperTokenizer.Load(
                WriteVocab(dir, [AsBpeToken("да")]),
                WriteAdded(dir, new Dictionary<string, int> { ["<|ru|>"] = 50263 }));

            await Assert.That(tok.IdOf("<|ru|>")).IsEqualTo(50263);
            await Assert.That(tok.TryIdOf("<|nope|>", out _)).IsFalse();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public async Task IdOf_MissingToken_Throws()
    {
        var dir = NewTempDir();
        try
        {
            var tok = WhisperTokenizer.Load(WriteVocab(dir, [AsBpeToken("да")]), WriteAdded(dir, []));
            await Assert.That(() => tok.IdOf("<|missing|>")).Throws<KeyNotFoundException>();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
