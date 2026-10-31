using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace MyASR;

/// <summary>
/// Renders the MyASR brand icon (midnight-to-cyan squircle with acoustic wave bars and condenser
/// microphone) via GDI+ and exports multi-resolution PNG-compressed .ico files.
/// </summary>
public static class AppIcon
{
    private static Icon? cached;

    /// <summary>Returns a 32×32 window/tray icon, created once and cached.</summary>
    public static Icon Get()
    {
        if (cached is not null) return cached;

        using var ms = new MemoryStream();
        WriteMultiSizeIco(ms, [16, 32, 48, 256]);
        ms.Position = 0;
        cached = new Icon(ms, new Size(32, 32));
        return cached;
    }

    /// <summary>Renders the MyASR icon at <paramref name="size"/>×<paramref name="size"/> pixels.</summary>
    public static Bitmap Render(int size)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 16);

        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.Clear(Color.Transparent);

        float s = size;
        float pad = MathF.Max(1f, s * 0.045f);
        float badgeSize = s - pad * 2f;
        float badgeRadius = badgeSize * 0.24f;

        // 1. Squircle badge background (deep indigo → electric cyan)
        using (var badgePath = RoundedRect(pad, pad, badgeSize, badgeSize, badgeRadius))
        {
            using var bgBrush = new LinearGradientBrush(
                new PointF(pad, pad),
                new PointF(s - pad, s - pad),
                Color.FromArgb(255, 17, 24, 54),
                Color.FromArgb(255, 8, 145, 198));
            bgBrush.InterpolationColors = new ColorBlend
            {
                Positions = [0f, 0.52f, 1f],
                Colors =
                [
                    Color.FromArgb(255, 15, 23, 48),
                    Color.FromArgb(255, 29, 53, 128),
                    Color.FromArgb(255, 6, 182, 212)
                ]
            };
            g.FillPath(bgBrush, badgePath);

            // Soft cyan radial glow behind the capsule
            using var glowPath = new GraphicsPath();
            glowPath.AddEllipse(s * 0.16f, s * 0.10f, s * 0.68f, s * 0.68f);
            using var glowBrush = new PathGradientBrush(glowPath)
            {
                CenterColor = Color.FromArgb(72, 103, 232, 249),
                SurroundColors = [Color.FromArgb(0, 103, 232, 249)]
            };
            g.FillPath(glowBrush, glowPath);

            // Crisp subtle inner rim
            float rimPenW = MathF.Max(1f, s * 0.018f);
            using var rimPen = new Pen(Color.FromArgb(68, 186, 230, 253), rimPenW);
            g.DrawPath(rimPen, badgePath);
        }

        // 2. Flanking acoustic equalizer pills (speech waves)
        if (size >= 24)
        {
            float barW = MathF.Max(1.6f, s * 0.044f);
            using var waveBrush = new SolidBrush(Color.FromArgb(190, 125, 243, 255));
            DrawVerticalPill(g, waveBrush, s * 0.165f - barW / 2f, s * 0.36f, barW, s * 0.16f);
            DrawVerticalPill(g, waveBrush, s * 0.248f - barW / 2f, s * 0.28f, barW, s * 0.26f);
            DrawVerticalPill(g, waveBrush, s * 0.752f - barW / 2f, s * 0.28f, barW, s * 0.26f);
            DrawVerticalPill(g, waveBrush, s * 0.835f - barW / 2f, s * 0.36f, barW, s * 0.16f);
        }

        // 3. Condenser microphone capsule (centered, bright white → ice-cyan tint)
        float bodyW = s * 0.26f;
        float bodyH = s * 0.39f;
        float bodyX = (s - bodyW) / 2f;
        float bodyY = s * 0.18f;
        float bodyR = bodyW / 2f;

        using (var micPath = RoundedRect(bodyX, bodyY, bodyW, bodyH, bodyR))
        using (var micBrush = new LinearGradientBrush(
            new PointF(bodyX, bodyY),
            new PointF(bodyX, bodyY + bodyH),
            Color.FromArgb(255, 255, 255, 255),
            Color.FromArgb(255, 214, 246, 255)))
        {
            g.FillPath(micBrush, micPath);
        }

        // Subtle studio grille slits at higher resolutions
        if (size >= 48)
        {
            using var slitPen = new Pen(Color.FromArgb(55, 14, 116, 144), MathF.Max(1f, s * 0.016f))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            float slitInset = bodyW * 0.26f;
            for (int i = 0; i < 3; i++)
            {
                float sy = bodyY + bodyH * (0.28f + i * 0.13f);
                g.DrawLine(slitPen, bodyX + slitInset, sy, bodyX + bodyW - slitInset, sy);
            }
        }

        // 4. Cradle arc, stand, and base
        float strokeW = MathF.Max(1.6f, s * 0.058f);
        using var standPen = new Pen(Color.FromArgb(250, 240, 251, 255), strokeW)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };

        float arcW = s * 0.38f;
        float arcH = s * 0.27f;
        float arcX = (s - arcW) / 2f;
        float arcY = s * 0.40f;
        g.DrawArc(standPen, arcX, arcY, arcW, arcH, 8f, 164f);

        float centerX = s / 2f;
        float stemTop = arcY + arcH;
        float stemBottom = s * 0.81f;
        g.DrawLine(standPen, centerX, stemTop, centerX, stemBottom);

        float baseW = s * 0.26f;
        g.DrawLine(standPen, centerX - baseW / 2f, stemBottom, centerX + baseW / 2f, stemBottom);

        return bmp;
    }

    /// <summary>Writes a multi-size PNG-compressed Windows .ico file to <paramref name="output"/>.</summary>
    public static void WriteMultiSizeIco(Stream output, ReadOnlySpan<int> sizes)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (sizes.IsEmpty) throw new ArgumentException("At least one icon size is required.", nameof(sizes));

        var payloads = new byte[sizes.Length][];
        for (int i = 0; i < sizes.Length; i++)
        {
            using var bmp = Render(sizes[i]);
            using var pngStream = new MemoryStream();
            bmp.Save(pngStream, ImageFormat.Png);
            payloads[i] = pngStream.ToArray();
        }

        using var bw = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);
        // ICONDIR header
        bw.Write((ushort)0);            // Reserved
        bw.Write((ushort)1);            // Type = 1 (Icon)
        bw.Write((ushort)sizes.Length); // Image count

        int offset = 6 + sizes.Length * 16;
        for (int i = 0; i < sizes.Length; i++)
        {
            int sz = sizes[i];
            byte dim = sz >= 256 ? (byte)0 : (byte)sz;
            int len = payloads[i].Length;

            bw.Write(dim);              // Width
            bw.Write(dim);              // Height
            bw.Write((byte)0);          // Palette count
            bw.Write((byte)0);          // Reserved
            bw.Write((ushort)1);        // Color planes
            bw.Write((ushort)32);       // Bits per pixel
            bw.Write(len);              // Image byte size
            bw.Write(offset);           // Offset in file
            offset += len;
        }

        for (int i = 0; i < payloads.Length; i++)
            bw.Write(payloads[i]);

        bw.Flush();
    }

    private static void DrawVerticalPill(Graphics g, Brush brush, float x, float y, float w, float h)
    {
        using var path = RoundedRect(x, y, w, h, w / 2f);
        g.FillPath(brush, path);
    }

    internal static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
    {
        r = MathF.Min(r, MathF.Min(w, h) / 2f);
        var path = new GraphicsPath();
        float d = r * 2f;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
