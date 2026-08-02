using System.Text.Json;
using MyASR.Asr.Whisper;

namespace MyASR.Tests.Asr.Whisper;

/// <summary>
/// Builds a tokenizer containing only the special tokens the reference prompt uses, so prompt
/// construction can be verified without the model's full 51896-entry vocabulary.
/// </summary>
internal static class WhisperTestVocab
{
    public static WhisperTokenizer Build(object referenceWithSpecialTokens)
    {
        var json = JsonSerializer.Serialize(referenceWithSpecialTokens);
        using var doc = JsonDocument.Parse(json);
        var specials = doc.RootElement.GetProperty("special_tokens");

        var added = new Dictionary<string, int>
        {
            ["<|startoftranscript|>"] = specials.GetProperty("startoftranscript").GetInt32(),
            ["<|ru|>"] = specials.GetProperty("ru").GetInt32(),
            ["<|transcribe|>"] = specials.GetProperty("transcribe").GetInt32(),
            ["<|notimestamps|>"] = specials.GetProperty("notimestamps").GetInt32(),
            ["<|endoftext|>"] = specials.GetProperty("endoftext").GetInt32(),
        };

        // All five intended-mode tags, taken from the reference prompt in the order it used them.
        var prompt = doc.RootElement.GetProperty("prompt_token_ids").EnumerateArray()
            .Select(e => e.GetInt32()).ToList();
        var intended = prompt.Where(id => id is >= 51885 and <= 51889).ToList();
        for (var i = 0; i < WhisperPrompt.IntendedModeTokens.Count && i < intended.Count; i++)
            added[WhisperPrompt.IntendedModeTokens[i]] = intended[i];

        var dir = Path.Combine(Path.GetTempPath(), "MyASR-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var vocabPath = Path.Combine(dir, "vocab.json");
            var addedPath = Path.Combine(dir, "added_tokens.json");
            File.WriteAllText(vocabPath, "{}");
            File.WriteAllText(addedPath, JsonSerializer.Serialize(added));
            // WhisperTokenizer.Load reads both files eagerly (via File.ReadAllText inside
            // Load/Merge), so the directory can be removed as soon as it returns.
            return WhisperTokenizer.Load(vocabPath, addedPath);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
