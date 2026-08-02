namespace MyASR.Features;

/// <summary>
/// Slaney-scaled, Slaney-normalised mel filters — what Whisper was trained with.
/// <see cref="MelFilterBank.BuildHtk"/> is a different scale and produces believable but
/// wrong features if used here, so the two live in separate types on purpose.
/// </summary>
public static class WhisperMelFilterBank
{
    private const double FMin = 0.0;
    private const double FSp = 200.0 / 3.0;
    private const double MinLogHz = 1000.0;
    private const double MinLogMel = (MinLogHz - FMin) / FSp; // 15.0

    private static readonly double LogStep = Math.Log(6.4) / 27.0;

    public static double[,] BuildSlaney(int sampleRate, int nFft, int nMels)
    {
        var nFreqs = nFft / 2 + 1;

        var fftFreqs = new double[nFreqs];
        for (var k = 0; k < nFreqs; k++)
            fftFreqs[k] = sampleRate / 2.0 * k / (nFreqs - 1);

        // nMels + 2 mel-spaced points spanning [0, Nyquist].
        var lowMel = HzToMel(0.0);
        var highMel = HzToMel(sampleRate / 2.0);
        var melF = new double[nMels + 2];
        for (var i = 0; i < melF.Length; i++)
            melF[i] = MelToHz(lowMel + (highMel - lowMel) * i / (melF.Length - 1));

        var fb = new double[nMels, nFreqs];
        for (var m = 0; m < nMels; m++)
        {
            var lowerWidth = melF[m + 1] - melF[m];
            var upperWidth = melF[m + 2] - melF[m + 1];

            // Slaney normalisation: unit area per filter.
            var enorm = 2.0 / (melF[m + 2] - melF[m]);

            for (var k = 0; k < nFreqs; k++)
            {
                var lower = (fftFreqs[k] - melF[m]) / lowerWidth;
                var upper = (melF[m + 2] - fftFreqs[k]) / upperWidth;
                var w = Math.Max(0.0, Math.Min(lower, upper));
                fb[m, k] = w * enorm;
            }
        }

        return fb;
    }

    private static double HzToMel(double hz) =>
        hz < MinLogHz
            ? (hz - FMin) / FSp
            : MinLogMel + Math.Log(hz / MinLogHz) / LogStep;

    private static double MelToHz(double mel) =>
        mel < MinLogMel
            ? FMin + FSp * mel
            : MinLogHz * Math.Exp(LogStep * (mel - MinLogMel));
}
