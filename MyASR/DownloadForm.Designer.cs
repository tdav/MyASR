namespace MyASR;

partial class DownloadForm
{
    private System.ComponentModel.IContainer components = null;

    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null)) components.Dispose();
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        _label = new Label();
        _progress = new ProgressBar();
        _detail = new Label();
        _retry = new Button();
        SuspendLayout();
        // 
        // _label
        // 
        _label.AutoEllipsis = true;
        _label.Location = new Point(16, 21);
        _label.Name = "_label";
        _label.Size = new Size(223, 24);
        _label.TabIndex = 0;
        _label.Text = "Подготовка...";
        // 
        // _progress
        // 
        _progress.Location = new Point(16, 55);
        _progress.Name = "_progress";
        _progress.Size = new Size(329, 24);
        _progress.Style = ProgressBarStyle.Continuous;
        _progress.TabIndex = 1;
        // 
        // _detail
        // 
        _detail.ForeColor = SystemColors.GrayText;
        _detail.Location = new Point(16, 84);
        _detail.Name = "_detail";
        _detail.Size = new Size(329, 18);
        _detail.TabIndex = 2;
        // 
        // _retry
        // 
        _retry.Location = new Point(245, 15);
        _retry.Name = "_retry";
        _retry.Size = new Size(100, 30);
        _retry.TabIndex = 3;
        _retry.Text = "Повторить";
        _retry.Visible = false;
        // 
        // DownloadForm
        // 
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(364, 110);
        Controls.Add(_label);
        Controls.Add(_progress);
        Controls.Add(_detail);
        Controls.Add(_retry);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        Icon = MyASR.AppIcon.Get();
        MaximizeBox = false;
        MinimizeBox = false;
        Name = "DownloadForm";
        StartPosition = FormStartPosition.CenterScreen;
        Text = "Загрузка модели";
        ResumeLayout(false);
    }

    private Label _label;
    private ProgressBar _progress;
    private Label _detail;
    private Button _retry;
}
