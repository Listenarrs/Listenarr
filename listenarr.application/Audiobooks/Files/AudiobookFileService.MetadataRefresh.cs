using Listenarr.Domain.Common;

namespace Listenarr.Application.Audiobooks.Files;

public partial class AudiobookFileService
{
    public Task<bool> RefreshMetadataAsync(
        Audiobook audiobook,
        int fileId,
        IAudiobookFileRegistrationLease registrationLease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audiobook);
        ArgumentNullException.ThrowIfNull(registrationLease);
        ArgumentException.ThrowIfNullOrWhiteSpace(registrationLease.PublicPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(registrationLease.MetadataPath);
        if (fileId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fileId));
        }

        return filesystemMutationCoordinator.ExecuteExclusiveAsync(
            globalToken => audiobookOperationCoordinator.ExecuteExclusiveAsync(
                audiobook.Id,
                token => RefreshMetadataCoreAsync(audiobook.Id, fileId, registrationLease, token),
                globalToken),
            cancellationToken);
    }

    private async Task<bool> RefreshMetadataCoreAsync(
        int audiobookId,
        int fileId,
        IAudiobookFileRegistrationLease lease,
        CancellationToken cancellationToken)
    {
        await moveQueueService.EnsureFilesystemMutationAllowedAsync(audiobookId, cancellationToken);
        var audiobook = await audiobookRepository.GetByIdSnapshotAsync(audiobookId, cancellationToken);
        var currentFile = await audiobookFileRepository.GetByIdAsync(fileId, cancellationToken);
        if (audiobook == null
            || currentFile == null
            || currentFile.AudiobookId != audiobookId
            || currentFile.PathIdentityState != PathIdentityState.Valid)
        {
            return false;
        }

        // Capture immutable expected state before extraction; the persistence port
        // compares it atomically and writes metadata fields only.
        var expectedFile = AudiobookFileMetadataRefreshSnapshot.Capture(currentFile, audiobook.BasePath);
        if (!await CanRefreshOwnedMetadataAsync(audiobook, expectedFile, lease, cancellationToken))
        {
            return false;
        }

        // Operation-local cache identity prevents a path-only read from inheriting
        // cached metadata for a previously visible object at the same pathname.
        var metadata = await ExtractMetadataAsync(
            lease.MetadataPath,
            $"metadata-read:{Guid.NewGuid():N}",
            lease.PublicPath);
        cancellationToken.ThrowIfCancellationRequested();
        if (metadata == null
            || !await CanRefreshOwnedMetadataAsync(audiobook, expectedFile, lease, cancellationToken))
        {
            return false;
        }

        return await audiobookFileRepository.RefreshMetadataAsync(
            expectedFile, metadata, cancellationToken);
    }

    private async Task<bool> CanRefreshOwnedMetadataAsync(
        Audiobook audiobook,
        AudiobookFileMetadataRefreshSnapshot expectedFile,
        IAudiobookFileRegistrationLease lease,
        CancellationToken cancellationToken)
    {
        var authorization = await ResolveAuthorizedClaimPathAsync(
            audiobook, lease.PublicPath, cancellationToken);
        if (authorization.Path == null)
        {
            return false;
        }

        var identity = await filePathIdentityResolver.ResolveAsync(
            audiobook, authorization.Path, cancellationToken);
        var storedIdentity = await filePathIdentityResolver.ResolveAsync(
            audiobook, expectedFile.PathState.StoredPath!, cancellationToken);
        if (identity.State != PathIdentityState.Valid
            || storedIdentity.State != PathIdentityState.Valid
            || string.IsNullOrWhiteSpace(identity.OwnershipKey)
            || identity.OwnershipKey != storedIdentity.OwnershipKey
            || identity.OwnershipKey != expectedFile.PathState.OwnershipKey
            || identity.LookupKey != expectedFile.PathState.LookupKey
            || identity.CanonicalPath != expectedFile.PathState.CanonicalPath
            || identity.Syntax != expectedFile.PathState.Syntax
            || identity.CaseSensitivity != expectedFile.PathState.CaseSensitivity
            || identity.RequestedMode != expectedFile.PathState.RequestedMode
            || identity.BoundaryPath != expectedFile.PathState.BoundaryPath
            || identity.Version != expectedFile.PathState.Version)
        {
            return false;
        }

        var ownership = await audiobookFileRepository.CheckOwnershipAsync(
            audiobook.Id, expectedFile.FileId, identity, cancellationToken);
        return ownership.Outcome == AudiobookFileOwnershipCheckOutcome.Available
            && lease.MatchesCurrentPublication();
    }
}
