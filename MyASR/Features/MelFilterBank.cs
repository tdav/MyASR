namespace MyASR.Features;

public static class MelFilterBank
{
    public static double[,] BuildHtk(int sampleRate, int nFft, int nMels)
    {
        var nFreqs = nFft / 2 + 1;
        var fLowMel  = HzToMel(0.0);
        var fHighMel = HzToMel(sampleRate / 2.0);

        var melPts = new double[nMels + 2];
        for (var i = 0; i < melPts.Length; i++)
            melPts[i] = fLowMel + (fHighMel - fLowMel) * i / (melPts.Length - 1);

        var binPts = new int[melPts.Length];
        for (var i = 0; i < melPts.Length; i++)
        {
            var hz = MelToHz(melPts[i]);
            binPts[i] = (int)Math.Floor((nFft + 1) * hz / sampleRate);
        }

        var fb = new double[nMels, nFreqs];
        for (var m = 1; m <= nMels; m++)
        {
            int left = binPts[m - 1], center = binPts[m], right = binPts[m + 1];
            for (var k = left; k < center; k++)
                if (center != left)
                    fb[m - 1, k] = (k - left) / (double)(center - left);
            for (var k = center; k < right; k++)
                if (right != center)
                    fb[m - 1, k] = (right - k) / (double)(right - center);
        }
        return fb;
    }

    private static double HzToMel(double f) => 2595.0 * Math.Log10(1.0 + f / 700.0);
    private static double MelToHz(double m) => 700.0 * (Math.Pow(10.0, m / 2595.0) - 1.0);
}
