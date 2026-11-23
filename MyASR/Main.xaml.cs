using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using MyASR.Asr;
using MyASR.Audio;
using MyASR.Hotkeys;
using MyASR.Models;

namespace MyASR;

public partial class Main : Window
{
    private const int HotkeyId = 0xA001;
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8;

    private readonly IAsrService asr;
    private readonly IMicCapture mic;
    private readonly IModelProvider modelProvider;
    private readonly Func<ModelDescriptor, ModelPaths> pathsFor;
    private readonly Func<ModelDescriptor, DownloadForm> downloadFormFor;
    private readonly IHotkeyManager hotkeys;
    private readonly ILogger<Main>? logger;
    private readonly UserSettings settings = UserSettings.Load();
    private readonly DispatcherTimer meterTimer = new() { Interval = TimeSpan.FromMilliseconds(32) };
    private readonly Stopwatch clock = new();
    private readonly uint _hotkeyMod, _hotkeyVk;
    private ModelDescriptor selectedModel;
    private HwndSource? source;
    private bool modelReady, preparing = true, closeRequested, closed;
    private bool hotkeyRegistered;
    private bool suppressSelection;
    private Task? stopping;
    private int historyLength;
    private string draft = "";
    private bool transcriptSessionActive;
    private readonly Action<string> writeClipboard;

    public Main(IAsrService asr, IMicCapture mic, IModelProvider modelProvider,
        Func<ModelDescriptor, ModelPaths> pathsFor, Func<ModelDescriptor, DownloadForm> downloadFormFor,
        IHotkeyManager hotkeys, ILogger<Main>? logger = null, Action<string>? writeClipboard = null)
    {
        InitializeComponent();
        this.asr = asr;
        this.mic = mic;
        this.modelProvider = modelProvider;
        this.pathsFor = pathsFor;
        this.downloadFormFor = downloadFormFor;
        this.hotkeys = hotkeys;
        this.logger = logger;
        this.writeClipboard = writeClipboard ?? System.Windows.Clipboard.SetText;
        selectedModel = ModelCatalog.ById(settings.ModelId);
        (_hotkeyMod, _hotkeyVk) = ParseHotkey(settings.Hotkey);
        if (_hotkeyVk == 0)
        {
            settings.Hotkey = "Ctrl+Shift+X";
            (_hotkeyMod, _hotkeyVk) = ParseHotkey(settings.Hotkey);
        }

        SourceInitialized += (_, _) =>
        {
            source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            source?.AddHook(WindowMessage);
        };
        Closing += OnClosing;
        Closed += (_, _) => Cleanup();
        asr.StateChanged += OnRecognitionStateChanged;
        asr.PreviewReady += PreviewReady;
        asr.ResultReady += ResultReady;
        asr.Failed += RecognitionFailed;
        meterTimer.Tick += (_, _) => UpdateMeter();
        ModelCombo.SelectionChanged += async (_, _) => await SwitchSelectionAsync();
        RecordButton.Click += (_, _) => ToggleRecording();
        RecordButton.PreviewKeyDown += (_, e) =>
        {
            if (e.IsRepeat && e.Key is Key.Enter or Key.Space) e.Handled = true;
        };
        ClearButton.Click += (_, _) =>
        {
            ResultBox.Select(0, historyLength);
            ResultBox.SelectedText = "";
            historyLength = 0;
            RefreshTextActions();
        };
        CopyButton.Click += (_, _) => CopyText(ResultBox.Text);
        SaveButton.Click += (_, _) => SaveText();
    }

