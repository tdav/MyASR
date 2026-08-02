using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using MyASR.Features;
using MyASR.Models;

namespace MyASR.Asr.Whisper;

/// <summary>CrisperWhisper 2.0: Whisper log-mel features into a greedy encoder-decoder decode.</summary>
public sealed class WhisperOnnxEngine : IAsrEngine
{
    private const int EncoderFrames = 1500;
    private const int MaxTargetPositions = 448;

    private readonly WhisperFeatureExtractor features;
    private readonly WhisperTokenizer tokenizer;
    private readonly InferenceSession encoder;
    private readonly string decoderPath;
    private readonly SessionOptions options;
    private readonly int[] prompt;
    private readonly int eotId;
    private readonly HashSet<int> suppress;
    private readonly HashSet<int> beginSuppress;
    private readonly int dModel;

    private WhisperOnnxEngine(
        WhisperFeatureExtractor features,
        WhisperTokenizer tokenizer,
        InferenceSession encoder,
        string decoderPath,
        SessionOptions options,
        int[] prompt,
        int eotId,
        HashSet<int> suppress,
        HashSet<int> beginSuppress,
        int dModel)
    {
        this.features = features;
        this.tokenizer = tokenizer;
        this.encoder = encoder;
        this.decoderPath = decoderPath;
        this.options = options;
        this.prompt = prompt;
        this.eotId = eotId;
        this.suppress = suppress;
        this.beginSuppress = beginSuppress;
        this.dModel = dModel;
    }

    public static WhisperOnnxEngine Create(ModelPaths paths)
    {
        var options = new SessionOptions
        {
            ExecutionMode = ExecutionMode.ORT_PARALLEL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            InterOpNumThreads = 1,
            IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2)
        };

        var tokenizer = WhisperTokenizer.Load(
            paths.File(CrisperWhisperFiles.Vocab),
            paths.File(CrisperWhisperFiles.AddedTokens));

        var encoder = new InferenceSession(paths.File(CrisperWhisperFiles.Encoder), options);
        var dModel = encoder.OutputMetadata.Values.First().Dimensions[^1];
        if (dModel <= 0) dModel = 768;

        var (suppress, beginSuppress) = LoadSuppression(paths.File(CrisperWhisperFiles.GenerationConfig));

        return new WhisperOnnxEngine(
            new WhisperFeatureExtractor(),
            tokenizer,
            encoder,
            paths.File(CrisperWhisperFiles.DecoderMerged),
            options,
            WhisperPrompt.Build(tokenizer, WhisperPrompt.RussianToken, WhisperPrompt.IntendedModeTokens),
            tokenizer.IdOf(WhisperPrompt.EndOfTextToken),
            suppress,
            beginSuppress,
            dModel);
    }

    internal static (HashSet<int> suppress, HashSet<int> beginSuppress) LoadSuppression(string generationConfigPath)
    {
        var suppress = new HashSet<int>();
        var begin = new HashSet<int>();
        if (!File.Exists(generationConfigPath)) return (suppress, begin);

        using var doc = JsonDocument.Parse(File.ReadAllText(generationConfigPath));
        Fill(doc.RootElement, "suppress_tokens", suppress);
        Fill(doc.RootElement, "begin_suppress_tokens", begin);
        return (suppress, begin);

        static void Fill(JsonElement root, string name, HashSet<int> into)
        {
            if (!root.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array) return;
            foreach (var e in array.EnumerateArray())
                if (e.TryGetInt32(out var v)) into.Add(v);
        }
    }

    public AsrResult Transcribe(ReadOnlySpan<float> pcm16kMono, TimeSpan audioDuration)
    {
        var sw = Stopwatch.StartNew();

        var mel = features.Extract(pcm16kMono);
        var encoded = RunEncoder(mel);

        using var step = new WhisperOnnxDecoderStep(
            new InferenceSession(decoderPath, options), encoded, EncoderFrames, dModel);

        var ids = WhisperGreedyLoop.Run(
            step, prompt, eotId, suppress, beginSuppress, MaxTargetPositions);

        return new AsrResult(tokenizer.Decode(ids).Trim(), audioDuration, sw.Elapsed);
    }

    private float[] RunEncoder(float[,] mel)
    {
        var frames = mel.GetLength(0);
        var mels = mel.GetLength(1);

        // ONNX wants [1, mels, frames]; the extractor produces [frames, mels].
        var data = new float[mels * frames];
        for (var t = 0; t < frames; t++)
            for (var m = 0; m < mels; m++)
                data[m * frames + t] = mel[t, m];

        var input = new DenseTensor<float>(data, [1, mels, frames]);
        var name = encoder.InputMetadata.Keys.First();
        using var results = encoder.Run([NamedOnnxValue.CreateFromTensor(name, input)]);
        return results.First().AsTensor<float>().ToArray();
    }

    public void Dispose()
    {
        encoder.Dispose();
        options.Dispose();
    }
}
