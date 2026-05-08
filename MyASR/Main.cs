using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyASR.Asr;
using MyASR.Audio;
using MyASR.Hotkeys;
using MyASR.Models;
using System.Diagnostics;

namespace MyASR;

public partial class Main : Form
{
    private const int HotkeyId = 0xA001;
    private const int WM_HOTKEY = 0x0312;

    private const uint MOD_ALT     = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT   = 0x0004;
    private const uint MOD_WIN     = 0x0008;

    private const uint VK_SHIFT   = 0x10;
    private const uint VK_CONTROL = 0x11;
    private const uint VK_MENU    = 0x12; // Alt

    private readonly uint _hotkeyMod;
    private readonly uint _hotkeyVk;

    private readonly IServiceProvider sp;
    private readonly IAsrService asr;
    private readonly IMicCapture mic;
    private readonly IModelProvider modelProvider;
    private readonly ModelPaths paths;
    private readonly IHotkeyManager hotkeys;
    private readonly ILogger<Main>? logger;
    private readonly UserSettings settings = UserSettings.Load();

    private readonly System.Windows.Forms.Timer vuTimer = new() { Interval = 33 };
    private readonly System.Windows.Forms.Timer hotkeyPoll = new() { Interval = 16 };
    private readonly Stopwatch recordingTimer = new();
    private readonly System.Windows.Forms.Timer maxLengthTimer = new() { Interval = 30_000 };
    private bool hotkeyDownPrev;

    // Дизайнер WinForms требует parameterless-конструктор для рендера формы.
    // В рантайме всегда используется DI-конструктор ниже.
    public Main()
    {
        InitializeComponent();
        sp = null!; asr = null!; mic = null!;
        modelProvider = null!; paths = null!; hotkeys = null!;
    }

    public Main(
        IServiceProvider sp,
        IAsrService asr,
        IMicCapture mic,
        IModelProvider modelProvider,
        ModelPaths paths,
        IHotkeyManager hotkeys,
        ILogger<Main>? logger = null) : this()
    {
        this.sp = sp; this.asr = asr; this.mic = mic;
        this.modelProvider = modelProvider; this.paths = paths;
        this.hotkeys = hotkeys; this.logger = logger;

        const string DefaultHotkey = "Pause";
        (_hotkeyMod, _hotkeyVk) = ParseHotkey(settings.Hotkey);
        if (_hotkeyVk == 0)
        {
            settings.Hotkey = DefaultHotkey;
            (_hotkeyMod, _hotkeyVk) = ParseHotkey(DefaultHotkey);
        }

        this.asr.StateChanged += OnStateChanged;
        this.asr.ResultReady  += OnResultReady;
        this.asr.Failed       += OnFailed;

        vuTimer.Tick   += (_, _) => UpdateVu();
        hotkeyPoll.Tick += (_, _) => PollHotkey();
        maxLengthTimer.Tick += async (_, _) => { maxLengthTimer.Stop(); await this.asr.StopAndRecognizeAsync(); };

        micButton.MouseDown += (_, _) => StartRecording();
        micButton.MouseUp   += async (_, _) => await StopRecording();
        micButton.MouseLeave += async (_, _) => { if (this.asr.State == AsrState.Recording) await StopRecording(); };

        clearBtn.Click   += (_, _) => resultBox.Clear();
        copyBtn.Click    += (_, _) => CopyToClipboard();
        saveTxtBtn.Click += (_, _) => SaveAsTxt();

        Shown += async (_, _) => await OnFormShownAsync();
        FormClosed += (_, _) => Cleanup();
    }

