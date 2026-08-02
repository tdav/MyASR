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

## CrisperWhisper 2.0 small — Russian quality

**Not yet measured.** This development environment has no microphone and no Russian speech
sample, so `MyASR.Tests/Assets/russian_sample.wav` does not exist here and cannot be produced in
this environment. The two gated tests in `MyASR.Tests/Asr/Whisper/CrisperWhisperEndToEndTests.cs`
(`Transcribes_RussianSpeech`, `IntendedMode_DoesNotEmitVocalEventMarkers`) therefore remain
`[Skip]`-ped, by design, until someone with a microphone runs the steps below.

What *is* confirmed on the real int8 export in this environment: constructing
`WhisperOnnxEngine` via `WhisperOnnxEngine.Create` and calling `Transcribe` twice on the same
instance against the existing synthetic `MyASR.Tests/Assets/reference.wav` completes without
throwing and returns the identical string `"."` both times — evidence that the shared decoder
session and the per-call KV cache behave consistently. That is not a Russian-speech result (the
WAV is a non-speech chirp); it says nothing about word error rate.

To measure the numbers below, a person needs about 10 minutes and a microphone:

1. Record 5-20 seconds of clear Russian speech at 16 kHz mono to
   `MyASR.Tests/Assets/russian_sample.wav`, and type its true transcript into
   `MyASR.Tests/Assets/russian_sample.txt`.
2. Point `MYASR_MODELS_DIR` at a folder that contains a `crisperwhisper2-small-int8\` subfolder
   with the six files `ModelCatalog.CrisperWhisperSmallInt8` expects (encoder, decoder, vocab,
   added tokens, generation config, preprocessor config) — for example
   `set MYASR_MODELS_DIR=C:\models\myasr` after copying the export into
   `C:\models\myasr\crisperwhisper2-small-int8\`.
3. Remove the `[Skip(...)]` attribute from `Transcribes_RussianSpeech` and
   `IntendedMode_DoesNotEmitVocalEventMarkers` (leave the third, reference-only test's `[Skip]`
   alone — it doesn't need a Russian sample) and run
   `dotnet test MyASR.Tests/MyASR.Tests.csproj`. `Transcribes_RussianSpeech` computes the word
   error rate itself via its `WordErrorRate` helper whenever `russian_sample.txt` is present and
   asserts it is below 0.30; note the number even if the assertion passes.
   **Watch for a silent no-op:** if either the model folder or `russian_sample.wav` is still
   missing when you strip `[Skip]`, `CanRun()` returns false and the test body does
   `if (!CanRun()) return;` — it reports as *passed* in milliseconds, not failed, so a green run
   does not by itself prove anything ran. A real run against the real encoder/decoder takes on the
   order of 5-10 seconds per test (loading a ~90 MB encoder and a ~195 MB decoder and running
   real inference); if the whole suite finishes in a couple of seconds, nothing was actually
   measured.
4. To compare int8 against fp32, repeat step 2-3 with `MYASR_MODELS_DIR` pointed at a folder
   holding `crisperwhisper2-small-fp32\` and temporarily change
   `ModelCatalog.CrisperWhisperSmallInt8` to `ModelCatalog.CrisperWhisperSmallFp32` in the test
   file (revert before committing — there is no test parameter for this today).
5. Restore the `[Skip(...)]` attributes before committing — CI has no model.

- Word error rate: not yet measured — requires the Russian recording described above.
- Verdict on making it the default instead of GigaAM v3: undecided. This is exactly the decision
  that hangs on the word error rate above; it should not be made without that number.
- Notes on int8 versus fp32 for Russian: not yet compared — requires running both exports against
  the same recording (step 4). The one behavioural fact known so far is that on non-speech audio
  the int8 build returns `"."` rather than an empty string, so downstream code must not assume
  silence yields `""`.

The spec keeps GigaAM as the catalogue default until these numbers say otherwise.
