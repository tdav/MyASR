namespace MyASR.Audio;

/// <summary>Read captured 16 kHz mono samples while the microphone continues recording.</summary>
public interface ILiveMicCapture : IMicCapture
{
    int CapturedSampleCount { get; }

    /// <summary>Copies up to count samples from the current recording. Returns empty after a drain.</summary>
    float[] ReadSamples(int start, int count);
}
