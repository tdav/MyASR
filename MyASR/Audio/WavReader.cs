using NAudio.Wave;

namespace MyASR.Audio;

public static class WavReader
{
    /// <summary>Reads a 16 kHz mono 16-bit/float PCM .wav into normalised float in [-1, 1].</summary>
    public static float[] ReadMono16kFloat(string path)
    {
        using var reader = new WaveFileReader(path);
        if (reader.WaveFormat.SampleRate != 16000)
            throw new InvalidDataException($"expected 16 kHz, got {reader.WaveFormat.SampleRate}");
        if (reader.WaveFormat.Channels != 1)
            throw new InvalidDataException($"expected mono, got {reader.WaveFormat.Channels} channels");

        var sp = reader.ToSampleProvider();
        var samples = new List<float>(checked((int)(reader.Length / Math.Max(1, reader.WaveFormat.BlockAlign))));
        var buf = new float[8192];
        int n;
        while ((n = sp.Read(buf, 0, buf.Length)) > 0)
            samples.AddRange(buf.AsSpan(0, n).ToArray());
        return [.. samples];
    }
}
