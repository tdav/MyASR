using System.IO;
using System.Text.Json;
using MyASR.Features;

namespace MyASR.Tests.Features;

public class MelFilterBankTests
{
    private record FbDto(int[] Shape, double[] Data);

    private static double[,] LoadReference()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "reference_filterbank_64x161.json");
        var dto = JsonSerializer.Deserialize<FbDto>(File.ReadAllText(path), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        })!;
        var rows = dto.Shape[0]; var cols = dto.Shape[1];
        var fb = new double[rows, cols];
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < cols; c++)
                fb[r, c] = dto.Data[r * cols + c];
        return fb;
    }

    [Test]
    public async Task Build_HasShape64x161()
    {
        var bank = MelFilterBank.BuildHtk(sampleRate: 16000, nFft: 320, nMels: 64);
        await Assert.That(bank.GetLength(0)).IsEqualTo(64);
        await Assert.That(bank.GetLength(1)).IsEqualTo(161);
    }

    [Test]
    public async Task Build_MatchesReferenceWithinTolerance()
    {
        var ours = MelFilterBank.BuildHtk(sampleRate: 16000, nFft: 320, nMels: 64);
        var theirs = LoadReference();
        var maxDiff = 0.0;
        for (var r = 0; r < 64; r++)
            for (var c = 0; c < 161; c++)
                maxDiff = Math.Max(maxDiff, Math.Abs(ours[r, c] - theirs[r, c]));
        await Assert.That(maxDiff).IsLessThan(1e-6);
    }
}
