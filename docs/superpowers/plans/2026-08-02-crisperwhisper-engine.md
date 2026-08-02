# CrisperWhisper 2.0 Engine Implementation Plan (Phase 2)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Prerequisite:** `docs/superpowers/plans/2026-08-02-model-selection-infrastructure.md` is complete. `IAsrEngine`, `ModelCatalog`, per-model folders and the model combo box already exist.

**Goal:** Add `nyralabs/CrisperWhisper2.0_small` as two selectable models (int8 and fp32) running on ONNX Runtime, transcribing Russian in `intended` mode.

**Architecture:** We export the ONNX ourselves because none exists for this model size. Two Python tools sit beside the existing `tools/generate_reference.py`: one exports and quantises, one runs the reference Python implementation and dumps golden features, prompt ids, output ids and text. Everything the C# side must match — the mel filter bank, the normalisation, the prompt prefix, the mode token — is asserted against that dump rather than inferred from documentation.

**Tech Stack:** .NET 10, `Microsoft.ML.OnnxRuntime` 1.26.0, TUnit 1.43.37; Python 3.10+ with `optimum[exporters]`, `onnxruntime`, `transformers`, `crisperwhisper==2.0.1`, `numpy`, `soundfile`.

**Spec:** `docs/superpowers/specs/2026-08-02-crisperwhisper-model-selection-design.md`

## Global Constraints

- Private fields are camelCase **without** a `_` prefix. Use `this.` on collisions.
- Whisper geometry for this model, taken from its `preprocessor_config.json` and `config.json` and not to be changed by guesswork: `sampling_rate` 16000, `n_fft` 400, `hop_length` 160, `feature_size` (mels) 80, `chunk_length` 30 s, `nb_max_frames` 3000, `d_model` 768, 12 encoder layers, 12 decoder layers, 12 attention heads, head dim 64, `vocab_size` 51896, `max_target_positions` 448, encoder output length 1500.
- Known token ids for this model: `<|endoftext|>` 50257, `<|startoftranscript|>` 50258, `<|ru|>` 50263, `<|translate|>` 50358, `<|transcribe|>` 50359, `<|notimestamps|>` 50363, `[verbatim_1..5]` 51880-51884, `[intended_1..5]` 51885-51889. Ids are still read from `added_tokens.json` at runtime; these values exist so tests can assert them.
- The model is licensed `nyra-health-non-commercial-research`. Both catalogue entries carry that as their `LicenceNote`.
- Language is forced to `<|ru|>`; transcription mode is `intended`. No auto-detection, no verbatim, no timestamps.
- Test command for every verification step: `dotnet test MyASR.Tests/MyASR.Tests.csproj`
- Every test except `CrisperWhisperEndToEndTests` must pass with **no** `.onnx` file on disk.
- Reference assets live in `MyASR.Tests/Assets/` and are committed, following the existing `reference_filterbank_64x161.json` / `reference_mel.bin` pattern.

---

### Task 1: ONNX export tooling

**Files:**
- Create: `tools/export_crisperwhisper_onnx.py`
- Create: `tools/requirements-crisperwhisper.txt`
- Modify: `README.md`

**Interfaces:**
- Consumes: nothing in C#.
- Produces: a folder per precision containing `encoder_model.onnx`, `decoder_model_merged.onnx`, any `*.onnx_data` siblings, `vocab.json`, `added_tokens.json`, `generation_config.json`, `preprocessor_config.json`; and a printed C# snippet listing each file's name, byte size and SHA-256 for pasting into `ModelCatalog`.

This task has no C# test because it produces build-time artefacts, not shipped code. Its
verification is that the script runs and its output loads in ONNX Runtime.

- [ ] **Step 1: Write the requirements file**

Create `tools/requirements-crisperwhisper.txt`:

```
optimum[exporters]>=1.24
onnx>=1.17
onnxruntime>=1.20
transformers>=4.46
torch>=2.4
numpy>=1.26
soundfile>=0.12
crisperwhisper==2.0.1
```

- [ ] **Step 2: Write the export script**

Create `tools/export_crisperwhisper_onnx.py`:

```python
"""
Export nyralabs/CrisperWhisper2.0_small to ONNX for MyASR.

There is no ONNX release for this model size, so we build it. The model's config
declares the custom class WhisperForConditionalGenerationWithAttentionLoss, which
optimum cannot resolve; the attention-loss head is training-only, so we rewrite
`architectures` to the plain Whisper class before exporting.

Usage:
    pip install -r tools/requirements-crisperwhisper.txt
    python tools/export_crisperwhisper_onnx.py --out build/crisperwhisper

Produces:
    <out>/fp32/   encoder_model.onnx, decoder_model_merged.onnx, tokenizer + config files
    <out>/int8/   the same, dynamically quantised
and prints a ModelFileEntry list for each precision, ready to paste into ModelCatalog.
"""
import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys

MODEL_ID = "nyralabs/CrisperWhisper2.0_small"

# Files the C# runtime needs beside the graphs.
SIDECARS = [
    "vocab.json",
    "added_tokens.json",
    "generation_config.json",
    "preprocessor_config.json",
]

# Graph files the C# runtime loads. Any *.onnx_data sibling is picked up automatically.
GRAPHS = ["encoder_model.onnx", "decoder_model_merged.onnx"]


def snapshot(work_dir):
    from huggingface_hub import snapshot_download
    local = snapshot_download(MODEL_ID, local_dir=os.path.join(work_dir, "src"))
    cfg_path = os.path.join(local, "config.json")
    with open(cfg_path, encoding="utf-8") as f:
        cfg = json.load(f)
    if cfg.get("architectures") != ["WhisperForConditionalGeneration"]:
        cfg["architectures"] = ["WhisperForConditionalGeneration"]
        with open(cfg_path, "w", encoding="utf-8") as f:
            json.dump(cfg, f, indent=2)
        print("Rewrote architectures -> WhisperForConditionalGeneration")
    return local


def export_fp32(src_dir, out_dir):
    os.makedirs(out_dir, exist_ok=True)
    cmd = [
        sys.executable, "-m", "optimum.commands.optimum_cli",
        "export", "onnx",
        "--model", src_dir,
        "--task", "automatic-speech-recognition-with-past",
        "--opset", "17",
        out_dir,
    ]
    print("+", " ".join(cmd))
    subprocess.run(cmd, check=True)


def quantize_int8(fp32_dir, int8_dir):
    from onnxruntime.quantization import QuantType, quantize_dynamic
    os.makedirs(int8_dir, exist_ok=True)
    for graph in GRAPHS:
        src = os.path.join(fp32_dir, graph)
        dst = os.path.join(int8_dir, graph)
        print(f"quantising {graph}")
        quantize_dynamic(src, dst, weight_type=QuantType.QInt8)
    for name in SIDECARS:
        shutil.copy2(os.path.join(fp32_dir, name), os.path.join(int8_dir, name))


def collect_sidecars(src_dir, out_dir):
    for name in SIDECARS:
        src = os.path.join(src_dir, name)
        if os.path.exists(src):
            shutil.copy2(src, os.path.join(out_dir, name))


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def print_catalog_entries(folder, label):
    wanted = []
    for graph in GRAPHS:
        wanted.append(graph)
        data = graph + "_data"
        if os.path.exists(os.path.join(folder, data)):
            wanted.append(data)
    wanted.extend(SIDECARS)

    print(f"\n--- ModelCatalog Files for {label} ---")
    for name in wanted:
        path = os.path.join(folder, name)
        if not os.path.exists(path):
            print(f'            // MISSING: {name}')
            continue
        size = os.path.getsize(path)
        print(f'            new("{name}", "{name}", {size:_}, "{sha256(path)}"),')
    print("--- end ---\n")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default=os.path.join("build", "crisperwhisper"))
    args = ap.parse_args()

    work = os.path.abspath(args.out)
    os.makedirs(work, exist_ok=True)

    src = snapshot(work)
    fp32 = os.path.join(work, "fp32")
    int8 = os.path.join(work, "int8")

    export_fp32(src, fp32)
    collect_sidecars(src, fp32)
    quantize_int8(fp32, int8)

    print_catalog_entries(fp32, "crisperwhisper2-small-fp32")
    print_catalog_entries(int8, "crisperwhisper2-small-int8")


if __name__ == "__main__":
    main()
```

- [ ] **Step 3: Run the export**

Run:

```bash
python -m pip install -r tools/requirements-crisperwhisper.txt
```

```bash
python tools/export_crisperwhisper_onnx.py --out build/crisperwhisper
```

Expected: two folders under `build/crisperwhisper/` and two printed `ModelCatalog Files` blocks.
Save both blocks — Task 7 pastes them into the catalogue.

If `optimum` fails to resolve the architecture despite the rewrite, the fallback is to load the
model explicitly and export from the in-memory object. Add this function and call it instead of
`export_fp32`:

```python
def export_fp32_via_python_api(src_dir, out_dir):
    from optimum.exporters.onnx import main_export
    from transformers import WhisperForConditionalGeneration
    model = WhisperForConditionalGeneration.from_pretrained(src_dir)
    main_export(
        model_name_or_path=src_dir,
        output=out_dir,
        task="automatic-speech-recognition-with-past",
        opset=17,
        model=model,
    )
```

- [ ] **Step 4: Sanity-check the graphs load**

Run:

```bash
python -c "import onnxruntime as ort; s=ort.InferenceSession('build/crisperwhisper/int8/encoder_model.onnx'); print([i.name for i in s.get_inputs()], [o.name for o in s.get_outputs()])"
```

Expected: inputs contain `input_features`, outputs contain `last_hidden_state`.

```bash
python -c "import onnxruntime as ort; s=ort.InferenceSession('build/crisperwhisper/int8/decoder_model_merged.onnx'); print([i.name for i in s.get_inputs()][:6]); print(len(s.get_inputs()))"
```

Expected: first inputs include `input_ids` and `encoder_hidden_states`; total input count is
`3 + 4 * 12 = 51` (`input_ids`, `encoder_hidden_states`, `use_cache_branch`, plus four past tensors
per decoder layer).

- [ ] **Step 5: Document the tool**

Append to `README.md`:

```markdown
## CrisperWhisper 2.0 model export

`nyralabs/CrisperWhisper2.0_small` has no published ONNX build, so MyASR exports it:

```bash
python -m pip install -r tools/requirements-crisperwhisper.txt
python tools/export_crisperwhisper_onnx.py --out build/crisperwhisper
```

The script prints a `ModelCatalog` file list for each precision. Publish the folders to an
HTTP source, or point MyASR at them locally:

```
setx MYASR_MODEL_SOURCE_CRISPERWHISPER2_SMALL_INT8 C:\path\to\build\crisperwhisper\int8
```

The model is licensed `nyra-health-non-commercial-research` — non-commercial research use only.
```

- [ ] **Step 6: Commit**

```bash
git add tools/export_crisperwhisper_onnx.py tools/requirements-crisperwhisper.txt README.md
git commit -m "build: export CrisperWhisper 2.0 small to ONNX in fp32 and int8"
```

---

### Task 2: Golden reference generation

