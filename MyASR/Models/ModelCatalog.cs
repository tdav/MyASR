namespace MyASR.Models;

/// <summary>File names of the GigaAM v3 RNN-T artefacts, shared by the catalogue and the engine.</summary>
public static class GigaAmFiles
{
    public const string Encoder = "v3_rnnt_encoder.onnx";
    public const string Decoder = "v3_rnnt_decoder.onnx";
    public const string Joint   = "v3_rnnt_joint.onnx";
    public const string Vocab   = "v3_vocab.txt";
}

public static class ModelCatalog
{
    private const string GigaAmBase = "https://huggingface.co/istupakov/gigaam-v3-onnx/resolve/main/";

    // SHA-256 digests stay null until known-good hashes are captured after a first download.
    public static readonly ModelDescriptor GigaAmV3 = new(
        Id: "gigaam-v3-rnnt",
        DisplayName: "GigaAM v3 (RU, RNN-T)",
        LicenceNote: null,
        Kind: AsrEngineKind.RnnT,
        FolderName: "gigaam-v3",
        DefaultBaseUri: GigaAmBase,
        Files:
        [
            new(GigaAmFiles.Encoder, GigaAmFiles.Encoder, 928_000_000, null),
            new(GigaAmFiles.Decoder, GigaAmFiles.Decoder, 3_490_000,   null),
            new(GigaAmFiles.Joint,   GigaAmFiles.Joint,   1_510_000,   null),
            new(GigaAmFiles.Vocab,   GigaAmFiles.Vocab,   198,         null),
        ]);

    public static IReadOnlyList<ModelDescriptor> All { get; } = [GigaAmV3];

    public static ModelDescriptor Default => GigaAmV3;

    /// <summary>Looks a model up by its persisted id, falling back to <see cref="Default"/>.</summary>
    public static ModelDescriptor ById(string? id) =>
        All.FirstOrDefault(m => m.Id == id) ?? Default;
}
