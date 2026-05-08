using System.IO;

namespace MyASR.Asr;

public sealed class Vocabulary
{
    private const string BlankToken = "<blk>";
    private const string SpaceMarker = "▁";

    private readonly string[] _tokens;

    private Vocabulary(string[] tokens)
    {
        _tokens = tokens;
        BlankIndex = Array.IndexOf(tokens, BlankToken);
        if (BlankIndex < 0)
            throw new InvalidDataException($"Vocabulary must contain '{BlankToken}'.");
    }

    public int Count => _tokens.Length;
    public int BlankIndex { get; }
    public string this[int index] => _tokens[index];

    public static Vocabulary Load(string path)
    {
        var lines = File.ReadAllLines(path);
        var tokens = new string[lines.Length];
        for (var i = 0; i < lines.Length; i++)
        {
            var space = lines[i].LastIndexOf(' ');
            tokens[i] = space < 0 ? lines[i] : lines[i][..space];
        }
        return new Vocabulary(tokens);
    }

    public string Decode(IReadOnlyList<int> ids)
    {
        var sb = new System.Text.StringBuilder(ids.Count);
        foreach (var id in ids)
        {
            if (id == BlankIndex) continue;
            var t = _tokens[id];
            sb.Append(t == SpaceMarker ? " " : t);
        }
        return sb.ToString();
    }
}