    internal async Task<bool> PrepareAsync(SplashForm splash, CancellationToken cancellation)
    {
        try
        {
            await splash.ShowStageAsync(1, "Поиск микрофонов…");
            var devices = await Task.Run(mic.EnumerateDevices, cancellation);
            cancellation.ThrowIfCancellationRequested();
            DeviceCombo.ItemsSource = devices;
            DeviceCombo.SelectedItem = devices.FirstOrDefault(d => d.Id == settings.MicDeviceId) ?? devices.FirstOrDefault();
            ModelCombo.ItemsSource = ModelCatalog.All;
            ModelCombo.SelectedItem = selectedModel;

            await splash.ShowStageAsync(2, "Проверка файлов модели…");
            await Task.Run(() => LegacyModelLayout.Migrate(ModelRoot.Resolve(), ModelCatalog.GigaAmV3, logger), cancellation);
            if (!await EnsureModelPresentAsync(selectedModel, splash, cancellation)) return false;

            await splash.ShowStageAsync(3, $"Загрузка «{selectedModel.DisplayName}» в память…", indeterminate: true);
            cancellation.ThrowIfCancellationRequested();
            await LoadModelAsync(selectedModel);
            cancellation.ThrowIfCancellationRequested();
            modelReady = true;

            await splash.ShowStageAsync(4, "Настройка горячих клавиш и автозапуска…");
            cancellation.ThrowIfCancellationRequested();
            hotkeyRegistered = settings.GlobalHotkey && hotkeys.Register(new WindowInteropHelper(this).EnsureHandle(),
                HotkeyId, _hotkeyMod | 0x4000, _hotkeyVk); // MOD_NOREPEAT
            HotkeyHint.Text = hotkeyRegistered ? $"Начать или остановить: {settings.Hotkey}" : "Нажмите ещё раз для остановки";
            await Task.Run(() =>
            {
                if (!StartupManager.IsEnabled()) StartupManager.SetEnabled(true);
            }, cancellation);
            cancellation.ThrowIfCancellationRequested();
            return true;
        }
        finally
        {
            preparing = false;
            UpdateControls();
        }
    }

    private async Task<bool> EnsureModelPresentAsync(ModelDescriptor descriptor, Window owner, CancellationToken cancellation = default)
    {
        var available = await Task.Run(() => modelProvider.AreModelsAvailable(pathsFor(descriptor)), cancellation);
        cancellation.ThrowIfCancellationRequested();
        if (available) return true;
        StatusLabel.Text = $"Загрузка файлов модели «{descriptor.DisplayName}»…";
        var dialog = downloadFormFor(descriptor);
        dialog.Owner = owner;
        return dialog.ShowDialog() == true;
    }

    private async Task LoadModelAsync(ModelDescriptor model)
    {
        StatusLabel.Text = $"Загрузка модели «{model.DisplayName}» в память…";
        if (asr.CurrentModel.Id == model.Id) await asr.WarmupAsync();
        else await asr.SwitchModelAsync(model, CancellationToken.None);
    }

    private async Task SwitchSelectionAsync()
    {
        if (suppressSelection || preparing || ModelCombo.SelectedItem is not ModelDescriptor chosen || chosen == selectedModel) return;
        if (!ModelSelectionPolicy.CanSwitch(asr.State))
        {
            RestoreSelection();
            return;
        }

        preparing = true;
        UpdateControls();
        try
        {
            if (!await EnsureModelPresentAsync(chosen, this)) return;
            try
            {
                await LoadModelAsync(chosen);
            }
            catch (Exception ex)
            {
                ShowError("Не удалось сменить модель", ex);
                modelReady = false;
                await LoadModelAsync(selectedModel);
                modelReady = true;
                return;
            }

            selectedModel = chosen;
            modelReady = true;
            settings.ModelId = chosen.Id;
            settings.Save();
        }
        catch (Exception ex) { ShowError("Ошибка подготовки модели", ex); }
        finally
        {
            RestoreSelection();
            preparing = false;
            UpdateControls();
            if (closeRequested) Close();
        }
    }

    private void RestoreSelection()
    {
        suppressSelection = true;
        ModelCombo.SelectedItem = selectedModel;
        suppressSelection = false;
    }

