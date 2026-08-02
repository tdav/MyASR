using MyASR.Features;
using MyASR.Models;

namespace MyASR.Asr;

/// <summary>GigaAM v3: 64-mel HTK log-mel features into a greedy RNN-T decode.</summary>
public sealed class GigaAmRnnTEngine(IFeatureExtractor features, IRnnTDecoder decoder) : IAsrEngine
{
    public static GigaAmRnnTEngine Create(ModelPaths paths)
    {
        var vocab = Vocabulary.Load(paths.File(GigaAmFiles.Vocab));
        var decoder = new RnnTGreedyDecoder(
            paths.File(GigaAmFiles.Encoder),
            paths.File(GigaAmFiles.Decoder),
            paths.File(GigaAmFiles.Joint),
            vocab);
        return new GigaAmRnnTEngine(new MelSpectrogramExtractor(), decoder);
    }

    public AsrResult Transcribe(ReadOnlySpan<float> pcm16kMono, TimeSpan audioDuration) =>
        decoder.Decode(features.Extract(pcm16kMono), audioDuration);

    public void Dispose()
    {
        decoder.Dispose();
        (features as IDisposable)?.Dispose();
    }
}