**Files:**
- Create: `tools/generate_crisperwhisper_reference.py`
- Create: `MyASR.Tests/Assets/reference.wav` (generated — currently gitignored and absent)
- Create: `MyASR.Tests/Assets/reference_filterbank_slaney_80x201.json` (generated)
- Create: `MyASR.Tests/Assets/crisperwhisper_features.bin` (generated)
- Create: `MyASR.Tests/Assets/crisperwhisper_reference.json` (generated)
- Modify: `.gitignore` (stop ignoring `reference.wav`)

**Interfaces:**
- Consumes: the export from Task 1 and the existing `MyASR.Tests/Assets/reference.wav`.
- Produces: `reference_filterbank_slaney_80x201.json` with `{"shape":[80,201],"data":[...]}`;
  `crisperwhisper_features.bin` with `<II` header `(frames, mels)` then row-major float32;
  `crisperwhisper_reference.json` with `{"prompt_token_ids":[...], "generated_token_ids":[...], "text":"...", "special_tokens":{...}, "suppress_tokens":[...], "begin_suppress_tokens":[...]}`.

- [ ] **Step 1: Write the generator**

Create `tools/generate_crisperwhisper_reference.py`:

```python
"""
Generate golden artefacts that lock MyASR's Whisper pipeline to the reference
implementation. Everything the C# side could get subtly wrong — the Slaney mel
filters, the log-mel normalisation, the decoder prompt prefix, the mode token —
is captured here instead of being inferred from documentation.

Usage:
    pip install -r tools/requirements-crisperwhisper.txt
    python tools/generate_crisperwhisper_reference.py

Writes into MyASR.Tests/Assets/:
    reference_filterbank_slaney_80x201.json
    crisperwhisper_features.bin
    crisperwhisper_reference.json
"""
import json
import os
import struct

import numpy as np

MODEL_ID = "nyralabs/CrisperWhisper2.0_small"
OUT_DIR = os.path.join(os.path.dirname(__file__), "..", "MyASR.Tests", "Assets")
SR = 16000
N_FFT = 400
N_MELS = 80


def write_slaney_filterbank():
    from transformers.audio_utils import mel_filter_bank
    fb = mel_filter_bank(
        num_frequency_bins=1 + N_FFT // 2,
        num_mel_filters=N_MELS,
        min_frequency=0.0,
        max_frequency=SR / 2.0,
        sampling_rate=SR,
        norm="slaney",
        mel_scale="slaney",
    )  # shape (201, 80)
    fb = np.ascontiguousarray(fb.T)  # -> (80, 201), the orientation MyASR uses
    payload = {"shape": list(fb.shape), "data": fb.flatten().tolist()}
    path = os.path.join(OUT_DIR, "reference_filterbank_slaney_80x201.json")
    with open(path, "w", encoding="utf-8") as f:
        json.dump(payload, f)
    print(f"wrote {path} shape={fb.shape}")


def ensure_reference_wav():
    """
    reference.wav is not in the repository (it was gitignored and never committed), so
    synthesize the same chirp tools/generate_reference.py describes. Needs only numpy and
    soundfile — no model — so the C# feature-parity test has a deterministic input.
    """
    import soundfile as sf
    wav_path = os.path.join(OUT_DIR, "reference.wav")
    if os.path.exists(wav_path):
        return wav_path

    t = np.linspace(0, 3, 3 * SR, endpoint=False)
    chirp = 0.3 * np.sin(2 * np.pi * (200 + (4000 - 200) * t / 3) * t)
    audio = np.concatenate([chirp, np.zeros(SR, dtype=np.float32)]).astype(np.float32)
    sf.write(wav_path, audio, SR, subtype="PCM_16")
    print(f"wrote {wav_path}")
    return wav_path


def write_features_and_tokens():
    from transformers import WhisperFeatureExtractor, WhisperTokenizerFast
    import soundfile as sf

    wav_path = ensure_reference_wav()
    audio, sr = sf.read(wav_path, dtype="float32")
    assert sr == SR, f"reference.wav must be {SR} Hz, got {sr}"

    fe = WhisperFeatureExtractor.from_pretrained(MODEL_ID)
    feats = fe(audio, sampling_rate=SR, return_tensors="np")["input_features"][0]  # (80, 3000)
    feats_t_mels = np.ascontiguousarray(feats.T.astype(np.float32))  # (3000, 80)

    bin_path = os.path.join(OUT_DIR, "crisperwhisper_features.bin")
    with open(bin_path, "wb") as f:
        f.write(struct.pack("<II", feats_t_mels.shape[0], feats_t_mels.shape[1]))
        f.write(feats_t_mels.tobytes())
    print(f"wrote {bin_path} shape={feats_t_mels.shape}")

    tok = WhisperTokenizerFast.from_pretrained(MODEL_ID)
    vocab = tok.get_vocab()

    def tid(name):
        return int(vocab[name])

    prompt_ids, mode_note = build_prompt(tok)
    generated, text = run_reference_decode(wav_path)

    payload = {
        "model_id": MODEL_ID,
        "language": "ru",
        "mode": "intended",
        "mode_source": mode_note,
        "n_mels": N_MELS,
        "n_frames": int(feats_t_mels.shape[0]),
        "special_tokens": {
            "endoftext": tid("<|endoftext|>"),
            "startoftranscript": tid("<|startoftranscript|>"),
            "ru": tid("<|ru|>"),
            "transcribe": tid("<|transcribe|>"),
            "notimestamps": tid("<|notimestamps|>"),
        },
        "prompt_token_ids": prompt_ids,
        "generated_token_ids": generated,
        "text": text,
        "suppress_tokens": load_generation_list("suppress_tokens"),
        "begin_suppress_tokens": load_generation_list("begin_suppress_tokens"),
        "cyrillic_probe": cyrillic_probe(tok),
    }

    json_path = os.path.join(OUT_DIR, "crisperwhisper_reference.json")
    with open(json_path, "w", encoding="utf-8") as f:
        json.dump(payload, f, ensure_ascii=False, indent=1)
    print(f"wrote {json_path}")


def build_prompt(tok):
    """
    Ask the reference PromptBuilder for the exact intended-mode Russian prefix.
    Falling back to a hand-built prefix would defeat the purpose of this file, so
    a failure here is fatal rather than silently approximated.
    """
    from crisperwhisper.prompting import PromptBuilder  # type: ignore
    builder = PromptBuilder(tok)
    ids = builder.build(language="ru", mode="intended", timestamps=False)
    return [int(i) for i in ids], "crisperwhisper.prompting.PromptBuilder"


def run_reference_decode(wav_path):
    from crisperwhisper import CrisperWhisperModel
    model = CrisperWhisperModel("small")
    result = model.transcribe(wav_path, language="ru", mode="intended")
    ids = [int(i) for i in getattr(result, "token_ids", [])]
    return ids, result.text


def load_generation_list(key):
    from transformers import GenerationConfig
    cfg = GenerationConfig.from_pretrained(MODEL_ID)
    value = getattr(cfg, key, None) or []
    return [int(v) for v in value]


def cyrillic_probe(tok):
    """A byte-level BPE round-trip the C# tokenizer must reproduce exactly."""
    text = "привет, как дела?"
    ids = tok.encode(text, add_special_tokens=False)
    return {"text": text, "ids": [int(i) for i in ids]}


if __name__ == "__main__":
    os.makedirs(OUT_DIR, exist_ok=True)
    write_slaney_filterbank()
    write_features_and_tokens()
    print("done")
```

- [ ] **Step 1b: Stop ignoring the reference WAV**

`.gitignore` currently lists `MyASR.Tests/Assets/reference.wav`, which is why the file has never
existed in a fresh clone. The Whisper feature-parity test needs it on any machine, and at 128 KB
it belongs in the repository. Delete that one line, keeping the `reference_mel.bin` line:

```
# Test artefacts that are large or generated
MyASR.Tests/Assets/reference_mel.bin
```

- [ ] **Step 2: Run the generator**

Run:

```bash
python tools/generate_crisperwhisper_reference.py
```

Expected output: three `wrote ...` lines and `done`.

If `crisperwhisper.prompting.PromptBuilder` is not importable under that path, find the real one
and fix the import rather than hand-writing the prompt:

```bash
python -c "import crisperwhisper, pkgutil; print([m.name for m in pkgutil.walk_packages(crisperwhisper.__path__, 'crisperwhisper.')])"
```

Then open the module that owns prompt construction and call its builder. The recorded
`mode_source` field must name whatever you actually called.

- [ ] **Step 3: Inspect what was captured**

Run:

```bash
python -c "import json;d=json.load(open('MyASR.Tests/Assets/crisperwhisper_reference.json',encoding='utf-8'));print(d['prompt_token_ids']);print(d['mode_source']);print(repr(d['text'])[:200])"
```

Expected: a prompt list starting with 50258, containing 50263 and 50359, and ending with the
intended-mode token in the 51885-51889 range.

- [ ] **Step 4: Commit the assets**

```bash
git add .gitignore tools/generate_crisperwhisper_reference.py MyASR.Tests/Assets/reference.wav MyASR.Tests/Assets/reference_filterbank_slaney_80x201.json MyASR.Tests/Assets/crisperwhisper_features.bin MyASR.Tests/Assets/crisperwhisper_reference.json
git commit -m "test: capture golden Whisper features, prompt and tokens from the reference impl"
```

---

### Task 3: Slaney mel filter bank

**Files:**
- Create: `MyASR/Features/WhisperMelFilterBank.cs`
- Test: `MyASR.Tests/Features/WhisperMelFilterBankTests.cs`

**Interfaces:**
- Consumes: `reference_filterbank_slaney_80x201.json` from Task 2.
- Produces: `WhisperMelFilterBank.BuildSlaney(int sampleRate, int nFft, int nMels) -> double[,]` shaped `[nMels, nFft/2 + 1]`.

- [ ] **Step 1: Write the failing test**

Create `MyASR.Tests/Features/WhisperMelFilterBankTests.cs`:

```csharp
using System.Text.Json;
using MyASR.Features;

namespace MyASR.Tests.Features;

public class WhisperMelFilterBankTests
{
    private sealed record Reference(int[] shape, double[] data);

    private static Reference LoadReference()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "reference_filterbank_slaney_80x201.json");
        return JsonSerializer.Deserialize<Reference>(File.ReadAllText(path))!;
    }

    [Test]
    public async Task Shape_Is80By201()
    {
        var fb = WhisperMelFilterBank.BuildSlaney(16000, 400, 80);
        await Assert.That(fb.GetLength(0)).IsEqualTo(80);
        await Assert.That(fb.GetLength(1)).IsEqualTo(201);
    }

    [Test]
    public async Task Matches_TransformersSlaneyReference()
    {
        var reference = LoadReference();
        var fb = WhisperMelFilterBank.BuildSlaney(16000, 400, 80);

        await Assert.That(reference.shape[0]).IsEqualTo(80);
        await Assert.That(reference.shape[1]).IsEqualTo(201);

        var worst = 0.0;
        for (var m = 0; m < 80; m++)
            for (var k = 0; k < 201; k++)
                worst = Math.Max(worst, Math.Abs(fb[m, k] - reference.data[m * 201 + k]));

        await Assert.That(worst).IsLessThan(1e-9);
    }

    [Test]
    public async Task AllWeights_AreNonNegative()
    {
        var fb = WhisperMelFilterBank.BuildSlaney(16000, 400, 80);
        for (var m = 0; m < 80; m++)
            for (var k = 0; k < 201; k++)
                await Assert.That(fb[m, k]).IsGreaterThanOrEqualTo(0.0);
    }

    [Test]
    public async Task DiffersFromHtk_SoTheWrongOneCannotBeUsedSilently()
    {
        var slaney = WhisperMelFilterBank.BuildSlaney(16000, 400, 80);
        var htk = MelFilterBank.BuildHtk(16000, 400, 80);

        var identical = true;
        for (var m = 0; m < 80 && identical; m++)
            for (var k = 0; k < 201; k++)
                if (Math.Abs(slaney[m, k] - htk[m, k]) > 1e-12) { identical = false; break; }

        await Assert.That(identical).IsFalse();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test MyASR.Tests/MyASR.Tests.csproj`
