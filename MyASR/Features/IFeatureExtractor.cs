namespace MyASR.Features;

public interface IFeatureExtractor
{
    /// <summary>Returns log-mel features as <c>[T, nMels]</c> row-major.</summary>
    float[,] Extract(ReadOnlySpan<float> pcm16kMono);
}
