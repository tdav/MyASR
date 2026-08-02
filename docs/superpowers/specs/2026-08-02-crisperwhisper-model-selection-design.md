# MyASR — Model Selection and CrisperWhisper 2.0 Support

**Date:** 2026-08-02
**Status:** Approved design, ready for implementation plan
**Owner:** tdavron@gmail.com
**Supersedes nothing** — extends `2026-05-09-asr-gigaam-design.md`

## Goal

Let the user pick the recognition model from the UI instead of being locked to GigaAM v3.
Add CrisperWhisper 2.0 small (`nyralabs/CrisperWhisper2.0_small`) as a second engine family.
A model that is missing from the working folder is fetched on demand with a progress UI, exactly
as GigaAM is today. Russian remains the primary target language.

Reference material:
- <https://huggingface.co/nyralabs/CrisperWhisper2.0_small>
- <https://github.com/nyrahealth/CrisperWhisper> (`DOCS.md`)

## Findings That Shaped This Design

These were verified against the model repository and the upstream docs, not assumed.

| # | Finding | Consequence |
|---|---|---|
| 1 | `CrisperWhisper2.0_small` ships **only** `model.safetensors` (BF16, 242 M params). No ONNX export exists for this size. | We export ONNX ourselves via `optimum`. The only third-party 2.0 ONNX repos (`Masterx/*-large`, `Masterx/*-turbo`) were created 2026-08-02 with zero downloads and were rejected as a dependency. |
| 2 | The card's `language: [en, de]` is legacy from v1.0. `generation_config.json` has `is_multilingual: true`, the full 98-language token set, and `<\|ru\|> = 50263`. `README.md` claims verbatim/intended across ten languages. | Russian is mechanically supported. Quality on Russian is **unverified** — no per-language Russian figure is published. Must be smoke-tested on real audio before CrisperWhisper becomes the default. |
| 3 | CrisperWhisper 1.0 is English/German only (upstream footnote). | All v1-based ONNX conversions (`onnx-community/CrisperWhisper-ONNX`, `hammeiam/*`, `Prince-1/*`) are disqualified. |
| 4 | `preprocessor_config.json`: `feature_size: 80`, `n_fft: 400`, `hop_length: 160`, `chunk_length: 30`, `nb_max_frames: 3000`. | The existing `MelSpectrogramExtractor` (64 mels, HTK, `n_fft` 320, no centre padding) cannot be reused. Whisper needs Slaney-normalised mel filters. |
| 5 | `config.json`: `num_mel_bins: 80`, `d_model: 768`, 12 encoder / 12 decoder layers, `vocab_size: 51896`, `max_target_positions: 448`. | Whisper-small geometry with an extended vocabulary. |
| 6 | Added tokens include `[verbatim_1..5]` (51880–51884) and `[intended_1..5]` (51885–51889), vocal-event tokens (`[UM]`, `[UH]`, `[laughter]`, …) and context markers `<vtx>/<evtx>`, `<ctx>/<ectx>`, `<htx>/<ehtx>`. | Verbatim is the model default and emits disfluencies. A dictation app wants **intended**. Modes differ *only* by the decoder prompt prefix — the encoder pass is shared. |
| 7 | `config.json` declares `architectures: ["WhisperForConditionalGenerationWithAttentionLoss"]`, a custom class. | `optimum-cli export onnx` will not resolve it. The export script rewrites `architectures` to `["WhisperForConditionalGeneration"]` first; the attention-loss head is training-only. |
| 8 | Licence is `nyra-health-non-commercial-research`. | Non-commercial, research only. GigaAM carries no such restriction. Surfaced in the UI next to the model name and recorded here; the decision to ship is the owner's. |

## User-Confirmed Decisions

| # | Topic | Choice |
|---|---|---|
| 1 | ONNX artefact | Export `CrisperWhisper2.0_small` ourselves with `optimum`, rather than depend on a third-party conversion |
| 2 | Distribution | Both: HTTP(S) base URI **or** a local folder / UNC path, selected by URI scheme |
| 3 | Precision | Ship both `int8` and `fp32` as separate catalogue entries |
| 4 | Language | `<\|ru\|>` forced. No auto-detection in this iteration |
| 5 | Transcription mode | `intended` by default (clean text). Verbatim is not exposed in this iteration |

## Architecture

### The central change: `IAsrEngine`

Today `AsrService` receives `IFeatureExtractor` and `Func<IRnnTDecoder>` as two independent
dependencies. With two model families that is a trap: GigaAM features (64 mels, HTK, `n_fft` 320)
and Whisper features (80 Slaney mels, `n_fft` 400, 30-second padding) are incompatible, and a
mismatch produces *plausible-looking nonsense text*, not an exception. The feature extractor and
the decoder must be one replaceable unit.

