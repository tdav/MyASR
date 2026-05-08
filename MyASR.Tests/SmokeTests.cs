namespace MyASR.Tests;

public class SmokeTests
{
    [Test]
    public async Task Sanity_OnePlusOne_Equals_Two()
    {
        await Assert.That(1 + 1).IsEqualTo(2);
    }
}
