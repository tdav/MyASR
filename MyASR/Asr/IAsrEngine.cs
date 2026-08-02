namespace MyASR.Asr;

/// <summary>
/// One complete recognition pipeline. Feature extraction and decoding live behind the same
/// interface on purpose: they are model-specific and a mismatched pair produces plausible
/// nonsense rather than an error.
/// </summary>
public interface IAsrEngine : IDisposable
{
    AsrResult Transcribe(ReadOnlySpan<float> pcm16kMono, TimeSpan audioDuration);
}
