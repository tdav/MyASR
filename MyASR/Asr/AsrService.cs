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

    // Serialises every path that builds, replaces or disposes `engine` so that at most one
    // ONNX session is ever being constructed and no build can clobber another build's result.
    // WarmupAsync, SwitchModelAsync, the lazy build in StopAndRecognizeAsync and Dispose all
    // take this before touching the field. Not disposed: disposing it here would make a
    // post-Dispose call into any of those methods throw ObjectDisposedException out of
    // WaitAsync before the `disposed` guard below ever runs, and a second Dispose() call
    // (routine during WinForms container teardown) would throw where it is harmless today.
    private readonly SemaphoreSlim engineLock = new(1, 1);

    private IAsrEngine? engine;
    private bool disposed;

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

    public async Task WarmupAsync()
    {
        await engineLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed || engine is not null) return;
            var model = CurrentModel;
            engine = await Task.Run(() => engineFactory(model)).ConfigureAwait(false);
        }
        finally
        {
            engineLock.Release();
        }
    }

    public async Task SwitchModelAsync(ModelDescriptor model, CancellationToken ct)
    {
        if (State != AsrState.Idle)
            throw new InvalidOperationException("Модель нельзя менять во время записи или распознавания.");

        await engineLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (disposed) return;
            if (model.Id == CurrentModel.Id && engine is not null) return;

            // Drop the old engine before building the new one: two loaded ONNX models
            // would double peak memory, and a stale cached engine is the failure mode
            // this method exists to prevent.
            var previousEngine = engine;
            engine = null;
            previousEngine?.Dispose();

            // CurrentModel moves only after the factory succeeds. Advancing it first would leave
            // the service pointing at a model it failed to load, and the next recording would try
            // to build that same broken model through the lazy build in StopAndRecognizeAsync.
            var built = await Task.Run(() => engineFactory(model), ct).ConfigureAwait(false);
            engine = built;
            CurrentModel = model;
        }
        finally
        {
            engineLock.Release();
        }
    }

    public async Task StopAndRecognizeAsync()
    {
        if (State != AsrState.Recording) return;
        var pcm = mic.StopAndDrain();
        if (pcm.Length < MinSamples) { SetState(AsrState.Idle); return; }

        SetState(AsrState.Decoding);
        try
        {
            var activeEngine = await AcquireEngineAsync().ConfigureAwait(false);
            if (activeEngine is null) return; // disposed while decoding; nothing to transcribe with

            var result = await Task.Run(() =>
                activeEngine.Transcribe(pcm, TimeSpan.FromSeconds(pcm.Length / 16000.0))).ConfigureAwait(false);
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

    // Builds the engine if absent, under the same lock every other mutator uses, then hands
    // the instance back so the caller can transcribe without holding the lock across that work.
    private async Task<IAsrEngine?> AcquireEngineAsync()
    {
        await engineLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return null;
            if (engine is null)
            {
                var model = CurrentModel;
                engine = await Task.Run(() => engineFactory(model)).ConfigureAwait(false);
            }
            return engine;
        }
        finally
        {
            engineLock.Release();
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

        // Block until any in-flight build releases the lock, so a build started just before
        // Dispose cannot leave a live engine behind after this method returns.
        engineLock.Wait();
        try
        {
            disposed = true;
            engine?.Dispose();
            engine = null;
        }
        finally
        {
            engineLock.Release();
        }
    }
}
