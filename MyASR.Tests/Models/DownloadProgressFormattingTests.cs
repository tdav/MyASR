using MyASR;
using MyASR.Models;

namespace MyASR.Tests.Models;

public class DownloadProgressFormattingTests
{
    private static readonly ModelDescriptor Model = ModelCatalog.GigaAmV3;

    [Test]
    public async Task Line_ShowsModelName_FileName_AndMegabytes()
    {
        var p = new DownloadProgress("v3_rnnt_encoder.onnx", 50_000_000, 928_000_000, 0, 0, 4);
        var line = DownloadProgressFormatter.Line(Model, p);

        await Assert.That(line).Contains("GigaAM v3");
        await Assert.That(line).Contains("v3_rnnt_encoder.onnx");
        await Assert.That(line).Contains("50");
        await Assert.That(line).Contains("928");
    }

    [Test]
    public async Task Line_ShowsFilePositionWithinTheModel()
    {
        var p = new DownloadProgress("v3_vocab.txt", 100, 200, 0, 2, 4);
        await Assert.That(DownloadProgressFormatter.Line(Model, p)).Contains("3/4");
    }

    [Test]
    public async Task Percent_IsOverallAcrossFiles_NotPerFile()
    {
        // Second of four files, half done → (1 + 0.5) / 4 = 37.5% → 37
        var p = new DownloadProgress("f", 5, 10, 0, 1, 4);
        await Assert.That(DownloadProgressFormatter.Percent(p)).IsEqualTo(37);
    }

    [Test]
    public async Task Percent_LastFileComplete_Is100()
    {
        var p = new DownloadProgress("f", 10, 10, 0, 3, 4);
        await Assert.That(DownloadProgressFormatter.Percent(p)).IsEqualTo(100);
    }

    [Test]
    public async Task Percent_ZeroTotal_DoesNotDivideByZero()
    {
        var p = new DownloadProgress("f", 0, 0, 0, 0, 1);
        await Assert.That(DownloadProgressFormatter.Percent(p)).IsEqualTo(0);
    }

    [Test]
    public async Task Percent_IsAlwaysClampedToZeroHundred()
    {
        var p = new DownloadProgress("f", 999, 10, 0, 3, 4);
        await Assert.That(DownloadProgressFormatter.Percent(p)).IsEqualTo(100);
    }

    [Test]
    public async Task Speed_ZeroBytesPerSecond_RendersEmpty()
    {
        var p = new DownloadProgress("f", 1, 2, 0, 0, 1);
        await Assert.That(DownloadProgressFormatter.Speed(p)).IsEqualTo("");
    }

    [Test]
    public async Task Speed_NonZero_RendersMegabytesPerSecond()
    {
        var p = new DownloadProgress("f", 1, 2, 12_300_000, 0, 1);
        await Assert.That(DownloadProgressFormatter.Speed(p)).IsEqualTo("12,3 MB/s");
    }
}