```csharp
namespace MyASR.Asr;

public interface IAsrEngine : IDisposable
{
    /// <summary>Recognises 16 kHz mono PCM. Owns its own feature extraction.</summary>
    AsrResult Transcribe(ReadOnlySpan<float> pcm16kMono, TimeSpan audioDuration);
}
```

Two implementations:

- `GigaAmRnnTEngine` — composes the existing `MelSpectrogramExtractor` and `RnnTGreedyDecoder`.
  Behaviour is unchanged; this is a wrapper, not a rewrite. `IFeatureExtractor` and `IRnnTDecoder`
  survive as internal seams and keep their current tests.
- `WhisperOnnxEngine` — composes `WhisperFeatureExtractor`, `WhisperTokenizer` and
  `WhisperGreedyDecoder`.

```
Main (UI)
  ├─ IAsrService
  │    ├─ IMicCapture                (unchanged)
  │    └─ Func<ModelDescriptor, IAsrEngine>
  │         ├─ GigaAmRnnTEngine  → IFeatureExtractor + IRnnTDecoder
  │         └─ WhisperOnnxEngine → WhisperFeatureExtractor + WhisperTokenizer
  │                                 + WhisperGreedyDecoder
  ├─ IModelCatalog                   (list of ModelDescriptor)
  ├─ IModelProvider                  (HTTP or local-folder acquisition)
  └─ IHotkeyManager                  (unchanged)
```

### Engine lifetime and model switching

`AsrService` currently caches with `_decoder ??= decoderFactory()` and never releases it. Switching
models without touching that would silently keep decoding with the previous model. `IAsrService`
gains:

```csharp
ModelDescriptor CurrentModel { get; }
Task SwitchModelAsync(ModelDescriptor model, CancellationToken ct);
```

`SwitchModelAsync` refuses while `State != Idle`, disposes the cached engine, sets it to `null`,
records the new descriptor, and warms up the replacement on a `ThreadPool` thread. `Dispose`
disposes the engine as before.

### Model catalogue

```csharp
public enum AsrEngineKind { RnnT, Whisper }

public sealed record ModelFileEntry(
    string LocalName,          // name on disk inside the model folder
    string SourceRelativePath, // appended to the resolved base URI
    long   Size,
    string? Sha256);

public sealed record ModelDescriptor(
    string Id,                 // stable key persisted in settings.json
    string DisplayName,        // shown in the combo box
    string? LicenceNote,       // e.g. "non-commercial research only"
    AsrEngineKind Kind,
    string FolderName,         // subfolder under the models root
    string DefaultBaseUri,
    IReadOnlyList<ModelFileEntry> Files)
{
    public long TotalBytes => Files.Sum(f => f.Size);
}
```

`ModelCatalog` exposes `IReadOnlyList<ModelDescriptor> All` and `ModelDescriptor Default`
(GigaAM v3 — the only entry with verified Russian quality today). Three entries:

| Id | Display name | Kind | Folder |
|---|---|---|---|
| `gigaam-v3-rnnt` | GigaAM v3 (RU, RNN-T) | `RnnT` | `gigaam-v3` |
| `crisperwhisper2-small-int8` | CrisperWhisper 2.0 small (int8) | `Whisper` | `crisperwhisper2-small-int8` |
| `crisperwhisper2-small-fp32` | CrisperWhisper 2.0 small (fp32) | `Whisper` | `crisperwhisper2-small-fp32` |

### Paths and the legacy-layout migration

`ModelPaths` stops hard-coding `v3_rnnt_*` filenames. It becomes root + descriptor:

```csharp
public sealed class ModelPaths(string root, ModelDescriptor model)
{
    public string Root { get; }                     // <root>/<model.FolderName>
    public string File(string localName);           // Path.Combine(Root, localName)
}
```

The models root itself is resolved exactly as today — `MYASR_MODELS_DIR` when set, otherwise
`%LOCALAPPDATA%\MyASR\models` — but that logic moves out of `ModelPaths` into
`ModelRoot.Resolve()` so `ModelPaths` has one job. DI registers `ModelRoot.Resolve()` as a
singleton string and builds `ModelPaths` per descriptor.

