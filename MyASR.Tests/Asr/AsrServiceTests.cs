using MyASR.Asr;
using MyASR.Audio;
using MyASR.Features;

namespace MyASR.Tests.Asr;

internal sealed class FakeMic : IMicCapture
{
    public List<float> Pcm { get; } = [];
    public bool Started { get; private set; }
    public double CurrentRmsDb => -30.0;
    public IReadOnlyList<DeviceInfo> EnumerateDevices() => [new("0", "fake")];
    public void Start(string deviceId) { Started = true; }
    public float[] StopAndDrain() { Started = false; return Pcm.ToArray(); }
    public void Dispose() { }
}

internal sealed class FakeFeatures : IFeatureExtractor
{
    public float[,] Extract(ReadOnlySpan<float> pcm) => new float[10, 64];
}

internal sealed class FakeDecoder : IRnnTDecoder
{
    public AsrResult Result { get; set; } = new("привет", TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1));
    public AsrResult Decode(float[,] mel, TimeSpan dur) => Result;
    public void Dispose() { }
}

internal sealed class ThrowingDecoder : IRnnTDecoder
{
    public AsrResult Decode(float[,] mel, TimeSpan dur) => throw new InvalidOperationException("boom");
    public void Dispose() { }
}

public class AsrServiceTests
{
    [Test]
    public async Task StartCapture_TransitionsToRecording()
    {
        var svc = new AsrService(new FakeMic(), new FakeFeatures(), new FakeDecoder());
        svc.StartCapture("0");
        await Assert.That(svc.State).IsEqualTo(AsrState.Recording);
    }

    [Test]
    public async Task StopAndRecognizeAsync_RaisesResultReady_AndReturnsToIdle()
    {
        var mic = new FakeMic();
        mic.Pcm.AddRange(new float[16000]); // 1 sec of audio
        var svc = new AsrService(mic, new FakeFeatures(), new FakeDecoder());
        AsrResult? captured = null;
        svc.ResultReady += r => captured = r;

        svc.StartCapture("0");
        await svc.StopAndRecognizeAsync();

        await Assert.That(captured).IsNotNull();
        await Assert.That(captured!.Text).IsEqualTo("привет");
        await Assert.That(svc.State).IsEqualTo(AsrState.Idle);
    }

    [Test]
    public async Task StopAndRecognizeAsync_TooShort_DoesNotCallDecoder()
    {
        var mic = new FakeMic();
        mic.Pcm.AddRange(new float[1600]); // 0.1 sec — too short
        var dec = new FakeDecoder();
        var svc = new AsrService(mic, new FakeFeatures(), dec);
        AsrResult? captured = null;
        svc.ResultReady += r => captured = r;

        svc.StartCapture("0");
        await svc.StopAndRecognizeAsync();

        await Assert.That(captured).IsNull();
        await Assert.That(svc.State).IsEqualTo(AsrState.Idle);
    }

    [Test]
    public async Task DecoderThrows_RaisesFailed_AndReturnsToIdle()
    {
        var mic = new FakeMic();
        mic.Pcm.AddRange(new float[16000]);
        var svc = new AsrService(mic, new FakeFeatures(), new ThrowingDecoder());
        Exception? captured = null;
        svc.Failed += e => captured = e;

        svc.StartCapture("0");
        await svc.StopAndRecognizeAsync();

        await Assert.That(captured).IsNotNull();
        await Assert.That(svc.State).IsEqualTo(AsrState.Idle);
    }

    [Test]
    public async Task StartCapture_WhileRecording_IsNoOp()
    {
        var svc = new AsrService(new FakeMic(), new FakeFeatures(), new FakeDecoder());
        svc.StartCapture("0");
        svc.StartCapture("0"); // second call — must not throw or change state
        await Assert.That(svc.State).IsEqualTo(AsrState.Recording);
    }
}
