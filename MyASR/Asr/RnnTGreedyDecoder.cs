using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using MyASR.Models;
using System.Diagnostics;

namespace MyASR.Asr;

/// <summary>
/// Greedy RNN-T decoder. Loads three ONNX sessions and runs the standard
/// encoder → (decoder + joint) loop emitting tokens whenever joint &gt; blank.
/// </summary>
public sealed class RnnTGreedyDecoder : IRnnTDecoder
{
    private const int MaxSymbolsPerStep = 5;
    private const int PredHidden = 320;

    private readonly InferenceSession _encoder;
    private readonly InferenceSession _decoder;
    private readonly InferenceSession _joint;
    private readonly Vocabulary _vocab;

    public RnnTGreedyDecoder(ModelPaths paths, Vocabulary vocab)
    {
        var opts = new SessionOptions
        {
            ExecutionMode = ExecutionMode.ORT_PARALLEL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            InterOpNumThreads = 1,
            IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2)
        };
        _encoder = new InferenceSession(paths.Encoder, opts);
        _decoder = new InferenceSession(paths.Decoder, opts);
        _joint   = new InferenceSession(paths.Joint,   opts);
        _vocab   = vocab;
    }

    public AsrResult Decode(float[,] mel, TimeSpan audioDuration)
    {
        var sw = Stopwatch.StartNew();
        var tokens = new List<int>(64);

        // Encoder: input [1, n_mels=64, T] → encoder output [1, d_model=768, T/4].
        var (encOut, tFrames) = RunEncoder(mel);

        // Greedy loop over encoder timesteps.
        var (decH, decC) = InitDecoderState();
        var lastToken = _vocab.BlankIndex;

        for (var t = 0; t < tFrames; t++)
        {
            var encT = SliceTime(encOut, t);
            var emittedAtT = 0;
            while (emittedAtT < MaxSymbolsPerStep)
            {
                var (predOut, ns1, ns2) = RunDecoder(lastToken, decH, decC);
                var logits = RunJoint(encT, predOut);
                var token = ArgMax(logits);
                if (token == _vocab.BlankIndex) break;
                tokens.Add(token);
                lastToken = token;
                decH = ns1; decC = ns2;
                emittedAtT++;
            }
        }

        var text = _vocab.Decode(tokens);
        return new AsrResult(text, audioDuration, sw.Elapsed);
    }

    // --- ONNX plumbing -----------------------------------------------------

    private (float[,,] encoded, int encodedLen) RunEncoder(float[,] mel)
    {
        var T = mel.GetLength(0);
        var M = mel.GetLength(1);
        var inputData = new float[1 * M * T];
        for (var t = 0; t < T; t++)
            for (var m = 0; m < M; m++)
                inputData[m * T + t] = mel[t, m]; // row-major: [1, M, T]

        var input = new DenseTensor<float>(inputData, new[] { 1, M, T });
        var lengths = new DenseTensor<long>(new long[] { T }, new[] { 1 });

        var inputNames = _encoder.InputMetadata.Keys.ToArray();
        var inputs = new[]
        {
            NamedOnnxValue.CreateFromTensor(inputNames[0], input),
            NamedOnnxValue.CreateFromTensor(inputNames[1], lengths)
        };
        using var results = _encoder.Run(inputs);
        var outArr = results.ToArray();
        var firstOut = outArr[0].AsTensor<float>(); // [1, 768, T/4]
        var dims = firstOut.Dimensions.ToArray();
        var arr = firstOut.ToArray();
        var outT = dims[2];
        var outD = dims[1];
        var output = new float[1, outD, outT];
        for (var d = 0; d < outD; d++)
            for (var t = 0; t < outT; t++)
                output[0, d, t] = arr[d * outT + t];
        var encodedLen = outArr[1].AsTensor<int>()[0]; // actual valid frames
        return (output, Math.Min(encodedLen, outT));
    }

    private static float[] SliceTime(float[,,] enc, int t)
    {
        var d = enc.GetLength(1);
        var slice = new float[d];
        for (var i = 0; i < d; i++) slice[i] = enc[0, i, t];
        return slice;
    }

    private static (DenseTensor<float>, DenseTensor<float>) InitDecoderState()
    {
        var z = new float[1 * 1 * PredHidden];
        return (new DenseTensor<float>(z,           new[] { 1, 1, PredHidden }),
                new DenseTensor<float>(z.ToArray(), new[] { 1, 1, PredHidden }));
    }

    private (float[] predHidden, DenseTensor<float>, DenseTensor<float>) RunDecoder(
        int lastToken, DenseTensor<float> h, DenseTensor<float> c)
    {
        // Decoder inputs: x [1,1], h.1 [1,1,320], c.1 [1,1,320]
        var input = new DenseTensor<long>(new long[] { lastToken }, new[] { 1, 1 });
        var inputNames = _decoder.InputMetadata.Keys.ToArray();
        var inputs = new[]
        {
            NamedOnnxValue.CreateFromTensor(inputNames[0], input),
            NamedOnnxValue.CreateFromTensor(inputNames[1], h),
            NamedOnnxValue.CreateFromTensor(inputNames[2], c)
        };
        using var results = _decoder.Run(inputs);
        var arr = results.ToArray();
        // dec output: [1,1,320] — reshape to [1,320,1] for joint network
        var rawDec = arr[0].AsTensor<float>().ToArray();
        var pred = new float[PredHidden];
        Array.Copy(rawDec, pred, PredHidden);
        var newH = new DenseTensor<float>(arr[1].AsTensor<float>().ToArray(), new[] { 1, 1, PredHidden });
        var newC = new DenseTensor<float>(arr[2].AsTensor<float>().ToArray(), new[] { 1, 1, PredHidden });
        return (pred, newH, newC);
    }

    private float[] RunJoint(float[] encStep, float[] predHidden)
    {
        // enc: [1,768,1], dec: [1,320,1]
        var enc = new DenseTensor<float>(encStep,     new[] { 1, encStep.Length, 1 });
        var pred = new DenseTensor<float>(predHidden, new[] { 1, PredHidden, 1 });
        var inputNames = _joint.InputMetadata.Keys.ToArray();
        var inputs = new[]
        {
            NamedOnnxValue.CreateFromTensor(inputNames[0], enc),
            NamedOnnxValue.CreateFromTensor(inputNames[1], pred)
        };
        using var results = _joint.Run(inputs);
        return results.First().AsTensor<float>().ToArray();
    }

    private static int ArgMax(ReadOnlySpan<float> v)
    {
        var idx = 0; var best = v[0];
        for (var i = 1; i < v.Length; i++) if (v[i] > best) { best = v[i]; idx = i; }
        return idx;
    }

    public void Dispose()
    {
        _encoder.Dispose();
        _decoder.Dispose();
        _joint.Dispose();
    }
}
