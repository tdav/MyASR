using MyASR.Audio;
using MyASR.Features;

namespace MyASR.Asr;

public sealed class AsrService(IMicCapture mic, IFeatureExtractor features, IRnnTDecoder decoder)
    : IAsrService, IDisposable
{
    private const int MinSamples = 16000 * 3 / 10; // 0.3 sec @ 16 kHz

    public AsrState State { get; private set; } = AsrState.Idle;

    public event Action<AsrStateChanged>? StateChanged;
    public event Action<AsrResult>? ResultReady;
    public event Action<Exception>? Failed;

    public void StartCapture(string deviceId)
    {
        if (State != AsrState.Idle) return;
        mic.Start(deviceId);
        SetState(AsrState.Recording);
    }

    public async Task StopAndRecognizeAsync()
    {
        if (State != AsrState.Recording) return;
        var pcm = mic.StopAndDrain();
        if (pcm.Length < MinSamples) { SetState(AsrState.Idle); return; }

        SetState(AsrState.Decoding);
        try
        {
            var result = await Task.Run(() =>
            {
                var feats = features.Extract(pcm);
                return decoder.Decode(feats, TimeSpan.FromSeconds(pcm.Length / 16000.0));
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
        decoder.Dispose();
    }
}
