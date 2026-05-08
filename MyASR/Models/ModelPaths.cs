using System.IO;

namespace MyASR.Models;

public sealed class ModelPaths(string root)
{
    public string Root { get; } = root;
    public string Encoder => Path.Combine(Root, "v3_rnnt_encoder.onnx");
    public string Decoder => Path.Combine(Root, "v3_rnnt_decoder.onnx");
    public string Joint   => Path.Combine(Root, "v3_rnnt_joint.onnx");
    public string Vocab   => Path.Combine(Root, "v3_vocab.txt");

    public static ModelPaths Default()
    {
        var env = Environment.GetEnvironmentVariable("MYASR_MODELS_DIR");
        if (!string.IsNullOrWhiteSpace(env)) return new ModelPaths(env);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new ModelPaths(Path.Combine(local, "MyASR", "models"));
    }
}
