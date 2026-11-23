using System.Collections.Concurrent;
using System.Threading.Channels;
using MyASR.Asr;
using MyASR.Audio;
using MyASR.Models;

namespace MyASR.Tests.Asr;

public class LiveRecognitionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // A snapshot reads a range without draining it, just like a running microphone.
    private sealed class LiveMic : ILiveMicCapture
    {
        private readonly List<float> samples = [];
        private readonly object gate = new();
        public int CapturedSampleCount { get { lock (gate) return samples.Count; } }
        public double CurrentRmsDb => -12;
        public IReadOnlyList<DeviceInfo> EnumerateDevices() => [new("0", "test")];
        public void Start(string deviceId) { lock (gate) samples.Clear(); }
        public void Append(float value, int seconds, int trailingSilenceSeconds = 0)
        {
            lock (gate)
            {
                samples.AddRange(Enumerable.Repeat(value, seconds * 16000));
                samples.AddRange(new float[trailingSilenceSeconds * 16000]);
            }
        }
        public float[] ReadSamples(int start, int count)
        {
            lock (gate) return samples.GetRange(start, count).ToArray();
        }
        public float[] StopAndDrain()
        {
            lock (gate)
            {
                var result = samples.ToArray();
                samples.Clear();
                return result;
            }
        }
        public void Dispose() { }
    }

    // Each amplitude marks a word. If any range is lost or decoded twice, the final text differs.
    private sealed class WordEngine(Action? onTranscribe = null) : IAsrEngine
    {
        public AsrResult Transcribe(ReadOnlySpan<float> pcm, TimeSpan duration)
        {
            onTranscribe?.Invoke();
            List<string> words = [];
            float previous = 0;
            foreach (float sample in pcm)
            {
                if (sample != previous && sample != 0)
                    words.Add(sample switch { 0.25f => "раз", 0.5f => "два", _ => "три" });
                previous = sample;
            }
            return new AsrResult(string.Join(" ", words), duration, TimeSpan.Zero);
        }
        public void Dispose() { }
    }

    [Test]
    public async Task PreviewArrivesDuringRecording_FinalIncludesTailExactlyOnce()
    {
        var mic = new LiveMic();
        using var service = new AsrService(mic, _ => new WordEngine(), ModelCatalog.Default);
        var previews = Channel.CreateUnbounded<string>();
        var finals = new ConcurrentQueue<AsrResult>();
        service.PreviewReady += text => previews.Writer.TryWrite(text);
        service.ResultReady += finals.Enqueue;

        service.StartCapture("0");
        mic.Append(0.25f, 8, trailingSilenceSeconds: 1);
        var first = await previews.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
        await Assert.That(first).IsEqualTo("раз");

        mic.Append(0.5f, 8, trailingSilenceSeconds: 1);
        var second = await previews.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
        await Assert.That(second).IsEqualTo("раз два");
        await Assert.That(service.State).IsEqualTo(AsrState.Recording);
        await Assert.That(finals.IsEmpty).IsTrue();

        mic.Append(0.75f, 1);
        await service.StopAndRecognizeAsync().WaitAsync(Timeout);
        var final = finals.Single();
        await Assert.That(final.Text).IsEqualTo("раз два три");
        await Assert.That(final.AudioDuration).IsEqualTo(TimeSpan.FromSeconds(19));
    }

    [Test]
    public async Task StopDuringPreview_WaitsForInferenceAndIsolatesNextSession()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mic = new LiveMic();
        var engine = new WordEngine(() =>
        {
            if (entered.TrySetResult() && !release.Wait(Timeout))
                throw new TimeoutException("Preview inference was never released.");
        });
        using var service = new AsrService(mic, _ => engine, ModelCatalog.Default);
        var finals = new ConcurrentQueue<AsrResult>();
        service.ResultReady += finals.Enqueue;
        service.StartCapture("0");
        mic.Append(0.25f, 8, trailingSilenceSeconds: 1);
        await entered.Task.WaitAsync(Timeout);
        Task stop;
        try
        {
            mic.Append(0.5f, 1);
            stop = service.StopAndRecognizeAsync();
            await Assert.That(stop.IsCompleted).IsFalse();
        }
        finally { release.Set(); }
        await stop.WaitAsync(Timeout);
        await Assert.That(finals.Single().Text).IsEqualTo("раз два");

        var previews = Channel.CreateUnbounded<string>();
        service.PreviewReady += text => previews.Writer.TryWrite(text);
        service.StartCapture("0");
        mic.Append(0.75f, 8, trailingSilenceSeconds: 1);
        var next = await previews.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
        await Assert.That(next).IsEqualTo("три");
        await service.StopAndRecognizeAsync().WaitAsync(Timeout);
        await Assert.That(finals.Select(r => r.Text).ToArray())
            .IsEquivalentTo(["раз два", "три"], CollectionOrdering.Matching);
        while (previews.Reader.TryRead(out var preview))
            await Assert.That(preview).IsEqualTo("три");
        await Assert.That(service.State).IsEqualTo(AsrState.Idle);
    }
}