Expected: compile error — `The name 'WhisperMelFilterBank' does not exist in the current context`.

- [ ] **Step 3: Write the implementation**

Create `MyASR/Features/WhisperMelFilterBank.cs`:

```csharp
namespace MyASR.Features;

/// <summary>
/// Slaney-scaled, Slaney-normalised mel filters — what Whisper was trained with.
/// <see cref="MelFilterBank.BuildHtk"/> is a different scale and produces believable but
/// wrong features if used here, so the two live in separate types on purpose.
/// </summary>
public static class WhisperMelFilterBank
{
    private const double FMin = 0.0;
    private const double FSp = 200.0 / 3.0;
    private const double MinLogHz = 1000.0;
    private const double MinLogMel = (MinLogHz - FMin) / FSp; // 15.0

    private static readonly double LogStep = Math.Log(6.4) / 27.0;

    public static double[,] BuildSlaney(int sampleRate, int nFft, int nMels)
    {
        var nFreqs = nFft / 2 + 1;

        var fftFreqs = new double[nFreqs];
        for (var k = 0; k < nFreqs; k++)
            fftFreqs[k] = sampleRate / 2.0 * k / (nFreqs - 1);

        // nMels + 2 mel-spaced points spanning [0, Nyquist].
        var lowMel = HzToMel(0.0);
        var highMel = HzToMel(sampleRate / 2.0);
        var melF = new double[nMels + 2];
        for (var i = 0; i < melF.Length; i++)
            melF[i] = MelToHz(lowMel + (highMel - lowMel) * i / (melF.Length - 1));

        var fb = new double[nMels, nFreqs];
        for (var m = 0; m < nMels; m++)
        {
            var lowerWidth = melF[m + 1] - melF[m];
            var upperWidth = melF[m + 2] - melF[m + 1];

            // Slaney normalisation: unit area per filter.
            var enorm = 2.0 / (melF[m + 2] - melF[m]);

            for (var k = 0; k < nFreqs; k++)
            {
                var lower = (fftFreqs[k] - melF[m]) / lowerWidth;
                var upper = (melF[m + 2] - fftFreqs[k]) / upperWidth;
                var w = Math.Max(0.0, Math.Min(lower, upper));
                fb[m, k] = w * enorm;
            }
        }

        return fb;
    }

    private static double HzToMel(double hz) =>
        hz < MinLogHz
            ? (hz - FMin) / FSp
            : MinLogMel + Math.Log(hz / MinLogHz) / LogStep;

    private static double MelToHz(double mel) =>
        mel < MinLogMel
            ? FMin + FSp * mel
            : MinLogHz * Math.Exp(LogStep * (mel - MinLogMel));
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test MyASR.Tests/MyASR.Tests.csproj`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add MyASR/Features/WhisperMelFilterBank.cs MyASR.Tests/Features/WhisperMelFilterBankTests.cs
git commit -m "feat(features): add Slaney mel filter bank for the Whisper pipeline"
```

---

### Task 4: Whisper feature extractor

**Files:**
- Modify: `MyASR/Features/PowerSpectrum.cs` (add a periodic Hann window)
- Create: `MyASR/Features/WhisperFeatureExtractor.cs`
- Test: `MyASR.Tests/Features/WhisperFeatureExtractorTests.cs`

**Interfaces:**
- Consumes: `WhisperMelFilterBank`, `PowerSpectrum`, `crisperwhisper_features.bin` from Tasks 2 and 3.
- Produces: `PowerSpectrum.HannWindowPeriodic(int n) -> float[]`; `WhisperFeatureExtractor : IFeatureExtractor` returning `float[3000, 80]`.

- [ ] **Step 1: Write the failing test**

Create `MyASR.Tests/Features/WhisperFeatureExtractorTests.cs`:

```csharp
using MyASR.Audio;
using MyASR.Features;

namespace MyASR.Tests.Features;

public class WhisperFeatureExtractorTests
{
    private const int Frames = 3000;
    private const int Mels = 80;

    private static (int frames, int mels, float[] data) LoadReferenceFeatures()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "crisperwhisper_features.bin");
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);
        var t = (int)br.ReadUInt32();
        var m = (int)br.ReadUInt32();
        var data = new float[t * m];
        for (var i = 0; i < data.Length; i++) data[i] = br.ReadSingle();
        return (t, m, data);
    }

    private static float[] LoadReferenceWav()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "reference.wav");
        return WavReader.ReadMono16kFloat(path);
    }

    [Test]
    public async Task HannWindowPeriodic_DiffersFromSymmetric_AndStartsAtZero()
    {
        var periodic = PowerSpectrum.HannWindowPeriodic(8);
        var symmetric = PowerSpectrum.HanningWindow(8);

        await Assert.That(periodic[0]).IsEqualTo(0f);
        await Assert.That(periodic[7]).IsNotEqualTo(symmetric[7]);
        await Assert.That(symmetric[7]).IsEqualTo(0f).Within(1e-6f);
    }

    [Test]
    public async Task Output_IsAlways3000By80_ForShortInput()
    {
        var feats = new WhisperFeatureExtractor().Extract(new float[1600]);
        await Assert.That(feats.GetLength(0)).IsEqualTo(Frames);
        await Assert.That(feats.GetLength(1)).IsEqualTo(Mels);
    }

    [Test]
    public async Task Output_IsAlways3000By80_ForExactlyThirtySeconds()
    {
        var feats = new WhisperFeatureExtractor().Extract(new float[480_000]);
        await Assert.That(feats.GetLength(0)).IsEqualTo(Frames);
        await Assert.That(feats.GetLength(1)).IsEqualTo(Mels);
    }

    [Test]
    public async Task Output_IsAlways3000By80_ForOverlongInput()
    {
        var feats = new WhisperFeatureExtractor().Extract(new float[600_000]);
        await Assert.That(feats.GetLength(0)).IsEqualTo(Frames);
        await Assert.That(feats.GetLength(1)).IsEqualTo(Mels);
    }

    [Test]
    public async Task DynamicRange_IsExactlyTwo_BecauseOfTheWhisperClamp()
    {
        var feats = new WhisperFeatureExtractor().Extract(LoadReferenceWav());
        var max = float.MinValue;
        var min = float.MaxValue;
        for (var t = 0; t < Frames; t++)
            for (var m = 0; m < Mels; m++)
            {
                max = Math.Max(max, feats[t, m]);
                min = Math.Min(min, feats[t, m]);
            }

        // log10 values are clamped to a span of 8 decades and then divided by 4, so the
        // spread is 2.0 regardless of how loud the input was. The absolute peak is not a
        // fixed number — it is (maxLog + 4) / 4, and maxLog depends on the signal.
        await Assert.That(max - min).IsEqualTo(2.0f).Within(1e-4f);
    }

    [Test]
    public async Task Matches_TransformersReferenceFeatures()
    {
        var (t, m, expected) = LoadReferenceFeatures();
        await Assert.That(t).IsEqualTo(Frames);
        await Assert.That(m).IsEqualTo(Mels);

        var actual = new WhisperFeatureExtractor().Extract(LoadReferenceWav());

        var worst = 0.0;
        for (var i = 0; i < Frames; i++)
            for (var j = 0; j < Mels; j++)
                worst = Math.Max(worst, Math.Abs(actual[i, j] - expected[i * Mels + j]));

        await Assert.That(worst).IsLessThan(1e-3);
    }

    [Test]
    public async Task SilentInput_ProducesFiniteValues()
    {
        var feats = new WhisperFeatureExtractor().Extract(new float[16_000]);
        for (var t = 0; t < Frames; t += 250)
            for (var m = 0; m < Mels; m += 20)
                await Assert.That(float.IsFinite(feats[t, m])).IsTrue();
    }
}
```

`WavReader.ReadMono16kFloat(string path) -> float[]` already exists in `MyASR/Audio/WavReader.cs`.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test MyASR.Tests/MyASR.Tests.csproj`
Expected: compile errors — `'PowerSpectrum' is inaccessible due to its protection level` and `The name 'WhisperFeatureExtractor' does not exist in the current context`.

- [ ] **Step 3: Add the periodic window and make PowerSpectrum public**

`PowerSpectrum` is currently `internal static`. The test asserts the two window shapes differ,
which is the whole point of adding a second one, so widen it. In `MyASR/Features/PowerSpectrum.cs`
change the declaration to `public static class PowerSpectrum` and add:

```csharp
    /// <summary>
    /// Periodic Hann window — the convention <c>torch.hann_window</c> uses, and therefore what
    /// Whisper's STFT expects. <see cref="HanningWindow"/> is the symmetric variant used by GigaAM.
    /// </summary>
    public static float[] HannWindowPeriodic(int n)
    {
        var w = new float[n];
        for (var i = 0; i < n; i++)
            w[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n));
        return w;
    }
```

- [ ] **Step 4: Write the extractor**

Create `MyASR/Features/WhisperFeatureExtractor.cs`:

```csharp
namespace MyASR.Features;

/// <summary>
/// Whisper log-mel features: 80 Slaney mels, n_fft 400, hop 160, reflect-centred STFT,
/// padded or trimmed to exactly 30 seconds. Output is <c>[3000, 80]</c>.
/// </summary>
public sealed class WhisperFeatureExtractor : IFeatureExtractor
{
    public const int SampleRate = 16000;
    public const int NFft = 400;
    public const int HopLength = 160;
    public const int NMels = 80;
    public const int NSamples = 480_000; // 30 s
    public const int NFrames = 3000;

    private const double LogFloor = 1e-10;
    private const double DynamicRange = 8.0;

    private readonly float[] window = PowerSpectrum.HannWindowPeriodic(NFft);
    private readonly double[,] filterBank = WhisperMelFilterBank.BuildSlaney(SampleRate, NFft, NMels);

    public float[,] Extract(ReadOnlySpan<float> pcm16kMono)
    {
        // Whisper pads to a fixed 30 s window before the STFT, so the global maximum used by
        // the normalisation below includes the padding. Padding after the STFT would shift it.
        var padded = PadOrTrim(pcm16kMono);
        var centred = ReflectPad(padded, NFft / 2);

        var spec = new float[NFft / 2 + 1];
        var logMel = new double[NFrames, NMels];
        var maxLog = double.NegativeInfinity;

        for (var t = 0; t < NFrames; t++)
        {
            PowerSpectrum.Compute(centred.AsSpan(t * HopLength, NFft), window, NFft, spec);

            for (var m = 0; m < NMels; m++)
            {
                double sum = 0;
                for (var k = 0; k < spec.Length; k++)
                    sum += filterBank[m, k] * spec[k];

                var value = Math.Log10(Math.Max(sum, LogFloor));
                logMel[t, m] = value;
                if (value > maxLog) maxLog = value;
            }
        }

        var floor = maxLog - DynamicRange;
        var feats = new float[NFrames, NMels];
        for (var t = 0; t < NFrames; t++)
            for (var m = 0; m < NMels; m++)
                feats[t, m] = (float)((Math.Max(logMel[t, m], floor) + 4.0) / 4.0);

        return feats;
    }

    internal static float[] PadOrTrim(ReadOnlySpan<float> pcm)
    {
        var buffer = new float[NSamples];
        var n = Math.Min(pcm.Length, NSamples);
        pcm[..n].CopyTo(buffer);
        return buffer;
    }

    /// <summary>Reflect padding that excludes the edge sample, matching numpy's "reflect" mode.</summary>
    internal static float[] ReflectPad(float[] x, int pad)
    {
        var result = new float[x.Length + 2 * pad];
        Array.Copy(x, 0, result, pad, x.Length);
        for (var i = 0; i < pad; i++)
        {
            result[pad - 1 - i] = x[i + 1];
            result[pad + x.Length + i] = x[x.Length - 2 - i];
        }
        return result;
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test MyASR.Tests/MyASR.Tests.csproj`
Expected: PASS.

