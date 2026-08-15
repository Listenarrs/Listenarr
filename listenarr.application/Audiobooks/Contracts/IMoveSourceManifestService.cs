using Listenarr.Domain.Common;

namespace Listenarr.Application.Audiobooks.Contracts;

public sealed record MoveSourceManifestEntry(
    string RelativePath,
    MoveJobEntryType EntryType,
    long Length,
    DateTime LastWriteTimeUtc,
    string? Sha256);

public sealed record MoveSourceManifest(
    string SourceRoot,
    PathIdentitySnapshot SourceIdentity,
    IReadOnlyList<MoveSourceManifestEntry> Entries,
    IReadOnlyList<int> AudiobookFileIds);

public interface IMoveSourceManifestService
{
    Task<MoveSourceManifest> BuildAsync(
        Audiobook audiobook,
        CancellationToken cancellationToken = default);
}

public interface IMoveSourcePlanService
{
    Task<MoveSourceManifest> BuildPlanAsync(
        AudiobookPathReferenceSnapshot audiobook,
        CancellationToken cancellationToken = default);
}
