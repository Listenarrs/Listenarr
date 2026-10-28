using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Scanning;

internal sealed partial class AudiobookScanService
{
    private Dictionary<int, string> ResolveExistingPaths(
        Audiobook audiobook,
        IEnumerable<AudiobookFile> files,
        FileSystemPathSemantics semantics,
        ICollection<AudiobookScanDiagnostic> diagnostics)
    {
        var resolved = new Dictionary<int, string>();
        foreach (var file in files)
        {
            if (TryResolveStoredFilePath(
                audiobook,
                file,
                semantics,
                out var path,
                out var reason))
            {
                resolved[file.Id] = path;
                continue;
            }

            diagnostics.Add(new AudiobookScanDiagnostic(
                "StoredPathUnresolved",
                file.Path,
                reason ?? "The stored audiobook file path could not be resolved."));
        }

        return resolved;
    }

    private async Task<Dictionary<string, int>> BuildOwnershipMapAsync(
        FileSystemPathSemantics semantics,
        ICollection<AudiobookScanDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var ownership = new Dictionary<string, int>(semantics.Comparer);
        var allFiles = await fileRepository.GetAllAsync(cancellationToken);
        foreach (var file in allFiles)
        {
            if (file.PathIdentityState != PathIdentityState.Valid
                || file.PathSyntax != semantics.Syntax
                || string.IsNullOrWhiteSpace(file.CanonicalPath))
            {
                continue;
            }

            string canonicalPath;
            try
            {
                canonicalPath = FileSystemPathIdentity.Canonicalize(
                    file.CanonicalPath,
                    semantics.Syntax);
            }
            catch (Exception exception) when (exception is
                ArgumentException or NotSupportedException or PathTooLongException)
            {
                diagnostics.Add(new AudiobookScanDiagnostic(
                    "OwnershipIdentityInvalid",
                    file.Path,
                    "The stored ownership identity is invalid for this filesystem."));
                continue;
            }

            if (ownership.TryGetValue(canonicalPath, out var existingOwner)
                && existingOwner != file.AudiobookId)
            {
                ownership[canonicalPath] = -1;
                diagnostics.Add(new AudiobookScanDiagnostic(
                    "OwnershipConflict",
                    canonicalPath,
                    $"The canonical path is claimed by audiobooks {existingOwner} and {file.AudiobookId}."));
                continue;
            }

            ownership[canonicalPath] = file.AudiobookId;
        }

        return ownership;
    }

    private async Task<string?> ApplyBasePathPlanAsync(
        AudiobookScanCommand command,
        Audiobook audiobook,
        IReadOnlyCollection<AudiobookFile> existingFiles,
        ScanDiscoveryResult discovery,
        FileSystemPathSemantics semantics,
        ICollection<AudiobookScanDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        if (!discovery.CanUpdateBasePath
            || discovery.AttributedFiles.Count == 0)
        {
            return audiobook.BasePath;
        }

        var planned = discovery.SelectedStableIdentifierBoundary
            ?? ScanPathPlanner.CalculateBasePath(
                discovery.AttributedFiles,
                semantics,
                discovery.CommonProvenBookBoundary(semantics),
                command.ScanRoot);
        if (string.IsNullOrWhiteSpace(planned))
        {
            return audiobook.BasePath;
        }

        if (IsFilesystemRoot(planned, semantics))
        {
            diagnostics.Add(new AudiobookScanDiagnostic(
                "BasePathRootRejected",
                planned,
                "A filesystem root cannot become an audiobook BasePath."));
            return audiobook.BasePath;
        }

        if (FileSystemPathIdentity.AreEquivalent(
                planned,
                command.ScanIdentity.BoundaryPath,
                semantics)
            && discovery.AttributedFiles.Any(path =>
                !FileSystemPathIdentity.AreEquivalent(
                    Path.GetDirectoryName(path) ?? path,
                    planned,
                    semantics)))
        {
            diagnostics.Add(new AudiobookScanDiagnostic(
                "ConfiguredRootBasePathRejected",
                planned,
                "The configured library root is broader than the attributed audiobook files."));
            return audiobook.BasePath;
        }

        var selected = SelectMonotonicBasePath(
            command,
            audiobook.BasePath,
            planned,
            existingFiles.Count,
            semantics,
            diagnostics);
        if (string.IsNullOrWhiteSpace(selected)
            || (!string.IsNullOrWhiteSpace(audiobook.BasePath)
                && FileSystemPathIdentity.AreEquivalent(
                    selected,
                    audiobook.BasePath,
                    semantics)))
        {
            return audiobook.BasePath;
        }

        var previousBasePath = audiobook.BasePath;
        audiobook.BasePath = selected;
        cancellationToken.ThrowIfCancellationRequested();
        if (!await audiobookRepository.UpdateAsync(audiobook))
        {
            audiobook.BasePath = previousBasePath;
            diagnostics.Add(new AudiobookScanDiagnostic(
                "BasePathPersistenceFailed",
                selected,
                "The planned BasePath could not be persisted and was not applied."));
            return previousBasePath;
        }

        logger.LogInformation(
            "Updated audiobook {AudiobookId} BasePath to {BasePath} from authoritative scan evidence",
            audiobook.Id,
            LogRedaction.SanitizeFilePath(selected));
        return selected;
    }

