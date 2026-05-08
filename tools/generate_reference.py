"""
Generate golden reference artefacts for MyASR.Tests:
- reference_filterbank_64x161.json
- reference.wav (16kHz mono PCM)         (added later in Task 3.2)
- reference_mel.bin                      (added later in Task 3.2)
- reference.txt                          (added later in Task 3.2)

Run once: python tools/generate_reference.py
Requires (for filterbank): numpy.
For audio/mel/txt extras: numpy, soundfile, gigaam (pip install gigaam).
"""
import json
import math
import os

import numpy as np

OUT_DIR = os.path.join(os.path.dirname(__file__), "..", "MyASR.Tests", "Assets")
os.makedirs(OUT_DIR, exist_ok=True)

SR = 16000
N_FFT = 320
HOP = 160
N_MELS = 64

def hz_to_mel(f):
    return 2595.0 * math.log10(1.0 + f / 700.0)

def mel_to_hz(m):
    return 700.0 * (10 ** (m / 2595.0) - 1.0)

def htk_filterbank(sr=SR, n_fft=N_FFT, n_mels=N_MELS):
    n_freqs = n_fft // 2 + 1
    f_low_mel = hz_to_mel(0.0)
    f_high_mel = hz_to_mel(sr / 2)
    mel_pts = np.linspace(f_low_mel, f_high_mel, n_mels + 2)
    hz_pts = np.array([mel_to_hz(m) for m in mel_pts])
    bin_pts = np.floor((n_fft + 1) * hz_pts / sr).astype(int)

    fb = np.zeros((n_mels, n_freqs), dtype=np.float64)
    for m in range(1, n_mels + 1):
        f_m_minus = bin_pts[m - 1]
        f_m       = bin_pts[m]
        f_m_plus  = bin_pts[m + 1]
        for k in range(f_m_minus, f_m):
            if f_m == f_m_minus: continue
            fb[m - 1, k] = (k - f_m_minus) / (f_m - f_m_minus)
        for k in range(f_m, f_m_plus):
            if f_m_plus == f_m: continue
            fb[m - 1, k] = (f_m_plus - k) / (f_m_plus - f_m)
    return fb

def write_filterbank_json(fb):
    data = {"shape": list(fb.shape), "data": fb.flatten().tolist()}
    with open(os.path.join(OUT_DIR, "reference_filterbank_64x161.json"), "w", encoding="utf-8") as f:
        json.dump(data, f)

def write_reference_audio_and_mel():
    """Synthesizes a chirp WAV, computes its mel via gigaam, and saves expected text."""
    try:
        import gigaam
        import soundfile as sf
        import torch
    except ImportError:
        print("gigaam/soundfile/torch not installed — skipping reference.wav/mel/txt.")
        return

    import struct
    t = np.linspace(0, 3, 3 * SR, endpoint=False)
    chirp = 0.3 * np.sin(2 * np.pi * (200 + (4000 - 200) * t / 3) * t)
    audio = np.concatenate([chirp, np.zeros(SR, dtype=np.float32)]).astype(np.float32)
    wav_path = os.path.join(OUT_DIR, "reference.wav")
    sf.write(wav_path, audio, SR, subtype="PCM_16")

    model = gigaam.load_model("v3_rnnt")
    fe = model.preprocessor
    feats = fe(torch.from_numpy(audio).unsqueeze(0)).squeeze(0).numpy()
    feats_t_mels = feats.T.astype(np.float32, copy=False)
    mel_path = os.path.join(OUT_DIR, "reference_mel.bin")
    with open(mel_path, "wb") as f:
        f.write(struct.pack("<II", feats_t_mels.shape[0], feats_t_mels.shape[1]))
        f.write(feats_t_mels.tobytes())

    text = model.recognize(wav_path).strip()
    with open(os.path.join(OUT_DIR, "reference.txt"), "w", encoding="utf-8") as f:
        f.write(text)


if __name__ == "__main__":
    fb = htk_filterbank()
    write_filterbank_json(fb)
    print(f"Wrote reference_filterbank_64x161.json with shape {fb.shape}")
    write_reference_audio_and_mel()
    print("done")
