using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyASR.Asr;
using MyASR.Audio;
using MyASR.Hotkeys;
using MyASR.Models;
using System.Diagnostics;

namespace MyASR;

public partial class Form1 : Form
{
    private const int HotkeyId = 0xA001;
    private const int WM_HOTKEY = 0x0312;
    private const uint VK_F9 = 0x78;

    private readonly IServiceProvider _sp;
    private readonly IAsrService _asr;
    private readonly IMicCapture _mic;
    private readonly IModelProvider _modelProvider;
    private readonly ModelPaths _paths;
    private readonly IHotkeyManager _hotkeys;
    private readonly ILogger<Form1>? _logger;
    private readonly UserSettings _settings = UserSettings.Load();

    private readonly System.Windows.Forms.Timer _vuTimer = new() { Interval = 33 };
    private readonly System.Windows.Forms.Timer _hotkeyPoll = new() { Interval = 16 };
    private readonly Stopwatch _recordingTimer = new();
    private readonly System.Windows.Forms.Timer _maxLengthTimer = new() { Interval = 30_000 };
    private bool _hotkeyDownPrev;

    // Дизайнер WinForms требует parameterless-конструктор для рендера формы.
    // В рантайме всегда используется DI-конструктор ниже.
    public Form1()
    {
        InitializeComponent();
        _sp = null!; _asr = null!; _mic = null!;
        _modelProvider = null!; _paths = null!; _hotkeys = null!;
    }

    public Form1(
        IServiceProvider sp,
        IAsrService asr,
        IMicCapture mic,
        IModelProvider modelProvider,
        ModelPaths paths,
        IHotkeyManager hotkeys,
        ILogger<Form1>? logger = null) : this()
    {
        _sp = sp; _asr = asr; _mic = mic;
        _modelProvider = modelProvider; _paths = paths;
        _hotkeys = hotkeys; _logger = logger;

        _asr.StateChanged += OnStateChanged;
        _asr.ResultReady  += OnResultReady;
        _asr.Failed       += OnFailed;

        _vuTimer.Tick   += (_, _) => UpdateVu();
        _hotkeyPoll.Tick += (_, _) => PollHotkey();
        _maxLengthTimer.Tick += async (_, _) => { _maxLengthTimer.Stop(); await _asr.StopAndRecognizeAsync(); };

        _micButton.MouseDown += (_, _) => StartRecording();
        _micButton.MouseUp   += async (_, _) => await StopRecording();
        _micButton.MouseLeave += async (_, _) => { if (_asr.State == AsrState.Recording) await StopRecording(); };

        _clearBtn.Click   += (_, _) => _resultBox.Clear();
        _copyBtn.Click    += (_, _) => CopyToClipboard();
        _saveTxtBtn.Click += (_, _) => SaveAsTxt();

        Shown += async (_, _) => await OnFormShownAsync();
        FormClosed += (_, _) => Cleanup();
    }

