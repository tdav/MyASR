namespace MyASR;

partial class Form1
{
    private System.ComponentModel.IContainer? components = null;

    private ComboBox _deviceCombo = null!;
    private ComboBox _hotkeyCombo = null!;
    private CheckBox _globalHotkey = null!;
    private Button _micButton = null!;
    private Panel _vuMeter = null!;
    private Label _vuLabel = null!;
    private Label _statusLabel = null!;
    private RichTextBox _resultBox = null!;
    private Button _clearBtn = null!;
    private Button _copyBtn = null!;
    private Button _saveTxtBtn = null!;
    private Label _modelLabel = null!;

    protected override void Dispose(bool disposing)
    {
        if (disposing) components?.Dispose();
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(720, 540);
        Text = "MyASR — GigaAM v3 RNN-T";
        MinimumSize = new Size(720, 540);

        var deviceLabel = new Label { Text = "Микрофон:", Location = new Point(16, 0), Size = new Size(80, 16) };
        Controls.Add(deviceLabel);

        _deviceCombo = new ComboBox
        {
            Location = new Point(16, 16),
            Size = new Size(560, 24),
            DropDownStyle = ComboBoxStyle.DropDownList
        };

        _hotkeyCombo = new ComboBox
        {
            Location = new Point(16, 56),
            Size = new Size(120, 24),
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        _hotkeyCombo.Items.AddRange(new object[] { "F9", "F10", "F11", "F12" });
        _hotkeyCombo.SelectedIndex = 0;

        _globalHotkey = new CheckBox
        {
            Text = "Глобальный",
            Location = new Point(150, 58),
            Size = new Size(120, 22),
            Checked = true
        };

        _micButton = new Button
        {
            Text = "🎤  Hold",
            Location = new Point(16, 100),
            Size = new Size(160, 80),
            Font = new Font(Font.FontFamily, 14)
        };

        _vuMeter = new Panel
        {
            Location = new Point(196, 110),
            Size = new Size(360, 40),
            BorderStyle = BorderStyle.FixedSingle
        };
        _vuLabel = new Label
        {
            Location = new Point(196, 156),
            Size = new Size(360, 20),
            ForeColor = SystemColors.GrayText,
            Text = "-∞ dB"
        };

        _statusLabel = new Label
        {
            Location = new Point(16, 200),
            Size = new Size(688, 22),
            Text = "● Idle",
            Font = new Font(Font.FontFamily, 10, FontStyle.Bold)
        };

        _resultBox = new RichTextBox
        {
            Location = new Point(16, 232),
            Size = new Size(688, 240),
            ReadOnly = true,
            Font = new Font("Segoe UI", 10)
        };

        _clearBtn   = new Button { Location = new Point(16, 484),  Size = new Size(100, 30), Text = "Очистить" };
        _copyBtn    = new Button { Location = new Point(124, 484), Size = new Size(120, 30), Text = "Скопировать" };
        _saveTxtBtn = new Button { Location = new Point(252, 484), Size = new Size(140, 30), Text = "Сохранить TXT..." };
        _modelLabel = new Label
        {
            Location = new Point(420, 490),
            Size = new Size(284, 22),
            Text = "Model: v3_rnnt FP32",
            ForeColor = SystemColors.GrayText,
            TextAlign = ContentAlignment.MiddleRight
        };

        Controls.AddRange(new Control[]
        {
            _deviceCombo, _hotkeyCombo, _globalHotkey,
            _micButton, _vuMeter, _vuLabel, _statusLabel,
            _resultBox, _clearBtn, _copyBtn, _saveTxtBtn, _modelLabel
        });

        _vuMeter.Paint += OnVuPaint;
    }

    private void OnVuPaint(object? sender, PaintEventArgs e)
    {
        if (_vuMeter.Tag is not double pct) return;
        var w = (int)((_vuMeter.ClientSize.Width - 2) * pct);
        if (w <= 0) return;
        using var brush = new SolidBrush(Color.FromArgb(60, 180, 90));
        e.Graphics.FillRectangle(brush, 1, 1, w, _vuMeter.ClientSize.Height - 2);
    }
}
