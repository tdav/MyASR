using MathNet.Numerics.IntegralTransforms;
using SystemComplex = System.Numerics.Complex;

namespace MyASR.Features;

internal static class PowerSpectrum
{
    /// <summary>
    /// Computes |FFT(x ∘ window)|² for a single frame and writes <c>nFft/2 + 1</c> bins to <paramref name="output"/>.
    /// </summary>
    public static void Compute(ReadOnlySpan<float> frame, ReadOnlySpan<float> window, int nFft, Span<float> output)
    {
        if (frame.Length != nFft) throw new ArgumentException("frame length != nFft", nameof(frame));
        if (window.Length != nFft) throw new ArgumentException("window length != nFft", nameof(window));
        if (output.Length != nFft / 2 + 1) throw new ArgumentException("output length != nFft/2+1", nameof(output));

        var buf = new SystemComplex[nFft];
        for (var i = 0; i < nFft; i++)
            buf[i] = new SystemComplex(frame[i] * window[i], 0.0);

        Fourier.Forward(buf, FourierOptions.Matlab); // forward, no scaling

        for (var k = 0; k <= nFft / 2; k++)
        {
            var re = (float)buf[k].Real; var im = (float)buf[k].Imaginary;
            output[k] = re * re + im * im;
        }
    }

    public static float[] HanningWindow(int n)
    {
        var w = new float[n];
        for (var i = 0; i < n; i++)
            w[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1)));
        return w;
    }
}
