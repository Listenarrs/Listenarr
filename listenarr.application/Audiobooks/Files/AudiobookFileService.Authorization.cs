using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks.Files;

public partial class AudiobookFileService
{
    private async Task<AuthorizedClaimPath> ResolveAuthorizedClaimPathAsync(
        Audiobook audiobook,
        string physicalPath,
        CancellationToken cancellationToken,
        bool requireExistingFile = true,
        bool requireAudioFile = true)
    {
        try
        {
            var fileExists = fileSystem.FileExists(physicalPath);
            if ((requireExistingFile && !fileExists)
                || fileSystem.IsReparsePoint(physicalPath))
            {
                return AuthorizedClaimPath.Failed(
                    "The audiobook file does not exist or is a linked filesystem entry.");
            }

            if (requireAudioFile && !FileUtils.IsAudioFile(physicalPath))
            {
                return AuthorizedClaimPath.Failed(
                    "The claimed audiobook file is not a supported audio file.");
            }

            var candidatePath = ResolveAbsolutePath(physicalPath);
            var basePath = ResolveStoredAbsolutePathForHost(audiobook.BasePath);
            if (!string.IsNullOrWhiteSpace(audiobook.BasePath)
                && string.IsNullOrWhiteSpace(basePath))
            {
                return AuthorizedClaimPath.Failed(
                    "The audiobook base path is unavailable on the current host.");
            }

            var existingDirectory = string.IsNullOrWhiteSpace(basePath)
                ? ResolveStoredFileDirectory(audiobook)
                : string.Empty;
            var hasAuthorizationBoundary = !string.IsNullOrWhiteSpace(existingDirectory)
                || !string.IsNullOrWhiteSpace(basePath);
            if (!hasAuthorizationBoundary)
            {
                return AuthorizedClaimPath.Failed(
                    "The audiobook has no authoritative folder for file ownership.");
            }

            var rootFolders = await GetRootFoldersForSemanticsAsync(cancellationToken);
            var allowedSafetyRoots = new List<string?>();
            var authorized = false;
            if (!string.IsNullOrWhiteSpace(existingDirectory))
            {
                var existingResolution = await ResolveLibraryPathSemanticsAsync(
                    existingDirectory,
                    rootFolders,
                    cancellationToken);
                authorized = existingResolution != null
                    && FileSystemPathIdentity.IsSameOrInside(
                        candidatePath,
                        existingDirectory,
                        existingResolution.Semantics);
                if (authorized)
                {
                    allowedSafetyRoots.Add(ResolvePhysicalSafetyRoot(
                        candidatePath,
                        existingDirectory,
                        existingResolution!));
                }
            }

            if (!authorized && !string.IsNullOrWhiteSpace(basePath))
            {
                var baseResolution = await ResolveLibraryPathSemanticsAsync(
                    basePath,
                    rootFolders,
                    cancellationToken);
                authorized = baseResolution != null
                    && FileSystemPathIdentity.IsSameOrInside(
                        candidatePath,
                        basePath,
                        baseResolution.Semantics);
                if (authorized)
                {
                    allowedSafetyRoots.Add(ResolvePhysicalSafetyRoot(
                        candidatePath,
                        basePath,
                        baseResolution!));
                }
            }

            if (!authorized)
            {
                return AuthorizedClaimPath.Failed(
                    "The audiobook file is outside the authoritative audiobook folder.");
            }

            if (!fileSystem.TryValidateMutationTarget(
                    candidatePath,
                    allowedSafetyRoots,
                    out var validatedPath,
                    out var reason))
            {
                return AuthorizedClaimPath.Failed(
                    reason ?? "The audiobook file path did not resolve safely inside the audiobook folder.");
            }

            return new AuthorizedClaimPath(validatedPath);
        }
        catch (Exception exception) when (exception is not OperationCanceledException
            && exception is not OutOfMemoryException
            && exception is not StackOverflowException)
        {
            logger.LogWarning(
                exception,
                "Audiobook file ownership authorization failed for audiobook {AudiobookId} at {Path}",
                audiobook.Id,
                LogRedaction.SanitizeFilePath(physicalPath));
            return AuthorizedClaimPath.Failed(
                "The audiobook file path could not be authorized safely.");
        }
    }

    private static string ResolveStoredFileDirectory(Audiobook audiobook)
    {
        if (string.IsNullOrWhiteSpace(audiobook.FilePath))
        {
            return string.Empty;
        }

        if (FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                audiobook.FilePath,
                out var absoluteFilePath,
                out _))
        {
            return Path.GetDirectoryName(absoluteFilePath) ?? string.Empty;
        }
        if (FileSystemPathIdentity.TryDetectAbsoluteSyntax(
                audiobook.FilePath,
                out _))
        {
            return string.Empty;
        }

        var basePath = ResolveStoredAbsolutePathForHost(audiobook.BasePath);
        if (string.IsNullOrWhiteSpace(basePath))
        {
            return string.Empty;
        }

        var semantics = new FileSystemPathSemantics(
            OperatingSystem.IsWindows()
                ? FileSystemPathSyntax.Windows
                : FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Sensitive);
        if (!FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                basePath,
                audiobook.FilePath,
                semantics,
                out absoluteFilePath))
        {
            return string.Empty;
        }

        return Path.GetDirectoryName(absoluteFilePath) ?? string.Empty;
    }

    private static string ResolveStoredAbsolutePathForHost(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        return FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
            path,
            out var canonicalPath,
            out _)
            ? canonicalPath
            : string.Empty;
    }

    private sealed record AuthorizedClaimPath(
        string? Path,
        string? Reason = null)
    {
        public static AuthorizedClaimPath Failed(string reason) =>
            new(null, reason);
    }
}
