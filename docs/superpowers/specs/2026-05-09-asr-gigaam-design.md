# MyASR — Push-to-Talk Speech Recognition (GigaAM v3 RNN-T)

**Date:** 2026-05-09
**Status:** Approved design, ready for implementation plan
**Owner:** tdavron@gmail.com

## Goal

Stand-alone Windows desktop application (WinForms, .NET 10) that performs offline Russian-language Automatic Speech Recognition by running the official GigaAM v3 RNN-T ONNX model on the CPU. The user holds a microphone button (or a global hotkey), the app captures the microphone audio, and after release the recognized text is appended to an on-screen log.

## User-Confirmed Decisions

| # | Topic | Choice |
|---|---|---|
| 1 | Capture trigger | Push-to-talk (mouse-down + mouse-up) |
| 2 | Model precision | FP32 full precision (~890 MB) |
| 3 | Model delivery | Auto-download from HuggingFace on first launch |
| 4 | UI complexity | Extended (mic + VU meter + device combo + clear/copy buttons + status bar) |
| 5 | PTT extra | Mouse + global Windows hotkey (default F9) |
| 6 | Test framework | TUnit |

## Architecture

Six isolated modules behind interfaces. The UI knows only about `IAsrService`.

```
Form1 (UI)
  └─ IAsrService
       ├─ IMicCapture        (NAudio WASAPI → 16 kHz mono float)
       ├─ IFeatureExtractor  (PCM → log-mel-spectrogram [T, 64])
       ├─ IRnnTDecoder       (encoder + decoder + joint ONNX, greedy)
       └─ IModelProvider     (download / verify ONNX files)
  └─ IHotkeyManager          (Win32 RegisterHotKey)
```

Heavy ONNX sessions are created **once at startup** and reused. Recognition runs on a `ThreadPool` thread; results are marshalled back to the UI via `Form.BeginInvoke`.

## Project Layout

```
MyASR/
├─ MyASR.csproj
├─ Program.cs
├─ Form1.cs / Form1.Designer.cs
├─ Audio/             IMicCapture, WasapiMicCapture, AudioRingBuffer, Resampler
├─ Features/          IFeatureExtractor, MelSpectrogramExtractor, MelFilterBank
├─ Asr/               IAsrService, AsrService, IRnnTDecoder, RnnTGreedyDecoder,
│                     Vocabulary, AsrResult
├─ Models/            IModelProvider, HuggingFaceModelProvider, ModelPaths,
│                     ModelManifest
├─ Hotkeys/           IHotkeyManager, WindowsHotkeyManager
└─ (no embedded resources — fbanks computed in code)

MyASR.Tests/
├─ MyASR.Tests.csproj (TUnit)
├─ Assets/reference.wav, reference.txt, reference_mel.bin
└─ unit tests per layer
```

