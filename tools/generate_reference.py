"""Regenerate the GigaAM acoustic test fixtures with NumPy, torch and torchaudio.
If the optional gigaam package is installed, also write a reference transcript.
"""
import json
import struct
import wave
from pathlib import Path

import numpy as np
import torch
import torchaudio

ASSETS = Path(__file__).resolve().parents[1] / "MyASR.Tests" / "Assets"
SR, N_FFT, HOP, N_MELS = 16000, 320, 160, 64


def write_filterbank():
    bank = torchaudio.functional.melscale_fbanks(
        N_FFT // 2 + 1, 0.0, SR / 2, N_MELS, SR, norm=None, mel_scale="htk"
    ).T
    data = {"shape": list(bank.shape), "data": bank.flatten().tolist()}
    (ASSETS / "reference_filterbank_64x161.json").write_text(json.dumps(data), encoding="utf-8")


def write_reference_mel(wav_path):
    with wave.open(str(wav_path), "rb") as wav:
        if (wav.getnchannels(), wav.getsampwidth(), wav.getframerate()) != (1, 2, SR):
            raise ValueError("reference.wav must be mono 16 kHz PCM-16")
        pcm = np.frombuffer(wav.readframes(wav.getnframes()), dtype="<i2")
    samples = torch.from_numpy(pcm.astype(np.float32) / 32768.0)
    transform = torchaudio.transforms.MelSpectrogram(
        sample_rate=SR, n_fft=N_FFT, win_length=N_FFT, hop_length=HOP,
        n_mels=N_MELS, center=False, mel_scale="htk",
    )
    features = transform(samples).clamp(1e-9, 1e9).log().T.numpy().astype("<f4", copy=False)
    with (ASSETS / "reference_mel.bin").open("wb") as output:
        output.write(struct.pack("<II", *features.shape))
        output.write(features.tobytes())


def write_optional_transcript(wav_path):
    try:
        import gigaam
    except ModuleNotFoundError as exc:
        if exc.name != "gigaam":
            raise
        print("No gigaam package installed, skipping optional reference.txt")
        return
    text = gigaam.load_model("v3_rnnt").transcribe(str(wav_path))
    (ASSETS / "reference.txt").write_text(text.strip(), encoding="utf-8")


if __name__ == "__main__":
    wav_path = ASSETS / "reference.wav"
    write_filterbank()
    write_reference_mel(wav_path)
    write_optional_transcript(wav_path)
