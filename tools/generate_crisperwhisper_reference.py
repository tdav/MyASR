"""
Generate golden artefacts that lock MyASR's Whisper pipeline to the reference
implementation. Everything the C# side could get subtly wrong -- the Slaney
mel filters, the log-mel normalisation, the decoder prompt prefix, the mode
token -- is captured here instead of being inferred from documentation.

Usage:
    pip install -r tools/requirements-crisperwhisper.txt
    python tools/generate_crisperwhisper_reference.py

Writes into MyASR.Tests/Assets/:
    reference_filterbank_slaney_80x201.json
    crisperwhisper_features.bin
    crisperwhisper_reference.json

Notes on adapting this from the original brief
------------------------------------------------
The brief guessed two things about the `crisperwhisper` package's API that
turned out to be wrong once the installed package was actually inspected
(`crisperwhisper==` whatever ships in the MyASR-build venv):

1. There is no `crisperwhisper.prompting.PromptBuilder`. The real class is
   `crisperwhisper.prompt.PromptBuilder`, and it is constructed from an
   *engine* object (it calls `engine.get_decoder_prefix()` /
   `engine.encode_text()`), not from a bare tokenizer. It also has no
   generic `.build(language=..., mode=..., timestamps=...)` method --
   language is bound at construction and mode is chosen via `.verbatim()`
   or `.intended()`.

2. `TranscriptionResult` (crisperwhisper.result) carries no `token_ids`
   field, so `CrisperWhisperModel.transcribe()` alone cannot yield the
   generated token ids the brief asked for. This script instead replays
   the *exact* internal short-audio decode path
   (`CrisperWhisperModel._transcribe_short`, verified by reading
   crisperwhisper/model.py) against the same engine instance the public
   `.transcribe()` call used, and asserts the replayed text matches the
   public API's text byte-for-byte before trusting the token ids.

Both adaptations are recorded verbatim in the output JSON's `mode_source`
and `decode_source` fields.
"""
import json
import os
import struct

import numpy as np

MODEL_ID = "nyralabs/CrisperWhisper2.0_small"

