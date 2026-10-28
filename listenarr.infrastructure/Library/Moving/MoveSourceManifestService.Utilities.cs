using System.Security.Cryptography;
using Listenarr.Application.Common.Exceptions;
using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class MoveSourceManifestService
{
    private static IReadOnlyList<MoveSourceManifestEntry> MergeEntries(
        IReadOnlyList<MoveSourceManifestEntry> trackedEntries,
        IReadOnlyList<MoveSourceManifestEntry> companionEntries,
        FileSystemPathSemantics semantics)
    {
        var merged = new Dictionary<string, MoveSourceManifestEntry>(
            semantics.Comparer);
        foreach (var entry in trackedEntries.Concat(companionEntries))
        {
            if (merged.TryGetValue(entry.RelativePath, out var existing))
            {
                if (existing.EntryType != entry.EntryType)
                {
                    throw Conflict(
                        $"Move manifest path changed type while companion files were being captured: {entry.RelativePath}");
                }

                continue;
            }

            merged.Add(entry.RelativePath, entry);
        }

        return merged.Values
            .OrderBy(entry => entry.EntryType == MoveJobEntryType.Directory ? 0 : 1)
            .ThenBy(entry => entry.RelativePath, semantics.Comparer)
            .ToList();
    }

    private static bool HasSameFilesystemAuthority(
        PathIdentitySnapshot left,
        PathIdentitySnapshot right) =>
        left.Syntax == right.Syntax
        && left.CaseSensitivity == right.CaseSensitivity
        && left.RequestedMode == right.RequestedMode
        && FileSystemPathIdentity.AreEquivalent(
            left.BoundaryPath,
            right.BoundaryPath,
            left.Semantics);

    private static bool IsFilesystemRoot(
        string path,
        FileSystemPathSemantics semantics)
    {
        var root = Path.GetPathRoot(path);
        return !string.IsNullOrWhiteSpace(root)
            && FileSystemPathIdentity.AreEquivalent(path, root, semantics);
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private static ApplicationConflictException Conflict(string message) =>
        new("move_source_unverified", message);

    private static ApplicationUnavailableException Unavailable(
        string message,
        Exception? innerException = null) =>
        new("move_source_temporarily_unavailable", message, innerException);

    private sealed record ValidatedTrackedFile(
        int AudiobookFileId,
        string Path,
        long Length,
        DateTime LastWriteTimeUtc,
        string? Sha256);
}
