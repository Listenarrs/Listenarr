using Listenarr.Domain.Common;

namespace Listenarr.Application.Audiobooks.Files;

public partial class AudiobookFileService
{
    public Task<bool> RollbackPhysicalGenerationClaimAsync(
        Audiobook audiobook,
        int fileId,
        string? expectedPath,
        string expectedPhysicalObjectIdentity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audiobook);
        if (fileId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fileId));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(
            expectedPhysicalObjectIdentity);

        return filesystemMutationCoordinator.ExecuteExclusiveAsync(
            globalToken => audiobookOperationCoordinator.ExecuteExclusiveAsync(
                audiobook.Id,
                _ => DeletePhysicalGenerationClaimCoreAsync(
                    fileId,
                    audiobook.Id,
                    expectedPath,
                    expectedPhysicalObjectIdentity,
                    basePathMutation: null),
                globalToken),
            cancellationToken);
    }

    public Task<bool> RefreshPhysicalGenerationAsync(
        Audiobook audiobook,
        int fileId,
        string? expectedPhysicalObjectIdentity,
        IAudiobookFileRegistrationLease registrationLease,
        string? source = "scan",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audiobook);
        ArgumentNullException.ThrowIfNull(registrationLease);
        if (!registrationLease.HasDurablePhysicalObjectIdentity)
        {
            throw new InvalidOperationException(
                "Physical-generation refresh requires durable physical identity evidence.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(registrationLease.PublicPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(registrationLease.MetadataPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(
            registrationLease.PhysicalObjectIdentity);

        return RefreshPhysicalGenerationAsync(
            audiobook,
            fileId,
            expectedPhysicalObjectIdentity,
            registrationLease,
            authoritativeBasePath: null,
            basePathCommitContext: null,
            source,
            cancellationToken);
    }

    private async Task<BasePathRegistrationOutcome>
        RefreshPhysicalGenerationWithBasePathAsync(
            Audiobook audiobook,
            int fileId,
            string? expectedPhysicalObjectIdentity,
            IAudiobookFileRegistrationLease registrationLease,
            string authoritativeBasePath,
            string? source,
            CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authoritativeBasePath);
        var context = new AudiobookBasePathCommitContext();
        var success = await RefreshPhysicalGenerationAsync(
            audiobook,
            fileId,
            expectedPhysicalObjectIdentity,
            registrationLease,
            FileUtils.NormalizeStoredPath(authoritativeBasePath),
            context,
            source,
            cancellationToken);
        return new BasePathRegistrationOutcome(
            success,
            success ? context.Mutation : null);
    }

    private Task<bool> RefreshPhysicalGenerationAsync(
        Audiobook audiobook,
        int fileId,
        string? expectedPhysicalObjectIdentity,
        IAudiobookFileRegistrationLease registrationLease,
        string? authoritativeBasePath,
        AudiobookBasePathCommitContext? basePathCommitContext,
        string? source,
        CancellationToken cancellationToken) =>
        filesystemMutationCoordinator.ExecuteExclusiveAsync(
            globalToken => audiobookOperationCoordinator.ExecuteExclusiveAsync(
                audiobook.Id,
                token => RefreshPhysicalGenerationCoreAsync(
                    audiobook.Id,
                    fileId,
                    expectedPhysicalObjectIdentity,
                    registrationLease,
                    authoritativeBasePath,
                    basePathCommitContext,
                    source,
                    token),
                globalToken),
            cancellationToken);

    private async Task<bool> RefreshPhysicalGenerationCoreAsync(
        int audiobookId,
        int fileId,
        string? expectedPhysicalObjectIdentity,
        IAudiobookFileRegistrationLease registrationLease,
        string? authoritativeBasePath,
        AudiobookBasePathCommitContext? basePathCommitContext,
        string? source,
        CancellationToken cancellationToken)
    {
        if (!registrationLease.MatchesCurrentPublication())
        {
            return false;
        }

        var audiobook = await audiobookRepository.GetByIdSnapshotAsync(
            audiobookId,
            cancellationToken);
        var currentFile = await audiobookFileRepository.GetByIdAsync(
            fileId,
            cancellationToken);
        if (audiobook == null
            || currentFile == null
            || currentFile.AudiobookId != audiobookId
            || string.IsNullOrWhiteSpace(currentFile.Path)
            || !string.Equals(
                currentFile.PhysicalObjectIdentity,
                expectedPhysicalObjectIdentity,
                StringComparison.Ordinal))
        {
            return false;
        }

        AudiobookBasePathMutation? basePathMutation = null;
        if (!string.IsNullOrWhiteSpace(authoritativeBasePath))
        {
            basePathMutation = new AudiobookBasePathMutation(
                audiobook.Id,
                audiobook.BasePath,
                authoritativeBasePath);
            basePathCommitContext!.Mutation = basePathMutation;
            audiobook.BasePath = authoritativeBasePath;
        }

        var authorization = await ResolveAuthorizedClaimPathAsync(
            audiobook,
            registrationLease.PublicPath,
            cancellationToken);
        if (authorization.Path == null)
        {
            return false;
        }

        var currentIdentity = await filePathIdentityResolver.ResolveAsync(
            audiobook,
            authorization.Path,
            cancellationToken);
        var storedIdentity = await filePathIdentityResolver.ResolveAsync(
            audiobook,
            currentFile.Path,
            cancellationToken);
        if (currentIdentity.State != PathIdentityState.Valid
            || storedIdentity.State != PathIdentityState.Valid
            || string.IsNullOrWhiteSpace(currentIdentity.OwnershipKey)
            || !string.Equals(
                storedIdentity.OwnershipKey,
                currentIdentity.OwnershipKey,
                StringComparison.Ordinal))
        {
            return false;
        }

        if (!registrationLease.MatchesCurrentPublication())
        {
            return false;
        }

        var metadata = await ExtractMetadataAsync(
            registrationLease.MetadataPath,
            registrationLease.PhysicalObjectIdentity,
            registrationLease.PublicPath);
        var replacement = CreatePhysicalGenerationSnapshot(
            currentFile,
            registrationLease,
            metadata,
            source,
            replaceMetadata: !string.IsNullOrWhiteSpace(
                    expectedPhysicalObjectIdentity)
                && !registrationLease.MatchesPhysicalObjectIdentity(
                    expectedPhysicalObjectIdentity));
        var predecessor = ClonePhysicalGeneration(currentFile);

        if (!registrationLease.MatchesCurrentPublication())
        {
            return false;
        }

        var updated = basePathMutation == null
            ? await audiobookFileRepository.ReplacePhysicalGenerationAsync(
                currentFile.Id,
                currentFile.AudiobookId,
                currentFile.Path,
                expectedPhysicalObjectIdentity,
                replacement,
                cancellationToken)
            : await audiobookFileRepository.ReplacePhysicalGenerationWithBasePathAsync(
                currentFile.Id,
                currentFile.AudiobookId,
                currentFile.Path,
                expectedPhysicalObjectIdentity,
                replacement,
                basePathMutation,
                cancellationToken);
        if (!updated)
        {
            return false;
        }

        var postCommitPublication = ProbeCurrentPublication(registrationLease);
        if (postCommitPublication != RegistrationPublicationMatchOutcome.Mismatch)
        {
            // The generation row is already committed. Temporary storage
            // unavailability is not evidence that the published namespace changed.
            return true;
        }

        var reverted = basePathMutation == null
            ? await audiobookFileRepository.ReplacePhysicalGenerationAsync(
                currentFile.Id,
                currentFile.AudiobookId,
                currentFile.Path,
                registrationLease.PhysicalObjectIdentity,
                predecessor,
                CancellationToken.None)
            : await audiobookFileRepository.ReplacePhysicalGenerationWithBasePathAsync(
                currentFile.Id,
                currentFile.AudiobookId,
                currentFile.Path,
                registrationLease.PhysicalObjectIdentity,
                predecessor,
                new AudiobookBasePathMutation(
                    currentFile.AudiobookId,
                    basePathMutation.ResultingBasePath,
                    basePathMutation.ExpectedCurrentBasePath),
                CancellationToken.None);
        if (!reverted)
        {
            throw new InvalidOperationException(
                "The audiobook file generation changed during persistence and the prior row could not be restored.");
        }

        return false;
    }

    private static AudiobookFile CreatePhysicalGenerationSnapshot(
        AudiobookFile currentFile,
        IAudiobookFileRegistrationLease registrationLease,
        AudioMetadata? metadata,
        string? source,
        bool replaceMetadata)
    {
        var fileInfo = new FileInfo(registrationLease.MetadataPath);
        var replacement = AudiobookFile.CreateUnresolved(currentFile.Path);
        replacement.AudiobookId = currentFile.AudiobookId;
        replacement.Size = fileInfo.Exists ? fileInfo.Length : currentFile.Size;
        replacement.DurationSeconds = replaceMetadata
            ? metadata?.Duration.TotalSeconds
            : Math.Abs(metadata?.Duration.TotalSeconds ?? 0) > double.Epsilon
                ? metadata!.Duration.TotalSeconds
                : currentFile.DurationSeconds;
        replacement.Format = replaceMetadata
            ? metadata?.Format
            : !string.IsNullOrEmpty(metadata?.Format)
                ? metadata.Format
                : currentFile.Format;
        replacement.Container = replaceMetadata
            ? metadata?.Container
            : !string.IsNullOrEmpty(metadata?.Container)
                ? metadata.Container
                : currentFile.Container;
        replacement.Codec = replaceMetadata
            ? metadata?.Codec
            : !string.IsNullOrEmpty(metadata?.Codec)
                ? metadata.Codec
                : currentFile.Codec;
        replacement.Bitrate = replaceMetadata
            ? metadata?.BitRate
            : metadata?.BitRate is int bitRate && bitRate != 0
                ? bitRate
                : currentFile.Bitrate;
        replacement.SampleRate = replaceMetadata
            ? metadata?.SampleRate
            : metadata?.SampleRate is int sampleRate && sampleRate != 0
                ? sampleRate
                : currentFile.SampleRate;
        replacement.Channels = replaceMetadata
            ? metadata?.Channels
            : metadata?.Channels is int channels && channels != 0
                ? channels
                : currentFile.Channels;
        replacement.Source = source ?? currentFile.Source;
        replacement.ApplyPhysicalObjectIdentity(
            registrationLease.PhysicalObjectIdentity,
            DateTime.UtcNow);
        return replacement;
    }

    private async Task DeleteCreatedPhysicalGenerationAsync(
        AudiobookFile createdFile,
        AudiobookBasePathMutation? basePathMutation)
    {
        ArgumentNullException.ThrowIfNull(createdFile);
        if (createdFile.Id <= 0)
        {
            throw new InvalidOperationException(
                "A persisted audiobook file claim is required for rollback.");
        }

        if (!await DeletePhysicalGenerationClaimCoreAsync(
                createdFile.Id,
                createdFile.AudiobookId,
                createdFile.Path,
                createdFile.PhysicalObjectIdentity,
                basePathMutation))
        {
            throw new InvalidOperationException(
                "The stale audiobook file generation claim remained after rollback retries.");
        }
    }

    private async Task<bool> DeletePhysicalGenerationClaimCoreAsync(
        int fileId,
        int audiobookId,
        string? expectedPath,
        string? expectedPhysicalObjectIdentity,
        AudiobookBasePathMutation? basePathMutation)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var deleted = basePathMutation == null
                    ? await audiobookFileRepository.DeletePhysicalGenerationAsync(
                        fileId,
                        audiobookId,
                        expectedPath,
                        expectedPhysicalObjectIdentity,
                        CancellationToken.None)
                    : await audiobookFileRepository.DeletePhysicalGenerationWithBasePathAsync(
                        fileId,
                        audiobookId,
                        expectedPath,
                        expectedPhysicalObjectIdentity,
                        new AudiobookBasePathMutation(
                            audiobookId,
                            basePathMutation.ResultingBasePath,
                            basePathMutation.ExpectedCurrentBasePath),
                        CancellationToken.None);
                if (deleted)
                {
                    return true;
                }

                var current = await audiobookFileRepository.GetByIdAsync(
                    fileId,
                    CancellationToken.None);
                if (current == null
                    || current.AudiobookId != audiobookId
                    || !string.Equals(
                        current.Path,
                        expectedPath,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        current.PhysicalObjectIdentity,
                        expectedPhysicalObjectIdentity,
                        StringComparison.Ordinal))
                {
                    return true;
                }
            }
            catch (Exception exception) when (exception is not (
                OutOfMemoryException or StackOverflowException))
            {
                if (attempt == maxAttempts)
                {
                    throw new InvalidOperationException(
                        "The stale audiobook file generation claim could not be rolled back.",
                        exception);
                }
            }

            await Task.Delay(
                TimeSpan.FromMilliseconds(100 * attempt * attempt),
                CancellationToken.None);
        }

        return false;
    }

    private static AudiobookFile ClonePhysicalGeneration(AudiobookFile source)
    {
        var clone = AudiobookFile.CreateUnresolved(source.Path);
        clone.AudiobookId = source.AudiobookId;
        clone.Size = source.Size;
        clone.DurationSeconds = source.DurationSeconds;
        clone.Format = source.Format;
        clone.Container = source.Container;
        clone.Codec = source.Codec;
        clone.Bitrate = source.Bitrate;
        clone.SampleRate = source.SampleRate;
        clone.Channels = source.Channels;
        clone.Source = source.Source;
        if (!string.IsNullOrWhiteSpace(source.PhysicalObjectIdentity)
            && source.PhysicalIdentityObservedAtUtc.HasValue)
        {
            clone.ApplyPhysicalObjectIdentity(
                source.PhysicalObjectIdentity,
                source.PhysicalIdentityObservedAtUtc.Value);
        }

        return clone;
    }
}
