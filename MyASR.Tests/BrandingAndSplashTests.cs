using System.IO;
using MyASR;

namespace MyASR.Tests;

public class BrandingAndSplashTests
{
    [Test]
    public async Task AppIcon_Render_DrawsSquircleWithTransparentCornersAndBrightCenter()
    {
        using var bmp = AppIcon.Render(256);
        await Assert.That(bmp.Width).IsEqualTo(256);
        await Assert.That(bmp.Height).IsEqualTo(256);

        // Squircle corners must remain transparent.
        var corner = bmp.GetPixel(1, 1);
        await Assert.That((int)corner.A).IsEqualTo(0);

        // Center microphone capsule must be bright and opaque.
        var center = bmp.GetPixel(128, 96);
        await Assert.That((int)center.A).IsEqualTo(255);
        await Assert.That((int)center.B).IsGreaterThan(180);
    }

    [Test]
    public async Task AppIcon_WriteMultiSizeIco_WritesValidIcoDirectoryAndPngPayloads()
    {
        using var ms = new MemoryStream();
        AppIcon.WriteMultiSizeIco(ms, [16, 32, 48, 256]);
        var bytes = ms.ToArray();

        // ICO header: reserved=0, type=1, count=4
        await Assert.That(BitConverter.ToUInt16(bytes, 0)).IsEqualTo((ushort)0);
        await Assert.That(BitConverter.ToUInt16(bytes, 2)).IsEqualTo((ushort)1);
        await Assert.That(BitConverter.ToUInt16(bytes, 4)).IsEqualTo((ushort)4);

        // 4th directory entry (index 3 -> offset 6 + 3*16 = 54) is 256x256, stored as 0x0 per ICO spec.
        await Assert.That(bytes[54]).IsEqualTo((byte)0);
        await Assert.That(bytes[55]).IsEqualTo((byte)0);

        var dataOffset = BitConverter.ToInt32(bytes, 54 + 12);
        // PNG magic: 89 50 4E 47
        await Assert.That(bytes[dataOffset]).IsEqualTo((byte)0x89);
        await Assert.That(bytes[dataOffset + 1]).IsEqualTo((byte)0x50);
        await Assert.That(bytes[dataOffset + 2]).IsEqualTo((byte)0x4E);
        await Assert.That(bytes[dataOffset + 3]).IsEqualTo((byte)0x47);
    }

    [Test]
    public async Task SplashForm_ReportStage_UpdatesStateClampsProgressAndRendersBitmap()
    {
        using var splash = new SplashForm();
        splash.ReportStage("Загрузка нейросети...", 0.65);

        await Assert.That(splash.CurrentStatus).IsEqualTo("Загрузка нейросети...");
        await Assert.That(splash.CurrentProgress).IsEqualTo(0.65);

        splash.ReportStage("Переполнение", 1.5);
        await Assert.That(splash.CurrentProgress).IsEqualTo(1.0);

        splash.ReportStage("Недополнение", -0.4);
        await Assert.That(splash.CurrentProgress).IsEqualTo(0.0);

        using var preview = splash.RenderPreviewBitmap();
        await Assert.That(preview.Width).IsEqualTo(splash.ClientSize.Width);
        await Assert.That(preview.Height).IsEqualTo(splash.ClientSize.Height);
        // Dark themed background in top-left interior
        var bg = preview.GetPixel(24, 24);
        await Assert.That((int)bg.R).IsLessThan(40);
    }
}
