# MyASR Manual QA Checklist

Run this after every change to UI, audio, or model paths.

## First-launch (clean machine)

- [ ] Delete `%LOCALAPPDATA%\MyASR\` if present.
- [ ] `dotnet run --project MyASR/MyASR.csproj`
- [ ] DownloadForm appears, progress moves, MB/s shown.
- [ ] After download (~890 MB), Form1 enables. `Status: ● Idle (модели готовы)`.

## Push-to-talk via mouse

- [ ] Pick the right microphone in the combo.
- [ ] Hold the Mic button, speak "проверка один два три", release.
- [ ] VU meter moves while recording.
- [ ] Status flips to `⏳ Decoding...` then back to `● Idle`.
- [ ] Result line appears, timestamped, with reasonable Russian text.

## Push-to-talk via global F9

- [ ] Open Notepad / browser; focus is *not* on MyASR.
- [ ] Hold F9, say "тест глобального хоткея", release.
- [ ] MyASR receives audio (foreground or background) and produces text.

## Edge cases

- [ ] Press-and-immediately-release Mic (≤300 ms): no result, no error.
- [ ] Hold Mic 30 sec without speaking: auto-stop fires, status returns to Idle.
- [ ] Disconnect microphone while recording: error toast, app stays alive.
- [ ] No internet on first launch: download form shows error + Retry button.

## Buttons

- [ ] `Очистить` empties result box.
- [ ] `Скопировать` puts the full text into clipboard.
- [ ] `Сохранить TXT...` writes a UTF-8 file with all results.

## Persistence

- [ ] Pick a non-default microphone, close, reopen — selection persists.
- [ ] Toggle "Глобальный" off, close, reopen — toggle stays off.

## Model integrity

- [ ] Truncate `%LOCALAPPDATA%\MyASR\models\v3_rnnt_decoder.onnx` to 1 byte.
- [ ] Restart MyASR. The corrupt file is replaced (download retried).

## Logs

- [ ] `%LOCALAPPDATA%\MyASR\logs\app-YYYYMMDD.log` accumulates lines while you use the app.
