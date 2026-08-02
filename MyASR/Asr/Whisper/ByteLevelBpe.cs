namespace MyASR.Asr.Whisper;

/// <summary>
/// GPT-2's reversible byte-to-unicode mapping. Token strings in vocab.json are not text —
/// they are raw bytes rendered as printable characters. Concatenating them directly produces
/// mojibake for anything outside ASCII, which is why Cyrillic needs this map.
/// </summary>
public static class ByteLevelBpe
{
    public static IReadOnlyDictionary<byte, char> ByteToUnicode { get; }
    public static IReadOnlyDictionary<char, byte> UnicodeToByte { get; }

    static ByteLevelBpe()
    {
        var printable = new List<int>();
        for (var b = '!'; b <= '~'; b++) printable.Add(b);
        for (var b = '¡'; b <= '¬'; b++) printable.Add(b);
        for (var b = '®'; b <= 'ÿ'; b++) printable.Add(b);

        var bytes = new List<int>(printable);
        var chars = new List<int>(printable);

        var next = 0;
        for (var b = 0; b < 256; b++)
        {
            if (printable.Contains(b)) continue;
            bytes.Add(b);
            chars.Add(256 + next);
            next++;
        }

        var toUnicode = new Dictionary<byte, char>(256);
        var toByte = new Dictionary<char, byte>(256);
        for (var i = 0; i < bytes.Count; i++)
        {
            var b = (byte)bytes[i];
            var c = (char)chars[i];
            toUnicode[b] = c;
            toByte[c] = b;
        }

        ByteToUnicode = toUnicode;
        UnicodeToByte = toByte;
    }
}