    private void UpdateControls()
    {
        bool idle = asr.State == AsrState.Idle;
        bool recording = asr.State == AsrState.Recording;
        ModelCombo.IsEnabled = !preparing && idle;
        DeviceCombo.IsEnabled = ModelCombo.IsEnabled;
        RecordButton.IsEnabled = !preparing && modelReady && DeviceCombo.SelectedItem != null && (idle || recording);
        RecordLabel.Text = recording ? "Остановить запись" : "Начать запись";
        RecordGlyph.SetResourceReference(System.Windows.Shapes.Path.DataProperty, recording ? "StopShape" : "MicShape");
        if (recording) RecordGlyph.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AccentText");
        else RecordGlyph.ClearValue(System.Windows.Shapes.Shape.FillProperty);
        StatusLabel.Text = preparing ? "Подготовка модели…" : asr.State switch
        {
            AsrState.Recording => "Текст появляется по мере речи и может уточняться.",
            AsrState.Decoding => "Уточняем итоговую расшифровку…",
            _ when !modelReady => ModelSelectionPolicy.NoModelLoadedStatusText(),
            _ when DeviceCombo.SelectedItem == null => "Подключите микрофон и перезапустите приложение.",
            _ when settings.GlobalHotkey && !hotkeyRegistered => $"Сочетание {settings.Hotkey} занято. Для записи используйте кнопку.",
            _ => selectedModel.LicenceNote ?? "Последняя распознанная фраза автоматически копируется в буфер обмена."
        };
    }

    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            ToggleRecording();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void ToggleRecording()
    {
        if (preparing || closeRequested || stopping is not null) return;
        if (asr.State == AsrState.Recording)
        {
            StopRecording();
            return;
        }
        if (asr.State != AsrState.Idle || !modelReady || DeviceCombo.SelectedItem is not DeviceInfo device) return;
        try
        {
            asr.StartCapture(device.Id);
            clock.Restart();
            meterTimer.Start();
        }
        catch (Exception ex) { ShowError("Не удалось открыть микрофон", ex); }
    }

    private void UpdateMeter()
    {
        var db = Math.Clamp(mic.CurrentRmsDb, -60, 0);
        LevelMeter.Value = db + 60;
        LevelLabel.Text = $"{db:F0} дБ";
        ClockLabel.Text = $"{(int)clock.Elapsed.TotalMinutes:00}:{clock.Elapsed.Seconds:00}";
    }

    private async void StopRecording()
    {
        if (stopping != null || asr.State != AsrState.Recording) return;
        meterTimer.Stop();
        clock.Stop();
        LevelMeter.Value = 0;
        LevelLabel.Text = "— дБ";
        ClockLabel.Text = "00:00";
        try
        {
            stopping = asr.StopAndRecognizeAsync();
            await stopping;
        }
        catch (Exception ex) { ShowError("Ошибка завершения записи", ex); }
        finally
        {
            stopping = null;
            if (closeRequested) await Dispatcher.InvokeAsync(Close, DispatcherPriority.Background);
        }
    }

    // Preview, final text, and state events share one dispatcher queue so session boundaries retain
    // their order even when the service completes on a worker and the next recording starts on the UI.
    private void PostRecognitionEvent(Action action) => Dispatcher.InvokeAsync(() =>
    {
        if (!closed) action();
    });

    private void OnRecognitionStateChanged(AsrStateChanged change) => PostRecognitionEvent(() =>
    {
        if (change.State == AsrState.Recording) transcriptSessionActive = true;
        if (change.State == AsrState.Idle && transcriptSessionActive)
        {
            ReplaceDraft("");
            transcriptSessionActive = false;
        }
        RefreshTextActions();
        UpdateControls();
    });

    private void PreviewReady(string text) => PostRecognitionEvent(() =>
    {
        if (!transcriptSessionActive) return;
        ReplaceDraft(text.Trim().ReplaceLineEndings(Environment.NewLine));
        RefreshTextActions();
    });

    private void ResultReady(AsrResult result) => PostRecognitionEvent(() =>
    {
        if (!transcriptSessionActive) return;
        var line = result.Text.Trim().ReplaceLineEndings(Environment.NewLine);
        ReplaceDraft(line.Length == 0 ? "" : line + Environment.NewLine);
        historyLength += draft.Length;
        draft = "";
        transcriptSessionActive = false;
        RefreshTextActions();
        if (line.Length != 0) CopyText(line);
    });

