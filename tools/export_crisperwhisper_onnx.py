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
        # decoder_model_merged.onnx's top-level graph is a single `If` node: the real
        # MatMul weights live in the then/else subgraphs but are declared as outer-scope
        # initializers on the parent graph. Without EnableSubgraph, onnxruntime's dynamic
        # quantizer only inspects the top-level node list, sees one `If` node, and quantizes
        # nothing inside it -- the "quantized" decoder comes out ~fp32-sized because no
        # weight ever actually changed dtype. EnableSubgraph makes it recurse into If/Loop
        # subgraphs and resolve their weights through the parent scope.
        quantize_dynamic(
            src, dst,
            weight_type=QuantType.QInt8,
            extra_options={"EnableSubgraph": True},
        )
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
