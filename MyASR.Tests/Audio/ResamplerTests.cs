using MyASR.Audio;
using NAudio.Wave;

namespace MyASR.Tests.Audio;

public class ResamplerTests
{
    [Test]
    public async Task Resample_48kStereoFloat_To_16kMonoFloat_ReducesSampleCount()
    {
        var src = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        var samples = new float[48000 * 2]; // 1 sec stereo
        for (var i = 0; i < samples.Length; i++) samples[i] = 0.1f;
        var output = Resampler.ToMono16k(src, samples);
        // Expect approximately 16000 samples (allow some slop from the resampler)
        await Assert.That(output.Length).IsGreaterThanOrEqualTo(15990);
        await Assert.That(output.Length).IsLessThanOrEqualTo(16010);
    }

    [Test]
    public async Task Resample_16kMonoFloat_NoOp()
    {
        var src = WaveFormat.CreateIeeeFloatWaveFormat(16000, 1);
        var samples = new float[1600];
        for (var i = 0; i < samples.Length; i++) samples[i] = 0.5f;
        var output = Resampler.ToMono16k(src, samples);
        await Assert.That(output.Length).IsEqualTo(1600);
        await Assert.That(output[0]).IsEqualTo(0.5f);
    }
}