    private async Task OnFormShownAsync()
    {
        PopulateDevices();
        if (_settings.MicDeviceId is { } savedId)
        {
            var devices = _mic.EnumerateDevices();
            var idx = -1;
            for (var i = 0; i < devices.Count; i++)
                if (devices[i].Id == savedId) { idx = i; break; }
            if (idx >= 0) _deviceCombo.SelectedIndex = idx;
        }
        if (_hotkeyCombo.Items.Contains(_settings.Hotkey))
            _hotkeyCombo.SelectedItem = _settings.Hotkey;
        _globalHotkey.Checked = _settings.GlobalHotkey;

        _micButton.Enabled = false;

        if (!_modelProvider.AreModelsAvailable(_paths))
        {
            _statusLabel.Text = "⏬ Загрузка моделей...";
            using var dlg = _sp.GetRequiredService<DownloadForm>();
            if (dlg.ShowDialog(this) != DialogResult.OK)
            {
                MessageBox.Show("Модели не загружены — приложение будет закрыто.",
                                "MyASR", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                Close();
                return;
            }
        }

        _statusLabel.Text = "⏳ Загрузка модели в память...";
        UseWaitCursor = true;
        Refresh();
        try
        {
            await _asr.WarmupAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Model warmup failed");
            UseWaitCursor = false;
            MessageBox.Show($"Не удалось загрузить ONNX-модели:\n{ex.Message}",
                            "MyASR", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }
        UseWaitCursor = false;

        _hotkeys.Register(Handle, HotkeyId, 0, VK_F9);
        _hotkeyPoll.Start();
        _micButton.Enabled = true;
        _statusLabel.Text = "● Idle (модели готовы)";
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && (int)m.WParam == HotkeyId)
        {
            if (_asr.State == AsrState.Idle && !_hotkeyDownPrev)
                StartRecording();
        }
        base.WndProc(ref m);
    }

    private void PollHotkey()
    {
        var down = _hotkeys.IsKeyDown(VK_F9);
        if (_hotkeyDownPrev && !down && _asr.State == AsrState.Recording)
            _ = _asr.StopAndRecognizeAsync();
        _hotkeyDownPrev = down;
    }

    private void StartRecording()
    {
        if (_asr.State != AsrState.Idle) return;
        if (_deviceCombo.SelectedItem is not DeviceInfo dev) return;
        _asr.StartCapture(dev.Id);
        _recordingTimer.Restart();
        _vuTimer.Start();
        _maxLengthTimer.Start();
    }

    private async Task StopRecording()
    {
        _vuTimer.Stop();
        _maxLengthTimer.Stop();
        _recordingTimer.Stop();
        if (_asr.State != AsrState.Recording) return;
        await _asr.StopAndRecognizeAsync();
    }

    private void OnStateChanged(AsrStateChanged s)
    {
        if (InvokeRequired) { BeginInvoke(() => OnStateChanged(s)); return; }
        _statusLabel.Text = s.State switch
        {
            AsrState.Idle      => "● Idle",
            AsrState.Recording => $"⏺ Recording {_recordingTimer.Elapsed.TotalSeconds:F1}s",
            AsrState.Decoding  => "⏳ Decoding...",
            _                  => "?"
        };
        _micButton.Enabled = s.State != AsrState.Decoding;
    }

    private void OnResultReady(AsrResult r)
    {
        if (InvokeRequired) { BeginInvoke(() => OnResultReady(r)); return; }
        if (string.IsNullOrWhiteSpace(r.Text)) return;
        var line = $"[{DateTime.Now:HH:mm:ss}] {r.Text.Trim()}";
        _resultBox.AppendText(line + Environment.NewLine);
        _resultBox.SelectionStart = _resultBox.TextLength;
        _resultBox.ScrollToCaret();
    }

    private void OnFailed(Exception ex)
    {
        if (InvokeRequired) { BeginInvoke(() => OnFailed(ex)); return; }
        _logger?.LogError(ex, "ASR failure");
        _resultBox.AppendText($"[{DateTime.Now:HH:mm:ss}] [ошибка распознавания: {ex.Message}]" + Environment.NewLine);
    }

    private void PopulateDevices()
    {
        _deviceCombo.DataSource    = _mic.EnumerateDevices().ToList();
        _deviceCombo.DisplayMember = nameof(DeviceInfo.FriendlyName);
        if (_deviceCombo.Items.Count > 0) _deviceCombo.SelectedIndex = 0;
    }

    private void UpdateVu()
    {
        var db = Math.Clamp(_mic.CurrentRmsDb, -60, 0);
        var pct = (db + 60) / 60.0;
        _vuMeter.Tag = pct;
        _vuMeter.Invalidate();
        _vuLabel.Text = $"{db:F0} dB";
        _statusLabel.Text = $"⏺ Recording {_recordingTimer.Elapsed.TotalSeconds:F1}s";
    }

    private void CopyToClipboard()
    {
        if (_resultBox.TextLength == 0) return;
        Clipboard.SetText(_resultBox.Text);
    }

    private void SaveAsTxt()
    {
        using var dlg = new SaveFileDialog { Filter = "Text|*.txt", FileName = "MyASR.txt" };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            File.WriteAllText(dlg.FileName, _resultBox.Text);
    }

    private void Cleanup()
    {
        _vuTimer.Stop();
        _hotkeyPoll.Stop();
        _maxLengthTimer.Stop();
        if (_deviceCombo.SelectedItem is DeviceInfo dev) _settings.MicDeviceId = dev.Id;
        if (_hotkeyCombo.SelectedItem is string hk) _settings.Hotkey = hk;
        _settings.GlobalHotkey = _globalHotkey.Checked;
        _settings.Save();
        _hotkeys.Dispose();
    }
}