# Prefer an already-downloaded local snapshot over hitting the Hugging Face
# Hub. Override with the CRISPERWHISPER_LOCAL_PATH env var if the snapshot
# lives elsewhere; fall back to the hub id if no local snapshot is found.
LOCAL_MODEL_PATH = os.environ.get(
    "CRISPERWHISPER_LOCAL_PATH",
    r"C:\Works\MyASR-build\crisperwhisper\src",
)
if not os.path.isdir(LOCAL_MODEL_PATH):
    LOCAL_MODEL_PATH = MODEL_ID

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
    soundfile -- no model -- so the C# feature-parity test has a deterministic input.
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

    fe = WhisperFeatureExtractor.from_pretrained(LOCAL_MODEL_PATH)
    feats = fe(audio, sampling_rate=SR, return_tensors="np")["input_features"][0]  # (80, 3000)
    feats_t_mels = np.ascontiguousarray(feats.T.astype(np.float32))  # (3000, 80)

    bin_path = os.path.join(OUT_DIR, "crisperwhisper_features.bin")
    with open(bin_path, "wb") as f:
        f.write(struct.pack("<II", feats_t_mels.shape[0], feats_t_mels.shape[1]))
        f.write(feats_t_mels.tobytes())
    print(f"wrote {bin_path} shape={feats_t_mels.shape}")

    tok = WhisperTokenizerFast.from_pretrained(LOCAL_MODEL_PATH)
    vocab = tok.get_vocab()

    def tid(name):
        return int(vocab[name])

    from crisperwhisper import CrisperWhisperModel
    model = CrisperWhisperModel(
        LOCAL_MODEL_PATH, backend="transformers", compute_type="float32", device="cpu",
    )

    prompt_ids, mode_note = build_prompt(model._engine)
    generated, text, decode_note = run_reference_decode(model, wav_path)

    payload = {
        "model_id": MODEL_ID,
        "language": "ru",
        "mode": "intended",
        "mode_source": mode_note,
        "decode_source": decode_note,
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


def build_prompt(engine):
    """
    Ask the reference PromptBuilder for the exact intended-mode Russian prefix.

    `crisperwhisper.prompting.PromptBuilder` (the brief's guess) does not exist.
    The real class is `crisperwhisper.prompt.PromptBuilder`; it is constructed
    from an *engine* (here, the TransformersEngine backing `CrisperWhisperModel`),
    because it needs `engine.get_decoder_prefix(language)` and
    `engine.encode_text(text)` -- not just a tokenizer. Its `_build()` method
    (verified by reading crisperwhisper/prompt.py) encodes the
    "[intended_1]...[intended_5]" mode-tag text *first* and appends the
    standard Whisper decoder prefix (<|startoftranscript|><|ru|><|transcribe|>
    <|notimestamps|>) *after* it -- the opposite order the brief guessed.
    Falling back to a hand-built prefix would defeat the purpose of this
    file, so a failure here is fatal rather than silently approximated.
    """
    from crisperwhisper.prompt import PromptBuilder
    builder = PromptBuilder(engine, language="ru")
    ids = builder.intended()
    return (
        [int(i) for i in ids],
        "crisperwhisper.prompt.PromptBuilder(engine, language='ru').intended()",
    )


def run_reference_decode(model, wav_path):
    """
    Run the real `CrisperWhisperModel.transcribe()` for the text, then recover
    the generated token ids by replaying the exact code path
    `CrisperWhisperModel._transcribe_short` takes for audio <= 30s (verified
    by reading crisperwhisper/model.py) against the *same* engine instance,
    since `TranscriptionResult` has no `token_ids` field. The replayed text is
    asserted equal to the public API's text before the ids are trusted.

    `hallucination_mitigation=False`: `crisperwhisper.hallucination`
    unconditionally does `import ctranslate2` at module import time, even
    though `TransformersEngine.generate_with_repair` only needs its pure
    loop-detection helper. This environment is CPU/PyTorch-only
    (backend="transformers", per the brief) and does not have ctranslate2
    installed, so `hallucination_mitigation=True` raises
    `ModuleNotFoundError: No module named 'ctranslate2'` even though nothing
    CT2-specific is actually required. Disabling it only turns off post-hoc
    n-gram-repeat repair; the underlying greedy decode is unaffected.
    """
    from crisperwhisper.prompt import PromptBuilder, strip_prompt_artifacts
    from crisperwhisper.fallback import decode_with_coverage_fallback
    from crisperwhisper.audio import load_audio

    result = model.transcribe(
        wav_path, language="ru", mode="intended", hallucination_mitigation=False,
    )

    engine = model._engine
    prompt_builder = PromptBuilder(engine, language="ru")
    prompt_tokens = prompt_builder.intended(hotwords=None)
    sibling_prompt = prompt_builder.verbatim(hotwords=None)

    audio_array = load_audio(wav_path, sr=None)
    features, mel = engine.extract_features_with_mel(audio_array)
    gen_ids = engine.generate_with_repair(
        features, prompt_tokens, max_length=256,
        hallucination_mitigation=False, suppress_tokens=None,
    )
    gen_ids, _ = decode_with_coverage_fallback(
        engine, features, mel, prompt_tokens, gen_ids, None,
        max_length=256, want_attention=False, enabled=True,
        ref_prompt_tokens=sibling_prompt, suppress_tokens=None,
    )
    ids = [int(i) for i in gen_ids]

    replay_text = strip_prompt_artifacts(engine.decode_tokens(ids, skip_special=True))
    assert replay_text == result.text, (
        "replayed token-id decode does not match CrisperWhisperModel.transcribe() "
        f"text: {replay_text!r} != {result.text!r}"
    )

    note = (
        "crisperwhisper.model.CrisperWhisperModel(LOCAL_MODEL_PATH, "
        "backend='transformers').transcribe(language='ru', mode='intended', "
        "hallucination_mitigation=False) for text; generated_token_ids recovered "
        "by replaying TransformersEngine.generate_with_repair(...) + "
        "fallback.decode_with_coverage_fallback(...) against the same "
        "model._engine instance, matching CrisperWhisperModel._transcribe_short's "
        "short-audio (<=30s) path token-for-token -- verified by asserting the "
        "replayed decode equals the public transcribe() text above."
    )
    return ids, result.text, note


def load_generation_list(key):
    from transformers import GenerationConfig
    cfg = GenerationConfig.from_pretrained(LOCAL_MODEL_PATH)
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
