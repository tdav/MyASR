using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace MyASR;

/// <summary>
/// Modern borderless dark-themed startup splash screen displaying initialisation stages and an
/// animated progress bar while ONNX models warm up in the background.
/// </summary>
[System.ComponentModel.DesignerCategory("Code")]
public sealed class SplashForm : Form
{
    private const int SplashWidth = 520;
    private const int SplashHeight = 300;
    private const float CornerRadius = 18f;

    private readonly Bitmap logoBitmap;
    private readonly System.Windows.Forms.Timer pulseTimer;
    private float shimmerPhase;

    public string CurrentStatus { get; private set; } = "Инициализация MyASR...";
    public double CurrentProgress { get; private set; } = 0.08;

    public SplashForm()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(SplashWidth, SplashHeight);
        BackColor = Color.FromArgb(14, 20, 38);
        ShowInTaskbar = true;
        Text = "MyASR";
        Icon = AppIcon.Get();

        logoBitmap = AppIcon.Render(88);

        using (var regionPath = AppIcon.RoundedRect(0, 0, SplashWidth, SplashHeight, CornerRadius))
            Region = new Region(regionPath);

        pulseTimer = new System.Windows.Forms.Timer { Interval = 33 };
        pulseTimer.Tick += (_, _) =>
        {
            shimmerPhase = (shimmerPhase + 0.032f) % 1f;
            Invalidate();
        };