Models live in `%LOCALAPPDATA%\MyASR\models\` — never embedded in the executable.

## NuGet Packages

| Package | Purpose |
|---|---|
| `Microsoft.ML.OnnxRuntime` 1.20+ | CPU inference |
| `NAudio.Wasapi` 2.2+ | Microphone capture (WASAPI shared mode) |
| `NAudio.Core` 2.2+ | Wave types, FFT, BufferedWaveProvider |
| `Microsoft.Extensions.DependencyInjection` 9.x | Composition root |
| `Microsoft.Extensions.Logging.Console` 9.x | Logging |
| `MathNet.Numerics` 5.x | (Conditional) FFT precision fallback |
| `TUnit`, `TUnit.Assertions` | Unit testing |

Excluded by design: `Microsoft.ML.OnnxRuntime.Gpu`, `System.Speech`, `Sherpa-onnx`, `NumSharp`, `TorchSharp`.

## Data Flow — Single Recognition

1. **Mouse-down on MicButton** (or global hotkey down) → `AsrService.StartCapture()`.
2. WASAPI callbacks deliver native-format chunks (typ. 48 kHz f32 stereo) every ~10 ms.
3. Chunks are converted to **16 kHz mono float32 in [-1, 1]** via `MediaFoundationResampler` and stored in `AudioRingBuffer`.
4. `VuMeter` reads RMS of the last 100 ms each 33 ms (30 fps).
5. A 30-second timer enforces a hard upper limit.
6. **Mouse-up** → `AsrService.StopCaptureAndRecognize()`.
7. If duration < 300 ms — show "too short" tooltip, abort.
8. `MelSpectrogramExtractor.Extract(pcm)` → `float[T, 64]`.
9. `RnnTGreedyDecoder.Decode(features)`:
   - `encoder.Run(input=[1, 64, T])` → `enc_out [1, 768, T/4]`.
   - Greedy RNN-T loop over `T/4` timesteps emitting tokens via `joint`/`decoder`.
10. Tokens → text via `Vocabulary`.
11. `Form.BeginInvoke` appends `[hh:mm:ss] text` to the result box.

## Audio Preprocessing — Exact Match to gigaam-v3

These constants are **non-negotiable** — any deviation breaks the model:

| Parameter | Value | Source |
|---|---|---|
| `sample_rate` | 16 000 | `v3_rnnt.yaml` |
| `n_fft` | 320 | `v3_rnnt.yaml` |
| `win_length` | 320 | `v3_rnnt.yaml` |
| `hop_length` | 160 | `v3_rnnt.yaml` |
| `features` (n_mels) | 64 | `v3_rnnt.yaml` |
| `mel_scale` | `htk` (`f_mel = 2595 · log10(1 + f / 700)`) | `v3_rnnt.yaml` |
| `mel_norm` | `null` (no `2 / (mel_high − mel_low)` scaling) | `v3_rnnt.yaml` |
| `center` | `false` (frame 0 starts at sample 0) | `v3_rnnt.yaml` |
| Window | Hanning, length = `n_fft` | gigaam preprocess source |
| Log offset | `1e-9` | gigaam preprocess source |
| Frame count | `T = (n_samples − n_fft) / hop_length + 1` | follows from `center=false` |

`MelFilterBank` is a `[64 × 161]` matrix of triangular filters spaced uniformly on the htk-mel scale from 0 Hz to 8 000 Hz, computed once at startup.

The pipeline must be validated by comparing the .NET output against a reference produced by `gigaam.preprocess.FeatureExtractor` on the same WAV: `max(abs(diff)) < 1e-3`.

## RNN-T Greedy Decoding

Vocabulary (from `v3_vocab.txt`, 34 tokens):
- index 0 = `' '` (space, `▁`-equivalent)
- indices 1–32 = Russian letters `а` … `я`
- index 33 = `<blk>` (blank)

Loop sketch:

```
state = decoder_initial_state
for t in 0 .. T/4 - 1:
    enc_t = encoder_output[:, :, t]
    while emitted_at_t < max_per_frame:
        logits = joint(enc_t, state.last_hidden)
        token  = argmax(logits)
        if token == BLANK:
            break
        emit(token)
        state = decoder.step(token, state)
```

`max_per_frame` (~5) prevents runaway loops on degenerate states.

## UI Layout

```
+---------------------------------- MyASR ---------------------------------+
| Microphone:  [▼ Realtek HD Audio (default) ........]                     |
| Hotkey:      [F9 ▼]   [✓] Глобальный                                     |
+--------------------------------------------------------------------------+
|                                                                          |
|       ╭────────╮                                                         |
|       │   🎤   │       ████████░░░░░░░░░░ -18 dB                         |
|       │  Hold  │                                                         |
|       ╰────────╯                                                         |
|   Status:  ● Idle                                                        |
+--------------------------------------------------------------------------+
| [12:34:01] Привет, как дела сегодня                                      |
| [12:34:18] Открой документ номер пять                                    |
|                                                                          |
+--------------------------------------------------------------------------+
| [Очистить]  [Скопировать]  [Сохранить TXT...]    Model: v3_rnnt FP32 ✓   |
+--------------------------------------------------------------------------+
```

States: `Idle → Recording (mm:ss elapsed) → Decoding → Idle`. MicButton disabled during `Decoding`. Result box auto-scrolls.

## First-Launch Flow

1. `Form1_Load` → `IModelProvider.AreModelsAvailable()`.
2. If missing → modal `DownloadForm`:
   - GET `https://huggingface.co/istupakov/gigaam-v3-onnx/resolve/main/{file}` for `v3_rnnt_encoder.onnx`, `v3_rnnt_decoder.onnx`, `v3_rnnt_joint.onnx`, `v3_vocab.txt`.
   - Show progress bar, throughput, ETA.
   - Verify SHA-256 against `ModelManifest`.
   - Support resume via `Range:` header on retry.
