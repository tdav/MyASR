namespace MyASR;

partial class Main
{
    private System.ComponentModel.IContainer components = null;

    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null)) components.Dispose();
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(Main));
        _deviceLabel = new Label();
        _deviceCombo = new ComboBox();
        micButton = new Button();
        _vuMeter = new Panel();
        resultBox = new RichTextBox();
        clearBtn = new Button();
        copyBtn = new Button();
        saveTxtBtn = new Button();
        statusStrip1 = new StatusStrip();
        statusLabel = new ToolStripStatusLabel();
        _vuLabel = new ToolStripStatusLabel();
        toolStripStatusLabel1 = new ToolStripStatusLabel();
        _modelLabel = new ToolStripStatusLabel();
        statusStrip1.SuspendLayout();
        SuspendLayout();
        // 
        // _deviceLabel
        // 
        _deviceLabel.Location = new Point(16, 22);
        _deviceLabel.Name = "_deviceLabel";
        _deviceLabel.Size = new Size(80, 16);
        _deviceLabel.TabIndex = 0;
        _deviceLabel.Text = "Микрофон:";
        // 
        // _deviceCombo
        // 
        _deviceCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _deviceCombo.Location = new Point(102, 19);
        _deviceCombo.Name = "_deviceCombo";
        _deviceCombo.Size = new Size(574, 23);
        _deviceCombo.TabIndex = 1;
        // 
        // micButton
        // 
        micButton.Location = new Point(16, 51);
        micButton.Name = "micButton";
        micButton.Size = new Size(160, 29);
        micButton.TabIndex = 4;
        micButton.Text = "Hold";
        // 
        // _vuMeter
        // 
        _vuMeter.BorderStyle = BorderStyle.FixedSingle;
        _vuMeter.Location = new Point(186, 54);
        _vuMeter.Name = "_vuMeter";
        _vuMeter.Size = new Size(490, 23);
        _vuMeter.TabIndex = 5;
        _vuMeter.Paint += OnVuPaint;
        // 
        // resultBox
        // 
        resultBox.Font = new Font("Segoe UI", 10F);
        resultBox.Location = new Point(16, 93);
        resultBox.Name = "resultBox";
        resultBox.ReadOnly = true;
        resultBox.Size = new Size(660, 272);
        resultBox.TabIndex = 6;
        resultBox.Text = "";
        // 
        // clearBtn
        // 
        clearBtn.Location = new Point(16, 377);
        clearBtn.Name = "clearBtn";
        clearBtn.Size = new Size(100, 30);
        clearBtn.TabIndex = 7;
        clearBtn.Text = "Очистить";
        // 
        // copyBtn
        // 
        copyBtn.Location = new Point(124, 377);
        copyBtn.Name = "copyBtn";
        copyBtn.Size = new Size(120, 30);
        copyBtn.TabIndex = 8;
        copyBtn.Text = "Скопировать";
        // 
        // saveTxtBtn
        // 
        saveTxtBtn.Location = new Point(252, 377);
        saveTxtBtn.Name = "saveTxtBtn";
        saveTxtBtn.Size = new Size(140, 30);
        saveTxtBtn.TabIndex = 9;
        saveTxtBtn.Text = "Сохранить TXT...";
        // 
        // statusStrip1
        // 
        statusStrip1.Items.AddRange(new ToolStripItem[] { statusLabel, _vuLabel, toolStripStatusLabel1, _modelLabel });
        statusStrip1.Location = new Point(0, 422);
        statusStrip1.Name = "statusStrip1";
        statusStrip1.Size = new Size(689, 22);
        statusStrip1.TabIndex = 10;
        // 
        // statusLabel
        // 
        statusLabel.Name = "statusLabel";
        statusLabel.Size = new Size(483, 17);
        statusLabel.Spring = true;
        statusLabel.Text = "Статус: Idle";
        statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        // 
        // _vuLabel
        // 
        _vuLabel.ForeColor = SystemColors.GrayText;
        _vuLabel.Name = "_vuLabel";
        _vuLabel.Size = new Size(64, 17);
        _vuLabel.Text = "VU: -inf dB";
        // 
        // toolStripStatusLabel1
        // 
        toolStripStatusLabel1.Name = "toolStripStatusLabel1";
        toolStripStatusLabel1.Size = new Size(13, 17);
        toolStripStatusLabel1.Text = "  ";
        // 
        // _modelLabel
        // 
        _modelLabel.ForeColor = SystemColors.GrayText;
        _modelLabel.Name = "_modelLabel";
        _modelLabel.Size = new Size(114, 17);
        _modelLabel.Text = "Model: v3_rnnt FP32";
        // 
        // Main
        // 
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(689, 444);
        Controls.Add(_deviceLabel);
        Controls.Add(_deviceCombo);
        Controls.Add(micButton);
        Controls.Add(_vuMeter);
        Controls.Add(resultBox);
        Controls.Add(clearBtn);
        Controls.Add(copyBtn);
        Controls.Add(saveTxtBtn);
        Controls.Add(statusStrip1);
        Icon = (Icon)resources.GetObject("$this.Icon");
        MinimumSize = new Size(220, 240);
        Name = "Main";
        StartPosition = FormStartPosition.CenterParent;
        Text = "MyASR — GigaAM v3 RNN-T";
        statusStrip1.ResumeLayout(false);
        statusStrip1.PerformLayout();
        ResumeLayout(false);
        PerformLayout();
    }

    private Label _deviceLabel;
    private ComboBox _deviceCombo;
    private Button micButton;
    private Panel _vuMeter;
    private ToolStripStatusLabel _vuLabel;
    private ToolStripStatusLabel statusLabel;
    private RichTextBox resultBox;
    private Button clearBtn;
    private Button copyBtn;
    private Button saveTxtBtn;
    private ToolStripStatusLabel _modelLabel;
    private StatusStrip statusStrip1;
    private ToolStripStatusLabel toolStripStatusLabel1;
}
