using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MyASR.Audio;

/// <summary>
/// Converts an arbitrary float-PCM waveform into 16 kHz mono float (range [-1, 1]).
/// </summary>
public static class Resampler
{
    public static float[] ToMono16k(WaveFormat src, ReadOnlySpan<float> samples)
    {
        if (src.SampleRate == 16000 && src.Channels == 1)
            return samples.ToArray();

        var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        foreach (var s in samples) bw.Write(s);
        bw.Flush();
        ms.Position = 0;

        var floatFormat = WaveFormat.CreateIeeeFloatWaveFormat(src.SampleRate, src.Channels);
        var raw = new RawSourceWaveStream(ms, floatFormat).ToSampleProvider();

        ISampleProvider mono = src.Channels == 1
            ? raw
            : new StereoToMonoSampleProvider(raw) { LeftVolume = 0.5f, RightVolume = 0.5f };

        var resampled = src.SampleRate == 16000
            ? mono
            : new WdlResamplingSampleProvider(mono, 16000);

        var output = new List<float>(samples.Length);
        var buffer = new float[8192];
        int n;
        while ((n = resampled.Read(buffer.AsSpan())) > 0)
            output.AddRange(buffer.AsSpan(0, n));
        return [.. output];
    }
}
