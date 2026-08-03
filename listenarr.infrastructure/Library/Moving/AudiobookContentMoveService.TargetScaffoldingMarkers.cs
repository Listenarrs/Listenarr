using System.Text.Json;
using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private static string ResolveScaffoldPath(
        string actualRoot,
        string publishedRoot,
        string finalPath,
        FileSystemPathSemantics semantics)
    {
        if (!FileSystemPathIdentity.TryGetRelativePathWithinBase(
                publishedRoot,
                finalPath,
                semantics,
                out var relativePath)
            || !FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                actualRoot,
                relativePath,
                semantics,
                out var resolved))
        {
            throw new MoveNeedsAttentionException(
                "A target scaffold path escaped its publication root.");
        }

        return resolved;
    }

    private static string GetTemporaryScaffoldRoot(string parent, Guid jobId) =>
        Path.Join(parent, $".listenarr-scaffold-{jobId:N}");

    private static void WriteScaffoldMarker(
        string directory,
        ScaffoldOwnershipMarker marker)
    {
        var markerPath = Path.Join(directory, ScaffoldOwnerFileName);
        using var stream = new FileStream(
            markerPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.WriteThrough);
        JsonSerializer.Serialize(stream, marker);
        stream.Flush(flushToDisk: true);
    }

    private static ScaffoldOwnershipMarker? ReadScaffoldMarker(string directory)
    {
        var markerPath = Path.Join(directory, ScaffoldOwnerFileName);
        if (!File.Exists(markerPath))
        {
            return null;
        }

        if ((File.GetAttributes(markerPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new MoveNeedsAttentionException(
                "The target scaffold ownership marker is linked.");
        }

        var length = new FileInfo(markerPath).Length;
        if (length <= 0 || length > MaximumScaffoldMarkerBytes)
        {
            throw new MoveNeedsAttentionException(
                "The target scaffold ownership marker has an invalid size.");
        }

        try
        {
            using var stream = new FileStream(
                markerPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.SequentialScan);
            return JsonSerializer.Deserialize<ScaffoldOwnershipMarker>(stream);
        }
        catch (JsonException exception)
        {
            throw new MoveNeedsAttentionException(
                $"The target scaffold ownership marker is invalid: {exception.Message}");
        }
    }

    private static ScaffoldOwnershipMarker ReadScaffoldMarker(
        PinnedDirectoryCreation.PinnedFileEntry markerEntry)
    {
        try
        {
            using var stream = markerEntry.OpenReadStream(
                bufferSize: 4096,
                asynchronous: false);
            if (stream.Length <= 0 || stream.Length > MaximumScaffoldMarkerBytes)
            {
                throw new MoveNeedsAttentionException(
                    "The target scaffold ownership marker has an invalid size.");
            }

            stream.Position = 0;
            return JsonSerializer.Deserialize<ScaffoldOwnershipMarker>(stream)
                ?? throw new MoveNeedsAttentionException(
                    "The target scaffold ownership marker is invalid.");
        }
        catch (JsonException exception)
        {
            throw new MoveNeedsAttentionException(
                $"The target scaffold ownership marker is invalid: {exception.Message}");
        }
    }

    private static void ValidateScaffoldMarker(
        ScaffoldOwnershipMarker? marker,
        Guid jobId,
        string target,
        string publishedRoot,
        FileSystemPathSemantics semantics)
    {
        if (marker == null
            || marker.Version != ScaffoldMarkerVersion
            || marker.JobId != jobId
            || !FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                marker.TargetPath,
                out var markerTargetPath,
                out _,
                semantics.Syntax)
            || !FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                marker.PublishedRoot,
                out var markerPublishedRoot,
                out _,
                semantics.Syntax)
            || !FileSystemPathIdentity.AreEquivalent(markerTargetPath, target, semantics)
            || !FileSystemPathIdentity.AreEquivalent(markerPublishedRoot, publishedRoot, semantics))
        {
            throw new MoveNeedsAttentionException(
                "The target scaffold ownership marker does not match this move job.");
        }
    }

    private static int GetPathDepth(string path) =>
        Path.GetFullPath(path)
            .Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries)
            .Length;

    private sealed record ScaffoldOwnershipMarker(
        int Version,
        Guid JobId,
        string TargetPath,
        string PublishedRoot);
}
