# MyASR

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