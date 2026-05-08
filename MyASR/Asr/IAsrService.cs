namespace MyASR.Asr;

public enum AsrState { Idle, Recording, Decoding }

public sealed record AsrStateChanged(AsrState State);

public interface IAsrService
{
    AsrState State { get; }

    event Action<AsrStateChanged>? StateChanged;
    event Action<AsrResult>? ResultReady;
    event Action<Exception>? Failed;

    void StartCapture(string deviceId);
    Task StopAndRecognizeAsync();
}
