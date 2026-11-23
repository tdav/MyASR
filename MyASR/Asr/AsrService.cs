using MyASR.Audio;
using MyASR.Models;

namespace MyASR.Asr;

public sealed class AsrService(
    IMicCapture mic,
    Func<ModelDescriptor, IAsrEngine> engineFactory,
    ModelDescriptor initialModel)
    : IAsrService, IDisposable
{
    private const int SampleRate = 16_000;
    private const int MinSamples = SampleRate * 3 / 10;
    private const int MaxChunk = SampleRate * 25;

    // Serialises every path that builds, replaces or disposes `engine` so that at most one
    // ONNX session is ever being constructed and no build can clobber another build's result.
    // WarmupAsync, SwitchModelAsync, the lazy build in StopAndRecognizeAsync and Dispose all
    // take this before touching the field. Not disposed: disposing it here would make a
    // post-Dispose call into any of those methods throw ObjectDisposedException out of
    // WaitAsync before the `disposed` guard below ever runs, and a second Dispose() call
    // (routine during service-provider teardown) would throw where it is harmless today.
    private readonly SemaphoreSlim engineLock = new(1, 1);

    private IAsrEngine? engine;
    private volatile bool disposed;
    private readonly object captureGate = new();
    private RecordingSession? session;

    private sealed class RecordingSession
    {
        public readonly CancellationTokenSource Stop = new();
        public Task Worker = Task.CompletedTask;
        public Task? Finish;
        public int CommittedSamples;
        public string CommittedText = string.Empty;
        public TimeSpan CommittedTime;
    }

    public AsrState State { get; private set; } = AsrState.Idle;

    public ModelDescriptor CurrentModel { get; private set; } = initialModel;

    public event Action<AsrStateChanged>? StateChanged;
    public event Action<string>? PreviewReady;
    public event Action<AsrResult>? ResultReady;
    public event Action<Exception>? Failed;

    public void StartCapture(string deviceId)
    {
        lock (captureGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (session != null) return;
            mic.Start(deviceId);
            var current = session = new RecordingSession();
            if (mic is ILiveMicCapture live)
            {
                var cancellation = current.Stop.Token;
                current.Worker = Task.Run(() => PreviewLoopAsync(live, current, cancellation));
            }
            SetState(AsrState.Recording);
        }
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

    private async Task PreviewLoopAsync(ILiveMicCapture live, RecordingSession current,
        CancellationToken cancellation)
    {
        int lastEnd = 0;
        try
        {
            while (true)
            {
                float[]? pcm = null;
                lock (captureGate)
                {
                    cancellation.ThrowIfCancellationRequested();
                    int end = live.CapturedSampleCount;
                    int available = end - current.CommittedSamples;
                    // With a second of lookahead, FindQuietBoundary chooses exactly the offline cut.
                    bool readyToCommit = available >= MaxChunk + SampleRate;
                    int count = readyToCommit ? MaxChunk + SampleRate : Math.Min(available, MaxChunk);
                    if (readyToCommit || current.CommittedSamples + count - lastEnd >= 2 * SampleRate)
                    {
                        pcm = live.ReadSamples(current.CommittedSamples, count);
                        lastEnd = current.CommittedSamples + pcm.Length;
                    }
                }
                if (pcm == null)
                {
                    await Task.Delay(200, cancellation).ConfigureAwait(false);
                    continue;
                }

                int boundary = pcm.Length >= MaxChunk + SampleRate ? FindQuietBoundary(pcm) : 0;
                int length = boundary > 0 ? boundary : pcm.Length;
                await engineLock.WaitAsync(cancellation).ConfigureAwait(false);
                AsrResult decoded;
                try
                {
                    cancellation.ThrowIfCancellationRequested();
                    engine ??= engineFactory(CurrentModel);
                    decoded = engine.Transcribe(pcm.AsSpan(0, length), TimeSpan.FromSeconds(length / (double)SampleRate));
                }
                finally { engineLock.Release(); }

                lock (captureGate)
                {
                    var preview = JoinText(current.CommittedText, decoded.Text);
                    if (boundary > 0)
                    {
                        current.CommittedText = preview;
                        current.CommittedSamples += length;
                        current.CommittedTime += decoded.RecognitionTime;
                        lastEnd = current.CommittedSamples;
                    }
                    if (!cancellation.IsCancellationRequested) PreviewReady?.Invoke(preview);
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // Keep capturing. Stop retries the uncommitted suffix through the normal error path.
            System.Diagnostics.Trace.TraceWarning("Live transcription stopped: {0}", ex.Message);
        }
    }

    private static string JoinText(string prefix, string suffix) =>
        string.IsNullOrWhiteSpace(suffix) ? prefix : prefix.Length == 0 ? suffix.Trim() : prefix + " " + suffix.Trim();

    public Task StopAndRecognizeAsync()
    {
        lock (captureGate)
        {
            var current = session;
            if (current == null || disposed) return Task.CompletedTask;
            if (current.Finish != null) return current.Finish;
            current.Stop.Cancel();
            float[] pcm;
            try { pcm = mic.StopAndDrain(); }
            catch (Exception ex)
            {
                Failed?.Invoke(ex);
                pcm = [];
            }
            if (pcm.Length < MinSamples && current.Worker.IsCompleted)
            {
                EndSession(current);
                return Task.CompletedTask;
            }
            current.Finish = Task.Run(() => FinishRecordingAsync(current, pcm));
            if (pcm.Length >= MinSamples) SetState(AsrState.Decoding);
            return current.Finish;
        }
    }

    private async Task FinishRecordingAsync(RecordingSession current, float[] pcm)
    {
        try
        {
            await current.Worker.ConfigureAwait(false);
            if (pcm.Length < MinSamples || disposed) return;
            AsrResult result;
            await engineLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (disposed) return;
                // A committed cut always leaves at least the lookahead in the final suffix.
                engine ??= engineFactory(CurrentModel);
                result = TranscribeRecording(engine, pcm.AsSpan(current.CommittedSamples));
                if (current.CommittedSamples != 0)
                {
                    result = new AsrResult(JoinText(current.CommittedText, result.Text),
                        TimeSpan.FromSeconds(pcm.Length / (double)SampleRate),
                        current.CommittedTime + result.RecognitionTime);
                }
            }
            finally { engineLock.Release(); }

            lock (captureGate)
                if (!disposed) ResultReady?.Invoke(result);
        }
        catch (Exception ex)
        {
            lock (captureGate)
                if (!disposed) Failed?.Invoke(ex);
        }
        finally
        {
            lock (captureGate) EndSession(current);
        }
    }

    private void EndSession(RecordingSession current)
    {
        current.Stop.Dispose();
        session = null;
        SetState(AsrState.Idle);
    }

    private static AsrResult TranscribeRecording(IAsrEngine engine, ReadOnlySpan<float> pcm)
    {
        var duration = TimeSpan.FromSeconds(pcm.Length / (double)SampleRate);
        if (pcm.Length <= MaxChunk) return engine.Transcribe(pcm, duration);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var parts = new List<string>();
        while (!pcm.IsEmpty)
        {
            var length = pcm.Length <= MaxChunk ? pcm.Length : FindQuietBoundary(pcm);
            var result = engine.Transcribe(pcm[..length], TimeSpan.FromSeconds(length / (double)SampleRate));
            if (!string.IsNullOrWhiteSpace(result.Text)) parts.Add(result.Text.Trim());
            pcm = pcm[length..];
        }
        return new AsrResult(string.Join(" ", parts), duration, clock.Elapsed);
    }

    private static int FindQuietBoundary(ReadOnlySpan<float> pcm)
    {
        // Split in the middle of the quietest 100 ms near the end of the model window.
        // Reserve a full second for the final chunk if the recording only just overflows.
        const int frame = SampleRate / 10;
        int end = Math.Min(MaxChunk, pcm.Length - SampleRate);
        int boundary = end;
        double lowestEnergy = double.PositiveInfinity;
        for (int stop = end; stop >= MaxChunk - 5 * SampleRate + frame; stop -= frame)
        {
            double energy = 0;
            foreach (float value in pcm.Slice(stop - frame, frame))
                energy += value * value;
            if (energy < lowestEnergy)
            {
                lowestEnergy = energy;
                boundary = stop - frame / 2;
            }
        }
        return boundary;
    }

    private void SetState(AsrState s)
    {
        if (State == s) return;
        State = s;
        StateChanged?.Invoke(new AsrStateChanged(s));
    }

    public void Dispose()
    {
        bool inCallback = Monitor.IsEntered(captureGate);
        Task pending;
        lock (captureGate)
        {
            if (disposed) return;
            disposed = true;
            session?.Stop.Cancel();
            pending = session?.Finish ?? session?.Worker ?? Task.CompletedTask;
            mic.Dispose();
        }
        if (!inCallback) pending.GetAwaiter().GetResult();
        engineLock.Wait();
        try
        {
            engine?.Dispose();
            engine = null;
        }
        finally { engineLock.Release(); }
        lock (captureGate)
        {
            if (session is { Finish: null } current)
            {
                EndSession(current);
            }
        }
    }
}
