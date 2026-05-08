using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;

namespace MyASR.Models;

public sealed class HuggingFaceModelProvider(HttpClient http, IReadOnlyList<ModelFileEntry> manifest)
    : IModelProvider
{
    public bool AreModelsAvailable(ModelPaths paths)
    {
        foreach (var f in manifest)
            if (!File.Exists(Path.Combine(paths.Root, f.FileName))) return false;
        return true;
    }

    public async Task EnsureDownloadedAsync(ModelPaths paths, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(paths.Root);
        foreach (var f in manifest)
        {
            var dst = Path.Combine(paths.Root, f.FileName);
            if (File.Exists(dst)) continue;
            await DownloadOneAsync(f, dst, progress, ct);
        }
    }

    private async Task DownloadOneAsync(ModelFileEntry f, string dst, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var partial = dst + ".partial";
        long resumeFrom = File.Exists(partial) ? new FileInfo(partial).Length : 0;

        using var req = new HttpRequestMessage(HttpMethod.Get, f.Url);
        if (resumeFrom > 0)
            req.Headers.Range = new RangeHeaderValue(resumeFrom, null);

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        var total = (resp.Content.Headers.ContentLength ?? f.Size) + resumeFrom;
        await using var input  = await resp.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(partial, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        output.Position = resumeFrom;

        var sw = Stopwatch.StartNew();
        var buffer = new byte[81920];
        long downloaded = resumeFrom;
        long lastReportedAt = downloaded;
        var lastReportTime = sw.Elapsed;

        int n;
        while ((n = await input.ReadAsync(buffer, ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, n), ct);
            downloaded += n;

            var now = sw.Elapsed;
            if (progress != null && (now - lastReportTime).TotalMilliseconds >= 250)
            {
                var bps = (downloaded - lastReportedAt) / (now - lastReportTime).TotalSeconds;
                progress.Report(new DownloadProgress(f.FileName, downloaded, total, bps));
                lastReportedAt = downloaded;
                lastReportTime = now;
            }
        }
        await output.FlushAsync(ct);
        output.Close();

        if (f.Sha256 is not null && !VerifySha(partial, f.Sha256))
        {
            File.Delete(partial);
            throw new InvalidDataException($"SHA-256 mismatch for {f.FileName}");
        }
        File.Move(partial, dst, overwrite: true);
        progress?.Report(new DownloadProgress(f.FileName, total, total, 0));
    }

    private static bool VerifySha(string path, string expectedHex)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var fs  = File.OpenRead(path);
        var actual = Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
        return actual.Equals(expectedHex.ToLowerInvariant(), StringComparison.Ordinal);
    }
}
