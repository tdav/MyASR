namespace MyASR.Models;

/// <summary>File names produced by <c>tools/export_crisperwhisper_onnx.py</c>.</summary>
public static class CrisperWhisperFiles
{
    public const string Encoder             = "encoder_model.onnx";
    public const string DecoderMerged       = "decoder_model_merged.onnx";
    public const string Vocab               = "vocab.json";
    public const string AddedTokens         = "added_tokens.json";
    public const string GenerationConfig    = "generation_config.json";
    public const string PreprocessorConfig  = "preprocessor_config.json";
}
