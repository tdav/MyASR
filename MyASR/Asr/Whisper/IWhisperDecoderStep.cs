namespace MyASR.Asr.Whisper;

/// <summary>
/// One decoder forward pass. Implementations own the KV cache; the greedy loop stays pure so it
/// can be tested without a model.
/// </summary>
public interface IWhisperDecoderStep
{
    /// <param name="inputIds">The whole prompt on the first call, then one token per call.</param>
    /// <param name="firstStep">True only for the initial no-cache pass.</param>
    /// <returns>Logits for the final position, length = vocabulary size.</returns>
    float[] Step(ReadOnlySpan<int> inputIds, bool firstStep);
}
