using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MyASR.Audio;

public sealed class WasapiMicCapture : IMicCapture
{
    private const int RingCapacity = 16000 * 31; // 31 sec headroom

    private readonly AudioRingBuffer _ring = new(RingCapacity);
    private WasapiCapture? _capture;
    private double _rmsDb = -120.0;

    public IReadOnlyList<DeviceInfo> EnumerateDevices()
    {
        using var en = new MMDeviceEnumerator();
        return [.. en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                     .Select(d => new DeviceInfo(d.ID, d.FriendlyName))];
    }

    public double CurrentRmsDb => _rmsDb;

    public void Start(string deviceId)
    {
        Stop();
        using var en = new MMDeviceEnumerator();
        var device = en.GetDevice(deviceId);
        _capture = new WasapiCapture(device);
        _capture.DataAvailable += OnData;
        _ring.Reset();
        _capture.StartRecording();
    }

    public float[] StopAndDrain()
    {
        Stop();
        return _ring.Snapshot();
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (_capture is null) return;
        var fmt = _capture.WaveFormat;
        var samples = BytesToFloat(fmt, e.Buffer.AsSpan(0, e.BytesRecorded));
        var mono16 = Resampler.ToMono16k(fmt, samples);
        _ring.Write(mono16);
        _rmsDb = ComputeRmsDb(mono16);
    }

    private static float[] BytesToFloat(WaveFormat fmt, ReadOnlySpan<byte> bytes)
    {
        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat && fmt.BitsPerSample == 32)
        {
            var floats = new float[bytes.Length / 4];
            for (var i = 0; i < floats.Length; i++)
                floats[i] = BitConverter.ToSingle(bytes.Slice(i * 4, 4));
            return floats;
        }
        if (fmt.Encoding == WaveFormatEncoding.Pcm && fmt.BitsPerSample == 16)
        {
            var floats = new float[bytes.Length / 2];
            for (var i = 0; i < floats.Length; i++)
            {
                var sample = (short)(bytes[i * 2] | (bytes[i * 2 + 1] << 8));
                floats[i] = sample / 32768f;
            }
            return floats;
        }
        throw new NotSupportedException($"Unsupported capture format: {fmt}");
    }

    private static double ComputeRmsDb(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return -120.0;
        double sum = 0;
        foreach (var s in samples) sum += s * s;
        var rms = Math.Sqrt(sum / samples.Length);
        return 20.0 * Math.Log10(Math.Max(rms, 1e-10));
    }

    private void Stop()
    {
        if (_capture is null) return;
        _capture.DataAvailable -= OnData;
        _capture.StopRecording();
        _capture.Dispose();
        _capture = null;
    }

    public void Dispose() => Stop();
}