3. After download → create three ONNX sessions in background → enable MicButton.
4. If no internet → fallback dialog "Choose folder containing model files" pointing to a manually-downloaded copy.

## Hotkey Manager

- `RegisterHotKey(hWnd, id, MOD_NONE, VK_F9)` via `user32.dll` P/Invoke.
- `WndProc` of Form1 traps `WM_HOTKEY` (down) and starts capture.
- "Up" detection: poll `GetAsyncKeyState(VK_F9)` on a `Timer` (16 ms) while recording, stop when bit clears. (Hotkeys do not deliver native key-up events.)

## Threading and State Machine

```
        +----- Idle -----+
        |       |        |
mouse-down|     |  hotkey down
        v       v
       Recording (background WASAPI thread + UI VU timer)
        |
mouse-up|  hotkey up  |  30s elapsed  |  too-short abort
        v
       Decoding (Task.Run)
        |
        v   result or error
        Idle
```

Invariants:
- At most one capture at a time.
- MicButton is disabled in `Decoding`.
- All UI updates pass through `Form.BeginInvoke`.

## Error Handling

| Failure | Behaviour |
|---|---|
| No microphone selected / disabled | Status "Микрофон недоступен", button disabled |
| WASAPI device removed during capture | Toast, log, return to Idle |
| Encoder `OnnxRuntimeException` | Append `[ошибка распознавания]` to results, log, Idle |
| Recording > 30 s | Auto-stop with status "Слишком длинно" |
| HTTP download interrupted | Resume with Range header; on hard fail show retry button |
| SHA-256 mismatch | Delete partial file, redownload, max 3 attempts |
| ONNX session creation fails | Offer redownload of corrupted model |
| Empty result box on Copy | Tooltip "нечего копировать" |

Logs land in `%LOCALAPPDATA%\MyASR\logs\app.log` with daily rolling, 7-day retention.

## Testing Strategy (TUnit)

| Layer | What | How |
|---|---|---|
| `MelFilterBank` | 64×161 matrix matches librosa htk reference | Compare against CSV/JSON snapshot, `tol=1e-5` |
| `MelSpectrogramExtractor` | log-mel of `reference.wav` matches gigaam Python output | Load `reference_mel.bin`, `max_abs_diff < 1e-3` |
| `Vocabulary` | Parses `v3_vocab.txt`, `BlankIndex == 33` | Direct asserts |
| `AudioRingBuffer` | Concurrent producer/consumer correctness | TUnit, `Parallel.For` |
| `RnnTGreedyDecoder` | End-to-end on `reference.wav` produces `reference.txt` | Real ONNX models, integration test |
| `HuggingFaceModelProvider` | Resume + SHA-256 + retry | Fake `HttpMessageHandler` |
| `AsrService` | FSM transitions and error swallowing | Hand-written fakes for all interfaces |

Out of automated scope (manual QA): WASAPI on real hardware, global hotkey across apps, download progress UI.

## Out of Scope (YAGNI)

- Streaming partial results during recording (model is non-streaming Conformer).
- INT8 quantized variant — explicitly rejected for FP32.
- GPU / DirectML inference.
- Speaker diarization, punctuation restoration (would require `gigaam-v3-e2e-rnnt`).
- Multi-language — model is Russian-only by vocabulary.
- System-wide auto-typing of recognized text into focused window.

## Open Items for the Implementation Plan

The implementation plan (next skill: `writing-plans`) must cover:

1. Order of phases that lets us test each module before the next is built.
2. How to obtain the reference `mel.bin` and `reference.txt` artifacts (Python one-shot script in `tools/`).
3. Concrete `csproj` updates: `<PackageReference>`s, copy-on-build of test assets.
4. Whether DI is wired through `Microsoft.Extensions.Hosting` or a hand-rolled `ServiceCollection`.
5. Bootstrapping of the `DownloadForm` so a developer can test without re-downloading 890 MB every clean build.
