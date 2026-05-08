namespace MyASR.Asr;

public interface IRnnTDecoder : IDisposable
{
    AsrResult Decode(float[,] logMelFeatures, TimeSpan audioDuration);
}
