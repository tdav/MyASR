using System.IO;

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

    public static readonly ModelDescriptor GigaAmV3E2eInt8 = new(
        Id: "gigaam-v3-e2e-rnnt-int8",
        DisplayName: "GigaAM v3 e2e (RU, RNN-T, int8 — с пунктуацией)",
        LicenceNote: null,
        Kind: AsrEngineKind.RnnT,
        FolderName: "gigaam-v3-e2e-int8",
        DefaultBaseUri: GigaAmBase,
        Files:
        [
            new(GigaAmFiles.Encoder, "v3_e2e_rnnt_encoder.int8.onnx", 224_570_477, null),
            new(GigaAmFiles.Decoder, "v3_e2e_rnnt_decoder.int8.onnx", 1_159_170,   null),
            new(GigaAmFiles.Joint,   "v3_e2e_rnnt_joint.int8.onnx",   687_791,     null),
            new(GigaAmFiles.Vocab,   "v3_e2e_rnnt_vocab.txt",         13_354,      null),
        ]);

    public static readonly ModelDescriptor GigaAmV3E2eFp32 = new(
        Id: "gigaam-v3-e2e-rnnt-fp32",
        DisplayName: "GigaAM v3 e2e (RU, RNN-T, fp32 — с пунктуацией)",
        LicenceNote: null,
        Kind: AsrEngineKind.RnnT,
        FolderName: "gigaam-v3-e2e-fp32",
        DefaultBaseUri: GigaAmBase,
        Files:
        [
            new(GigaAmFiles.Encoder, "v3_e2e_rnnt_encoder.onnx", 885_084_534, null),
            new(GigaAmFiles.Decoder, "v3_e2e_rnnt_decoder.onnx", 4_599_910,   null),
            new(GigaAmFiles.Joint,   "v3_e2e_rnnt_joint.onnx",   2_712_896,   null),
            new(GigaAmFiles.Vocab,   "v3_e2e_rnnt_vocab.txt",    13_354,      null),
        ]);

    public static readonly ModelDescriptor GigaAmV3Int8 = new(
        Id: "gigaam-v3-rnnt-int8",
        DisplayName: "GigaAM v3 (RU, RNN-T, int8 — без пунктуации)",
        LicenceNote: null,
        Kind: AsrEngineKind.RnnT,
        FolderName: "gigaam-v3-int8",
        DefaultBaseUri: GigaAmBase,
        Files:
        [
            new(GigaAmFiles.Encoder, "v3_rnnt_encoder.int8.onnx", 224_570_473, null),
            new(GigaAmFiles.Decoder, "v3_rnnt_decoder.int8.onnx", 842_048,     null),
            new(GigaAmFiles.Joint,   "v3_rnnt_joint.int8.onnx",   366_698,     null),
            new(GigaAmFiles.Vocab,   GigaAmFiles.Vocab,           198,         null),
        ]);

    // SHA-256 digests stay null until known-good hashes are captured after a first download.
    public static readonly ModelDescriptor GigaAmV3 = new(
        Id: "gigaam-v3-rnnt",
        DisplayName: "GigaAM v3 (RU, RNN-T, fp32 — без пунктуации)",
        LicenceNote: null,
        Kind: AsrEngineKind.RnnT,
        FolderName: "gigaam-v3",
        DefaultBaseUri: GigaAmBase,
        Files:
        [
            new(GigaAmFiles.Encoder, GigaAmFiles.Encoder, 885_084_534, null),
            new(GigaAmFiles.Decoder, GigaAmFiles.Decoder, 3_331_429,   null),
            new(GigaAmFiles.Joint,   GigaAmFiles.Joint,   1_440_448,   null),
            new(GigaAmFiles.Vocab,   GigaAmFiles.Vocab,   198,         null),
        ]);

    private const string CrisperWhisperLicence =
        "некоммерческая исследовательская лицензия nyra-health";

    /// <summary>
    /// CrisperWhisper has no published ONNX build, so there is no URL to default to. The default
    /// source is a local folder the user fills with the output of
    /// tools/export_crisperwhisper_onnx.py; an HTTP mirror can be supplied per model through
    /// MYASR_MODEL_SOURCE_&lt;ID&gt; or settings.json without touching this file.
    /// </summary>
    private static string LocalSourceFolder(string folderName) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MyASR", "model-sources", folderName);

    // Sizes and digests below are the values printed by tools/export_crisperwhisper_onnx.py for
    // the build documented in .superpowers/sdd/p2-task-1-report.md. The digests are verified on
    // acquisition, so they must match the artefacts the user actually has; if you re-export with
    // different versions, paste the newly printed block over these.
    public static readonly ModelDescriptor CrisperWhisperSmallInt8 = new(
        Id: "crisperwhisper2-small-int8",
        DisplayName: "CrisperWhisper 2.0 small (int8)",
        LicenceNote: CrisperWhisperLicence,
        Kind: AsrEngineKind.Whisper,
        FolderName: "crisperwhisper2-small-int8",
        DefaultBaseUri: LocalSourceFolder("crisperwhisper2-small-int8"),
        Files:
        [
            new("encoder_model.onnx", "encoder_model.onnx", 92_216_514, "5d9167e3c5a583702f2c951782cbf7ab924896ac18accf8e7bd3492d93ad51b9"),
            new("decoder_model_merged.onnx", "decoder_model_merged.onnx", 195_142_781, "ce58bf3997f1ecb937e6f1c125211515578c8e41e66f41f981961da6c51412b7"),
            new("vocab.json", "vocab.json", 835_550, "8f680bba319e01a653d2e8a5dbc17a9157179e0576e6ce74ce0c06356c6e24f9"),
            new("added_tokens.json", "added_tokens.json", 35_275, "54e87fd086baa530e05082c11feacf9df5439453c5fae279611f3099ec844dc5"),
            new("generation_config.json", "generation_config.json", 3_834, "f1003e27072800e0d7c764a03159c17c07b3448102e24463eb099b3e4899b048"),
            new("preprocessor_config.json", "preprocessor_config.json", 356, "994838f1fa6462c8b9b3c90edada831f11f3dd8b4664634e18f4694d005c9dbf"),
        ]);

    public static readonly ModelDescriptor CrisperWhisperSmallFp32 = new(
        Id: "crisperwhisper2-small-fp32",
        DisplayName: "CrisperWhisper 2.0 small (fp32)",
        LicenceNote: CrisperWhisperLicence,
        Kind: AsrEngineKind.Whisper,
        FolderName: "crisperwhisper2-small-fp32",
        DefaultBaseUri: LocalSourceFolder("crisperwhisper2-small-fp32"),
        Files:
        [
            new("encoder_model.onnx", "encoder_model.onnx", 352_747_519, "7ac7c1d6b93f974550759d5eb98d22d012378325861f1bbe64f63d4108df9983"),
            new("decoder_model_merged.onnx", "decoder_model_merged.onnx", 774_523_818, "45bb2a2e5e102615767477570aab60e7a4551e182bfc7b28cb86072265a0c86e"),
            new("vocab.json", "vocab.json", 835_550, "8f680bba319e01a653d2e8a5dbc17a9157179e0576e6ce74ce0c06356c6e24f9"),
            new("added_tokens.json", "added_tokens.json", 35_275, "54e87fd086baa530e05082c11feacf9df5439453c5fae279611f3099ec844dc5"),
            new("generation_config.json", "generation_config.json", 3_834, "f1003e27072800e0d7c764a03159c17c07b3448102e24463eb099b3e4899b048"),
            new("preprocessor_config.json", "preprocessor_config.json", 356, "994838f1fa6462c8b9b3c90edada831f11f3dd8b4664634e18f4694d005c9dbf"),
        ]);

    public static IReadOnlyList<ModelDescriptor> All { get; } =
        [GigaAmV3E2eInt8, GigaAmV3E2eFp32, GigaAmV3Int8, GigaAmV3, CrisperWhisperSmallInt8, CrisperWhisperSmallFp32];

    public static ModelDescriptor Default => GigaAmV3E2eInt8;

    /// <summary>Looks a model up by its persisted id, falling back to <see cref="Default"/>.</summary>
    public static ModelDescriptor ById(string? id) =>
        All.FirstOrDefault(m => m.Id == id) ?? Default;
}
