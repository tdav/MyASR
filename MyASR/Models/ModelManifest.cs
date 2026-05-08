namespace MyASR.Models;

public sealed record ModelFileEntry(string FileName, string Url, long Size, string? Sha256);

public static class ModelManifest
{
    private const string Base = "https://huggingface.co/istupakov/gigaam-v3-onnx/resolve/main/";

    // SHA256s left as null until known good hashes are captured via `Get-FileHash` after first download.
    public static readonly IReadOnlyList<ModelFileEntry> Files =
    [
        new("v3_rnnt_encoder.onnx", Base + "v3_rnnt_encoder.onnx", 928_000_000, null),
        new("v3_rnnt_decoder.onnx", Base + "v3_rnnt_decoder.onnx", 3_490_000,   null),
        new("v3_rnnt_joint.onnx",   Base + "v3_rnnt_joint.onnx",   1_510_000,   null),
        new("v3_vocab.txt",         Base + "v3_vocab.txt",         198,         null),
    ];
}
