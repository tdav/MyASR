# Улучшения MyASR по мотивам статьи о GigaAM v3 (@chisto_text_bot) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Снять 30-секундное ограничение диктовки, защитить распознавание от галлюцинаций на тишине, добавить транскрибацию длинных аудиофайлов с авто-абзацами и детекцию нерусской речи на базе практик из статьи `https://habr.com/ru/articles/1102072/`.

**Architecture:** Длинный PCM-поток `16 кГц моно` перед подачей в `IAsrEngine` режется адаптивным энергетическим `PauseChunker` на куски по `10–20 с` в точках естественных пауз (без отдельной VAD-модели в памяти), а чисто тихие сегменты отбрасываются. `RnnTGreedyDecoder` отдаёт таймкоды и средний `log-prob` токенов (`40 мс` на шаг субдискретизированного энкодера), из которых `TranscriptAssembler` склеивает куски без ложных заглавных букв внутри фраз и расставляет абзацы, а `LanguageConfidenceGuard` ловит нерусскую речь.

**Tech Stack:** .NET 11 (`net11.0-windows`, `PublishAot=true`), WinForms, `Microsoft.ML.OnnxRuntime` 1.31.1, `NAudio.Core` 3.3.0, TUnit 1.43.37.

**Spec:** Анализ статьи Хабра `https://habr.com/ru/articles/1102072/` (`kozliq/tekst`) применительно к десктопному приложению `MyASR`. Базовый переход каталога на `GigaAM v3 e2e RNN-T (int8)` с пунктуацией и заглавными уже выполнен в `MyASR/Models/ModelCatalog.cs` и `MyASR/Asr/Vocabulary.cs`.

## Оценка статьи: что улучшит MyASR, а что не нужно

| Идея из статьи | Оценка для MyASR | Решение |
|---|---|---|
| `GigaAM v3 e2e RNN-T (int8)` вместо базовой безпунктуационной `fp32` | В 4× меньше скачивать (`~226 МБ` против `~890 МБ`), в ~2× меньше RAM, ставит запятые, точки и заглавные из коробки | **Уже внедрено** в `ModelCatalog` и `Vocabulary` |
| Адаптивная энергетическая нарезка по паузам (`10-й перцентиль + 8 дБ` и `медиана − 6 дБ`, окно `10–20 с`) | Позволяет убрать жёсткий лимит записи `30 с` в `Main` и распознавать часовые лекции/созвоны без загрузки отдельной нейросети Silero VAD | **Включить в план (Task 2)** |
| Отсечение тихих кусков (`< 0,3 с` звука громче `−50 дБ`) | Убирает типичные галлюцинации нейросетей при случайном нажатии хоткея в тишине | **Включить в план (Task 2)** |
| Склейка регистра на стыках кусков и разбивка на абзацы по таймингам токенов | Длинная диктовка и расшифровка файлов превращаются из «сплошной простыни с лишними заглавными» в читаемый текст | **Включить в план (Task 1, Task 3)** |
| Проверка первых `20 с` на нерусскую речь (`> 40%` латиницы или `mean log-prob < -0.25`) | На английской речи `GigaAM` уверенно пишет кириллическую бессмыслицу; детектор позволяет автоматически переключить на Whisper (если модель скачана) или предупредить пользователя | **Включить в план (Task 1, Task 5)** |
| Round-robin очередь по пользователям, локальный сервер Bot API до 2 ГБ, квоты от флуда, `systemd`-изоляция | Серверная специфика публичного многопользовательского Telegram-бота | **Отбросить (YAGNI для десктопа)** |

## Global Constraints

- Совместимость с **NativeAOT (`PublishAot=true`)**: никакой новой рефлексии, `dynamic`, классического `[ComImport]` или неаннотированной JSON-сериализации.
- Приватные поля в новых классах — `camelCase` без префикса `_`.
- Все пользовательские сообщения в UI — на русском языке.
- Команда верификации для каждой задачи: `dotnet test MyASR.Tests/MyASR.Tests.csproj` (без `.onnx` файлов на диске).
- Частота дискретизации во всём конвейере ASR фиксирована: `16 000 Гц`, моно, `float` в `[-1, 1]`; шаг времени выхода энкодера GigaAM v3 (`hop=160`, `subsampling=4`) равен ровно `0,04 с` (`40 мс`).

