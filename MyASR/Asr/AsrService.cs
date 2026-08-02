using MyASR.Audio;
using MyASR.Models;

namespace MyASR.Asr;

public sealed class AsrService(
    IMicCapture mic,
    Func<ModelDescriptor, IAsrEngine> engineFactory,
    ModelDescriptor initialModel)
    : IAsrService, IDisposable
{
    private const int MinSamples = 16000 * 3 / 10; // 0.3 sec @ 16 kHz

    private IAsrEngine? engine;

    public AsrState State { get; private set; } = AsrState.Idle;

    public ModelDescriptor CurrentModel { get; private set; } = initialModel;

    public event Action<AsrStateChanged>? StateChanged;
    public event Action<AsrResult>? ResultReady;
    public event Action<Exception>? Failed;

    public void StartCapture(string deviceId)
    {
        if (State != AsrState.Idle) return;
        mic.Start(deviceId);
        SetState(AsrState.Recording);
    }

    public Task WarmupAsync()
    {
        var model = CurrentModel;
        return Task.Run(() => engine ??= engineFactory(model));
    }

    public async Task SwitchModelAsync(ModelDescriptor model, CancellationToken ct)
    {
        if (State != AsrState.Idle)
            throw new InvalidOperationException("Модель нельзя менять во время записи или распознавания.");

        if (model.Id == CurrentModel.Id && engine is not null) return;

        // Drop the old engine before building the new one: two loaded ONNX models
        // would double peak memory, and a stale cached engine is the failure mode
        // this method exists to prevent.
        var previousEngine = engine;
        engine = null;
        previousEngine?.Dispose();

        // CurrentModel moves only after the factory succeeds. Advancing it first would leave
        // the service pointing at a model it failed to load, and the next recording would try
        // to build that same broken model through the ??= in StopAndRecognizeAsync.
        var built = await Task.Run(() => engineFactory(model), ct);
        engine = built;
        CurrentModel = model;
    }

    public async Task StopAndRecognizeAsync()
    {
        if (State != AsrState.Recording) return;
        var pcm = mic.StopAndDrain();
        if (pcm.Length < MinSamples) { SetState(AsrState.Idle); return; }

        SetState(AsrState.Decoding);
        try
        {
            var model = CurrentModel;
            var result = await Task.Run(() =>
            {
                engine ??= engineFactory(model);
                return engine.Transcribe(pcm, TimeSpan.FromSeconds(pcm.Length / 16000.0));
            });
            ResultReady?.Invoke(result);
        }
        catch (Exception ex)
        {
            Failed?.Invoke(ex);
        }
        finally
        {
            SetState(AsrState.Idle);
        }
    }

    private void SetState(AsrState s)
    {
        if (State == s) return;
        State = s;
        StateChanged?.Invoke(new AsrStateChanged(s));
    }

    public void Dispose()
    {
        mic.Dispose();
        engine?.Dispose();
    }
}
