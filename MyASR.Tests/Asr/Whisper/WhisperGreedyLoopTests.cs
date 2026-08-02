using MyASR.Asr.Whisper;

namespace MyASR.Tests.Asr.Whisper;

public class WhisperGreedyLoopTests
{
    /// <summary>Replays canned logits so the loop can be tested without any ONNX model.</summary>
    private sealed class ScriptedStep(params int[][] argmaxPerCall) : IWhisperDecoderStep
    {
        private const int Vocab = 64;
        private int call;

        public List<int[]> Received { get; } = [];
        public List<bool> FirstStepFlags { get; } = [];

        public float[] Step(ReadOnlySpan<int> inputIds, bool firstStep)
        {
            Received.Add(inputIds.ToArray());
            FirstStepFlags.Add(firstStep);

            var logits = new float[Vocab];
            var winners = argmaxPerCall[Math.Min(call, argmaxPerCall.Length - 1)];
            call++;
            // Give every "winner" a descending score so ties never decide the outcome.
            for (var i = 0; i < winners.Length; i++) logits[winners[i]] = 10f - i;
            return logits;
        }
    }

    private const int Eot = 50;
    private static readonly int[] Prompt = [1, 2, 3, 4];
    private static readonly HashSet<int> None = [];

    [Test]
    public async Task FirstCall_FeedsTheWholePrompt_WithFirstStepTrue()
    {
        var step = new ScriptedStep([Eot]);
        WhisperGreedyLoop.Run(step, Prompt, Eot, None, None, maxTokens: 10);

        await Assert.That(step.Received[0]).IsEquivalentTo(Prompt, CollectionOrdering.Matching);
        await Assert.That(step.FirstStepFlags[0]).IsTrue();
    }

    [Test]
    public async Task LaterCalls_FeedOneTokenWithFirstStepFalse()
    {
        var step = new ScriptedStep([7], [8], [Eot]);
        WhisperGreedyLoop.Run(step, Prompt, Eot, None, None, maxTokens: 10);

        await Assert.That(step.Received[1]).IsEquivalentTo(new[] { 7 }, CollectionOrdering.Matching);
        await Assert.That(step.Received[2]).IsEquivalentTo(new[] { 8 }, CollectionOrdering.Matching);
        await Assert.That(step.FirstStepFlags[1]).IsFalse();
        await Assert.That(step.FirstStepFlags[2]).IsFalse();
    }

    [Test]
    public async Task StopsAtEndOfText_AndDoesNotEmitIt()
    {
        var step = new ScriptedStep([7], [8], [Eot]);
        var result = WhisperGreedyLoop.Run(step, Prompt, Eot, None, None, maxTokens: 10);
        await Assert.That(result).IsEquivalentTo(new[] { 7, 8 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task StopsAtMaxTokens_WhenEndOfTextNeverArrives()
    {
        var step = new ScriptedStep([7]);
        var result = WhisperGreedyLoop.Run(step, Prompt, Eot, None, None, maxTokens: 5);
        await Assert.That(result.Count).IsEqualTo(5);
    }

    [Test]
    public async Task SuppressedTokens_AreNeverChosen()
    {
        // 7 would win every step; suppressing it must hand the win to 8.
        var step = new ScriptedStep([7, 8], [7, 8], [Eot]);
        var result = WhisperGreedyLoop.Run(step, Prompt, Eot, new HashSet<int> { 7 }, None, maxTokens: 10);
        await Assert.That(result).IsEquivalentTo(new[] { 8, 8 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task BeginSuppressedTokens_AreBlockedOnlyOnTheFirstGeneratedToken()
    {
        var step = new ScriptedStep([7, 8], [7, 8], [Eot]);
        var result = WhisperGreedyLoop.Run(step, Prompt, Eot, None, new HashSet<int> { 7 }, maxTokens: 10);
        await Assert.That(result).IsEquivalentTo(new[] { 8, 7 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task SuppressingEverythingExceptEot_TerminatesImmediately()
    {
        var step = new ScriptedStep([7, Eot]);
        var result = WhisperGreedyLoop.Run(step, Prompt, Eot, new HashSet<int> { 7 }, None, maxTokens: 10);
        await Assert.That(result).IsEmpty();
    }

    [Test]
    public async Task EmptyPrompt_Throws()
    {
        var step = new ScriptedStep([Eot]);
        await Assert.That(() => WhisperGreedyLoop.Run(step, [], Eot, None, None, 10))
            .Throws<ArgumentException>();
    }
}
