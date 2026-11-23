using MyASR.Audio;

namespace MyASR.Tests.Audio;

public class RecordingBufferTests
{
    [Test]
    public async Task ReadingLiveRanges_PreservesPacketsForLaterReadsAndDrain()
    {
        var buffer = new RecordingBuffer();
        buffer.Append([1, 2, 3]);
        buffer.Append([]);
        buffer.Append([4, 5]);
        var preview = buffer.ReadSamples(1, 3);
        buffer.Append([6, 7]);

        await Assert.That(preview).IsEquivalentTo(new float[] { 2, 3, 4 }, CollectionOrdering.Matching);
        await Assert.That(buffer.ReadSamples(3, 100)).IsEquivalentTo(new float[] { 4, 5, 6, 7 }, CollectionOrdering.Matching);
        await Assert.That(buffer.SampleCount).IsEqualTo(7);
        await Assert.That(buffer.Drain()).IsEquivalentTo(new float[] { 1, 2, 3, 4, 5, 6, 7 }, CollectionOrdering.Matching);
        await Assert.That(buffer.ReadSamples(0, 2)).IsEmpty();
        buffer.Append([8]);
        await Assert.That(buffer.ReadSamples(0, 1)).IsEquivalentTo(new float[] { 8 });
    }
}