If `Matches_TransformersReferenceFeatures` fails, the three usual causes in order of likelihood
are: the window is symmetric instead of periodic; the padding to 30 s happens after the STFT
instead of before; the max used by the clamp is per-frame instead of global. Do not widen the
tolerance to make it pass — the tolerance is what makes this test worth having.

- [ ] **Step 6: Commit**

```bash
git add MyASR/Features/PowerSpectrum.cs MyASR/Features/WhisperFeatureExtractor.cs MyASR.Tests/Features/WhisperFeatureExtractorTests.cs
git commit -m "feat(features): add the Whisper log-mel feature extractor"
```

---

### Task 5: Whisper tokenizer (decode only)

**Files:**
- Create: `MyASR/Asr/Whisper/ByteLevelBpe.cs`
- Create: `MyASR/Asr/Whisper/WhisperTokenizer.cs`
- Test: `MyASR.Tests/Asr/Whisper/WhisperTokenizerTests.cs`

**Interfaces:**
- Consumes: `vocab.json` and `added_tokens.json` from the export; `crisperwhisper_reference.json` from Task 2.
- Produces: `ByteLevelBpe.UnicodeToByte` (`IReadOnlyDictionary<char, byte>`); `WhisperTokenizer.Load(string vocabJsonPath, string addedTokensJsonPath)`; instance members `int Count`, `int IdOf(string token)`, `bool TryIdOf(string token, out int id)`, `string Decode(IReadOnlyList<int> ids)`.

Only decoding is implemented: MyASR never turns text into tokens, so `merges.txt` is not needed.

- [ ] **Step 1: Write the failing test**

Create `MyASR.Tests/Asr/Whisper/WhisperTokenizerTests.cs`:

```csharp
using System.Text;
using System.Text.Json;
using MyASR.Asr.Whisper;

namespace MyASR.Tests.Asr.Whisper;

public class WhisperTokenizerTests
{
    /// <summary>
    /// Builds a miniature byte-level BPE vocabulary on the fly so the tokenizer can be tested
    /// without shipping the model's 51896-entry vocab.json.
    /// </summary>
    private static string WriteVocab(string dir, IEnumerable<string> tokens)
    {
        var map = new Dictionary<string, int>();
        var i = 0;
        foreach (var t in tokens) map[t] = i++;
        var path = Path.Combine(dir, "vocab.json");
        File.WriteAllText(path, JsonSerializer.Serialize(map));
        return path;
    }

    private static string WriteAdded(string dir, Dictionary<string, int> added)
    {
        var path = Path.Combine(dir, "added_tokens.json");
        File.WriteAllText(path, JsonSerializer.Serialize(added));
        return path;
    }

    /// <summary>Encodes raw UTF-8 bytes the way GPT-2 byte-level BPE stores them in vocab.json.</summary>
    private static string AsBpeToken(string text)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(text))
            sb.Append(ByteLevelBpe.ByteToUnicode[b]);
        return sb.ToString();
    }

    private static string NewTempDir()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "MyASR-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(tmp);
        return tmp;
    }

    [Test]
    public async Task ByteMaps_AreInverses_AndCoverAll256Bytes()
    {
        await Assert.That(ByteLevelBpe.ByteToUnicode.Count).IsEqualTo(256);
        for (var b = 0; b < 256; b++)
        {
            var c = ByteLevelBpe.ByteToUnicode[(byte)b];
            await Assert.That(ByteLevelBpe.UnicodeToByte[c]).IsEqualTo((byte)b);
        }
    }

    [Test]
    public async Task Decode_ReassemblesCyrillicFromMultiTokenUtf8()
    {
        var dir = NewTempDir();
        try
        {
            // "привет" split across two tokens, each holding raw UTF-8 bytes.
            var vocab = WriteVocab(dir, [AsBpeToken("при"), AsBpeToken("вет")]);
            var added = WriteAdded(dir, []);
            var tok = WhisperTokenizer.Load(vocab, added);

            await Assert.That(tok.Decode([0, 1])).IsEqualTo("привет");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public async Task Decode_SplitsMidCodepoint_StillYieldsValidText()
    {
        var dir = NewTempDir();
        try
        {
            // Split the two bytes of 'п' (0xD0 0xBF) across two tokens.
            var first = ByteLevelBpe.ByteToUnicode[0xD0].ToString();
            var second = ByteLevelBpe.ByteToUnicode[0xBF].ToString();
            var vocab = WriteVocab(dir, [first, second]);
            var tok = WhisperTokenizer.Load(vocab, WriteAdded(dir, []));

            await Assert.That(tok.Decode([0, 1])).IsEqualTo("п");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public async Task Decode_SkipsAngleSpecialTokens()
    {
        var dir = NewTempDir();
        try
        {
            var vocab = WriteVocab(dir, [AsBpeToken("да")]);
            var added = WriteAdded(dir, new Dictionary<string, int>
            {
                ["<|startoftranscript|>"] = 1,
                ["<|ru|>"] = 2,
                ["<|endoftext|>"] = 3
            });
            var tok = WhisperTokenizer.Load(vocab, added);

            await Assert.That(tok.Decode([1, 2, 0, 3])).IsEqualTo("да");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public async Task Decode_SkipsBracketVocalEventTokens()
    {
        var dir = NewTempDir();
        try
        {
            var vocab = WriteVocab(dir, [AsBpeToken("да")]);
            var added = WriteAdded(dir, new Dictionary<string, int>
            {
                ["[UM]"] = 1,
                ["[laughter]"] = 2,
                ["[intended_3]"] = 3
            });
            var tok = WhisperTokenizer.Load(vocab, added);

            await Assert.That(tok.Decode([1, 0, 2, 3])).IsEqualTo("да");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public async Task Decode_UnknownId_IsIgnoredRatherThanThrowing()
    {
        var dir = NewTempDir();
        try
        {
            var tok = WhisperTokenizer.Load(WriteVocab(dir, [AsBpeToken("да")]), WriteAdded(dir, []));
            await Assert.That(tok.Decode([0, 9999])).IsEqualTo("да");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public async Task IdOf_FindsAddedTokens_AndTryIdOfReportsMisses()
    {
        var dir = NewTempDir();
        try
        {
            var tok = WhisperTokenizer.Load(
                WriteVocab(dir, [AsBpeToken("да")]),
                WriteAdded(dir, new Dictionary<string, int> { ["<|ru|>"] = 50263 }));

            await Assert.That(tok.IdOf("<|ru|>")).IsEqualTo(50263);
            await Assert.That(tok.TryIdOf("<|nope|>", out _)).IsFalse();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public async Task IdOf_MissingToken_Throws()
    {
        var dir = NewTempDir();
        try
        {
            var tok = WhisperTokenizer.Load(WriteVocab(dir, [AsBpeToken("да")]), WriteAdded(dir, []));
            await Assert.That(() => tok.IdOf("<|missing|>")).Throws<KeyNotFoundException>();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test MyASR.Tests/MyASR.Tests.csproj`
Expected: compile errors — `The type or namespace name 'Whisper' does not exist in the namespace 'MyASR.Asr'`.

- [ ] **Step 3: Write the byte map**

Create `MyASR/Asr/Whisper/ByteLevelBpe.cs`:

```csharp
namespace MyASR.Asr.Whisper;

/// <summary>
/// GPT-2's reversible byte-to-unicode mapping. Token strings in vocab.json are not text —
/// they are raw bytes rendered as printable characters. Concatenating them directly produces
/// mojibake for anything outside ASCII, which is why Cyrillic needs this map.
/// </summary>
public static class ByteLevelBpe
{
    public static IReadOnlyDictionary<byte, char> ByteToUnicode { get; }
    public static IReadOnlyDictionary<char, byte> UnicodeToByte { get; }

    static ByteLevelBpe()
    {
        var printable = new List<int>();
        for (var b = '!'; b <= '~'; b++) printable.Add(b);
        for (var b = '\u00A1'; b <= '\u00AC'; b++) printable.Add(b);
        for (var b = '\u00AE'; b <= '\u00FF'; b++) printable.Add(b);

        var bytes = new List<int>(printable);
        var chars = new List<int>(printable);

        var next = 0;
        for (var b = 0; b < 256; b++)
        {
            if (printable.Contains(b)) continue;
            bytes.Add(b);
            chars.Add(256 + next);
            next++;
        }

        var toUnicode = new Dictionary<byte, char>(256);
        var toByte = new Dictionary<char, byte>(256);
        for (var i = 0; i < bytes.Count; i++)
        {
            var b = (byte)bytes[i];
            var c = (char)chars[i];
            toUnicode[b] = c;
            toByte[c] = b;
        }

        ByteToUnicode = toUnicode;
        UnicodeToByte = toByte;
    }
}
```

- [ ] **Step 4: Write the tokenizer**

Create `MyASR/Asr/Whisper/WhisperTokenizer.cs`:

