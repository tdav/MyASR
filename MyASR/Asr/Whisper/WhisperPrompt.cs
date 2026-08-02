namespace MyASR.Asr.Whisper;

/// <param name="ModeTags">The style-control tokens, which precede everything else.</param>
/// <param name="EndOfText">Terminates generation.</param>
public sealed record WhisperSpecialTokens(
    IReadOnlyList<int> ModeTags,
    int StartOfTranscript,
    int EndOfText,
    int Language,
    int Transcribe,
    int NoTimestamps);

/// <summary>
/// Builds the decoder prompt prefix. CrisperWhisper's verbatim and intended styles differ only
/// by this prefix — the encoder pass is identical — so the mode token belongs here.
/// The exact ordering is locked by <c>WhisperPromptTests.Build_ReproducesTheReferencePrompt</c>
/// against a dump from the reference Python implementation.
/// </summary>
public static class WhisperPrompt
{
    public const string StartOfTranscriptToken = "<|startoftranscript|>";
    public const string EndOfTextToken = "<|endoftext|>";
    public const string RussianToken = "<|ru|>";
    public const string TranscribeToken = "<|transcribe|>";
    public const string NoTimestampsToken = "<|notimestamps|>";

    /// <summary>
    /// Intended style: clean readable text. Verbatim would emit [UM], [UH] and stutters.
    /// All five intensity tags are emitted, not one — that is what the reference implementation
    /// does, captured from `crisperwhisper.prompt.PromptBuilder(...).intended()`.
    /// </summary>
    public static readonly IReadOnlyList<string> IntendedModeTokens =
        ["[intended_1]", "[intended_2]", "[intended_3]", "[intended_4]", "[intended_5]"];

    public static readonly IReadOnlyList<string> VerbatimModeTokens =
        ["[verbatim_1]", "[verbatim_2]", "[verbatim_3]", "[verbatim_4]", "[verbatim_5]"];

    public static WhisperSpecialTokens Resolve(
        WhisperTokenizer tokenizer, string languageToken, IReadOnlyList<string> modeTokens) =>
        new(
            ModeTags: [.. modeTokens.Select(tokenizer.IdOf)],
            StartOfTranscript: tokenizer.IdOf(StartOfTranscriptToken),
            EndOfText: tokenizer.IdOf(EndOfTextToken),
            Language: tokenizer.IdOf(languageToken),
            Transcribe: tokenizer.IdOf(TranscribeToken),
            NoTimestamps: tokenizer.IdOf(NoTimestampsToken));

    /// <summary>
    /// Mode tags come <b>first</b>, then the standard Whisper decoder prefix. This ordering is
    /// the opposite of what Whisper's own documentation would suggest, and was taken from the
    /// reference implementation's captured output — do not "correct" it.
    /// </summary>
    public static int[] Build(
        WhisperTokenizer tokenizer, string languageToken, IReadOnlyList<string> modeTokens)
    {
        var t = Resolve(tokenizer, languageToken, modeTokens);
        return [.. t.ModeTags, t.StartOfTranscript, t.Language, t.Transcribe, t.NoTimestamps];
    }
}
