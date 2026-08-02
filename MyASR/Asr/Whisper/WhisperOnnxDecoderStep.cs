using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace MyASR.Asr.Whisper;

/// <summary>
/// Drives <c>decoder_model_merged.onnx</c>. The merged graph carries both a no-cache and a
/// with-cache branch selected by <c>use_cache_branch</c>; the no-cache pass still requires the
/// past inputs to be present, so it gets zero-length tensors.
/// </summary>
/// <remarks>
/// The <see cref="InferenceSession"/> is owned by the caller (<see cref="WhisperOnnxEngine"/>)
/// and outlives any single instance of this type — an <c>InferenceSession</c> carries no
/// per-call state between <c>Run</c> invocations, so sharing it across utterances is safe. The
/// only state that must not leak between utterances is the KV <see cref="cache"/> below, which
/// is why callers must construct a fresh <see cref="WhisperOnnxDecoderStep"/> per utterance.
/// </remarks>
public sealed class WhisperOnnxDecoderStep : IWhisperDecoderStep
{
    private const string InputIds = "input_ids";
    private const string EncoderHiddenStates = "encoder_hidden_states";
    private const string UseCacheBranch = "use_cache_branch";
    private const string PastPrefix = "past_key_values.";
    private const string PresentPrefix = "present.";

    private readonly InferenceSession session;
    private readonly DenseTensor<float> encoderHidden;
    private readonly string[] pastNames;
    private readonly int numHeads;
    private readonly int headDim;

    private readonly Dictionary<string, DenseTensor<float>> cache;

    public WhisperOnnxDecoderStep(InferenceSession session, float[] encoderHidden, int encoderFrames, int dModel)
    {
        this.session = session;
        this.encoderHidden = new DenseTensor<float>(encoderHidden, [1, encoderFrames, dModel]);

        pastNames = session.InputMetadata.Keys
            .Where(k => k.StartsWith(PastPrefix, StringComparison.Ordinal))
            .ToArray();

        // past_key_values.N.decoder.key has shape [batch, heads, seq, head_dim]; heads is fixed
        // in the graph, head_dim follows from d_model. For Whisper the encoder's output width
        // equals the decoder's d_model, which is why one value serves both — do not "simplify"
        // this by reading the head dim from somewhere else.
        var sample = session.InputMetadata[pastNames[0]].Dimensions;
        numHeads = sample[1] > 0 ? sample[1] : 12;
        headDim = dModel / numHeads;

        cache = new Dictionary<string, DenseTensor<float>>(pastNames.Length);
    }

    public float[] Step(ReadOnlySpan<int> inputIds, bool firstStep)
    {
        var ids = new long[inputIds.Length];
        for (var i = 0; i < inputIds.Length; i++) ids[i] = inputIds[i];

        var inputs = new List<NamedOnnxValue>(pastNames.Length + 3)
        {
            NamedOnnxValue.CreateFromTensor(InputIds, new DenseTensor<long>(ids, [1, ids.Length])),
            NamedOnnxValue.CreateFromTensor(EncoderHiddenStates, encoderHidden),
        };

        if (session.InputMetadata.ContainsKey(UseCacheBranch))
            inputs.Add(NamedOnnxValue.CreateFromTensor(
                UseCacheBranch, new DenseTensor<bool>(new[] { !firstStep }, [1])));

        foreach (var name in pastNames)
            inputs.Add(NamedOnnxValue.CreateFromTensor(name, PastFor(name, firstStep)));

        using var results = session.Run(inputs);

        float[]? logits = null;

        foreach (var r in results)
        {
            if (r.Name == "logits")
            {
                var tensor = r.AsTensor<float>();
                var dims = tensor.Dimensions;
                var vocab = dims[^1];
                var seq = dims[^2];
                var all = tensor.ToArray();
                logits = all[((seq - 1) * vocab)..(seq * vocab)]; // last position only
            }
            else if (r.Name.StartsWith(PresentPrefix, StringComparison.Ordinal))
            {
                var pastName = PastPrefix + r.Name[PresentPrefix.Length..];

                // Cross-attention K/V are computed from encoder_hidden_states on the no-cache
                // branch only. On later steps the graph merely passes the past inputs through,
                // and several optimum exports emit those present.*.encoder.* outputs as
                // zero-length. Overwriting the encoder cache with an empty tensor makes the
                // decode degrade into nonsense with no exception, so capture it once and keep it.
                var isEncoderSide = pastName.Contains(".encoder.", StringComparison.Ordinal);
                if (isEncoderSide && !firstStep) continue;

                var tensor = r.AsTensor<float>();
                cache[pastName] = new DenseTensor<float>(tensor.ToArray(), tensor.Dimensions.ToArray());
            }
        }

        return logits ?? throw new InvalidOperationException("Декодер не вернул logits.");
    }

    private DenseTensor<float> PastFor(string name, bool firstStep)
    {
        if (!firstStep)
        {
            if (cache.TryGetValue(name, out var cached)) return cached;

            // A miss here on a non-first step means the KV cache is missing an entry that the
            // with-cache branch needs — feeding a zero-length fallback would silently degrade
            // into fluent nonsense instead of failing, which is the trap this guards against.
            throw new InvalidOperationException(
                $"В кэше декодера отсутствует тензор \"{name}\" на шаге, отличном от первого.");
        }

        // The no-cache branch still requires every past input to be present, so it gets
        // zero-length tensors for both the decoder and the encoder side.
        return new DenseTensor<float>(Array.Empty<float>(), [1, numHeads, 0, headDim]);
    }
}
