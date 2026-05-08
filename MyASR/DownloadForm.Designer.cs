namespace MyASR;

partial class DownloadForm
{
    private System.ComponentModel.IContainer? components = null;
    private Label _label = null!;
    private ProgressBar _progress = null!;
    private Label _detail = null!;
    private Button _retry = null!;

    protected override void Dispose(bool disposing)
    {
        if (disposing) components?.Dispose();
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(520, 160);
        Text = "Загрузка моделей";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;

        _label = new Label
        {
            Text = "Подготовка...",
            Location = new Point(16, 16),
            Size = new Size(488, 24),
            AutoEllipsis = true
        };
        _progress = new ProgressBar
        {
            Location = new Point(16, 50),
            Size = new Size(488, 24),
            Style = ProgressBarStyle.Continuous,
            Minimum = 0, Maximum = 100
        };
        _detail = new Label
        {
            Text = "",
            Location = new Point(16, 84),
            Size = new Size(488, 18),
            ForeColor = SystemColors.GrayText
        };
        _retry = new Button
        {
            Text = "Повторить",
            Location = new Point(404, 110),
            Size = new Size(100, 30),
            Visible = false
        };
        Controls.AddRange([_label, _progress, _detail, _retry]);
    }
}
