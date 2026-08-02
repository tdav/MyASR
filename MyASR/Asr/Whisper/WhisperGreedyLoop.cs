namespace MyASR.Asr.Whisper;

public static class WhisperGreedyLoop
{
    public static List<int> Run(
        IWhisperDecoderStep step,
        int[] prompt,
        int eotId,
        IReadOnlySet<int> suppress,
        IReadOnlySet<int> beginSuppress,
        int maxTokens)
    {
        if (prompt.Length == 0)
            throw new ArgumentException("Промпт декодера не может быть пустым.", nameof(prompt));

        var generated = new List<int>(Math.Min(maxTokens, 64));

        var logits = step.Step(prompt, firstStep: true);

        for (var i = 0; i < maxTokens; i++)
        {
            // begin_suppress_tokens applies only to the first generated position; without it the
            // model happily opens with a token it was told never to start on.
            var next = ArgMax(logits, suppress, i == 0 ? beginSuppress : null);
            if (next == eotId) break;

            generated.Add(next);
            if (generated.Count >= maxTokens) break;

            Span<int> one = [next];
            logits = step.Step(one, firstStep: false);
        }

        return generated;
    }

    private static int ArgMax(float[] logits, IReadOnlySet<int> suppress, IReadOnlySet<int>? alsoSuppress)
    {
        var best = -1;
        var bestValue = float.NegativeInfinity;

        for (var i = 0; i < logits.Length; i++)
        {
            if (suppress.Contains(i)) continue;
            if (alsoSuppress is not null && alsoSuppress.Contains(i)) continue;
            if (logits[i] <= bestValue) continue;
            bestValue = logits[i];
            best = i;
        }

        if (best < 0)
            throw new InvalidOperationException("Все токены подавлены — декодировать нечего.");

        return best;
    }
}
