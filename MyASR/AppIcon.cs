namespace MyASR;

/// <summary>Renders a microphone icon at runtime via GDI+ — no external .ico file needed.</summary>
internal static class AppIcon
{
    private static Icon? _cached;

    /// <summary>Returns a 32×32 microphone icon, created once and cached.</summary>
    public static Icon Get()
    {
        if (_cached is not null) return _cached;

        using var bmp = Render(32);
        _cached = Icon.FromHandle(bmp.GetHicon());
        return _cached;
    }

    /// <summary>Returns a <see cref="Bitmap"/> of the given size for use as a form icon.</summary>
    public static Bitmap Render(int size)
    {
        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        float s = size;

        // Background circle
        using var bgBrush = new SolidBrush(Color.FromArgb(30, 120, 200));
        g.FillEllipse(bgBrush, 0, 0, s - 1, s - 1);

        // Microphone body (rounded rectangle)
        float bodyW  = s * 0.32f;
        float bodyH  = s * 0.40f;
        float bodyX  = (s - bodyW) / 2f;
        float bodyY  = s * 0.10f;
        float radius = bodyW / 2f;

        using var micBrush = new SolidBrush(Color.White);
        using var micPath  = RoundedRect(bodyX, bodyY, bodyW, bodyH, radius);
        g.FillPath(micBrush, micPath);

        // Arc below body
        float arcW = s * 0.54f;
        float arcH = s * 0.30f;
        float arcX = (s - arcW) / 2f;
        float arcY = bodyY + bodyH * 0.55f;

        using var arcPen = new Pen(Color.White, s * 0.07f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
        g.DrawArc(arcPen, arcX, arcY, arcW, arcH, 0, 180);

        // Vertical line (stand)
        float lineX    = s / 2f;
        float lineTopY = arcY + arcH / 2f;
        float lineBotY = s * 0.88f;
        g.DrawLine(arcPen, lineX, lineTopY, lineX, lineBotY);

        // Horizontal base
        float baseW = s * 0.38f;
        float baseX = (s - baseW) / 2f;
        g.DrawLine(arcPen, baseX, lineBotY, baseX + baseW, lineBotY);

        return bmp;
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(x, y, r * 2, r * 2, 180, 90);
        path.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
        path.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
        path.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
        path.CloseFigure();
        return path;
    }
}