## Review Focus

1. **Абсолютная тишина или микрофон на mute (`all zeros` / шум `-70 дБ`):** `PauseChunker` должен вернуть пустой список активных сегментов и `AsrService` не должен вызывать ONNX-сессию вообще.
2. **Непрерывная быстрая речь или музыка на фоне без явных пауз в окне `10–20 с`:** `PauseChunker` не должен резать жёстко по 20-й секунде на ударном слоге — обязан сработать фоллбэк на минимум сглаженной громкости в окне `10–20 с`.
3. **Аббревиатуры и имена собственные на стыке кусков (`«...в компании Сбер»` / `«...через API»`):** понижение регистра первой буквы следующего куска не должно ломать слова из двух и более заглавных букв подряд (`API`, `CPU`, `РФ`).
4. **Короткая голосовая команда (`0,5–9 с`):** кусок короче `20 с` с достаточной громкостью проходит через `PauseChunker` одним сегментом без лишних копирований и искажения регистров.
5. **Нерусская речь при отсутствии скачанной модели Whisper на диске:** `LanguageConfidenceGuard` не должен падать или запускать скачивание без спроса — он возвращает результат с явной пометкой в статусе, предлагая переключить модель.

---

### Task 1: Таймкоды токенов и средний `log-prob` в `RnnTGreedyDecoder`

**Files:**
- Modify: `MyASR/Asr/AsrResult.cs`
- Modify: `MyASR/Asr/RnnTGreedyDecoder.cs`
- Modify: `MyASR/Asr/Vocabulary.cs`
- Test: `MyASR.Tests/Asr/RnnTGreedyDecoderTests.cs`

**Interfaces:**
- Consumes: `Vocabulary` (`MyASR/Asr/Vocabulary.cs`).
- Produces:
  - `public readonly record struct TokenSpan(string Text, double TimeSec, float LogProb);`
  - Расширенный `public sealed record AsrResult(string Text, TimeSpan AudioDuration, TimeSpan Elapsed, IReadOnlyList<TokenSpan>? Tokens = null, float MeanLogProb = 0f);` (значения по умолчанию сохраняют обратную совместимость со всеми существующими вызовами `new AsrResult(text, dur, elapsed)`).
  - Внутренний статический хелпер `RnnTGreedyDecoder.LogSoftmaxAt(ReadOnlySpan<float> logits, int index) -> float` (численно устойчивый через `max`).

- [ ] **Step 1: Write the failing test**

В `MyASR.Tests/Asr/RnnTGreedyDecoderTests.cs` добавить тест `LogSoftmaxAt_IsNumericallyStable_AndTokenFrameMapsTo40Ms()`, проверяющий `LogSoftmaxAt([1000f, 1000f], 0)` ≈ `-0.693147f` (без `NaN`/`Infinity`) и перевод индекса кадра `t` в `t * 0.04`.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test MyASR.Tests/MyASR.Tests.csproj`
Expected: FAIL (`CS0117: 'RnnTGreedyDecoder' does not contain a definition for 'LogSoftmaxAt'`).

- [ ] **Step 3: Implement `TokenSpan`, `LogSoftmaxAt` и сбор таймкодов в `RnnTGreedyDecoder.Decode`**

На каждом эмитированном не-blank и не-`<unk>` токене на шаге энкодера `t` вычислять `logProb = LogSoftmaxAt(logits, token)`, записывать `new TokenSpan(piece, t * 0.04, logProb)` и считать среднее `MeanLogProb` по всем эмитированным токенам.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test MyASR.Tests/MyASR.Tests.csproj`
Expected: PASS (все тесты зелёные).

- [ ] **Step 5: Commit**

