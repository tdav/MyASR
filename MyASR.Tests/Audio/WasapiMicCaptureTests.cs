using MyASR.Audio;
using NAudio.Wave;

namespace MyASR.Tests.Audio;

public class WasapiMicCaptureTests
{
    [Test]
    public async Task DecodeWaveBuffer_IeeeFloat32_And_Pcm16_ProducesExpectedFloats()
    {
        var floatFmt = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
        var floatBytes = new byte[8];
        BitConverter.TryWriteBytes(floatBytes.AsSpan(0, 4), 0.25f);
        BitConverter.TryWriteBytes(floatBytes.AsSpan(4, 4), -0.5f);

        var decodedFloats = WasapiMicCapture.DecodeWaveBuffer(floatFmt, floatBytes, silent: false);
        await Assert.That(decodedFloats.Length).IsEqualTo(2);
        await Assert.That(decodedFloats[0]).IsEqualTo(0.25f);
        await Assert.That(decodedFloats[1]).IsEqualTo(-0.5f);

        var pcmFmt = new WaveFormat(16000, 16, 1);
        var pcmBytes = new byte[4];
        BitConverter.TryWriteBytes(pcmBytes.AsSpan(0, 2), (short)16384);
        BitConverter.TryWriteBytes(pcmBytes.AsSpan(2, 2), (short)-16384);

        var decodedPcm = WasapiMicCapture.DecodeWaveBuffer(pcmFmt, pcmBytes, silent: false);
        await Assert.That(decodedPcm.Length).IsEqualTo(2);
        await Assert.That(decodedPcm[0]).IsEqualTo(0.5f);
        await Assert.That(decodedPcm[1]).IsEqualTo(-0.5f);

        var silentOut = WasapiMicCapture.DecodeWaveBuffer(floatFmt, floatBytes, silent: true);
        await Assert.That(silentOut[0]).IsEqualTo(0f);
        await Assert.That(silentOut[1]).IsEqualTo(0f);
    }

    [Test]
    public async Task EnumerateDevices_ViaNativeComVtable_ReturnsListWithoutThrowing()
    {
        using var capture = new WasapiMicCapture();
        var devices = capture.EnumerateDevices();
        await Assert.That(devices).IsNotNull();
    }
}
