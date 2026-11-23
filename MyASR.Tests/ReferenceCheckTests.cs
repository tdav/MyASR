namespace MyASR.Tests;

public class ReferenceCheckTests
{
    [Test]
    public async Task MainWindow_UsesWpfFromTheProductionAssembly()
    {
        var main = typeof(MyASR.Main);
        await Assert.That(typeof(System.Windows.Window).IsAssignableFrom(main)).IsTrue();
        var asm = main.Assembly;
        await Assert.That(asm.GetName().Name).IsEqualTo("MyASR");
    }
}
