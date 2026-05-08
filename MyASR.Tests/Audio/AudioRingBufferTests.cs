using MyASR.Audio;

namespace MyASR.Tests.Audio;

public class AudioRingBufferTests
{
    [Test]
    public async Task Snapshot_OnEmpty_ReturnsZeroLength()
    {
        var buf = new AudioRingBuffer(capacitySamples: 1024);
        await Assert.That(buf.Snapshot().Length).IsEqualTo(0);
    }

    [Test]
    public async Task Write_Then_Snapshot_PreservesOrder()
    {
        var buf = new AudioRingBuffer(1024);
        buf.Write([1f, 2f, 3f, 4f]);
        var s = buf.Snapshot();
        await Assert.That(s.Length).IsEqualTo(4);
        await Assert.That(s[0]).IsEqualTo(1f);
        await Assert.That(s[1]).IsEqualTo(2f);
        await Assert.That(s[2]).IsEqualTo(3f);
        await Assert.That(s[3]).IsEqualTo(4f);
    }

    [Test]
    public async Task Write_BeyondCapacity_DiscardsOldest()
    {
        var buf = new AudioRingBuffer(4);
        buf.Write([1f, 2f, 3f, 4f, 5f, 6f]);
        var s = buf.Snapshot();
        await Assert.That(s.Length).IsEqualTo(4);
        await Assert.That(s[0]).IsEqualTo(3f);
        await Assert.That(s[3]).IsEqualTo(6f);
    }

    [Test]
    public async Task Reset_ClearsBuffer()
    {
        var buf = new AudioRingBuffer(16);
        buf.Write([1f, 2f, 3f]);
        buf.Reset();
        await Assert.That(buf.Snapshot().Length).IsEqualTo(0);
    }

    [Test]
    public async Task Concurrent_WriterAndSnapshot_NoCorruption()
    {
        var buf = new AudioRingBuffer(64_000);
        var stop = false;
        var writer = Task.Run(() =>
        {
            var batch = new float[256];
            for (var i = 0; i < batch.Length; i++) batch[i] = 1.0f;
            while (!stop) buf.Write(batch);
        });
        for (var i = 0; i < 1000; i++)
        {
            var s = buf.Snapshot();
            await Assert.That(s.Length).IsLessThanOrEqualTo(64_000);
        }
        stop = true;
        await writer;
    }
}
