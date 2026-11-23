using MyASR.Asr;
using MyASR.Audio;
using MyASR.Models;

namespace MyASR.Tests.Asr;

public class LongRecordingTests
{
    [Test]
    public async Task CapturePackets_KeepEverySamplePastOneMinuteAndDrainOnce()
    {
        var buffer = new RecordingBuffer();
        for (int second = 0; second < 65; second++)
            buffer.Append(Enumerable.Repeat((float)second, 16000).ToArray());

        var pcm = buffer.Drain();
        await Assert.That(pcm.Length).IsEqualTo(65 * 16000);
        await Assert.That(pcm[0]).IsEqualTo(0f);
        await Assert.That(pcm[32 * 16000]).IsEqualTo(32f);
        await Assert.That(pcm[^1]).IsEqualTo(64f);
        await Assert.That(buffer.Drain().Length).IsEqualTo(0);
    }

    [Test]
    [Arguments("gigaam-v3-e2e-rnnt-int8")]
    [Arguments("crisperwhisper2-small-int8")]
    public async Task LongRecording_SplitsAtPausesAndTranscribesEverySampleOnce(string modelId)
    {
        const int rate = 16000;
        var original = new float[rate * 68 + 73];
        for (int i = 0; i < original.Length; i++)
            original[i] = 0.2f + i % 211 / 1000f;
        Array.Clear(original, rate * 22, rate / 2);
        Array.Clear(original, rate * 46, rate / 2);

        var mic = new FakeMic();
        mic.Pcm.AddRange(original);
        var engine = new TrackingEngine();
        using var service = new AsrService(mic, _ => engine, ModelCatalog.ById(modelId));
        AsrResult? result = null;
        service.ResultReady += value => result = value;
        service.StartCapture("0");
        await service.StopAndRecognizeAsync();

        await Assert.That(result!.Text).IsEqualTo("1 2 3");
        await Assert.That(result.AudioDuration).IsEqualTo(TimeSpan.FromSeconds(original.Length / (double)rate));
        await Assert.That(engine.Samples.SequenceEqual(original)).IsTrue();
        await Assert.That(engine.Lengths.All(length => length <= 25 * rate)).IsTrue();
        var first = engine.Lengths[0];
        var second = first + engine.Lengths[1];
        await Assert.That(first >= rate * 22 && first < rate * 22.5).IsTrue();
        await Assert.That(second >= rate * 46 && second < rate * 46.5).IsTrue();
    }

    private sealed class TrackingEngine : IAsrEngine
    {
        public List<float> Samples { get; } = [];
        public List<int> Lengths { get; } = [];

        public AsrResult Transcribe(ReadOnlySpan<float> pcm, TimeSpan duration)
        {
            Samples.AddRange(pcm);
            Lengths.Add(pcm.Length);
            return new AsrResult(Lengths.Count.ToString(), duration, TimeSpan.Zero);
        }

        public void Dispose() { }
    }
}
