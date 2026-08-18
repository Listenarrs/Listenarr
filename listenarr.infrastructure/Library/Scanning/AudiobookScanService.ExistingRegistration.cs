using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Scanning;

internal sealed partial class AudiobookScanService
{
    public async Task<bool> RegisterExistingFileAsync(
        int audiobookId,
        string audiobookBasePath,
        string filePath,
        string source = "manual-import",
        CancellationToken cancellationToken = default)
    {
        if (audiobookId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(audiobookId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(audiobookBasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        cancellationToken.ThrowIfCancellationRequested();

        if (!FileUtils.IsAudioFile(filePath))
        {
            return false;
        }

        var authorization = await pathAuthorizationService.AuthorizeAsync(
            audiobookBasePath,
            cancellationToken);
        if (!authorization.IsAuthorized
            || string.IsNullOrWhiteSpace(authorization.Path)
            || !authorization.Identity.HasValue
            || !authorization.PhysicalIdentity.HasValue)
        {
            logger.LogWarning(
                "Refused in-place audiobook file registration because the audiobook folder is not scan-authorized. AudiobookId={AudiobookId} BasePath={BasePath} Reason={Reason}",
                audiobookId,
                LogRedaction.SanitizeFilePath(audiobookBasePath),
                LogRedaction.SanitizeText(authorization.Error));
            return false;
        }

        var command = new AudiobookScanCommand(
            audiobookId,
            authorization.Path,
            authorization.Identity.Value,
            authorization.PhysicalIdentity.Value,
            AllowReconciliation: false,
            IsAuthoritativeScope: false,
            Source: source,
            CorrelationId: $"register-existing:{audiobookId}:{Guid.NewGuid():N}");
        var semantics = await ValidateCommandAsync(command, cancellationToken);
        var canonicalFilePath = FileSystemPathIdentity.Canonicalize(
            filePath,
            command.ScanIdentity.Syntax);
        if (!FileSystemPathIdentity.IsSameOrInside(
                canonicalFilePath,
                command.ScanRoot,
                semantics))
        {
            logger.LogWarning(
                "Refused in-place audiobook file registration outside the audiobook folder. AudiobookId={AudiobookId} BasePath={BasePath} File={File}",
                audiobookId,
                LogRedaction.SanitizeFilePath(command.ScanRoot),
                LogRedaction.SanitizeFilePath(canonicalFilePath));
            return false;
        }

        using var pinnedAuthority = OpenPinnedScanAuthority(command);
        var audiobook = await audiobookRepository.GetForScanAsync(
            audiobookId,
            cancellationToken);
        if (audiobook == null
            || string.IsNullOrWhiteSpace(audiobook.BasePath)
            || !FileSystemPathIdentity.AreEquivalent(
                audiobook.BasePath,
                command.ScanRoot,
                semantics))
        {
            logger.LogWarning(
                "Refused in-place audiobook file registration because the audiobook BasePath changed or does not match the authorized existing-file folder. AudiobookId={AudiobookId} RequestedBasePath={RequestedBasePath} CurrentBasePath={CurrentBasePath}",
                audiobookId,
                LogRedaction.SanitizeFilePath(command.ScanRoot),
                LogRedaction.SanitizeFilePath(audiobook?.BasePath));
            return false;
        }

        var trackedFiles = await fileRepository.GetByAudiobookIdAsync(
            audiobookId,
            cancellationToken);
        var resolvedTrackedPaths = ResolveExistingPaths(
            audiobook,
            trackedFiles,
            semantics,
            []);
        var existingFile = trackedFiles.FirstOrDefault(candidate =>
            resolvedTrackedPaths.TryGetValue(candidate.Id, out var resolvedPath)
            && FileSystemPathIdentity.AreEquivalent(
                resolvedPath,
                canonicalFilePath,
                semantics));

        using var registrationLease = OpenPinnedRegistrationFile(
            command,
            pinnedAuthority,
            canonicalFilePath);
        if (existingFile != null)
        {
            if (existingFile.PathIdentityState != PathIdentityState.Valid
                || !existingFile.PathSyntax.HasValue
                || existingFile.PathCaseSensitivity == FileSystemCaseSensitivity.Unknown
                || string.IsNullOrWhiteSpace(existingFile.CanonicalPath)
                || string.IsNullOrWhiteSpace(existingFile.PathIdentityBoundary)
                || string.IsNullOrWhiteSpace(existingFile.PathIdentityLookupKey)
                || string.IsNullOrWhiteSpace(existingFile.PathOwnershipKey)
                || !registrationLease.MatchesCurrentPublication())
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(existingFile.PhysicalObjectIdentity))
            {
                return true;
            }

            // Path equality alone cannot prove that a path-only observation is the
            // same generation as a previously durable ownership record. Preserve
            // the stronger evidence and require repair rather than silently erasing it.
            return registrationLease.HasDurablePhysicalObjectIdentity
                && registrationLease.MatchesPhysicalObjectIdentity(
                    existingFile.PhysicalObjectIdentity);
        }

        return await fileService.EnsureAudiobookFileAsync(
            audiobook,
            registrationLease,
            source,
            cancellationToken);
    }
}