    private async Task OnFormShownAsync()
    {
        PopulateDevices();
        if (settings.MicDeviceId is { } savedId)
        {
            var devices = mic.EnumerateDevices();
            var idx = -1;
            for (var i = 0; i < devices.Count; i++)
                if (devices[i].Id == savedId) { idx = i; break; }
            if (idx >= 0) _deviceCombo.SelectedIndex = idx;
        }
        
        micButton.Enabled = false;

        if (!modelProvider.AreModelsAvailable(paths))
        {
            statusLabel.Text = "⏬ Загрузка моделей...";
            using var dlg = sp.GetRequiredService<DownloadForm>();
            if (dlg.ShowDialog(this) != DialogResult.OK)
            {
                MessageBox.Show("Модели не загружены — приложение будет закрыто.",
                                "MyASR", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                Close();
                return;
            }
        }

        statusLabel.Text = "⏳ Загрузка модели в память...";
        UseWaitCursor = true;
        Refresh();
        try
        {
            await asr.WarmupAsync();
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Model warmup failed");
            UseWaitCursor = false;
            MessageBox.Show($"Не удалось загрузить ONNX-модели:\n{ex.Message}",
                            "MyASR", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }
        UseWaitCursor = false;

        hotkeys.Register(Handle, HotkeyId, _hotkeyMod, _hotkeyVk);
        hotkeyPoll.Start();
        micButton.Enabled = true;
        statusLabel.Text = "● Idle (модели готовы)";
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && (int)m.WParam == HotkeyId)
        {
            if (asr.State == AsrState.Idle && !hotkeyDownPrev)
                StartRecording();
        }
        base.WndProc(ref m);
    }

    private void PollHotkey()
    {
        // Хоткей считается удержанным, только если основная клавиша
        // и все требуемые модификаторы нажаты одновременно.
        var down = hotkeys.IsKeyDown(_hotkeyVk)
            && ((_hotkeyMod & MOD_CONTROL) == 0 || hotkeys.IsKeyDown(VK_CONTROL))
            && ((_hotkeyMod & MOD_SHIFT)   == 0 || hotkeys.IsKeyDown(VK_SHIFT))
            && ((_hotkeyMod & MOD_ALT)     == 0 || hotkeys.IsKeyDown(VK_MENU));
        if (hotkeyDownPrev && !down && asr.State == AsrState.Recording)
            _ = asr.StopAndRecognizeAsync();
        hotkeyDownPrev = down;
    }

    private void StartRecording()
    {
        if (asr.State != AsrState.Idle) return;
        if (_deviceCombo.SelectedItem is not DeviceInfo dev) return;
        asr.StartCapture(dev.Id);
        recordingTimer.Restart();
        vuTimer.Start();
        maxLengthTimer.Start();
    }

    private async Task StopRecording()
    {
        vuTimer.Stop();
        maxLengthTimer.Stop();
        recordingTimer.Stop();
        if (asr.State != AsrState.Recording) return;
        await asr.StopAndRecognizeAsync();
    }

    private void OnStateChanged(AsrStateChanged s)
    {
        if (InvokeRequired) { BeginInvoke(() => OnStateChanged(s)); return; }
        statusLabel.Text = s.State switch
        {
            AsrState.Idle      => "● Idle",
            AsrState.Recording => $"⏺ Recording {recordingTimer.Elapsed.TotalSeconds:F1}s",
            AsrState.Decoding  => "⏳ Decoding...",
            _                  => "?"
        };
        micButton.Enabled = s.State != AsrState.Decoding;
    }

    private void OnResultReady(AsrResult r)
    {
        if (InvokeRequired) { BeginInvoke(() => OnResultReady(r)); return; }
        if (string.IsNullOrWhiteSpace(r.Text)) return;
        var line = r.Text.Trim();

        resultBox.AppendText(line + Environment.NewLine);
        resultBox.SelectionStart = resultBox.TextLength;
        resultBox.ScrollToCaret();
    }

    private void OnFailed(Exception ex)
    {
        if (InvokeRequired) { BeginInvoke(() => OnFailed(ex)); return; }
        logger?.LogError(ex, "ASR failure");
        resultBox.AppendText($"[{DateTime.Now:HH:mm:ss}] [ошибка распознавания: {ex.Message}]" + Environment.NewLine);
    }

    private void PopulateDevices()
    {
        _deviceCombo.DataSource    = mic.EnumerateDevices().ToList();
        _deviceCombo.DisplayMember = nameof(DeviceInfo.FriendlyName);
        if (_deviceCombo.Items.Count > 0) _deviceCombo.SelectedIndex = 0;
    }

    private void UpdateVu()
    {
        var db = Math.Clamp(mic.CurrentRmsDb, -60, 0);
        var pct = (db + 60) / 60.0;
        _vuMeter.Tag = pct;
        _vuMeter.Invalidate();
        _vuLabel.Text = $"{db:F0} dB";
        statusLabel.Text = $"⏺ Recording {recordingTimer.Elapsed.TotalSeconds:F1}s";
    }

    private void CopyToClipboard()
    {
        if (resultBox.TextLength == 0) return;
        Clipboard.SetText(resultBox.Text);
    }

    private void SaveAsTxt()
    {
        using var dlg = new SaveFileDialog { Filter = "Text|*.txt", FileName = "MyASR.txt" };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            File.WriteAllText(dlg.FileName, resultBox.Text);
    }

    private void OnVuPaint(object? sender, PaintEventArgs e)
    {
        if (_vuMeter.Tag is not double pct) return;
        var w = (int)((_vuMeter.ClientSize.Width - 2) * pct);
        if (w <= 0) return;
        using var brush = new SolidBrush(Color.FromArgb(60, 180, 90));
        e.Graphics.FillRectangle(brush, 1, 1, w, _vuMeter.ClientSize.Height - 2);
    }

    /// <summary>Parses a hotkey string like "Ctrl+Shift+X" into Win32 modifiers and virtual key code.</summary>
    private static (uint modifiers, uint vk) ParseHotkey(string hotkey)
    {
        uint modifiers = 0;
        uint vk = 0;

        foreach (var part in hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            modifiers |= part.ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => MOD_CONTROL,
                "ALT"               => MOD_ALT,
                "SHIFT"             => MOD_SHIFT,
                "WIN"               => MOD_WIN,
                _                   => 0u
            };

            if (part.Length == 1 && char.IsLetter(part[0]))
            {
                vk = (uint)char.ToUpperInvariant(part[0]);
                continue;
            }

            vk = part.ToUpperInvariant() switch
            {
                "F1"  => 0x70u, "F2"  => 0x71u, "F3"  => 0x72u, "F4"  => 0x73u,
                "F5"  => 0x74u, "F6"  => 0x75u, "F7"  => 0x76u, "F8"  => 0x77u,
                "F9"  => 0x78u, "F10" => 0x79u, "F11" => 0x7Au, "F12" => 0x7Bu,
                "SPACE" => 0x20u, "ENTER" or "RETURN" => 0x0Du,
                "TAB"   => 0x09u, "ESC"  or "ESCAPE"  => 0x1Bu,
                "INS"   or "INSERT" => 0x2Du, "DEL" or "DELETE" => 0x2Eu,
                "HOME" => 0x24u, "END" => 0x23u,
                "PGUP" or "PAGEUP" => 0x21u, "PGDN" or "PAGEDOWN" => 0x22u,
                "PAUSE" or "BREAK" => 0x13u,
                "SCROLL" or "SCROLLLOCK" => 0x91u,
                _ => vk
            };
        }

        return (modifiers, vk);
    }

    private void Cleanup()
    {
        vuTimer.Stop();
        hotkeyPoll.Stop();
        maxLengthTimer.Stop();
        if (_deviceCombo.SelectedItem is DeviceInfo dev) settings.MicDeviceId = dev.Id;        
        settings.GlobalHotkey = true;
        settings.Save();
        hotkeys.Dispose();
    }
}
