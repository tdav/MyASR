namespace MyASR.Models;

public sealed record DownloadProgress(string FileName, long Downloaded, long Total, double BytesPerSecond);

public interface IModelProvider
{
    bool AreModelsAvailable(ModelPaths paths);
    Task EnsureDownloadedAsync(
        ModelPaths paths,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct);
}
