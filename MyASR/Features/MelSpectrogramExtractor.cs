namespace MyASR.Features;

public sealed class MelSpectrogramExtractor : IFeatureExtractor
{
    private const int SampleRate = 16000;
    private const int NFft       = 320;
    private const int HopLength  = 160;
    private const int NMels      = 64;
    private const float LogOffset = 1e-9f;

    private readonly float[] _window;
    private readonly double[,] _filterBank;

    public MelSpectrogramExtractor()
    {
        _window     = PowerSpectrum.HanningWindow(NFft);
        _filterBank = MelFilterBank.BuildHtk(SampleRate, NFft, NMels);
    }

    public float[,] Extract(ReadOnlySpan<float> pcm)
    {
        if (pcm.Length < NFft) return new float[0, NMels];
        var t = (pcm.Length - NFft) / HopLength + 1;
        var feats = new float[t, NMels];

        Span<float> spec = stackalloc float[NFft / 2 + 1];

        for (var i = 0; i < t; i++)
        {
            var offset = i * HopLength;
            PowerSpectrum.Compute(pcm.Slice(offset, NFft), _window, NFft, spec);
            for (var m = 0; m < NMels; m++)
            {
                double sum = 0;
                for (var k = 0; k < spec.Length; k++)
                    sum += _filterBank[m, k] * spec[k];
                feats[i, m] = (float)Math.Log(sum + LogOffset);
            }
        }
        return feats;
    }
}
