using MyASR.Asr;
using MyASR.Audio;
using MyASR.Models;

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

internal sealed class FakeEngine(string text = "привет") : IAsrEngine
{
    public bool Disposed { get; private set; }
    public int Calls { get; private set; }

    public AsrResult Transcribe(ReadOnlySpan<float> pcm, TimeSpan dur)
    {
        Calls++;
        return new AsrResult(text, dur, TimeSpan.FromMilliseconds(1));
    }

    public void Dispose() => Disposed = true;
}

internal sealed class ThrowingEngine : IAsrEngine
{
    public AsrResult Transcribe(ReadOnlySpan<float> pcm, TimeSpan dur) =>
        throw new InvalidOperationException("boom");
    public void Dispose() { }
}

public class AsrServiceTests
{
    private static readonly ModelDescriptor ModelA = ModelCatalog.GigaAmV3;
    private static readonly ModelDescriptor ModelB =
        ModelCatalog.GigaAmV3 with { Id = "model-b", FolderName = "model-b" };

    private static AsrService Build(IMicCapture mic, Func<ModelDescriptor, IAsrEngine> factory) =>
        new(mic, factory, ModelA);

    [Test]
    public async Task StartCapture_TransitionsToRecording()
    {
        var svc = Build(new FakeMic(), _ => new FakeEngine());
        svc.StartCapture("0");
        await Assert.That(svc.State).IsEqualTo(AsrState.Recording);
    }

    [Test]
    public async Task StopAndRecognizeAsync_RaisesResultReady_AndReturnsToIdle()
    {
        var mic = new FakeMic();
        mic.Pcm.AddRange(new float[16000]);
        var svc = Build(mic, _ => new FakeEngine());
        AsrResult? captured = null;
        svc.ResultReady += r => captured = r;

        svc.StartCapture("0");
        await svc.StopAndRecognizeAsync();

        await Assert.That(captured).IsNotNull();
        await Assert.That(captured!.Text).IsEqualTo("привет");
        await Assert.That(svc.State).IsEqualTo(AsrState.Idle);
    }

    [Test]
    public async Task StopAndRecognizeAsync_TooShort_DoesNotCallEngine()
    {
        var mic = new FakeMic();
        mic.Pcm.AddRange(new float[1600]);
        var engine = new FakeEngine();
        var svc = Build(mic, _ => engine);
        svc.StartCapture("0");
        await svc.StopAndRecognizeAsync();

        await Assert.That(engine.Calls).IsEqualTo(0);
        await Assert.That(svc.State).IsEqualTo(AsrState.Idle);
    }

    [Test]
    public async Task EngineThrows_RaisesFailed_AndReturnsToIdle()
    {
        var mic = new FakeMic();
        mic.Pcm.AddRange(new float[16000]);
        var svc = Build(mic, _ => new ThrowingEngine());
        Exception? captured = null;
        svc.Failed += e => captured = e;

        svc.StartCapture("0");
        await svc.StopAndRecognizeAsync();

        await Assert.That(captured).IsNotNull();
        await Assert.That(svc.State).IsEqualTo(AsrState.Idle);
    }

