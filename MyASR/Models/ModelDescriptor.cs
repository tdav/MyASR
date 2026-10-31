namespace MyASR.Models;

/// <summary>Which recognition pipeline a model needs. Features and decoder must always match.</summary>
public enum AsrEngineKind
{
    RnnT,
    Whisper
}

/// <param name="LocalName">File name inside the model's own folder.</param>
/// <param name="SourceRelativePath">Appended to the resolved base URI to locate the source.</param>
/// <param name="Size">Approximate size in bytes; used for progress before the source reports one.</param>
/// <param name="Sha256">Lower-case hex digest, or null when no digest has been captured yet.</param>
public sealed record ModelFileEntry(
    string LocalName,
    string SourceRelativePath,
    long Size,
    string? Sha256);

public sealed record ModelDescriptor(
    string Id,
    string DisplayName,
    string? LicenceNote,
    AsrEngineKind Kind,
    string FolderName,
    string DefaultBaseUri,
    IReadOnlyList<ModelFileEntry> Files)
{
    public long TotalBytes => Files.Sum(f => f.Size);

    public override string ToString() => DisplayName;
}
