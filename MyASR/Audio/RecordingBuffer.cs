namespace MyASR.Audio;

/// <summary>Stores immutable resampled packets for live reads and the final recording.</summary>
public sealed class RecordingBuffer
{
    private readonly Lock gate = new();
    private readonly List<float[]> packets = [];
    private readonly List<int> packetOffsets = [];
    private int sampleCount;

    public int SampleCount
    {
        get { lock (gate) return sampleCount; }
    }

    public void Append(float[] packet)
    {
        if (packet.Length == 0) return;
        lock (gate)
        {
            var end = checked(sampleCount + packet.Length);
            packetOffsets.Add(sampleCount);
            packets.Add(packet);
            sampleCount = end;
        }
    }

    public float[] ReadSamples(int start, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        lock (gate)
        {
            if (start >= sampleCount || count == 0) return [];
            var audio = new float[Math.Min(count, sampleCount - start)];
            var index = packetOffsets.BinarySearch(start);
            if (index < 0) index = ~index - 1;
            int offset = start - packetOffsets[index];
            for (int written = 0; written < audio.Length; index++)
            {
                var packet = packets[index].AsSpan(offset);
                int length = Math.Min(packet.Length, audio.Length - written);
                packet[..length].CopyTo(audio.AsSpan(written));
                written += length;
                offset = 0;
            }
            return audio;
        }
    }

    public float[] Drain()
    {
        lock (gate)
        {
            var audio = ReadSamples(0, sampleCount);
            Clear();
            return audio;
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            packets.Clear();
            packetOffsets.Clear();
            sampleCount = 0;
        }
    }
}