        Shown += (_, _) => pulseTimer.Start();
        FormClosed += (_, _) => pulseTimer.Stop();
    }

    public void ReportStage(string status, double progress)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => ReportStage(status, progress));
            return;
        }

        CurrentStatus = status;
        CurrentProgress = Math.Clamp(progress, 0.0, 1.0);
        Invalidate();
        Update();
    }

    /// <summary>Renders the splash surface off-screen for deterministic verification.</summary>
    public Bitmap RenderPreviewBitmap()
    {
        var bmp = new Bitmap(ClientSize.Width, ClientSize.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        PaintSurface(g, new Rectangle(0, 0, bmp.Width, bmp.Height));
        return bmp;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        PaintSurface(e.Graphics, ClientRectangle);
    }

    private void PaintSurface(Graphics g, Rectangle bounds)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        // 1. Deep dark slate-indigo background
        using (var bgBrush = new LinearGradientBrush(
            bounds,
            Color.FromArgb(12, 18, 36),
            Color.FromArgb(20, 33, 61),
            LinearGradientMode.ForwardDiagonal))
        {
            g.FillRectangle(bgBrush, bounds);
        }

        // 2. Ambient cyan glow in top-right corner
        using (var glowPath = new GraphicsPath())
        {
            glowPath.AddEllipse(bounds.Width - 240, -90, 320, 260);
            using var glowBrush = new PathGradientBrush(glowPath)
            {
                CenterColor = Color.FromArgb(46, 6, 182, 212),
                SurroundColors = [Color.FromArgb(0, 6, 182, 212)]
            };
            g.FillPath(glowBrush, glowPath);
        }

        // 3. Icon + Header block
        int iconX = 38;
        int iconY = 48;
        g.DrawImage(logoBitmap, iconX, iconY, 88, 88);

        int textLeft = iconX + 108;
        using (var titleFont = new Font("Segoe UI Semibold", 24f, FontStyle.Bold, GraphicsUnit.Point))
        using (var titleBrush = new SolidBrush(Color.FromArgb(248, 250, 252)))
        {
            g.DrawString("MyASR", titleFont, titleBrush, textLeft - 4, iconY + 2);
        }

        using (var subFont = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point))
        using (var subBrush = new SolidBrush(Color.FromArgb(148, 163, 184)))
        {
            g.DrawString("Локальное нейросетевое распознавание речи", subFont, subBrush, textLeft, iconY + 48);
        }

        // 4. Pill badge ("ONNX Runtime • NativeAOT • Offline")
        using (var badgeFont = new Font("Segoe UI Semibold", 8.5f, FontStyle.Regular, GraphicsUnit.Point))
        {
            const string badgeText = "ONNX Runtime  •  GigaAM v3 e2e  •  NativeAOT";
            var badgeSize = g.MeasureString(badgeText, badgeFont);
            var badgeRect = new RectangleF(textLeft, iconY + 74, badgeSize.Width + 18, 23);
            using var badgePath = AppIcon.RoundedRect(badgeRect.X, badgeRect.Y, badgeRect.Width, badgeRect.Height, 11f);
            using var badgeFill = new SolidBrush(Color.FromArgb(40, 56, 189, 248));
            using var badgeBorder = new Pen(Color.FromArgb(85, 56, 189, 248), 1f);
            using var badgeTextBrush = new SolidBrush(Color.FromArgb(186, 230, 253));
            g.FillPath(badgeFill, badgePath);
            g.DrawPath(badgeBorder, badgePath);
            g.DrawString(badgeText, badgeFont, badgeTextBrush, badgeRect.X + 9, badgeRect.Y + 3);
        }

        // 5. Status caption + percentage
        int barX = 38;
        int barW = bounds.Width - 76;
        int barY = bounds.Height - 52;
        int barH = 8;

        using (var statusFont = new Font("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point))
        using (var statusBrush = new SolidBrush(Color.FromArgb(203, 213, 225)))
        using (var pctBrush = new SolidBrush(Color.FromArgb(103, 232, 249)))
        {
            var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            g.DrawString(CurrentStatus, statusFont, statusBrush, new RectangleF(barX, barY - 26, barW - 56, 20), format);

            var pctText = $"{(int)Math.Round(CurrentProgress * 100)}%";
            var pctSize = g.MeasureString(pctText, statusFont);
            g.DrawString(pctText, statusFont, pctBrush, barX + barW - pctSize.Width, barY - 26);
        }

        // 6. Progress track + animated gradient fill
        using (var trackPath = AppIcon.RoundedRect(barX, barY, barW, barH, barH / 2f))
        using (var trackBrush = new SolidBrush(Color.FromArgb(30, 41, 59)))
        {
            g.FillPath(trackBrush, trackPath);
        }

        float fillW = MathF.Max(barH, (float)(barW * CurrentProgress));
        if (CurrentProgress > 0.005)
        {
            using var fillPath = AppIcon.RoundedRect(barX, barY, fillW, barH, barH / 2f);
            using var fillBrush = new LinearGradientBrush(
                new PointF(barX, barY),
                new PointF(barX + barW, barY),
                Color.FromArgb(59, 130, 246),
                Color.FromArgb(34, 211, 238));
            g.FillPath(fillBrush, fillPath);

            // Animated shimmer highlight across filled region
            float shimmerW = MathF.Min(90f, fillW * 0.55f);
            if (shimmerW > 8f)
            {
                float sx = barX + (fillW - shimmerW) * shimmerPhase;
                var clipState = g.Save();
                try
                {
                    g.SetClip(fillPath);
                    using var shimmerBrush = new LinearGradientBrush(
                        new PointF(sx, barY),
                        new PointF(sx + shimmerW, barY),
                        Color.FromArgb(0, 255, 255, 255),
                        Color.FromArgb(0, 255, 255, 255));
                    shimmerBrush.InterpolationColors = new ColorBlend
                    {
                        Positions = [0f, 0.5f, 1f],
                        Colors =
                        [
                            Color.FromArgb(0, 255, 255, 255),
                            Color.FromArgb(120, 255, 255, 255),
                            Color.FromArgb(0, 255, 255, 255)
                        ]
                    };
                    g.FillRectangle(shimmerBrush, sx, barY, shimmerW, barH);
                }
                finally
                {
                    g.Restore(clipState);
                }
            }
        }

        // 7. Outer subtle border
        using var borderPath = AppIcon.RoundedRect(0.5f, 0.5f, bounds.Width - 1f, bounds.Height - 1f, CornerRadius);
        using var borderPen = new Pen(Color.FromArgb(65, 148, 163, 184), 1f);
        g.DrawPath(borderPen, borderPath);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            pulseTimer.Dispose();
            logoBitmap.Dispose();
        }
        base.Dispose(disposing);
    }
}
