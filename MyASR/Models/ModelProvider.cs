using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;

namespace MyASR.Models;

/// <summary>
/// Acquires a model's files from either an HTTP(S) base URI or a local folder / UNC path.
/// Both paths share the same staging, progress and digest semantics so they cannot drift apart.
/// </summary>
public sealed class ModelProvider(HttpClient http, IModelSourceResolver sources) : IModelProvider
{
    private const int BufferSize = 81920;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    public bool AreModelsAvailable(ModelPaths paths)
    {
        foreach (var f in paths.Model.Files)
            if (!File.Exists(paths.File(f.LocalName))) return false;
        return true;
    }

    public async Task EnsureDownloadedAsync(
        ModelPaths paths, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(paths.Root);
        var baseUri = sources.ResolveBaseUri(paths.Model);
        var files = paths.Model.Files;

        for (var i = 0; i < files.Count; i++)
        {
            var f = files[i];
            var dst = paths.File(f.LocalName);
            if (File.Exists(dst)) continue;

            if (IsHttp(baseUri))
                await FetchOverHttpAsync(baseUri, f, dst, i, files.Count, progress, ct);
            else
                await CopyFromFolderAsync(baseUri, f, dst, i, files.Count, progress, ct);
        }
    }

    internal static bool IsHttp(string baseUri) =>
        Uri.TryCreate(baseUri, UriKind.Absolute, out var u)
        && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    internal static string CombineHttp(string baseUri, string relative) =>
        new Uri(new Uri(baseUri.EndsWith('/') ? baseUri : baseUri + "/"), relative).ToString();

    internal static string CombineLocal(string baseDir, string relative) =>
        Path.Combine(baseDir, relative.Replace('/', Path.DirectorySeparatorChar));

    private async Task FetchOverHttpAsync(
        string baseUri, ModelFileEntry f, string dst, int index, int count,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var partial = dst + ".partial";
        long resumeFrom = File.Exists(partial) ? new FileInfo(partial).Length : 0;

        using var req = new HttpRequestMessage(HttpMethod.Get, CombineHttp(baseUri, f.SourceRelativePath));
        if (resumeFrom > 0) req.Headers.Range = new RangeHeaderValue(resumeFrom, null);

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        var total = (resp.Content.Headers.ContentLength ?? f.Size) + resumeFrom;
        await using var input = await resp.Content.ReadAsStreamAsync(ct);
        await PumpAsync(input, partial, dst, f, total, resumeFrom, index, count, progress, ct);
    }

    private static async Task CopyFromFolderAsync(
        string baseDir, ModelFileEntry f, string dst, int index, int count,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var source = CombineLocal(baseDir, f.SourceRelativePath);
        if (!File.Exists(source))
            throw new FileNotFoundException($"Файл модели не найден в источнике: {source}", source);

        var partial = dst + ".partial";
        if (File.Exists(partial)) File.Delete(partial); // a local copy is cheap; always start clean

        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        await PumpAsync(input, partial, dst, f, input.Length, 0, index, count, progress, ct);
    }

    private static async Task PumpAsync(
        Stream input, string partial, string dst, ModelFileEntry f,
        long total, long resumeFrom, int index, int count,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        await using (var output = new FileStream(partial, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
        {
            output.Position = resumeFrom;

            var sw = Stopwatch.StartNew();
            var buffer = new byte[BufferSize];
            var downloaded = resumeFrom;
            var lastReportedAt = downloaded;
            var lastReportTime = sw.Elapsed;

            int n;
            while ((n = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, n), ct);
                downloaded += n;

                var now = sw.Elapsed;
                if (progress is not null && now - lastReportTime >= ProgressInterval)
                {
                    var bps = (downloaded - lastReportedAt) / (now - lastReportTime).TotalSeconds;
                    progress.Report(new DownloadProgress(f.LocalName, downloaded, total, bps, index, count));
                    lastReportedAt = downloaded;
                    lastReportTime = now;
                }
            }

            await output.FlushAsync(ct);
        }

        if (f.Sha256 is not null && !VerifySha(partial, f.Sha256))
        {
            File.Delete(partial);
            throw new InvalidDataException($"SHA-256 mismatch for {f.LocalName}");
        }

        File.Move(partial, dst, overwrite: true);
        progress?.Report(new DownloadProgress(f.LocalName, total, total, 0, index, count));
    }

    private static bool VerifySha(string path, string expectedHex)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var fs = File.OpenRead(path);
        var actual = Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
        return actual.Equals(expectedHex.ToLowerInvariant(), StringComparison.Ordinal);
    }
}
