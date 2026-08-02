namespace MyASR.Features;

/// <summary>
/// Whisper log-mel features: 80 Slaney mels, n_fft 400, hop 160, reflect-centred STFT,
/// padded or trimmed to exactly 30 seconds. Output is <c>[3000, 80]</c>.
/// </summary>
public sealed class WhisperFeatureExtractor : IFeatureExtractor
{
    public const int SampleRate = 16000;
    public const int NFft = 400;
    public const int HopLength = 160;
    public const int NMels = 80;
    public const int NSamples = 480_000; // 30 s
    public const int NFrames = 3000;

    private const double LogFloor = 1e-10;
    private const double DynamicRange = 8.0;

    private readonly float[] window = PowerSpectrum.HannWindowPeriodic(NFft);
    private readonly double[,] filterBank = WhisperMelFilterBank.BuildSlaney(SampleRate, NFft, NMels);

    public float[,] Extract(ReadOnlySpan<float> pcm16kMono)
    {
        // Whisper pads to a fixed 30 s window before the STFT, so the global maximum used by
        // the normalisation below includes the padding. Padding after the STFT would shift it.
        var padded = PadOrTrim(pcm16kMono);
        var centred = ReflectPad(padded, NFft / 2);

        var spec = new float[NFft / 2 + 1];
        var logMel = new double[NFrames, NMels];
        var maxLog = double.NegativeInfinity;

        for (var t = 0; t < NFrames; t++)
        {
            PowerSpectrum.Compute(centred.AsSpan(t * HopLength, NFft), window, NFft, spec);

            for (var m = 0; m < NMels; m++)
            {
                double sum = 0;
                for (var k = 0; k < spec.Length; k++)
                    sum += filterBank[m, k] * spec[k];

                var value = Math.Log10(Math.Max(sum, LogFloor));
                logMel[t, m] = value;
                if (value > maxLog) maxLog = value;
            }
        }

        var floor = maxLog - DynamicRange;
        var feats = new float[NFrames, NMels];
        for (var t = 0; t < NFrames; t++)
            for (var m = 0; m < NMels; m++)
                feats[t, m] = (float)((Math.Max(logMel[t, m], floor) + 4.0) / 4.0);

        return feats;
    }

    internal static float[] PadOrTrim(ReadOnlySpan<float> pcm)
    {
        var buffer = new float[NSamples];
        var n = Math.Min(pcm.Length, NSamples);
        pcm[..n].CopyTo(buffer);
        return buffer;
    }

    /// <summary>Reflect padding that excludes the edge sample, matching numpy's "reflect" mode.</summary>
    internal static float[] ReflectPad(float[] x, int pad)
    {
        var result = new float[x.Length + 2 * pad];
        Array.Copy(x, 0, result, pad, x.Length);
        for (var i = 0; i < pad; i++)
        {
            result[pad - 1 - i] = x[i + 1];
            result[pad + x.Length + i] = x[x.Length - 2 - i];
        }
        return result;
    }
}
