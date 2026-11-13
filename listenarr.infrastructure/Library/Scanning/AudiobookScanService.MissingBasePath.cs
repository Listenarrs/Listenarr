using System.ComponentModel;
using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Scanning;

internal sealed partial class AudiobookScanService
{
    public async Task<AudiobookScanResult?> ReconcileMissingBasePathAsync(
        int audiobookId,
        string source,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var audiobook = await audiobookRepository.GetForScanAsync(
            audiobookId,
            cancellationToken)
            ?? throw new InvalidOperationException(
                $"Audiobook {audiobookId} no longer exists.");
        if (string.IsNullOrWhiteSpace(audiobook.BasePath)
            || !FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                audiobook.BasePath,
                out var basePath,
                out _))
        {
            return null;
        }

        var parent = FindNearestExistingParent(basePath);
        if (parent == null)
        {
            return null;
        }

        // The nearest existing parent must itself be an authorized scan path:
        // inside a configured root, with no unavailable or ambiguous root that
        // could also contain it, and reachable without links.
        var authorization = await pathAuthorizationService.AuthorizeAsync(
            parent,
            cancellationToken);
        if (!authorization.IsAuthorized
            || !authorization.Identity.HasValue)
        {
            return null;
        }

        var command = new AudiobookScanCommand(
            audiobook.Id,
            parent,
            authorization.Identity.Value,
            AllowReconciliation: true,
            IsAuthoritativeScope: true,
            Source: source,
            CorrelationId: $"scan:{audiobook.Id}:{Guid.NewGuid():N}");
        var semantics = await ValidateCommandAsync(command, cancellationToken);

        // An empty root folder usually means an unmounted or replaced library
        // volume, where every BasePath looks missing. Never treat that as proof.
        if (!fileSystem.EnumerateFileSystemEntries(command.ScanIdentity.BoundaryPath).Any())
        {
            logger.LogWarning(
                "Not reconciling the missing BasePath of audiobook {AudiobookId}: its root folder is empty",
                audiobook.Id);
            return null;
        }

        using var pinnedAuthority = OpenPinnedScanAuthority(command);
        if (!IsPinnedChildAbsent(pinnedAuthority, parent, basePath)
            || HasCaseInsensitiveAlias(parent, basePath, semantics))
        {
            return null;
        }

        var existingFiles = await fileRepository.GetByAudiobookIdAsync(
            audiobook.Id,
            cancellationToken);
        var diagnostics = new List<AudiobookScanDiagnostic>
        {
            new(
                "BasePathMissing",
                basePath,
                "The BasePath no longer exists beneath its configured root; only tracked rows inside it were reconciled.")
        };
        var resolvedPaths = ResolveExistingPaths(
            audiobook,
            existingFiles,
            semantics,
            diagnostics);
        var filesInsideBasePath = new List<AudiobookFile>();
        foreach (var file in existingFiles)
        {
            if (!resolvedPaths.TryGetValue(file.Id, out var resolvedPath))
            {
                continue;
            }

            if (FileSystemPathIdentity.IsSameOrInside(resolvedPath, basePath, semantics))
            {
                filesInsideBasePath.Add(file);
                continue;
            }

            diagnostics.Add(new AudiobookScanDiagnostic(
                "TrackedFileOutsideScope",
                resolvedPath,
                "The tracked file lies outside the missing BasePath and was preserved."));
        }

        // Nothing is enumerated: the only pinned directory is the existing
        // parent, so every removal below re-proves through it that the file is
        // still absent.
        var discovery = new ScanDiscoveryResult(
            [],
            [],
            new Dictionary<string, string>(semantics.Comparer),
            [],
            new Dictionary<string, string>(semantics.Comparer)
            {
                [FileSystemPathIdentity.Canonicalize(parent, semantics.Syntax)] =
                    ScanFileDiscovery.PinnedPathOnlyIdentity
            },
            new Dictionary<string, string>(semantics.Comparer),
            null,
            false,
            []);
        var removedFiles = await ReconcileMissingFilesAsync(
            command,
            pinnedAuthority,
            audiobook,
            filesInsideBasePath,
            resolvedPaths,
            discovery,
            semantics,
            diagnostics,
            cancellationToken);

        var refreshed = await audiobookRepository.GetForScanSnapshotAsync(
            audiobook.Id,
            cancellationToken)
            ?? throw new InvalidOperationException(
                $"Audiobook {audiobook.Id} disappeared before scan completion.");
        return new AudiobookScanResult(
            refreshed,
            [],
            0,
            removedFiles,
            refreshed.BasePath,
            IsComplete: true,
            ReconciliationPerformed: true,
            diagnostics);
    }

    private string? FindNearestExistingParent(string path)
    {
        var current = Path.GetDirectoryName(path);
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (fileSystem.DirectoryExists(current))
            {
                return current;
            }

            current = Path.GetDirectoryName(current);
        }

        return null;
    }

    private static string GetFirstMissingSegment(string parent, string path) =>
        Path.GetRelativePath(parent, path)
            .Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries)
            .First();

    // A root in case-insensitive mode can sit on a case-sensitive filesystem. A
    // re-cased folder is then still the BasePath under the root's semantics and
    // its files may still exist, so an exact-name miss is not proof of absence.
    private bool HasCaseInsensitiveAlias(
        string parent,
        string path,
        FileSystemPathSemantics semantics)
    {
        if (semantics.CaseSensitivity != FileSystemCaseSensitivity.Insensitive)
        {
            return false;
        }

        var childName = GetFirstMissingSegment(parent, path);
        return fileSystem.EnumerateFileSystemEntries(parent)
            .Any(entry => semantics.Comparer.Equals(Path.GetFileName(entry), childName));
    }

    private static bool IsPinnedChildAbsent(
        PinnedScanAuthority authority,
        string parent,
        string path)
    {
        var childName = GetFirstMissingSegment(parent, path);
        try
        {
            authority.Root.OpenExistingChild(childName).Dispose();
            return false;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode is 2 or 3)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception exception) when (exception is
            Win32Exception or IOException or UnauthorizedAccessException
                or InvalidOperationException or ArgumentException)
        {
            // A link, a file, or an unreadable entry in place of the BasePath is
            // not proof that the book folder is gone.
            return false;
        }
    }
}
