namespace MyASR.Models;

/// <param name="FileIndex">Zero-based index of the file being fetched.</param>
/// <param name="FileCount">How many files this model needs in total.</param>
public sealed record DownloadProgress(
    string FileName,
    long Downloaded,
    long Total,
    double BytesPerSecond,
    int FileIndex,
    int FileCount);

public interface IModelProvider
{
    bool AreModelsAvailable(ModelPaths paths);

    Task EnsureDownloadedAsync(
        ModelPaths paths,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct);
}
