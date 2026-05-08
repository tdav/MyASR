namespace MyASR.Audio;

public sealed record DeviceInfo(string Id, string FriendlyName);

public interface IMicCapture : IDisposable
{
    IReadOnlyList<DeviceInfo> EnumerateDevices();

    void Start(string deviceId);
    /// <summary>Returns the captured PCM at 16 kHz mono float, then resets the buffer.</summary>
    float[] StopAndDrain();

    /// <summary>Latest 100 ms RMS of input, in dBFS, for VU meter UI.</summary>
    double CurrentRmsDb { get; }
}
