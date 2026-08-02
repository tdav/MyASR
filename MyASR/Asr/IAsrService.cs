using MyASR.Models;

namespace MyASR.Asr;

public enum AsrState { Idle, Recording, Decoding }

public sealed record AsrStateChanged(AsrState State);

public interface IAsrService
{
    AsrState State { get; }
    ModelDescriptor CurrentModel { get; }

    event Action<AsrStateChanged>? StateChanged;
    event Action<AsrResult>? ResultReady;
    event Action<Exception>? Failed;

    void StartCapture(string deviceId);
    Task StopAndRecognizeAsync();
    Task WarmupAsync();

    /// <summary>
    /// Disposes the current engine and warms up the new model's engine.
    /// Throws <see cref="InvalidOperationException"/> unless the service is idle.
    /// </summary>
    Task SwitchModelAsync(ModelDescriptor model, CancellationToken ct);
}