```bash
git add MyASR/Asr/AsrResult.cs MyASR/Asr/RnnTGreedyDecoder.cs MyASR.Tests/Asr/RnnTGreedyDecoderTests.cs
git commit -m "feat(asr): expose token timestamps and mean log-prob from RNN-T decoder"
```

---

### Task 2: Адаптивная энергетическая нарезка по паузам и фильтр тишины (`PauseChunker`)

**Files:**
- Create: `MyASR/Audio/PauseChunker.cs`
- Test: `MyASR.Tests/Audio/PauseChunkerTests.cs`

**Interfaces:**
- Consumes: `ReadOnlySpan<float>` PCM `16 кГц моно`.
- Produces:
  - `public readonly record struct AudioChunk(int StartSample, int Length, double OffsetSec);`
  - `public static class PauseChunker` с методом `public static IReadOnlyList<AudioChunk> Split(ReadOnlySpan<float> pcm16kMono)`:
    - Кадры по `30 мс` (`480` отсчётов @ `16 кГц`), уровень в дБ: `20 * log10(max(rms, 1e-10))`.
    - Адаптивный порог тишины для всей записи: `quiet[i] = (db[i] < p10 + 8.0 && db[i] < median - 6.0) || db[i] < -50.0`.
    - Пока остаток длиннее `20 с` (`320 000` отсчётов): искать самую длинную серию `quiet == true` в окне `[10 с .. 20 с]` от начала текущего куска и резать посередине этой паузы; если в окне нет ни одного `quiet`-кадра, резать в точке минимума скользящего среднего `db` (окно `5` кадров = `150 мс`) в том же интервале `[10 с .. 20 с]`.
    - Любой полученный кусок, в котором суммарная длительность кадров с `db > -50.0` меньше `0,3 с` (`< 10` кадров), отбрасывать как чистую тишину.

- [ ] **Step 1: Write the failing test**

Создать `MyASR.Tests/Audio/PauseChunkerTests.cs` с 5 тестами:
1. `AllSilence_ReturnsEmpty()` — 5 секунд нулей или шума `-65 дБ` возвращают `[]`.
2. `ShortActiveUtterance_ReturnsSingleChunk()` — 4 секунды синусоиды `-12 дБ` возвращают 1 чанк `[0, 64000]`.
3. `ThirtyFiveSecondSpeech_SplitsAtMiddleOfLongestPauseBetween10And20Sec()` — речь с паузой `14,0..15,0 с` режется около `14,5 с` (`±60 мс`).
4. `ContinuousLoudSignalWithoutPauses_SplitsAtSmoothedMinimumInWindow()` — сигнал `-10 дБ` с просадкой до `-14 дБ` на `16,2 с` режется около `16,2 с`.
5. `TrailingSilenceChunk_IsDropped()` — 15 с речи + 10 с тишины после паузы дают только первый активный чанк.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test MyASR.Tests/MyASR.Tests.csproj`
Expected: FAIL (`PauseChunker` does not exist).

- [ ] **Step 3: Implement `PauseChunker` in `MyASR/Audio/PauseChunker.cs`**

Реализовать расчёт перцентилей на копии массива `db` кадров без сторонних зависимостей, разбиение и фильтрацию активных кадров `db > -50.0`.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test MyASR.Tests/MyASR.Tests.csproj`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add MyASR/Audio/PauseChunker.cs MyASR.Tests/Audio/PauseChunkerTests.cs
git commit -m "feat(audio): add adaptive energy-based PauseChunker and silence filter"
```

---

### Task 3: Умная склейка стыков и авто-абзацы по паузам (`TranscriptAssembler`)

**Files:**
- Create: `MyASR/Asr/TranscriptAssembler.cs`
- Test: `MyASR.Tests/Asr/TranscriptAssemblerTests.cs`

**Interfaces:**
- Consumes: `AudioChunk` (`MyASR/Audio/PauseChunker.cs`), `AsrResult` / `TokenSpan` (`MyASR/Asr/AsrResult.cs`).
- Produces:
  - `public readonly record struct ChunkTranscript(double ChunkOffsetSec, AsrResult Result);`
  - `public static class TranscriptAssembler` с методом `public static string Assemble(IReadOnlyList<ChunkTranscript> chunks)`:
    - **Заглавные на стыке:** если предыдущий непустой фрагмент не заканчивается на `.`, `!`, `?` или `…`, а первое слово следующего начинается с одной заглавной и далее строчной буквы (например, `«Продолжаем»`, но не `«API»` или `«США»`), переводить первую букву в строчную.
    - **Абзацы по паузам между предложениями:** по глобальному времени токенов (`ChunkOffsetSec + token.TimeSec`) начинать новый абзац (`\n\n`) после `.!?…`, если пауза до следующего токена `> 1,5 с` при длине текущего абзаца `>= 80` символов, либо `> 0,6 с` при длине `>= 300` символов, либо безусловно на ближайшем конце предложения при длине абзаца `> 550` символов.

- [ ] **Step 1: Write the failing test**

Создать `MyASR.Tests/Asr/TranscriptAssemblerTests.cs`, покрывающий:
1. Понижение ложной заглавной посреди фразы (`«Мы решили,»` + `«Что пойдём дальше.»` -> `«Мы решили, что пойдём дальше.»`).
2. Сохранение аббревиатур (`«Подключили»` + `«API вчера.»` -> `«Подключили API вчера.»`).
3. Разбивку на абзацы при паузе `1,8 с` после предложения длиннее `80` знаков и принудительный разрыв после `550` знаков.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test MyASR.Tests/MyASR.Tests.csproj`
Expected: FAIL (`TranscriptAssembler` does not exist).