```csharp
using System.IO;
using System.Text;
using System.Text.Json;

namespace MyASR.Asr.Whisper;

/// <summary>
/// Decode-only Whisper tokenizer. MyASR never encodes text, so merges.txt is not needed —
/// an id-to-token table plus the byte-level map is enough.
/// </summary>
public sealed class WhisperTokenizer
{
    private readonly Dictionary<int, string> idToToken;
    private readonly Dictionary<string, int> tokenToId;

    private WhisperTokenizer(Dictionary<int, string> idToToken, Dictionary<string, int> tokenToId)
    {
        this.idToToken = idToToken;
        this.tokenToId = tokenToId;
    }

    public int Count => idToToken.Count;

    public static WhisperTokenizer Load(string vocabJsonPath, string addedTokensJsonPath)
    {
        var tokenToId = new Dictionary<string, int>(StringComparer.Ordinal);
        Merge(tokenToId, vocabJsonPath);
        if (File.Exists(addedTokensJsonPath)) Merge(tokenToId, addedTokensJsonPath);

        var idToToken = new Dictionary<int, string>(tokenToId.Count);
        foreach (var (token, id) in tokenToId) idToToken[id] = token;

        return new WhisperTokenizer(idToToken, tokenToId);
    }

    private static void Merge(Dictionary<string, int> into, string jsonPath)
    {
        var parsed = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(jsonPath))
                     ?? throw new InvalidDataException($"Не удалось прочитать словарь: {jsonPath}");
        foreach (var (token, id) in parsed) into[token] = id;
    }

    public int IdOf(string token) =>
        tokenToId.TryGetValue(token, out var id)
            ? id
            : throw new KeyNotFoundException($"Токен '{token}' отсутствует в словаре модели.");

    public bool TryIdOf(string token, out int id) => tokenToId.TryGetValue(token, out id);

    /// <summary>
    /// Turns generated ids into text. Special tokens (<c>&lt;|...|&gt;</c>) and vocal-event or
    /// mode tokens (<c>[UM]</c>, <c>[intended_3]</c>) are dropped; everything else is
    /// reassembled byte-wise and decoded as UTF-8.
    /// </summary>
    public string Decode(IReadOnlyList<int> ids)
    {
        var bytes = new List<byte>(ids.Count * 2);

        foreach (var id in ids)
        {
            if (!idToToken.TryGetValue(id, out var token)) continue;
            if (IsSpecial(token)) continue;

            foreach (var c in token)
                if (ByteLevelBpe.UnicodeToByte.TryGetValue(c, out var b))
                    bytes.Add(b);
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static bool IsSpecial(string token) =>
        (token.StartsWith("<|", StringComparison.Ordinal) && token.EndsWith("|>", StringComparison.Ordinal))
        || (token.StartsWith('[') && token.EndsWith(']'))
        || (token.StartsWith('<') && token.EndsWith('>') && token.Length > 2 && !token.Contains('|'));
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test MyASR.Tests/MyASR.Tests.csproj`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add MyASR/Asr/Whisper MyASR.Tests/Asr/Whisper
git commit -m "feat(asr): add a decode-only byte-level BPE Whisper tokenizer"
```

---

### Task 6: Greedy decode loop

**Files:**
- Create: `MyASR/Asr/Whisper/WhisperPrompt.cs`
- Create: `MyASR/Asr/Whisper/IWhisperDecoderStep.cs`
- Create: `MyASR/Asr/Whisper/WhisperGreedyLoop.cs`
- Test: `MyASR.Tests/Asr/Whisper/WhisperGreedyLoopTests.cs`
- Test: `MyASR.Tests/Asr/Whisper/WhisperPromptTests.cs`

**Interfaces:**
- Consumes: `WhisperTokenizer` from Task 5; `crisperwhisper_reference.json` from Task 2.
- Produces: `WhisperSpecialTokens` record; `WhisperPrompt.Build(WhisperTokenizer, string languageToken, string modeToken) -> int[]`; `IWhisperDecoderStep.Step(ReadOnlySpan<int> inputIds, bool firstStep) -> float[]`; `WhisperGreedyLoop.Run(IWhisperDecoderStep step, int[] prompt, int eotId, IReadOnlySet<int> suppress, IReadOnlySet<int> beginSuppress, int maxTokens) -> List<int>`.

Splitting the loop from the ONNX plumbing is what makes suppression, the cache handover and the
stop condition testable with no model on disk.

- [ ] **Step 1: Write the failing tests**

Create `MyASR.Tests/Asr/Whisper/WhisperGreedyLoopTests.cs`:

```csharp
using MyASR.Asr.Whisper;

namespace MyASR.Tests.Asr.Whisper;

public class WhisperGreedyLoopTests
{
    /// <summary>Replays canned logits so the loop can be tested without any ONNX model.</summary>
    private sealed class ScriptedStep(params int[][] argmaxPerCall) : IWhisperDecoderStep
    {
        private const int Vocab = 64;
        private int call;

        public List<int[]> Received { get; } = [];
        public List<bool> FirstStepFlags { get; } = [];

        public float[] Step(ReadOnlySpan<int> inputIds, bool firstStep)
        {
            Received.Add(inputIds.ToArray());
            FirstStepFlags.Add(firstStep);

            var logits = new float[Vocab];
            var winners = argmaxPerCall[Math.Min(call, argmaxPerCall.Length - 1)];
            call++;
            // Give every "winner" a descending score so ties never decide the outcome.
            for (var i = 0; i < winners.Length; i++) logits[winners[i]] = 10f - i;
            return logits;
        }
    }

    private const int Eot = 50;
    private static readonly int[] Prompt = [1, 2, 3, 4];
    private static readonly HashSet<int> None = [];

    [Test]
    public async Task FirstCall_FeedsTheWholePrompt_WithFirstStepTrue()
    {
        var step = new ScriptedStep([Eot]);
        WhisperGreedyLoop.Run(step, Prompt, Eot, None, None, maxTokens: 10);

        await Assert.That(step.Received[0]).IsEquivalentTo(Prompt);
        await Assert.That(step.FirstStepFlags[0]).IsTrue();
    }

    [Test]
    public async Task LaterCalls_FeedOneTokenWithFirstStepFalse()
    {
        var step = new ScriptedStep([7], [8], [Eot]);
        WhisperGreedyLoop.Run(step, Prompt, Eot, None, None, maxTokens: 10);

        await Assert.That(step.Received[1]).IsEquivalentTo(new[] { 7 });
        await Assert.That(step.Received[2]).IsEquivalentTo(new[] { 8 });
        await Assert.That(step.FirstStepFlags[1]).IsFalse();
        await Assert.That(step.FirstStepFlags[2]).IsFalse();
    }

    [Test]
    public async Task StopsAtEndOfText_AndDoesNotEmitIt()
    {
        var step = new ScriptedStep([7], [8], [Eot]);
        var result = WhisperGreedyLoop.Run(step, Prompt, Eot, None, None, maxTokens: 10);
        await Assert.That(result).IsEquivalentTo(new[] { 7, 8 });
    }

    [Test]
    public async Task StopsAtMaxTokens_WhenEndOfTextNeverArrives()
    {
        var step = new ScriptedStep([7]);
        var result = WhisperGreedyLoop.Run(step, Prompt, Eot, None, None, maxTokens: 5);
        await Assert.That(result.Count).IsEqualTo(5);
    }

    [Test]
    public async Task SuppressedTokens_AreNeverChosen()
    {
        // 7 would win every step; suppressing it must hand the win to 8.
        var step = new ScriptedStep([7, 8], [7, 8], [Eot]);
        var result = WhisperGreedyLoop.Run(step, Prompt, Eot, new HashSet<int> { 7 }, None, maxTokens: 10);
        await Assert.That(result).IsEquivalentTo(new[] { 8, 8 });
    }

    [Test]
    public async Task BeginSuppressedTokens_AreBlockedOnlyOnTheFirstGeneratedToken()
    {
        var step = new ScriptedStep([7, 8], [7, 8], [Eot]);
        var result = WhisperGreedyLoop.Run(step, Prompt, Eot, None, new HashSet<int> { 7 }, maxTokens: 10);
        await Assert.That(result).IsEquivalentTo(new[] { 8, 7 });
    }

    [Test]
    public async Task SuppressingEverythingExceptEot_TerminatesImmediately()
    {
        var step = new ScriptedStep([7, Eot]);
        var result = WhisperGreedyLoop.Run(step, Prompt, Eot, new HashSet<int> { 7 }, None, maxTokens: 10);
        await Assert.That(result).IsEmpty();
    }

    [Test]
    public async Task EmptyPrompt_Throws()
    {
        var step = new ScriptedStep([Eot]);
        await Assert.That(() => WhisperGreedyLoop.Run(step, [], Eot, None, None, 10))
            .Throws<ArgumentException>();
    }
}
```

Create `MyASR.Tests/Asr/Whisper/WhisperPromptTests.cs`:

```csharp
using System.Text.Json;
using MyASR.Asr.Whisper;

namespace MyASR.Tests.Asr.Whisper;

public class WhisperPromptTests
{
    private sealed record Reference(
        Dictionary<string, int> special_tokens,
        int[] prompt_token_ids,
        int[] suppress_tokens,
        int[] begin_suppress_tokens,
        string mode_source);

    private static Reference LoadReference()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "crisperwhisper_reference.json");
        return JsonSerializer.Deserialize<Reference>(File.ReadAllText(path))!;
    }

    [Test]
    public async Task Reference_UsesTheKnownRussianAndTranscribeIds()
    {
        var r = LoadReference();
        await Assert.That(r.special_tokens["startoftranscript"]).IsEqualTo(50258);
        await Assert.That(r.special_tokens["ru"]).IsEqualTo(50263);
        await Assert.That(r.special_tokens["transcribe"]).IsEqualTo(50359);
        await Assert.That(r.special_tokens["endoftext"]).IsEqualTo(50257);
    }

    [Test]
    public async Task Reference_PromptStartsWithStartOfTranscript_AndPicksRussian()
    {
        var r = LoadReference();
        await Assert.That(r.prompt_token_ids[0]).IsEqualTo(50258);
        await Assert.That(r.prompt_token_ids).Contains(50263);
        await Assert.That(r.prompt_token_ids).Contains(50359);
    }

    [Test]
    public async Task Reference_PromptCarriesAnIntendedModeToken()
    {
        var r = LoadReference();
        var intended = r.prompt_token_ids.Where(id => id is >= 51885 and <= 51889).ToList();
        await Assert.That(intended).IsNotEmpty();
    }

    [Test]
    public async Task Reference_RecordsWhereThePromptCameFrom()
    {
        var r = LoadReference();
        await Assert.That(string.IsNullOrWhiteSpace(r.mode_source)).IsFalse();
    }

    [Test]
    public async Task Build_ReproducesTheReferencePrompt()
    {
        var r = LoadReference();
        var tokenizer = WhisperTestVocab.Build(r);

        var built = WhisperPrompt.Build(tokenizer, WhisperPrompt.RussianToken, WhisperPrompt.IntendedModeToken);

        await Assert.That(built).IsEquivalentTo(r.prompt_token_ids);
    }
}
```

Create the shared helper `MyASR.Tests/Asr/Whisper/WhisperTestVocab.cs`:

```csharp
using System.Text.Json;
using MyASR.Asr.Whisper;

namespace MyASR.Tests.Asr.Whisper;

