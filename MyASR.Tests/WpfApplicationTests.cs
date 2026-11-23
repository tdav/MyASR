using System.IO;
using System.Windows;
using System.Xml.Linq;

namespace MyASR.Tests;

public class WpfApplicationTests
{
    [Test]
    public async Task ApplicationWindows_UseWpf()
    {
        var assembly = typeof(Main).Assembly;
        var windows = assembly.GetTypes().Where(type => !type.IsAbstract && type.IsSubclassOf(typeof(Window)));

        await Assert.That(windows.Select(type => type.Name).ToArray())
            .IsEquivalentTo(["Main", "DownloadForm", "SplashForm", "ErrorWindow"]);
        await Assert.That(assembly.GetReferencedAssemblies().Any(a => a.Name == "System.Windows.Forms")).IsFalse();
    }

    [Test]
    public async Task PublishProfile_UsesManagedX64RuntimeForWpf()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "MyASR.slnx")))
            directory = directory.Parent;
        if (directory == null) throw new DirectoryNotFoundException("MyASR.slnx");

        var profile = XDocument.Load(Path.Combine(directory.FullName, "MyASR", "Properties",
            "PublishProfiles", "FolderProfile.pubxml"));
        var settings = profile.Root!.Element("PropertyGroup")!;

        await Assert.That((string?)settings.Element("RuntimeIdentifier")).IsEqualTo("win-x64");
        await Assert.That((bool)settings.Element("SelfContained")!).IsTrue();
        await Assert.That((bool)settings.Element("PublishAot")!).IsFalse();
        await Assert.That((bool)settings.Element("PublishTrimmed")!).IsFalse();
    }
}