- [ ] **Step 3: Implement `TranscriptAssembler` in `MyASR/Asr/TranscriptAssembler.cs`**

Реализовать посимвольную/потокенную сборку с учётом границ предложений и условий длины абзаца.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test MyASR.Tests/MyASR.Tests.csproj`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add MyASR/Asr/TranscriptAssembler.cs MyASR.Tests/Asr/TranscriptAssemblerTests.cs
git commit -m "feat(asr): add TranscriptAssembler for seam casing and pause paragraphs"
```

---

### Task 4: Длинная запись без лимита 30 с и распознавание аудиофайлов в `AsrService` и `Main`

**Files:**
- Modify: `MyASR/Audio/WasapiMicCapture.cs`
- Modify: `MyASR/Audio/WavReader.cs`
- Modify: `MyASR/Asr/IAsrService.cs`
- Modify: `MyASR/Asr/AsrService.cs`
- Modify: `MyASR/Main.cs`
- Modify: `MyASR/Main.Designer.cs`
- Test: `MyASR.Tests/Asr/AsrServiceTests.cs`

**Interfaces:**
- Consumes: `PauseChunker`, `TranscriptAssembler`, `WavReader`.
- Produces:
  - Расширяемый буфер записи в `WasapiMicCapture` (до `30 минут` диктовки вместо фиксированных `31 с`) и удаление принудительного `maxLengthTimer` (`30_000 мс`) из `Main`.
  - `Task TranscribeSamplesAsync(float[] pcm16kMono, CancellationToken ct = default)` в `IAsrService` / `AsrService`, прогоняющий `PauseChunker.Split` -> цикл `activeEngine.Transcribe` по кускам с событием прогресса `DecodingProgress(int DoneChunks, int TotalChunks)` -> `TranscriptAssembler.Assemble`.
  - Кнопка `«Открыть файл...»` и `AllowDrop = true` (`DragEnter`/`DragDrop`) на `Main` для расшифровки `.wav`-файлов любой частоты/канальности (с ресемплингом через `Resampler.ToMono16k`).

- [ ] **Step 1: Write the failing test**

