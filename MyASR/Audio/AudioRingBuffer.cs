namespace MyASR.Audio;

/// <summary>
/// Single-producer, single-consumer ring buffer of <see cref="float"/> samples.
/// <see cref="Snapshot"/> returns the last <c>≤Capacity</c> samples in FIFO order.
/// Older samples are discarded silently when the writer overruns.
/// </summary>
public sealed class AudioRingBuffer(int capacitySamples)
{
    private readonly float[] _buffer = new float[capacitySamples];
    private long _writePos;            // monotonic, writer-only
    private readonly Lock _gate = new();

    public int Capacity => _buffer.Length;

    public void Write(ReadOnlySpan<float> data)
    {
        lock (_gate)
        {
            foreach (var s in data)
            {
                _buffer[(int)(_writePos % _buffer.Length)] = s;
                _writePos++;
            }
        }
    }

    public float[] Snapshot()
    {
        lock (_gate)
        {
            var n = (int)Math.Min(_writePos, _buffer.Length);
            if (n == 0) return [];
            var output = new float[n];
            var startWrap = (int)((_writePos - n) % _buffer.Length);
            var first = Math.Min(n, _buffer.Length - startWrap);
            Array.Copy(_buffer, startWrap, output, 0, first);
            if (first < n)
                Array.Copy(_buffer, 0, output, first, n - first);
            return output;
        }
    }

    public void Reset()
    {
        lock (_gate) { _writePos = 0; Array.Clear(_buffer); }
    }
}