    private void ReplaceDraft(string text)
    {
        if (draft == text) return;
        var shared = draft.AsSpan().CommonPrefixLength(text.AsSpan());
        if (shared > 0 && char.IsHighSurrogate(draft[shared - 1])) shared--;
        int start = historyLength + shared;
        int selection = ResultBox.SelectionStart, length = ResultBox.SelectionLength;
        bool keepSelection = length > 0 && selection + length <= start;
        double offset = ResultBox.VerticalOffset;
        bool follow = !keepSelection && offset + ResultBox.ViewportHeight >= ResultBox.ExtentHeight - 1;
        ResultBox.Select(start, draft.Length - shared);
        ResultBox.SelectedText = text[shared..];
        draft = text;
        if (keepSelection) ResultBox.Select(selection, length);
        else ResultBox.Select(historyLength + draft.Length, 0);
        if (follow) ResultBox.ScrollToEnd();
        else ResultBox.ScrollToVerticalOffset(offset);
    }

    private void RefreshTextActions()
    {
        bool hasText = historyLength > 0 || draft.Length > 0;
        EmptyHint.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
        TextActions.IsEnabled = hasText;
        ClearButton.IsEnabled = historyLength > 0;
        ClearButton.Content = transcriptSessionActive ? "Очистить историю" : "Очистить";
    }

    private void RecognitionFailed(Exception error) => PostRecognitionEvent(() =>
    {
        ReplaceDraft("");
        transcriptSessionActive = false;
        RefreshTextActions();
        if (closeRequested) logger?.LogError(error, "Recognition failed while closing");
        else ShowError("Ошибка распознавания", error);
    });

    private void CopyText(string text)
    {
        try { writeClipboard(text); }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            logger?.LogWarning(ex, "Clipboard is busy");
            StatusLabel.Text = "Буфер обмена занят. Нажмите «Копировать», чтобы повторить.";
        }
    }

    private void SaveText()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "Текстовые файлы|*.txt", FileName = "MyASR.txt" };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllText(dialog.FileName, ResultBox.Text); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowError("Не удалось сохранить текст", ex); }
    }

    private void ShowError(string title, Exception ex)
    {
        logger?.LogError(ex, "{Message}", title);
        new ErrorWindow(title, ex.Message) { Owner = this }.ShowDialog();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        closeRequested = true;
        if (preparing || stopping is not null || asr.State != AsrState.Idle)
        {
            e.Cancel = true;
            if (asr.State == AsrState.Recording) Dispatcher.InvokeAsync(StopRecording);
        }
    }

    private void Cleanup()
    {
        closed = true;
        meterTimer.Stop();
        asr.StateChanged -= OnRecognitionStateChanged;
        asr.PreviewReady -= PreviewReady;
        asr.ResultReady -= ResultReady;
        asr.Failed -= RecognitionFailed;
        source?.RemoveHook(WindowMessage);
        hotkeys.Dispose();
        if (DeviceCombo.SelectedItem is DeviceInfo device) settings.MicDeviceId = device.Id;
        settings.ModelId = selectedModel.Id;
        settings.Save();
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
                "ALT" => MOD_ALT,
                "SHIFT" => MOD_SHIFT,
                "WIN" => MOD_WIN,
                _ => 0u
            };

            if (part.Length == 1 && char.IsLetter(part[0]))
            {
                vk = (uint)char.ToUpperInvariant(part[0]);
                continue;
            }

            vk = part.ToUpperInvariant() switch
            {
                "F1" => 0x70u,
                "F2" => 0x71u,
                "F3" => 0x72u,
                "F4" => 0x73u,
                "F5" => 0x74u,
                "F6" => 0x75u,
                "F7" => 0x76u,
                "F8" => 0x77u,
                "F9" => 0x78u,
                "F10" => 0x79u,
                "F11" => 0x7Au,
                "F12" => 0x7Bu,
                "SPACE" => 0x20u,
                "ENTER" or "RETURN" => 0x0Du,
                "TAB" => 0x09u,
                "ESC" or "ESCAPE" => 0x1Bu,
                "INS" or "INSERT" => 0x2Du,
                "DEL" or "DELETE" => 0x2Eu,
                "HOME" => 0x24u,
                "END" => 0x23u,
                "PGUP" or "PAGEUP" => 0x21u,
                "PGDN" or "PAGEDOWN" => 0x22u,
                "PAUSE" or "BREAK" => 0x13u,
                "SCROLL" or "SCROLLLOCK" => 0x91u,
                _ => vk
            };
        }

        return (modifiers, vk);
    }


}