В `MyASR.Tests/Asr/AsrServiceTests.cs` добавить тесты:
1. `StopAndRecognizeAsync_AllSilence_SkipsEngineAndReturnsToIdle()`.
2. `TranscribeSamplesAsync_FortyFiveSeconds_SplitsIntoMultipleEngineCallsAndAssembles()`.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test MyASR.Tests/MyASR.Tests.csproj`
Expected: FAIL (`TranscribeSamplesAsync` missing / silence still calls `FakeEngine`).

- [ ] **Step 3: Implement chunked pipeline in `AsrService`,任意-format WAV loading in `WavReader`, and File Open / Drag-and-Drop in `Main`**

В `WavReader` поддержать произвольную частоту дискретизации и стерео через `Resampler.ToMono16k`. В `Main` убрать `maxLengthTimer`, добавить кнопку `openFileBtn` (`«Открыть WAV...»`) и обработчик перетаскивания файла.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test MyASR.Tests/MyASR.Tests.csproj`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add MyASR/Audio/WasapiMicCapture.cs MyASR/Audio/WavReader.cs MyASR/Asr/IAsrService.cs MyASR/Asr/AsrService.cs MyASR/Main.cs MyASR/Main.Designer.cs MyASR.Tests/Asr/AsrServiceTests.cs
git commit -m "feat: support unlimited recording length and chunked WAV file transcription"
```

---

### Task 5: Детекция нерусской речи (`LanguageConfidenceGuard`)

**Files:**
- Create: `MyASR/Asr/LanguageConfidenceGuard.cs`
- Modify: `MyASR/Asr/AsrService.cs`
- Test: `MyASR.Tests/Asr/LanguageConfidenceGuardTests.cs`

**Interfaces:**
- Consumes: `AsrResult` (`Text`, `MeanLogProb`), `ModelCatalog`, `IModelProvider`.
- Produces:
  - `public static class LanguageConfidenceGuard` с методом `public static bool LooksSuspiciousForRussian(string text, float meanLogProb)`:
    - Возвращает `true`, если среди букв (`char.IsLetter`) доля латиницы (`A-Z`, `a-z`) превышает `0,40` (при минимум 5 буквах) **или** `meanLogProb < -0.25f` (когда `meanLogProb != 0f`).
  - В `AsrService`: если текущая модель — `AsrEngineKind.RnnT` (русскоязычная GigaAM) и первый чанк помечен `LooksSuspiciousForRussian == true`:
    - Если на диске уже присутствует `CrisperWhisperSmallInt8` или `CrisperWhisperSmallFp32`, выполнить повторное распознавание через Whisper без выгрузки/потери настроек пользователя;
    - Если локальной мультиязычной модели нет, выставить предупреждение в строку состояния (`«Похоже на нерусскую речь — для других языков выберите CrisperWhisper»`), не прерывая работу.

- [ ] **Step 1: Write the failing test**

Создать `MyASR.Tests/Asr/LanguageConfidenceGuardTests.cs`, проверяющий:
1. Обычный русский текст с `meanLogProb = -0.08f` -> `false`.
2. Текст с `> 40%` латиницы (`«Hello world это test message»`) -> `true`.
3. Неуверенный вывод с кириллицей и `meanLogProb = -0.34f` -> `true`.
4. Короткое междометие `< 5` букв с латиницей (`«Ок, PR»`, `meanLogProb = -0.05f`) -> `false` (не даёт ложных срабатываний на IT-жаргоне).

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test MyASR.Tests/MyASR.Tests.csproj`
Expected: FAIL (`LanguageConfidenceGuard` does not exist).

- [ ] **Step 3: Implement `LanguageConfidenceGuard` and wire non-intrusive fallback/hint in `AsrService`**

Реализовать чистую функцию `LooksSuspiciousForRussian` и опциональный повтор на скачанной Whisper-модели.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test MyASR.Tests/MyASR.Tests.csproj`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add MyASR/Asr/LanguageConfidenceGuard.cs MyASR/Asr/AsrService.cs MyASR.Tests/Asr/LanguageConfidenceGuardTests.cs
git commit -m "feat(asr): detect non-Russian speech on GigaAM and fallback or hint"
```
