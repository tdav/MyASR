"""Prepare Russian read speech from FLEURS (Google, CC BY 4.0).

Source and attribution: https://huggingface.co/datasets/google/fleurs
License: https://creativecommons.org/licenses/by/4.0/
Audio is copied verbatim. The manifests use the corpus's normalized transcription.
Requires Python 3.11+; run from the repository root: python tools/prepare_fleurs_ru.py
"""

import argparse
import hashlib
import os
from pathlib import Path
import shutil
import tarfile
import tempfile
from urllib.request import urlopen


REVISION = "70bb2e84b976b7e960aa89f1c648e09c59f894dd"
BASE_URL = f"https://huggingface.co/datasets/google/fleurs/resolve/{REVISION}/data/ru_ru"
CHECKSUMS = {
    "dev.tsv": "25d9dc88a7533fc0f303354a8e6d3c8186c557229c9a0024487090a11226171e",
    "test.tsv": "cd54f261220f49afbb4c128633a737eca4a22f6c0a8233d3cc891478d06676e6",
    "audio/dev.tar.gz": "4dc533cd0f312f749bd6d0fe968f3183288d938e07ed97dc141d9f10570457f9",
    "audio/test.tar.gz": "8a0d6a0d23c3421f50c575bcf65d875cd19dadae2f7cabb415024f81b65178b1",
}


def checksum(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def write_changed(path, data):
    if path.exists() and path.read_bytes() == data:
        return
    with tempfile.NamedTemporaryFile(dir=path.parent, delete=False) as output:
        temporary = Path(output.name)
    try:
        temporary.write_bytes(data)
        temporary.replace(path)
    finally:
        temporary.unlink(missing_ok=True)


def fetch(root, relative):
    destination = root / Path(relative).name
    expected = CHECKSUMS[relative]
    if destination.exists() and checksum(destination) == expected:
        return destination
    with tempfile.NamedTemporaryFile(dir=root, delete=False) as handle:
        temporary = Path(handle.name)
    try:
        with urlopen(f"{BASE_URL}/{relative}", timeout=120) as response, temporary.open("wb") as output:
            shutil.copyfileobj(response, output)
        if checksum(temporary) != expected:
            raise ValueError(f"SHA-256 mismatch for {relative}")
        os.replace(temporary, destination)
    finally:
        temporary.unlink(missing_ok=True)
    return destination


def prepare(root, split, count, used_prompts):
    labels = fetch(root, f"{split}.tsv")
    rows = [line.split("\t") for line in labels.read_text(encoding="utf-8").splitlines()]
    # Deterministic pseudo-random order, independent of quality or ASR predictions.
    rows.sort(key=lambda row: hashlib.sha256(row[1].encode()).digest())
    samples = {}
    for prompt, filename, _, reference, _, frames, _ in rows:
        if prompt in used_prompts or not reference.strip():
            continue
        samples[filename] = (reference, int(frames))
        used_prompts.add(prompt)
        if len(samples) == count:
            break
    if len(samples) != count:
        raise ValueError(f"Only {len(samples)} distinct prompts in {split}, requested {count}")

    folder = root / split
    folder.mkdir(exist_ok=True)
    expected_names = {f"{split}/{name}" for name in samples}
    archive = fetch(root, f"audio/{split}.tar.gz")
    with tarfile.open(archive, "r|gz") as tar:
        for member in tar:
            if member.name in expected_names and member.isfile():
                with tar.extractfile(member) as audio:
                    write_changed(folder / Path(member.name).name, audio.read())
                expected_names.remove(member.name)
    if expected_names:
        raise ValueError(f"Missing audio in {archive}: {sorted(expected_names)}")

    name = "validation" if split == "dev" else "test"
    manifest = root / f"{name}.manifest.tsv"
    content = "".join(f"{folder / filename}\t{ref}\n" for filename, (ref, _) in samples.items())
    write_changed(manifest, content.replace("\n", os.linesep).encode("utf-8"))
    seconds = sum(frames for _, frames in samples.values()) / 16000
    print(f"{manifest}: {len(samples)} utterances, {seconds:.2f} seconds", flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=Path(".asr-eval/fleurs-ru"))
    parser.add_argument("--count", type=int, default=48, help="distinct utterances per split")
    args = parser.parse_args()
    if args.count < 1:
        parser.error("--count must be positive")
    root = args.output.resolve()
    root.mkdir(parents=True, exist_ok=True)
    used_prompts = set()
    for split in ("dev", "test"):
        prepare(root, split, args.count, used_prompts)


if __name__ == "__main__":
    main()