    [Test]
    public async Task WarmupAsync_BuildsEngineOnce()
    {
        var calls = 0;
        var svc = Build(new FakeMic(), _ => { calls++; return new FakeEngine(); });
        await svc.WarmupAsync();
        await svc.WarmupAsync();
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task StartCapture_WhileRecording_IsNoOp()
    {
        var svc = Build(new FakeMic(), _ => new FakeEngine());
        svc.StartCapture("0");
        svc.StartCapture("0");
        await Assert.That(svc.State).IsEqualTo(AsrState.Recording);
    }

    [Test]
    public async Task CurrentModel_StartsAtTheInitialDescriptor()
    {
        var svc = Build(new FakeMic(), _ => new FakeEngine());
        await Assert.That(svc.CurrentModel.Id).IsEqualTo(ModelA.Id);
    }

    [Test]
    public async Task SwitchModelAsync_DisposesTheOldEngine()
    {
        var built = new List<FakeEngine>();
        var svc = Build(new FakeMic(), _ => { var e = new FakeEngine(); built.Add(e); return e; });

        await svc.WarmupAsync();
        await svc.SwitchModelAsync(ModelB, CancellationToken.None);

        await Assert.That(built.Count).IsEqualTo(2);
        await Assert.That(built[0].Disposed).IsTrue();
        await Assert.That(built[1].Disposed).IsFalse();
        await Assert.That(svc.CurrentModel.Id).IsEqualTo(ModelB.Id);
    }

    [Test]
    public async Task SwitchModelAsync_PassesTheNewDescriptorToTheFactory()
    {
        var requested = new List<string>();
        var svc = Build(new FakeMic(), d => { requested.Add(d.Id); return new FakeEngine(); });

        await svc.WarmupAsync();
        await svc.SwitchModelAsync(ModelB, CancellationToken.None);

        await Assert.That(requested).IsEquivalentTo(new[] { ModelA.Id, ModelB.Id });
    }

    [Test]
    public async Task RecognitionAfterSwitch_UsesTheNewEngine()
    {
        var mic = new FakeMic();
        mic.Pcm.AddRange(new float[16000]);
        var svc = Build(mic, d => new FakeEngine(d.Id == ModelB.Id ? "новая модель" : "старая модель"));

        await svc.WarmupAsync();
        await svc.SwitchModelAsync(ModelB, CancellationToken.None);

        AsrResult? captured = null;
        svc.ResultReady += r => captured = r;
        svc.StartCapture("0");
        await svc.StopAndRecognizeAsync();

        await Assert.That(captured!.Text).IsEqualTo("новая модель");
    }

    [Test]
    public async Task SwitchModelAsync_WhileRecording_Throws_AndKeepsTheOldModel()
    {
        var svc = Build(new FakeMic(), _ => new FakeEngine());
        await svc.WarmupAsync();
        svc.StartCapture("0");

        await Assert.That(async () => await svc.SwitchModelAsync(ModelB, CancellationToken.None))
            .Throws<InvalidOperationException>();
        await Assert.That(svc.CurrentModel.Id).IsEqualTo(ModelA.Id);
    }

    [Test]
    public async Task SwitchModelAsync_FactoryThrows_KeepsThePreviousModelAsCurrent()
    {
        var svc = Build(new FakeMic(), d =>
            d.Id == ModelB.Id ? throw new FileNotFoundException("нет файла модели") : new FakeEngine());

        await svc.WarmupAsync();

        await Assert.That(async () => await svc.SwitchModelAsync(ModelB, CancellationToken.None))
            .Throws<FileNotFoundException>();
        await Assert.That(svc.CurrentModel.Id).IsEqualTo(ModelA.Id);
    }

    [Test]
    public async Task SwitchModelAsync_FactoryThrows_ThenRetryingThePreviousModelRebuildsIt()
    {
        var built = 0;
        var svc = Build(new FakeMic(), d =>
        {
            if (d.Id == ModelB.Id) throw new FileNotFoundException("нет файла модели");
            built++;
            return new FakeEngine();
        });

        await svc.WarmupAsync();
        try { await svc.SwitchModelAsync(ModelB, CancellationToken.None); }
        catch (FileNotFoundException) { /* expected */ }

        await svc.WarmupAsync(); // the failed switch disposed the old engine, so this rebuilds

        await Assert.That(built).IsEqualTo(2);
        await Assert.That(svc.CurrentModel.Id).IsEqualTo(ModelA.Id);
    }

    [Test]
    public async Task SwitchModelAsync_ToTheSameModel_KeepsTheWarmEngine()
    {
        var calls = 0;
        var svc = Build(new FakeMic(), _ => { calls++; return new FakeEngine(); });

        await svc.WarmupAsync();
        await svc.SwitchModelAsync(ModelA, CancellationToken.None);

        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task Dispose_DisposesTheEngine()
    {
        FakeEngine? engine = null;
        var svc = Build(new FakeMic(), _ => engine = new FakeEngine());
        await svc.WarmupAsync();

        svc.Dispose();

        await Assert.That(engine!.Disposed).IsTrue();
    }
}
