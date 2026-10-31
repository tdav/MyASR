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
        _detail = new TextBox();
        _retry = new Button();
        SuspendLayout();
        // 
        // _label
        // 
        _label.Location = new Point(16, 15);
        _label.Name = "_label";
        _label.Size = new Size(412, 36);
        _label.TabIndex = 0;
        _label.Text = "Подготовка...";
        // 
        // _progress
        // 
        _progress.Location = new Point(16, 57);
        _progress.Name = "_progress";
        _progress.Size = new Size(524, 24);
        _progress.Style = ProgressBarStyle.Continuous;
        _progress.TabIndex = 1;
        // 
        // _detail
        // 
        _detail.BackColor = SystemColors.Control;
        _detail.BorderStyle = BorderStyle.None;
        _detail.ForeColor = SystemColors.GrayText;
        _detail.Location = new Point(16, 89);
        _detail.Name = "_detail";
        _detail.ReadOnly = true;
        _detail.ScrollBars = ScrollBars.Vertical;
        _detail.Size = new Size(524, 16);
        _detail.TabIndex = 2;
        // 
        // _retry
        // 
        _retry.Location = new Point(440, 15);
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
        ClientSize = new Size(556, 100);
        Controls.Add(_label);
        Controls.Add(_progress);
        Controls.Add(_detail);
        Controls.Add(_retry);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Name = "DownloadForm";
        StartPosition = FormStartPosition.CenterScreen;
        Text = "Загрузка модели";
        ResumeLayout(false);
        PerformLayout();
    }

    private Label _label;
    private ProgressBar _progress;
    private TextBox _detail;
    private Button _retry;
}
