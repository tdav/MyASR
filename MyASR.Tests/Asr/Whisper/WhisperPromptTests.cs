using System.Text.Json;
using MyASR.Asr.Whisper;

namespace MyASR.Tests.Asr.Whisper;

public class WhisperPromptTests
{
    private sealed record Reference(
        Dictionary<string, int> special_tokens,
        int[] prompt_token_ids,
        int[] suppress_tokens,
        int[] begin_suppress_tokens,
        string mode_source);

    private static Reference LoadReference()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "crisperwhisper_reference.json");
        return JsonSerializer.Deserialize<Reference>(File.ReadAllText(path))!;
    }

    [Test]
    public async Task Reference_UsesTheKnownRussianAndTranscribeIds()
    {
        var r = LoadReference();
        await Assert.That(r.special_tokens["startoftranscript"]).IsEqualTo(50258);
        await Assert.That(r.special_tokens["ru"]).IsEqualTo(50263);
        await Assert.That(r.special_tokens["transcribe"]).IsEqualTo(50359);
        await Assert.That(r.special_tokens["endoftext"]).IsEqualTo(50257);
    }

    [Test]
    public async Task Reference_PromptLeadsWithModeTags_ThenTheWhisperPrefix()
    {
        var r = LoadReference();

        // The reference implementation emits all five intended-mode tags before the standard
        // Whisper prefix. This is the opposite of the obvious guess, which is why it is asserted.
        await Assert.That(r.prompt_token_ids.Take(5))
            .IsEquivalentTo(new[] { 51885, 51886, 51887, 51888, 51889 }, CollectionOrdering.Matching);
        await Assert.That(r.prompt_token_ids.Skip(5))
            .IsEquivalentTo(new[] { 50258, 50263, 50359, 50363 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Reference_PromptSelectsRussianAndTranscription()
    {
        var r = LoadReference();
        await Assert.That(r.prompt_token_ids).Contains(50263); // <|ru|>
        await Assert.That(r.prompt_token_ids).Contains(50359); // <|transcribe|>
        await Assert.That(r.prompt_token_ids).DoesNotContain(50358); // <|translate|>
    }

    [Test]
    public async Task Reference_RecordsWhereThePromptCameFrom()
    {
        var r = LoadReference();
        await Assert.That(string.IsNullOrWhiteSpace(r.mode_source)).IsFalse();
    }

    [Test]
    public async Task Build_ReproducesTheReferencePrompt()
    {
        var r = LoadReference();
        var tokenizer = WhisperTestVocab.Build(r);

        var built = WhisperPrompt.Build(tokenizer, WhisperPrompt.RussianToken, WhisperPrompt.IntendedModeTokens);

        await Assert.That(built).IsEquivalentTo(r.prompt_token_ids, CollectionOrdering.Matching);
    }
}