    private static string? SelectMonotonicBasePath(
        AudiobookScanCommand command,
        string? existingBasePath,
        string plannedBasePath,
        int existingFileCount,
        FileSystemPathSemantics semantics,
        ICollection<AudiobookScanDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(existingBasePath))
        {
            return plannedBasePath;
        }

        if (FileSystemPathIdentity.AreEquivalent(
                existingBasePath,
                plannedBasePath,
                semantics))
        {
            return existingBasePath;
        }

        if (command.MoveOwned)
        {
            diagnostics.Add(new AudiobookScanDiagnostic(
                "MoveOwnedBasePathPreserved",
                existingBasePath,
                "A move-owned scan cannot replace its durable move target."));
            return existingBasePath;
        }

        if (!command.IsAuthoritativeScope)
        {
            diagnostics.Add(new AudiobookScanDiagnostic(
                "FocusedScanBasePathPreserved",
                existingBasePath,
                "A focused scan cannot redefine the complete audiobook root."));
            return existingBasePath;
        }

        if (FileSystemPathIdentity.IsSameOrInside(
                plannedBasePath,
                existingBasePath,
                semantics))
        {
            return plannedBasePath;
        }

        if (FileSystemPathIdentity.IsSameOrInside(
                existingBasePath,
                plannedBasePath,
                semantics))
        {
            diagnostics.Add(new AudiobookScanDiagnostic(
                "BasePathWideningRejected",
                plannedBasePath,
                "An ordinary scan cannot widen an existing audiobook BasePath."));
            return existingBasePath;
        }

        if (existingFileCount == 0)
        {
            return plannedBasePath;
        }

        diagnostics.Add(new AudiobookScanDiagnostic(
            "BasePathConflict",
            plannedBasePath,
            "The discovered files are unrelated to the existing tracked audiobook root."));
        return existingBasePath;
    }

    private static bool TryResolveStoredFilePath(
        Audiobook audiobook,
        AudiobookFile file,
        FileSystemPathSemantics semantics,
        out string path,
        out string? reason)
    {
        path = string.Empty;
        reason = null;
        try
        {
            if (file.PathIdentityState == PathIdentityState.Valid
                && file.PathSyntax == semantics.Syntax
                && !string.IsNullOrWhiteSpace(file.CanonicalPath))
            {
                path = FileSystemPathIdentity.Canonicalize(
                    file.CanonicalPath,
                    semantics.Syntax);
                return true;
            }

            if (string.IsNullOrWhiteSpace(file.Path))
            {
                reason = "The stored file path is empty.";
                return false;
            }

            if (FileSystemPathIdentity.TryDetectAbsoluteSyntax(
                    file.Path,
                    semantics.Syntax,
                    out _))
            {
                path = FileSystemPathIdentity.Canonicalize(
                    file.Path,
                    semantics.Syntax);
                return true;
            }

            if (FileSystemPathIdentity.TryDetectAbsoluteSyntax(file.Path, out _))
            {
                reason = "The stored file path uses an unexpected filesystem syntax.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(audiobook.BasePath)
                || !FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                    audiobook.BasePath,
                    file.Path,
                    semantics,
                    out path))
            {
                reason = "The relative stored file path cannot be resolved inside BasePath.";
                return false;
            }

            return true;
        }
        catch (Exception exception) when (exception is
            ArgumentException or NotSupportedException or PathTooLongException
            or System.Security.SecurityException)
        {
            reason = "The stored audiobook file path is invalid for this filesystem.";
            return false;
        }
    }

    private static bool IsFilesystemRoot(
        string path,
        FileSystemPathSemantics semantics)
    {
        var root = Path.GetPathRoot(path);
        return !string.IsNullOrWhiteSpace(root)
            && FileSystemPathIdentity.AreEquivalent(path, root, semantics);
    }
}
