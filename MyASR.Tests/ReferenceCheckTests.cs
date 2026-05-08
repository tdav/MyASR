namespace MyASR.Tests;

public class ReferenceCheckTests
{
    [Test]
    public async Task ProductionAssembly_IsReferenceable()
    {
        var asm = typeof(MyASR.Form1).Assembly;
        await Assert.That(asm.GetName().Name).IsEqualTo("MyASR");
    }
}