/// <summary>
/// Builds a tokenizer containing only the special tokens the reference prompt uses, so prompt
/// construction can be verified without the model's full 51896-entry vocabulary.
/// </summary>
internal static class WhisperTestVocab
{
    public static WhisperTokenizer Build(object referenceWithSpecialTokens)
    {
        var json = JsonSerializer.Serialize(referenceWithSpecialTokens);
        using var doc = JsonDocument.Parse(json);
        var specials = doc.RootElement.GetProperty("special_tokens");

        var added = new Dictionary<string, int>
        {
            ["<|startoftranscript|>"] = specials.GetProperty("startoftranscript").GetInt32(),
            ["<|ru|>"] = specials.GetProperty("ru").GetInt32(),
            ["<|transcribe|>"] = specials.GetProperty("transcribe").GetInt32(),
            ["<|notimestamps|>"] = specials.GetProperty("notimestamps").GetInt32(),
            ["<|endoftext|>"] = specials.GetProperty("endoftext").GetInt32(),
        };

        // The intended-mode token id is whichever 51885-51889 id the reference prompt used.
        var prompt = doc.RootElement.GetProperty("prompt_token_ids").EnumerateArray()
            .Select(e => e.GetInt32()).ToList();
        var intended = prompt.FirstOrDefault(id => id is >= 51885 and <= 51889);
        if (intended != 0) added[WhisperPrompt.IntendedModeToken] = intended;

        var dir = Path.Combine(Path.GetTempPath(), "MyASR-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var vocabPath = Path.Combine(dir, "vocab.json");
        var addedPath = Path.Combine(dir, "added_tokens.json");
        File.WriteAllText(vocabPath, "{}");
        File.WriteAllText(addedPath, JsonSerializer.Serialize(added));
        return WhisperTokenizer.Load(vocabPath, addedPath);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test MyASR.Tests/MyASR.Tests.csproj`
Expected: compile errors — `The name 'WhisperGreedyLoop' does not exist`, `The name 'WhisperPrompt' does not exist`.

- [ ] **Step 3: Write the prompt builder**

Create `MyASR/Asr/Whisper/WhisperPrompt.cs`:

```csharp
namespace MyASR.Asr.Whisper;

/// <param name="StartOfTranscript">Always the first prompt token.</param>
/// <param name="EndOfText">Terminates generation.</param>
public sealed record WhisperSpecialTokens(
    int StartOfTranscript,
    int EndOfText,
    int Language,
    int Transcribe,
    int NoTimestamps,
    int Mode);

/// <summary>
/// Builds the decoder prompt prefix. CrisperWhisper's verbatim and intended styles differ only
/// by this prefix — the encoder pass is identical — so the mode token belongs here.
/// The exact ordering is locked by <c>WhisperPromptTests.Build_ReproducesTheReferencePrompt</c>
/// against a dump from the reference Python implementation.
/// </summary>
public static class WhisperPrompt
{
    public const string StartOfTranscriptToken = "<|startoftranscript|>";
    public const string EndOfTextToken = "<|endoftext|>";
    public const string RussianToken = "<|ru|>";
    public const string TranscribeToken = "<|transcribe|>";
    public const string NoTimestampsToken = "<|notimestamps|>";

    /// <summary>Intended style: clean readable text. Verbatim would emit [UM], [UH] and stutters.</summary>
    public const string IntendedModeToken = "[intended_3]";

    public static WhisperSpecialTokens Resolve(WhisperTokenizer tokenizer, string languageToken, string modeToken) =>
        new(
            StartOfTranscript: tokenizer.IdOf(StartOfTranscriptToken),
            EndOfText: tokenizer.IdOf(EndOfTextToken),
            Language: tokenizer.IdOf(languageToken),
            Transcribe: tokenizer.IdOf(TranscribeToken),
            NoTimestamps: tokenizer.IdOf(NoTimestampsToken),
            Mode: tokenizer.IdOf(modeToken));

    public static int[] Build(WhisperTokenizer tokenizer, string languageToken, string modeToken)
    {
        var t = Resolve(tokenizer, languageToken, modeToken);
        return [t.StartOfTranscript, t.Language, t.Transcribe, t.NoTimestamps, t.Mode];
    }
}
```

If `Build_ReproducesTheReferencePrompt` fails, the reference dump is authoritative: reorder the
array here (and change `IntendedModeToken` if the reference used a different intensity level) to
match `prompt_token_ids`. Do not weaken the test.

- [ ] **Step 4: Write the loop**

Create `MyASR/Asr/Whisper/IWhisperDecoderStep.cs`:

```csharp
namespace MyASR.Asr.Whisper;

/// <summary>
/// One decoder forward pass. Implementations own the KV cache; the greedy loop stays pure so it
/// can be tested without a model.
/// </summary>
public interface IWhisperDecoderStep
{
    /// <param name="inputIds">The whole prompt on the first call, then one token per call.</param>
    /// <param name="firstStep">True only for the initial no-cache pass.</param>
    /// <returns>Logits for the final position, length = vocabulary size.</returns>
    float[] Step(ReadOnlySpan<int> inputIds, bool firstStep);
}
```

Create `MyASR/Asr/Whisper/WhisperGreedyLoop.cs`:

```csharp
namespace MyASR.Asr.Whisper;

public static class WhisperGreedyLoop
{
    public static List<int> Run(
        IWhisperDecoderStep step,
        int[] prompt,
        int eotId,
        IReadOnlySet<int> suppress,
        IReadOnlySet<int> beginSuppress,
        int maxTokens)
    {
        if (prompt.Length == 0)
            throw new ArgumentException("Промпт декодера не может быть пустым.", nameof(prompt));

        var generated = new List<int>(Math.Min(maxTokens, 64));

        var logits = step.Step(prompt, firstStep: true);

        for (var i = 0; i < maxTokens; i++)
        {
            // begin_suppress_tokens applies only to the first generated position; without it the
            // model happily opens with a token it was told never to start on.
            var next = ArgMax(logits, suppress, i == 0 ? beginSuppress : null);
            if (next == eotId) break;

            generated.Add(next);
            if (generated.Count >= maxTokens) break;

            Span<int> one = [next];
            logits = step.Step(one, firstStep: false);
        }

        return generated;
    }

    private static int ArgMax(float[] logits, IReadOnlySet<int> suppress, IReadOnlySet<int>? alsoSuppress)
    {
        var best = -1;
        var bestValue = float.NegativeInfinity;

        for (var i = 0; i < logits.Length; i++)
        {
            if (suppress.Contains(i)) continue;
            if (alsoSuppress is not null && alsoSuppress.Contains(i)) continue;
            if (logits[i] <= bestValue) continue;
            bestValue = logits[i];
            best = i;
        }

        if (best < 0)
            throw new InvalidOperationException("Все токены подавлены — декодировать нечего.");

        return best;
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test MyASR.Tests/MyASR.Tests.csproj`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add MyASR/Asr/Whisper MyASR.Tests/Asr/Whisper
git commit -m "feat(asr): add the Whisper prompt builder and a testable greedy decode loop"
```

---

### Task 7: ONNX decoder step, engine and catalogue entries

**Files:**
- Create: `MyASR/Asr/Whisper/WhisperOnnxDecoderStep.cs`
- Create: `MyASR/Asr/Whisper/WhisperOnnxEngine.cs`
- Create: `MyASR/Models/CrisperWhisperFiles.cs`
- Modify: `MyASR/Models/ModelCatalog.cs`
- Modify: `MyASR/Program.cs` (engine factory)
- Test: `MyASR.Tests/Models/CrisperWhisperCatalogTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 3-6, plus `ModelPaths` and `IAsrEngine` from phase 1.
- Produces: `CrisperWhisperFiles` constants; `ModelCatalog.CrisperWhisperSmallInt8`, `ModelCatalog.CrisperWhisperSmallFp32`; `WhisperOnnxDecoderStep(InferenceSession decoder, float[] encoderHidden, int encoderFrames, int dModel)`; `WhisperOnnxEngine.Create(ModelPaths paths) -> WhisperOnnxEngine`.

- [ ] **Step 1: Write the failing test**

Create `MyASR.Tests/Models/CrisperWhisperCatalogTests.cs`:

```csharp
using MyASR.Models;

namespace MyASR.Tests.Models;

public class CrisperWhisperCatalogTests
{
    [Test]
    public async Task Catalogue_ContainsBothPrecisions()
    {
        var ids = ModelCatalog.All.Select(m => m.Id).ToList();
        await Assert.That(ids).Contains("crisperwhisper2-small-int8");
        await Assert.That(ids).Contains("crisperwhisper2-small-fp32");
    }

    [Test]
    public async Task BothEntries_UseTheWhisperEngine()
    {
        foreach (var id in new[] { "crisperwhisper2-small-int8", "crisperwhisper2-small-fp32" })
            await Assert.That(ModelCatalog.ById(id).Kind).IsEqualTo(AsrEngineKind.Whisper);
    }

    [Test]
    public async Task BothEntries_CarryTheNonCommercialLicenceNote()
    {
        foreach (var id in new[] { "crisperwhisper2-small-int8", "crisperwhisper2-small-fp32" })
        {
            var note = ModelCatalog.ById(id).LicenceNote;
            await Assert.That(string.IsNullOrWhiteSpace(note)).IsFalse();
            await Assert.That(note!).Contains("некоммерч");
        }
    }

    [Test]
    public async Task BothEntries_ListEveryFileTheEngineOpens()
    {
        foreach (var id in new[] { "crisperwhisper2-small-int8", "crisperwhisper2-small-fp32" })
        {
            var names = ModelCatalog.ById(id).Files.Select(f => f.LocalName).ToList();
            await Assert.That(names).Contains(CrisperWhisperFiles.Encoder);
            await Assert.That(names).Contains(CrisperWhisperFiles.DecoderMerged);
            await Assert.That(names).Contains(CrisperWhisperFiles.Vocab);
            await Assert.That(names).Contains(CrisperWhisperFiles.AddedTokens);
            await Assert.That(names).Contains(CrisperWhisperFiles.GenerationConfig);
        }
    }

    [Test]
    public async Task Int8_IsSmallerThanFp32()
    {
        var int8 = ModelCatalog.ById("crisperwhisper2-small-int8").TotalBytes;
        var fp32 = ModelCatalog.ById("crisperwhisper2-small-fp32").TotalBytes;
        await Assert.That(int8).IsLessThan(fp32);
    }

    [Test]
    public async Task Default_IsStillGigaAm_BecauseRussianQualityIsUnverified()
    {
        await Assert.That(ModelCatalog.Default.Id).IsEqualTo("gigaam-v3-rnnt");
    }

    [Test]
    public async Task EachEntry_HasItsOwnFolder()
    {
        var folders = ModelCatalog.All.Select(m => m.FolderName).ToList();
        await Assert.That(folders.Distinct().Count()).IsEqualTo(folders.Count);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test MyASR.Tests/MyASR.Tests.csproj`
Expected: compile error — `The name 'CrisperWhisperFiles' does not exist in the current context`.

- [ ] **Step 3: Add the file names and catalogue entries**

Create `MyASR/Models/CrisperWhisperFiles.cs`:

```csharp
namespace MyASR.Models;

/// <summary>File names produced by <c>tools/export_crisperwhisper_onnx.py</c>.</summary>
public static class CrisperWhisperFiles
{
    public const string Encoder          = "encoder_model.onnx";
    public const string DecoderMerged    = "decoder_model_merged.onnx";
    public const string Vocab            = "vocab.json";
    public const string AddedTokens      = "added_tokens.json";
    public const string GenerationConfig = "generation_config.json";
}
```

In `MyASR/Models/ModelCatalog.cs`, add the two descriptors and extend `All`:

```csharp
    private const string CrisperWhisperLicence =
        "некоммерческая исследовательская лицензия nyra-health";

    // Replace DefaultBaseUri with wherever the exported folders are published, or leave it and
    // set MYASR_MODEL_SOURCE_CRISPERWHISPER2_SMALL_INT8 to a local folder.
    private const string CrisperWhisperInt8Base = "https://huggingface.co/USER/CrisperWhisper2.0_small-onnx/resolve/main/int8/";
    private const string CrisperWhisperFp32Base = "https://huggingface.co/USER/CrisperWhisper2.0_small-onnx/resolve/main/fp32/";

    // Sizes below are the values printed by tools/export_crisperwhisper_onnx.py. They only feed
    // the progress bar before the source reports a real length, so being approximate is fine —
    // but paste the printed block over them, digests included, once you have published a build.
    public static readonly ModelDescriptor CrisperWhisperSmallInt8 = new(
        Id: "crisperwhisper2-small-int8",
        DisplayName: "CrisperWhisper 2.0 small (int8)",
        LicenceNote: CrisperWhisperLicence,
        Kind: AsrEngineKind.Whisper,
        FolderName: "crisperwhisper2-small-int8",
        DefaultBaseUri: CrisperWhisperInt8Base,
        Files:
        [
            new(CrisperWhisperFiles.Encoder,          CrisperWhisperFiles.Encoder,           92_000_000, null),
            new(CrisperWhisperFiles.DecoderMerged,    CrisperWhisperFiles.DecoderMerged,    170_000_000, null),
            new(CrisperWhisperFiles.Vocab,            CrisperWhisperFiles.Vocab,              1_100_000, null),
            new(CrisperWhisperFiles.AddedTokens,      CrisperWhisperFiles.AddedTokens,           40_000, null),
            new(CrisperWhisperFiles.GenerationConfig, CrisperWhisperFiles.GenerationConfig,       4_000, null),
        ]);

    public static readonly ModelDescriptor CrisperWhisperSmallFp32 = new(
        Id: "crisperwhisper2-small-fp32",
        DisplayName: "CrisperWhisper 2.0 small (fp32)",
        LicenceNote: CrisperWhisperLicence,
        Kind: AsrEngineKind.Whisper,
        FolderName: "crisperwhisper2-small-fp32",
        DefaultBaseUri: CrisperWhisperFp32Base,
        Files:
        [
            new(CrisperWhisperFiles.Encoder,          CrisperWhisperFiles.Encoder,          350_000_000, null),
            new(CrisperWhisperFiles.DecoderMerged,    CrisperWhisperFiles.DecoderMerged,    620_000_000, null),
            new(CrisperWhisperFiles.Vocab,            CrisperWhisperFiles.Vocab,              1_100_000, null),
            new(CrisperWhisperFiles.AddedTokens,      CrisperWhisperFiles.AddedTokens,           40_000, null),
            new(CrisperWhisperFiles.GenerationConfig, CrisperWhisperFiles.GenerationConfig,       4_000, null),
        ]);

    public static IReadOnlyList<ModelDescriptor> All { get; } =
        [GigaAmV3, CrisperWhisperSmallInt8, CrisperWhisperSmallFp32];
```

If the export produced `*.onnx_data` siblings, add them as extra `ModelFileEntry` rows with the
same name for `LocalName` and `SourceRelativePath` — ONNX Runtime resolves them by exact filename
next to the graph.

- [ ] **Step 4: Write the ONNX decoder step**

Create `MyASR/Asr/Whisper/WhisperOnnxDecoderStep.cs`:

```csharp
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace MyASR.Asr.Whisper;

/// <summary>
/// Drives <c>decoder_model_merged.onnx</c>. The merged graph carries both a no-cache and a
/// with-cache branch selected by <c>use_cache_branch</c>; the no-cache pass still requires the
/// past inputs to be present, so it gets zero-length tensors.
/// </summary>
public sealed class WhisperOnnxDecoderStep : IWhisperDecoderStep, IDisposable
{
    private const string InputIds = "input_ids";
    private const string EncoderHiddenStates = "encoder_hidden_states";
    private const string UseCacheBranch = "use_cache_branch";
    private const string PastPrefix = "past_key_values.";
    private const string PresentPrefix = "present.";

    private readonly InferenceSession session;
    private readonly DenseTensor<float> encoderHidden;
    private readonly string[] pastNames;
    private readonly int numHeads;
    private readonly int headDim;

    private readonly Dictionary<string, DenseTensor<float>> cache;

    public WhisperOnnxDecoderStep(InferenceSession session, float[] encoderHidden, int encoderFrames, int dModel)
    {
        this.session = session;
        this.encoderHidden = new DenseTensor<float>(encoderHidden, [1, encoderFrames, dModel]);

        pastNames = session.InputMetadata.Keys
            .Where(k => k.StartsWith(PastPrefix, StringComparison.Ordinal))
            .ToArray();

        // past_key_values.N.decoder.key has shape [batch, heads, seq, head_dim]; heads is fixed
        // in the graph, head_dim follows from d_model. For Whisper the encoder's output width
        // equals the decoder's d_model, which is why one value serves both — do not "simplify"
        // this by reading the head dim from somewhere else.
        var sample = session.InputMetadata[pastNames[0]].Dimensions;
        numHeads = sample[1] > 0 ? sample[1] : 12;
        headDim = dModel / numHeads;

        cache = new Dictionary<string, DenseTensor<float>>(pastNames.Length);
    }

    public float[] Step(ReadOnlySpan<int> inputIds, bool firstStep)
    {
        var ids = new long[inputIds.Length];
        for (var i = 0; i < inputIds.Length; i++) ids[i] = inputIds[i];

        var inputs = new List<NamedOnnxValue>(pastNames.Length + 3)
        {
            NamedOnnxValue.CreateFromTensor(InputIds, new DenseTensor<long>(ids, [1, ids.Length])),
            NamedOnnxValue.CreateFromTensor(EncoderHiddenStates, encoderHidden),
        };

        if (session.InputMetadata.ContainsKey(UseCacheBranch))
            inputs.Add(NamedOnnxValue.CreateFromTensor(
                UseCacheBranch, new DenseTensor<bool>(new[] { !firstStep }, [1])));

        foreach (var name in pastNames)
            inputs.Add(NamedOnnxValue.CreateFromTensor(name, PastFor(name, firstStep)));

        using var results = session.Run(inputs);

        float[]? logits = null;

        foreach (var r in results)
        {
            if (r.Name == "logits")
            {
                var tensor = r.AsTensor<float>();
                var dims = tensor.Dimensions;
                var vocab = dims[^1];
                var seq = dims[^2];
                var all = tensor.ToArray();
                logits = all[((seq - 1) * vocab)..(seq * vocab)]; // last position only
            }
            else if (r.Name.StartsWith(PresentPrefix, StringComparison.Ordinal))
            {
                var pastName = PastPrefix + r.Name[PresentPrefix.Length..];

                // Cross-attention K/V are computed from encoder_hidden_states on the no-cache
                // branch only. On later steps the graph merely passes the past inputs through,
                // and several optimum exports emit those present.*.encoder.* outputs as
                // zero-length. Overwriting the encoder cache with an empty tensor makes the
                // decode degrade into nonsense with no exception, so capture it once and keep it.
                var isEncoderSide = pastName.Contains(".encoder.", StringComparison.Ordinal);
                if (isEncoderSide && !firstStep) continue;

                var tensor = r.AsTensor<float>();
                cache[pastName] = new DenseTensor<float>(tensor.ToArray(), tensor.Dimensions.ToArray());
            }
        }

        return logits ?? throw new InvalidOperationException("Декодер не вернул logits.");
    }

    private DenseTensor<float> PastFor(string name, bool firstStep)
    {
        if (!firstStep && cache.TryGetValue(name, out var cached)) return cached;

        // The no-cache branch still requires every past input to be present, so it gets
        // zero-length tensors for both the decoder and the encoder side.
        return new DenseTensor<float>(Array.Empty<float>(), [1, numHeads, 0, headDim]);
    }

    public void Dispose() => session.Dispose();
}
```

- [ ] **Step 4b: Probe the exported graph's cache behaviour before trusting it**

The single most likely way this feature produces fluent nonsense instead of an error is the
cross-attention cache going empty after the first step. Confirm what your export actually does:

```bash
python -c "
import numpy as np, onnxruntime as ort
s = ort.InferenceSession('build/crisperwhisper/int8/decoder_model_merged.onnx')
names = [i.name for i in s.get_inputs()]
past = [n for n in names if n.startswith('past_key_values.')]
enc = np.zeros((1, 1500, 768), np.float32)
empty = {n: np.zeros((1, 12, 0, 64), np.float32) for n in past}
out0 = s.run(None, {'input_ids': np.array([[50258, 50263, 50359, 50363]], np.int64),
                    'encoder_hidden_states': enc, 'use_cache_branch': np.array([False]), **empty})
onames = [o.name for o in s.get_outputs()]
cache0 = {onames[i].replace('present.', 'past_key_values.'): out0[i]
          for i in range(len(onames)) if onames[i].startswith('present.')}
out1 = s.run(None, {'input_ids': np.array([[123]], np.int64),
                    'encoder_hidden_states': enc, 'use_cache_branch': np.array([True]), **cache0})
enc_out = [(onames[i], out1[i].shape) for i in range(len(onames)) if '.encoder.' in onames[i]][:2]
print('encoder present shapes on step 2:', enc_out)
"
```

Expected: shapes like `(1, 12, 1500, 64)`. If they come back with a `0` in the third position,
the guard added in Step 4 is what keeps the decode correct — leave it in either way, it costs
nothing when the outputs are full.

- [ ] **Step 5: Write the engine**

Create `MyASR/Asr/Whisper/WhisperOnnxEngine.cs`:

```csharp
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using MyASR.Features;
using MyASR.Models;

namespace MyASR.Asr.Whisper;

/// <summary>CrisperWhisper 2.0: Whisper log-mel features into a greedy encoder-decoder decode.</summary>
public sealed class WhisperOnnxEngine : IAsrEngine
{
    private const int EncoderFrames = 1500;
    private const int MaxTargetPositions = 448;

    private readonly WhisperFeatureExtractor features;
    private readonly WhisperTokenizer tokenizer;
    private readonly InferenceSession encoder;
    private readonly string decoderPath;
    private readonly SessionOptions options;
    private readonly int[] prompt;
    private readonly int eotId;
    private readonly HashSet<int> suppress;
    private readonly HashSet<int> beginSuppress;
    private readonly int dModel;

    private WhisperOnnxEngine(
        WhisperFeatureExtractor features,
        WhisperTokenizer tokenizer,
        InferenceSession encoder,
        string decoderPath,
        SessionOptions options,
        int[] prompt,
        int eotId,
        HashSet<int> suppress,
        HashSet<int> beginSuppress,
        int dModel)
    {
        this.features = features;
        this.tokenizer = tokenizer;
        this.encoder = encoder;
        this.decoderPath = decoderPath;
        this.options = options;
        this.prompt = prompt;
        this.eotId = eotId;
        this.suppress = suppress;
        this.beginSuppress = beginSuppress;
        this.dModel = dModel;
    }

    public static WhisperOnnxEngine Create(ModelPaths paths)
    {
        var options = new SessionOptions
        {
            ExecutionMode = ExecutionMode.ORT_PARALLEL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            InterOpNumThreads = 1,
            IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2)
        };

        var tokenizer = WhisperTokenizer.Load(
            paths.File(CrisperWhisperFiles.Vocab),
            paths.File(CrisperWhisperFiles.AddedTokens));

        var encoder = new InferenceSession(paths.File(CrisperWhisperFiles.Encoder), options);
        var dModel = encoder.OutputMetadata.Values.First().Dimensions[^1];
        if (dModel <= 0) dModel = 768;

        var (suppress, beginSuppress) = LoadSuppression(paths.File(CrisperWhisperFiles.GenerationConfig));

        return new WhisperOnnxEngine(
            new WhisperFeatureExtractor(),
            tokenizer,
            encoder,
            paths.File(CrisperWhisperFiles.DecoderMerged),
            options,
            WhisperPrompt.Build(tokenizer, WhisperPrompt.RussianToken, WhisperPrompt.IntendedModeToken),
            tokenizer.IdOf(WhisperPrompt.EndOfTextToken),
            suppress,
            beginSuppress,
            dModel);
    }

    internal static (HashSet<int> suppress, HashSet<int> beginSuppress) LoadSuppression(string generationConfigPath)
    {
        var suppress = new HashSet<int>();
        var begin = new HashSet<int>();
        if (!File.Exists(generationConfigPath)) return (suppress, begin);

        using var doc = JsonDocument.Parse(File.ReadAllText(generationConfigPath));
        Fill(doc.RootElement, "suppress_tokens", suppress);
        Fill(doc.RootElement, "begin_suppress_tokens", begin);
        return (suppress, begin);

        static void Fill(JsonElement root, string name, HashSet<int> into)
        {
            if (!root.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array) return;
            foreach (var e in array.EnumerateArray())
                if (e.TryGetInt32(out var v)) into.Add(v);
        }
    }

    public AsrResult Transcribe(ReadOnlySpan<float> pcm16kMono, TimeSpan audioDuration)
    {
        var sw = Stopwatch.StartNew();

        var mel = features.Extract(pcm16kMono);
        var encoded = RunEncoder(mel);

        using var step = new WhisperOnnxDecoderStep(
            new InferenceSession(decoderPath, options), encoded, EncoderFrames, dModel);

        var ids = WhisperGreedyLoop.Run(
            step, prompt, eotId, suppress, beginSuppress, MaxTargetPositions);

        return new AsrResult(tokenizer.Decode(ids).Trim(), audioDuration, sw.Elapsed);
    }

    private float[] RunEncoder(float[,] mel)
    {
        var frames = mel.GetLength(0);
        var mels = mel.GetLength(1);

        // ONNX wants [1, mels, frames]; the extractor produces [frames, mels].
        var data = new float[mels * frames];
        for (var t = 0; t < frames; t++)
            for (var m = 0; m < mels; m++)
                data[m * frames + t] = mel[t, m];

        var input = new DenseTensor<float>(data, [1, mels, frames]);
        var name = encoder.InputMetadata.Keys.First();
        using var results = encoder.Run([NamedOnnxValue.CreateFromTensor(name, input)]);
        return results.First().AsTensor<float>().ToArray();
    }

    public void Dispose()
    {
        encoder.Dispose();
        options.Dispose();
    }
}
```

- [ ] **Step 6: Wire the engine into DI**

In `MyASR/Program.cs`, extend the engine factory added in phase 1:

```csharp
            return descriptor => descriptor.Kind switch
            {
                AsrEngineKind.RnnT => GigaAmRnnTEngine.Create(pathsFor(descriptor)),
                AsrEngineKind.Whisper => WhisperOnnxEngine.Create(pathsFor(descriptor)),
                _ => throw new NotSupportedException($"Движок {descriptor.Kind} ещё не реализован.")
            };
```

Add `using MyASR.Asr.Whisper;` to the file.

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test MyASR.Tests/MyASR.Tests.csproj`
Expected: PASS, including the seven `CrisperWhisperCatalogTests`.

- [ ] **Step 8: Commit**

```bash
git add MyASR/Asr/Whisper MyASR/Models MyASR/Program.cs MyASR.Tests/Models/CrisperWhisperCatalogTests.cs
git commit -m "feat(asr): run CrisperWhisper 2.0 small through ONNX Runtime"
```

---

### Task 8: End-to-end verification on real Russian speech

**Files:**
- Create: `MyASR.Tests/Asr/Whisper/CrisperWhisperEndToEndTests.cs`
- Modify: `docs/superpowers/plans/qa-checklist.md`

**Interfaces:**
- Consumes: everything above.
- Produces: a test that runs the real model when it is on disk and is skipped otherwise, and a recorded verdict on Russian quality.

- [ ] **Step 1: Write the gated test**

Create `MyASR.Tests/Asr/Whisper/CrisperWhisperEndToEndTests.cs`:

```csharp
using MyASR.Asr;
using MyASR.Asr.Whisper;
using MyASR.Audio;
using MyASR.Models;

namespace MyASR.Tests.Asr.Whisper;

/// <summary>
/// Runs the real ONNX model. Skipped unless both the model folder and a Russian sample exist,
/// mirroring how the GigaAM reference tests treat missing model files.
///
/// To run it:
///   set MYASR_MODELS_DIR to a root containing crisperwhisper2-small-int8/
///   place a 16 kHz mono WAV of Russian speech at MyASR.Tests/Assets/russian_sample.wav
///   place its expected transcript at MyASR.Tests/Assets/russian_sample.txt
/// </summary>
public class CrisperWhisperEndToEndTests
{
    private static readonly ModelDescriptor Model = ModelCatalog.CrisperWhisperSmallInt8;

    private static string? ModelFolder()
    {
        var root = Environment.GetEnvironmentVariable("MYASR_MODELS_DIR");
        if (string.IsNullOrWhiteSpace(root)) return null;
        var paths = new ModelPaths(root, Model);
        foreach (var f in Model.Files)
            if (!File.Exists(paths.File(f.LocalName))) return null;
        return paths.Root;
    }

    private static string SamplePath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "russian_sample.wav");

    private static string ExpectedPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "russian_sample.txt");

    private static bool CanRun() => ModelFolder() is not null && File.Exists(SamplePath);

    [Test]
    [Skip("Требуются файлы модели и MyASR.Tests/Assets/russian_sample.wav")]
    public async Task Transcribes_RussianSpeech()
    {
        if (!CanRun()) return;

        var root = Environment.GetEnvironmentVariable("MYASR_MODELS_DIR")!;
        using var engine = WhisperOnnxEngine.Create(new ModelPaths(root, Model));

        var pcm = WavReader.ReadMono16kFloat(SamplePath);
        var result = engine.Transcribe(pcm, TimeSpan.FromSeconds(pcm.Length / 16000.0));

        await Assert.That(string.IsNullOrWhiteSpace(result.Text)).IsFalse();
        await Assert.That(ContainsCyrillic(result.Text)).IsTrue();

        if (File.Exists(ExpectedPath))
        {
            var expected = Normalise(File.ReadAllText(ExpectedPath));
            var actual = Normalise(result.Text);
            await Assert.That(WordErrorRate(expected, actual)).IsLessThan(0.30);
        }
    }

    [Test]
    [Skip("Требуются файлы модели и MyASR.Tests/Assets/russian_sample.wav")]
    public async Task IntendedMode_DoesNotEmitVocalEventMarkers()
    {
        if (!CanRun()) return;

        var root = Environment.GetEnvironmentVariable("MYASR_MODELS_DIR")!;
        using var engine = WhisperOnnxEngine.Create(new ModelPaths(root, Model));

        var pcm = WavReader.ReadMono16kFloat(SamplePath);
        var text = engine.Transcribe(pcm, TimeSpan.FromSeconds(pcm.Length / 16000.0)).Text;

        foreach (var marker in new[] { "[UM]", "[UH]", "[laughter]", "<|", "[intended_" })
            await Assert.That(text).DoesNotContain(marker);
    }

    private static bool ContainsCyrillic(string s) => s.Any(c => c is >= '\u0400' and <= '\u04FF');

    private static string[] Normalise(string s) =>
        s.ToLowerInvariant()
         .Where(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c))
         .Aggregate(new System.Text.StringBuilder(), (sb, c) => sb.Append(c))
         .ToString()
         .Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Levenshtein distance over words, divided by the reference length.</summary>
    private static double WordErrorRate(string[] reference, string[] hypothesis)
    {
        if (reference.Length == 0) return hypothesis.Length == 0 ? 0 : 1;

        var previous = new int[hypothesis.Length + 1];
        var current = new int[hypothesis.Length + 1];
        for (var j = 0; j <= hypothesis.Length; j++) previous[j] = j;

        for (var i = 1; i <= reference.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= hypothesis.Length; j++)
            {
                var cost = reference[i - 1] == hypothesis[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }

        return previous[hypothesis.Length] / (double)reference.Length;
    }
}
```

- [ ] **Step 2: Run the suite and confirm the gate**

Run: `dotnet test MyASR.Tests/MyASR.Tests.csproj`
Expected: PASS, with the two end-to-end tests reported as skipped.

- [ ] **Step 3: Run the real model manually**

Record a 5-20 second Russian WAV at 16 kHz mono, save it as `MyASR.Tests/Assets/russian_sample.wav`
with its transcript in `russian_sample.txt`, export the model, then:

```bash
set MYASR_MODELS_DIR=C:\models\myasr
```

Copy `build/crisperwhisper/int8` into `C:\models\myasr\crisperwhisper2-small-int8`, remove the two
`[Skip(...)]` attributes locally, and run:

```bash
dotnet test MyASR.Tests/MyASR.Tests.csproj
```

Expected: both tests pass. Restore the `[Skip]` attributes before committing — CI has no model.

- [ ] **Step 4: Record the Russian-quality verdict**

Append to `docs/superpowers/plans/qa-checklist.md`:

```markdown
## CrisperWhisper 2.0 small — Russian quality

Measured on `MyASR.Tests/Assets/russian_sample.wav` with the int8 export:

- Word error rate: _fill in_
- Verdict on making it the default instead of GigaAM v3: _fill in_
- Notes on int8 versus fp32 for Russian: _fill in_

The spec keeps GigaAM as the catalogue default until these numbers say otherwise.
```

Fill the three lines with the numbers you measured before committing. Leaving them blank defeats
the purpose of the step — the whole feature rests on whether Russian works.

- [ ] **Step 5: Manual application check**

Run: `dotnet build MyASR/MyASR.csproj -c Debug`, launch the app, and verify:

1. The model combo lists three entries.
2. Selecting "CrisperWhisper 2.0 small (int8)" with no files present opens the download dialog
   titled with that model name and showing overall progress.
3. Cancelling the download reverts the combo to GigaAM and the app still transcribes.
4. Completing the acquisition switches the engine, and the status bar shows the licence note.
5. Restarting the app reopens with the previously selected model.

- [ ] **Step 6: Commit**

```bash
git add MyASR.Tests/Asr/Whisper/CrisperWhisperEndToEndTests.cs docs/superpowers/plans/qa-checklist.md
git commit -m "test: gate CrisperWhisper end-to-end checks on model and sample availability"
```

---

## Phase 2 Done When

- `dotnet test MyASR.Tests/MyASR.Tests.csproj` passes on a machine with no model files, with only
  the two end-to-end tests skipped.
- `WhisperFeatureExtractorTests.Matches_TransformersReferenceFeatures` passes at 1e-3, proving the
  Slaney filters, the periodic window, the centred STFT and the normalisation all match.
- `WhisperPromptTests.Build_ReproducesTheReferencePrompt` passes, proving the prompt prefix and the
  intended-mode token were taken from the reference implementation rather than guessed.
- A real Russian recording transcribes into Cyrillic with no `[UM]`, `[intended_*]` or `<|...|>`
  markers in the output, and the measured word error rate is written into the QA checklist.