Existing installations have GigaAM files flat in `%LOCALAPPDATA%\MyASR\models\`. A one-shot
`LegacyModelLayout.Migrate(root)` runs at startup: if the four `v3_*` files sit directly in the
root and `gigaam-v3/` does not already contain them, move them into `gigaam-v3/`. This avoids a
928 MB re-download. The migration is idempotent and logs what it moved.

### Acquisition source

The base URI is resolved per model, first match wins:

1. Environment variable `MYASR_MODEL_SOURCE_<ID>` (`<ID>` upper-cased, `-` → `_`)
2. `UserSettings.ModelSources[modelId]`
3. `ModelDescriptor.DefaultBaseUri`

```csharp
public interface IModelSourceResolver { string ResolveBaseUri(ModelDescriptor model); }
```

`HuggingFaceModelProvider` is renamed to `ModelProvider` and gains a scheme switch. `http`/`https`
keeps the current behaviour — `.partial` resume via `Range`, 250 ms throttled progress, SHA-256
verification, atomic rename. Any other value is treated as a directory or UNC path and the file is
stream-copied with the same `IProgress<DownloadProgress>` contract, the same `.partial` staging and
the same SHA-256 check. Both paths share one `DownloadOneAsync` skeleton so progress semantics
cannot drift.

`.onnx_data` external-data siblings are ordinary manifest entries and must land beside their
`.onnx` with the exact expected name, otherwise ONNX Runtime fails to load the graph.

### Whisper pipeline

**`WhisperFeatureExtractor : IFeatureExtractor`** — 16 kHz mono in, `float[3000, 80]` out:

1. Reflect-pad the signal by `n_fft / 2` on both sides (Whisper uses `center=True`).
2. STFT with a **periodic** Hann window, `n_fft` 400, hop 160; take frames `[:-1]`; power = `|X|²`.
3. Slaney mel filter bank: 80 filters, `sr` 16000, `n_fft` 400, `htk=False`, `norm="slaney"`.
   This is a **new** filter bank — `MelFilterBank.BuildHtk` is wrong for this path and would yield
   believable but incorrect features.
4. `log10(max(mel, 1e-10))`, clamp to `max − 8.0`, then `(x + 4) / 4`.
5. Pad with the log-mel of silence or trim to exactly 3000 frames.

The 30-second window is a natural fit: `Main` already caps a recording at 30 s via `maxLengthTimer`.
Audio longer than one window is out of scope for this iteration; the cap enforces it.

**`WhisperTokenizer`** — decode-only. Loads `vocab.json` (token → id) and inverts it, plus
`added_tokens.json` for specials. `merges.txt` is not needed because we never encode text.
Decoding **must** reverse the GPT-2 byte-to-unicode map and reassemble UTF-8: concatenating raw
token strings produces mojibake for Cyrillic. Special tokens (`<|…|>`, `[UM]`, `<vtx>` and friends)
are skipped in the visible output.

**`WhisperGreedyDecoder`** — two ONNX sessions:

- `encoder_model.onnx`: `input_features [1, 80, 3000]` → `last_hidden_state [1, 1500, 768]`
- `decoder_model_merged.onnx`: `input_ids`, `encoder_hidden_states`, `use_cache_branch`, and
  `past_key_values.{layer}.{decoder,encoder}.{key,value}` → `logits`, `present.*`

Greedy loop: step 0 runs with `use_cache_branch = false` and zero-length dummy past tensors; every
later step feeds the returned `present.*` back as `past_key_values.*` with `use_cache_branch = true`.
Stop on `<|endoftext|>` (50257) or 448 tokens. `suppress_tokens` and `begin_suppress_tokens` from
`generation_config.json` are applied — skipping them invites looping hallucinations rather than an
error.

The prompt prefix is `<|startoftranscript|> <|ru|> <|transcribe|> <|notimestamps|>` plus the mode
token. **The exact position and identity of the mode token is not guessed** — see below.

### Deriving the prompt instead of guessing it

`tools/` already holds `generate_reference.py`. Two scripts join it:

- `tools/export_crisperwhisper_onnx.py` — downloads the model, rewrites `architectures`, runs
  `optimum-cli export onnx --task automatic-speech-recognition-with-past`, produces the int8 variant
  via dynamic quantisation, prints each output file's size and SHA-256 in a form that can be pasted
  straight into `ModelCatalog`, and optionally uploads to a chosen HF repository.
- `tools/generate_crisperwhisper_reference.py` — runs the Python `crisperwhisper` package
  (v2.0.1, `PromptBuilder`) on a fixed WAV in `intended` mode and dumps a JSON reference:
  `input_features` (80 × 3000), the prompt token ids it actually built, the generated token ids, and
  the final text.

The C# tests assert parity against that JSON. The prompt format, the mode token and the feature
normalisation are therefore *extracted from the reference implementation and locked by a test*,
not inferred from documentation.

### UI

`Main.Designer.cs` gains a `modelCombo` in the top row beside the device combo:
`DisplayMember = DisplayName`, populated from `ModelCatalog.All`, initial selection from
`UserSettings.ModelId` (falling back to `ModelCatalog.Default` when the id is unknown). The licence
note, when present, is shown as a tooltip and in the status bar on selection.

On change:

1. Ignore if the recogniser is not `Idle`; revert the selection and flash the status bar.
2. If `!provider.AreModelsAvailable(paths)` → open `DownloadForm` for that descriptor. On cancel or
   failure, revert the combo to the previous model — the app must not end up with no working model.
3. Disable the mic, `await asr.SwitchModelAsync(...)`, re-enable, persist `UserSettings.ModelId`.

`DownloadForm` takes a `ModelDescriptor` instead of the ambient manifest and shows
`"{DisplayName}: {file} — {done}/{total} MB"` plus overall progress across the model's files.
The existing retry button and cancellation are preserved.

Startup keeps its current shape: resolve the descriptor from settings, migrate the legacy layout,
download if absent, warm up, fail loudly with a message box if warm-up throws.

## Error Handling

| Situation | Behaviour |
|---|---|
| Model files missing | `DownloadForm` for that descriptor; cancel → revert to previous model (startup: close the app, as today) |
| Download interrupted | `.partial` resume on retry, unchanged |
| SHA-256 mismatch | Delete `.partial`, throw, retry button offered |
| Local source path unreachable | Same failure surface as an HTTP error: message plus retry |
| Switch while recording or decoding | Rejected; combo reverts; status bar explains |
| Warm-up throws after a switch | Message box, revert to the previous model, keep the app alive |
| Tokenizer or vocabulary file corrupt | Fails during warm-up, not mid-recognition |

## Testing

New and changed tests, all TUnit, all offline:

**Registry layer (phase 1, no model files needed)**
- `ModelCatalogTests` — ids unique, folders unique, every descriptor has files, default is GigaAM.
- `ModelPathsTests` — per-model subfolder composition; extended from the existing file.
- `LegacyModelLayoutTests` — flat `v3_*` files move into `gigaam-v3/`; idempotent; no-op when the
  target already exists.
- `ModelSourceResolverTests` — env beats settings beats default.
- `ModelProviderTests` — extends `HuggingFaceModelProviderTests`: local-folder copy reports the same
  progress shape as HTTP, `.partial` staging, SHA-256 mismatch deletes the staging file and throws,
  `AreModelsAvailable` is per-model.
- `AsrServiceTests` — `SwitchModelAsync` disposes the old engine and builds a new one; a switch is
  rejected while recording; `Transcribe` after a switch uses the new engine.

**Whisper layer (phase 2)**
- `WhisperMelFilterBankTests` — parity with a Python-generated Slaney 80 × 201 reference.
- `WhisperFeatureExtractorTests` — parity with the reference `input_features` for the fixed WAV,
  tolerance 1e-4; output shape is exactly 3000 × 80 for short, exact and over-long input.
- `WhisperTokenizerTests` — Cyrillic byte-level BPE round-trip against reference token ids; special
  and vocal-event tokens are stripped; unknown ids do not throw.
- `WhisperPromptTests` — the prompt built for Russian + intended equals the reference prompt ids.
- `WhisperGreedyDecoderTests` — driven by a fake ONNX session that replays reference logits, so the
  greedy loop, cache handover, suppression and stop condition are tested without model files.
- `CrisperWhisperEndToEndTests` — real ONNX on the fixed WAV, asserting the reference text.
  **Skipped** when the model folder is absent, mirroring how the existing tests treat GigaAM.

## Out of Scope

Verbatim mode and the vocal-event tokens; word-level timestamps; `transcribe_dual`; longform audio
beyond one 30-second window; automatic language detection; hotwords (Pro models only); GPU execution
providers; CrisperWhisper turbo/large/medium entries. The catalogue makes each of these a later
additive change rather than a rewrite.

## Risks

1. **Russian quality is unmeasured.** Mitigated by keeping GigaAM the default and gating the
   end-to-end test on a real recording. If Russian output is poor, the feature's value proposition
   changes and the owner decides before anything ships.
2. **`optimum` export of the custom architecture may still fail** after the `architectures` rewrite.
   Fallback: export from `unsloth/CrisperWhisper`-style plain Whisper class loading, or export the
   encoder and decoder separately. This is the first task in phase 2 so failure surfaces early.
3. **int8 quality on Russian** may degrade more than the ~1 % WER typical for English. Both variants
   are in the catalogue precisely so this can be compared rather than assumed.
4. **Non-commercial licence** constrains distribution of the app if CrisperWhisper is bundled or
   auto-downloaded by default. Not bundling, and keeping GigaAM the default, limits the exposure.

## Phasing

**Phase 1 — model selection infrastructure.** `IAsrEngine`, `GigaAmRnnTEngine`, catalogue, per-model
paths, legacy migration, source resolver, provider scheme switch, `DownloadForm` per descriptor,
combo box, settings, engine switching, and the whole registry test set. Ships and is fully testable
with GigaAM alone; the catalogue simply has one usable entry.

**Phase 2 — CrisperWhisper.** Export tooling, reference generation, Whisper feature extractor,
tokenizer, greedy decoder, engine, catalogue entries, Whisper tests, Russian smoke test.
