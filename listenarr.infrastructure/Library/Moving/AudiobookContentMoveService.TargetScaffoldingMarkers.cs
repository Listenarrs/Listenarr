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
        if (!Directory.Exists(directory))
        {
            return null;
        }

        try
        {
            using var directoryAnchor =
                PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(directory);
            if (!directoryAnchor.VisiblePathMatches())
            {
                throw new MoveNeedsAttentionException(
                    "The target scaffold directory changed while its ownership marker was being inspected.");
            }

            PinnedDirectoryCreation.PinnedFileEntry markerEntry;
            try
            {
                markerEntry = directoryAnchor.OpenExistingFileForStableRead(
                    ScaffoldOwnerFileName);
            }
            catch (System.ComponentModel.Win32Exception exception) when (
                exception.NativeErrorCode is 2 or 3)
            {
                return null;
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }

            using (markerEntry)
            {
                if (!markerEntry.VisiblePathMatches())
                {
                    throw new MoveNeedsAttentionException(
                        "The target scaffold ownership marker changed while it was being inspected.");
                }

                return ReadScaffoldMarker(markerEntry);
            }
        }
        catch (MoveNeedsAttentionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            throw new MoveNeedsAttentionException(
                $"The target scaffold ownership marker is unreadable: {exception.Message}");
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
