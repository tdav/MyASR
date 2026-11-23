namespace MyASR.Features;

public static class MelFilterBank
{
    public static double[,] BuildHtk(int sampleRate, int nFft, int nMels)
    {
        var nFreqs = nFft / 2 + 1;
        var fLowMel  = HzToMel(0.0);
        var fHighMel = HzToMel(sampleRate / 2.0);

        var edgesHz = new double[nMels + 2];
        for (var i = 0; i < edgesHz.Length; i++)
            edgesHz[i] = MelToHz(fLowMel + (fHighMel - fLowMel) * i / (edgesHz.Length - 1));

        var fb = new double[nMels, nFreqs];
        for (var m = 0; m < nMels; m++)
        {
            var left = edgesHz[m];
            var center = edgesHz[m + 1];
            var right = edgesHz[m + 2];
            for (var k = 0; k < nFreqs; k++)
            {
                var hz = (double)k * sampleRate / nFft;
                var rising = (hz - left) / (center - left);
                var falling = (right - hz) / (right - center);
                fb[m, k] = Math.Max(0, Math.Min(rising, falling));
            }
        }
        return fb;
    }

    private static double HzToMel(double f) => 2595.0 * Math.Log10(1.0 + f / 700.0);
    private static double MelToHz(double m) => 700.0 * (Math.Pow(10.0, m / 2595.0) - 1.0);
}
