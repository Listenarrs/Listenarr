using Listenarr.Domain.Common;

namespace Listenarr.Application.Audiobooks.Files;

public partial class AudiobookFileService
{
    public Task<AudiobookFileOwnershipCheckResult> CheckAudiobookFileOwnershipAsync(
        Audiobook audiobook,
        string plannedPhysicalPath,
        string? plannedBasePath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audiobook);
        ArgumentException.ThrowIfNullOrWhiteSpace(plannedPhysicalPath);

        return filesystemMutationCoordinator.ExecuteExclusiveAsync(
            globalToken => audiobookOperationCoordinator.ExecuteExclusiveAsync(
                audiobook.Id,
                async token =>
                {
                    var currentAudiobook = await audiobookRepository.GetByIdSnapshotAsync(
                        audiobook.Id,
                        token);
                    if (currentAudiobook == null)
                    {
                        return new AudiobookFileOwnershipCheckResult(
                            AudiobookFileOwnershipCheckOutcome.IdentityUnavailable,
                            Reason: "The owning audiobook no longer exists.");
                    }

                    if (string.IsNullOrWhiteSpace(currentAudiobook.BasePath)
                        && !string.IsNullOrWhiteSpace(plannedBasePath))
                    {
                        currentAudiobook.BasePath = plannedBasePath;
                    }

                    var authorization = await ResolveAuthorizedClaimPathAsync(
                        currentAudiobook,
                        plannedPhysicalPath,
                        token,
                        requireExistingFile: false,
                        requireAudioFile: false);
                    if (authorization.Path == null)
                    {
                        return new AudiobookFileOwnershipCheckResult(
                            AudiobookFileOwnershipCheckOutcome.IdentityUnavailable,
                            Reason: authorization.Reason ?? "The audiobook file path is not authorized.");
                    }

                    var identity = await filePathIdentityResolver.ResolveAsync(
                        currentAudiobook,
                        authorization.Path,
                        token);
                    return await audiobookFileRepository.CheckOwnershipAsync(
                        currentAudiobook.Id,
                        null,
                        identity,
                        token);
                },
                globalToken),
            cancellationToken);
    }

    public Task<AudiobookFileClaimResult> ClaimAudiobookFileAsync(
        Audiobook audiobook,
        AudiobookFile file,
        string physicalPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audiobook);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentException.ThrowIfNullOrWhiteSpace(file.Path);
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalPath);

        return filesystemMutationCoordinator.ExecuteExclusiveAsync(
            globalToken => audiobookOperationCoordinator.ExecuteExclusiveAsync(
                audiobook.Id,
                async token =>
                {
                    await moveQueueService.EnsureFilesystemMutationAllowedAsync(
                        audiobook.Id,
                        token);
                    var currentAudiobook = await audiobookRepository.GetByIdSnapshotAsync(
                        audiobook.Id,
                        token);
                    if (currentAudiobook == null)
                    {
                        return new AudiobookFileClaimResult(
                            AudiobookFileClaimOutcome.IdentityUnavailable,
                            Reason: "The owning audiobook no longer exists.");
                    }

                    return await ClaimAudiobookFileCoreAsync(
                        currentAudiobook,
                        file,
                        physicalPath,
                        basePathMutation: null,
                        token);
                },
                globalToken),
            cancellationToken);
    }

    private async Task<AudiobookFileClaimResult> ClaimAudiobookFileCoreAsync(
        Audiobook audiobook,
        AudiobookFile file,
        string physicalPath,
        AudiobookBasePathMutation? basePathMutation,
        CancellationToken cancellationToken)
    {
        var authorization = await ResolveAuthorizedClaimPathAsync(
            audiobook,
            physicalPath,
            cancellationToken);
        if (authorization.Path == null)
        {
            return new AudiobookFileClaimResult(
                AudiobookFileClaimOutcome.IdentityUnavailable,
                Reason: authorization.Reason ?? "The audiobook file path is not authorized.");
        }

        var identity = await filePathIdentityResolver.ResolveAsync(
            audiobook,
            authorization.Path,
            cancellationToken);
        if (identity.State != PathIdentityState.Valid)
        {
            return new AudiobookFileClaimResult(
                AudiobookFileClaimOutcome.IdentityUnavailable,
                Reason: identity.Reason ?? "Filesystem identity is unavailable.");
        }

        file.AudiobookId = audiobook.Id;
        file.ApplyPathIdentity(file.Path!, identity);
        return basePathMutation == null
            ? await audiobookFileRepository.ClaimAsync(file, cancellationToken)
            : await audiobookFileRepository.ClaimWithBasePathAsync(
                file,
                basePathMutation,
                cancellationToken);
    }
}
