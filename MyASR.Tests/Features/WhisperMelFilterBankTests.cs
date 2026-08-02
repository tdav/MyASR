using System.Text.Json;
using MyASR.Features;

namespace MyASR.Tests.Features;

public class WhisperMelFilterBankTests
{
    private sealed record Reference(int[] shape, double[] data);

    private static Reference LoadReference()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "reference_filterbank_slaney_80x201.json");
        return JsonSerializer.Deserialize<Reference>(File.ReadAllText(path))!;
    }

    [Test]
    public async Task Shape_Is80By201()
    {
        var fb = WhisperMelFilterBank.BuildSlaney(16000, 400, 80);
        await Assert.That(fb.GetLength(0)).IsEqualTo(80);
        await Assert.That(fb.GetLength(1)).IsEqualTo(201);
    }

    [Test]
    public async Task Matches_TransformersSlaneyReference()
    {
        var reference = LoadReference();
        var fb = WhisperMelFilterBank.BuildSlaney(16000, 400, 80);

        await Assert.That(reference.shape[0]).IsEqualTo(80);
        await Assert.That(reference.shape[1]).IsEqualTo(201);

        var worst = 0.0;
        for (var m = 0; m < 80; m++)
            for (var k = 0; k < 201; k++)
                worst = Math.Max(worst, Math.Abs(fb[m, k] - reference.data[m * 201 + k]));

        await Assert.That(worst).IsLessThan(1e-9);
    }

    [Test]
    public async Task AllWeights_AreNonNegative()
    {
        var fb = WhisperMelFilterBank.BuildSlaney(16000, 400, 80);
        for (var m = 0; m < 80; m++)
            for (var k = 0; k < 201; k++)
                await Assert.That(fb[m, k]).IsGreaterThanOrEqualTo(0.0);
    }

    [Test]
    public async Task DiffersFromHtk_SoTheWrongOneCannotBeUsedSilently()
    {
        var slaney = WhisperMelFilterBank.BuildSlaney(16000, 400, 80);
        var htk = MelFilterBank.BuildHtk(16000, 400, 80);

        var identical = true;
        for (var m = 0; m < 80 && identical; m++)
            for (var k = 0; k < 201; k++)
                if (Math.Abs(slaney[m, k] - htk[m, k]) > 1e-12) { identical = false; break; }

        await Assert.That(identical).IsFalse();
    }
}
