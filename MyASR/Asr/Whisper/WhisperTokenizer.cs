using System.IO;
using System.Text;
using System.Text.Json;

namespace MyASR.Asr.Whisper;

/// <summary>
/// Decode-only Whisper tokenizer. MyASR never encodes text, so merges.txt is not needed —
/// an id-to-token table plus the byte-level map is enough.
/// </summary>
public sealed class WhisperTokenizer
{
    private readonly Dictionary<int, string> idToToken;
    private readonly Dictionary<string, int> tokenToId;

    private WhisperTokenizer(Dictionary<int, string> idToToken, Dictionary<string, int> tokenToId)
    {
        this.idToToken = idToToken;
        this.tokenToId = tokenToId;
    }

    public static WhisperTokenizer Load(string vocabJsonPath, string addedTokensJsonPath)
    {
        var tokenToId = new Dictionary<string, int>(StringComparer.Ordinal);
        Merge(tokenToId, vocabJsonPath);
        if (File.Exists(addedTokensJsonPath)) Merge(tokenToId, addedTokensJsonPath);

        var idToToken = new Dictionary<int, string>(tokenToId.Count);
        foreach (var (token, id) in tokenToId) idToToken[id] = token;

        return new WhisperTokenizer(idToToken, tokenToId);
    }

    private static void Merge(Dictionary<string, int> into, string jsonPath)
    {
        var parsed = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(jsonPath))
                     ?? throw new InvalidDataException($"Не удалось прочитать словарь: {jsonPath}");
        foreach (var (token, id) in parsed) into[token] = id;
    }

    public int IdOf(string token) =>
        tokenToId.TryGetValue(token, out var id)
            ? id
            : throw new KeyNotFoundException($"Токен '{token}' отсутствует в словаре модели.");

    public bool TryIdOf(string token, out int id) => tokenToId.TryGetValue(token, out id);

    /// <summary>
    /// Turns generated ids into text. Special tokens (<c>&lt;|...|&gt;</c>) and vocal-event or
    /// mode tokens (<c>[UM]</c>, <c>[intended_3]</c>) are dropped; everything else is
    /// reassembled byte-wise and decoded as UTF-8.
    /// </summary>
    public string Decode(IReadOnlyList<int> ids)
    {
        var bytes = new List<byte>(ids.Count * 2);

        foreach (var id in ids)
        {
            if (!idToToken.TryGetValue(id, out var token)) continue;
            if (IsSpecial(token)) continue;

            foreach (var c in token)
                if (ByteLevelBpe.UnicodeToByte.TryGetValue(c, out var b))
                    bytes.Add(b);
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static bool IsSpecial(string token) =>
        (token.StartsWith("<|", StringComparison.Ordinal) && token.EndsWith("|>", StringComparison.Ordinal))
        || (token.StartsWith('[') && token.EndsWith(']'))
        || (token.StartsWith('<') && token.EndsWith('>') && token.Length > 2 && !token.Contains('|'));
}
