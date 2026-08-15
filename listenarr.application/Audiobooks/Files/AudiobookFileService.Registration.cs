using Listenarr.Domain.Common;

namespace Listenarr.Application.Audiobooks.Files;

public partial class AudiobookFileService
{
    private sealed class AudiobookBasePathCommitContext
    {
        public AudiobookBasePathMutation? Mutation { get; set; }
    }

    private sealed record BasePathRegistrationOutcome(
        bool Success,
        AudiobookBasePathMutation? Mutation);

    public Task<bool> RegisterPublishedGenerationAsync(
        Audiobook audiobook,
        AudiobookFileOwnershipCheckResult initialOwnership,
        IAudiobookFileRegistrationLease registrationLease,
        string? source = "scan",
        CancellationToken cancellationToken = default) =>
        RegisterPublishedGenerationCoreAsync(
            audiobook,
            initialOwnership,
            registrationLease,
            authoritativeBasePath: null,
            source,
            cancellationToken);

    public Task<bool> RegisterPublishedGenerationWithBasePathAsync(
        Audiobook audiobook,
        AudiobookFileOwnershipCheckResult initialOwnership,
        IAudiobookFileRegistrationLease registrationLease,
        string authoritativeBasePath,
        string? source = "scan",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authoritativeBasePath);
        return RegisterPublishedGenerationCoreAsync(
            audiobook,
            initialOwnership,
            registrationLease,
            FileUtils.NormalizeStoredPath(authoritativeBasePath),
            source,
            cancellationToken);
    }

    private async Task<bool> RegisterPublishedGenerationCoreAsync(
        Audiobook audiobook,
        AudiobookFileOwnershipCheckResult initialOwnership,
        IAudiobookFileRegistrationLease registrationLease,
        string? authoritativeBasePath,
        string? source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audiobook);
        ArgumentNullException.ThrowIfNull(registrationLease);
        if (!registrationLease.MatchesCurrentPublication()
            || !registrationLease.PrepareCleanupRecovery(audiobook.Id))
        {
            return false;
        }

        var ownership = initialOwnership;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            switch (ownership.Outcome)
            {
                case AudiobookFileOwnershipCheckOutcome.Available:
                    {
                        var registration = authoritativeBasePath == null
                            ? new BasePathRegistrationOutcome(
                                await EnsureAudiobookFileAsync(
                                    audiobook,
                                    registrationLease,
                                    source,
                                    cancellationToken),
                                null)
                            : await EnsureAudiobookFileWithBasePathAsync(
                                audiobook,
                                registrationLease,
                                authoritativeBasePath,
                                source,
                                cancellationToken);
                        if (registration.Success)
                        {
                            ApplyCommittedBasePath(audiobook, registration.Mutation);
                            if (registrationLease.MatchesCurrentPublication())
                            {
                                return CompleteRegisteredPublication(
                                    registrationLease);
                            }

                            await RollbackPublishedGenerationIfStaleAsync(
                                audiobook,
                                registrationLease,
                                registration.Mutation);
                            return false;
                        }

                        ownership = await CheckAudiobookFileOwnershipAsync(
                            audiobook,
                            registrationLease.PublicPath,
                            authoritativeBasePath
                                ?? Path.GetDirectoryName(registrationLease.PublicPath),
                            cancellationToken);
                        continue;
                    }

                case AudiobookFileOwnershipCheckOutcome.AlreadyOwnedByAudiobook:
                    {
                        var existingFile = ownership.ExistingFile;
                        if (existingFile == null)
                        {
                            return false;
                        }

                        if (string.Equals(
                                existingFile.PhysicalObjectIdentity,
                                registrationLease.PhysicalObjectIdentity,
                                StringComparison.Ordinal))
                        {
                            var basePathCommit = authoritativeBasePath == null
                                ? new BasePathRegistrationOutcome(true, null)
                                : await ApplyAuthoritativeBasePathAsync(
                                    audiobook.Id,
                                    authoritativeBasePath,
                                    cancellationToken);
                            if (!basePathCommit.Success)
                            {
                                return false;
                            }

                            ApplyCommittedBasePath(audiobook, basePathCommit.Mutation);
                            if (registrationLease.MatchesCurrentPublication())
                            {
                                return CompleteRegisteredPublication(
                                    registrationLease);
                            }

                            await RollbackPublishedGenerationIfStaleAsync(
                                audiobook,
                                registrationLease,
                                basePathCommit.Mutation);
                            return false;
                        }

                        var refresh = authoritativeBasePath == null
                            ? new BasePathRegistrationOutcome(
                                await RefreshPhysicalGenerationAsync(
                                    audiobook,
                                    existingFile.Id,
                                    existingFile.PhysicalObjectIdentity,
                                    registrationLease,
                                    source,
                                    cancellationToken),
                                null)
                            : await RefreshPhysicalGenerationWithBasePathAsync(
                                audiobook,
                                existingFile.Id,
                                existingFile.PhysicalObjectIdentity,
                                registrationLease,
                                authoritativeBasePath,
                                source,
                                cancellationToken);
                        if (!refresh.Success)
                        {
                            return false;
                        }

                        ApplyCommittedBasePath(audiobook, refresh.Mutation);
                        if (registrationLease.MatchesCurrentPublication())
                        {
                            return CompleteRegisteredPublication(
                                registrationLease);
                        }

                        await RollbackPublishedGenerationIfStaleAsync(
                            audiobook,
                            registrationLease,
                            refresh.Mutation);
                        return false;
                    }

                default:
                    return false;
            }
        }

        return false;
    }

    private static bool CompleteRegisteredPublication(
        IAudiobookFileRegistrationLease registrationLease) =>
        registrationLease.CompletePublication() is
            RegistrationPublicationCompletion.Completed or
            RegistrationPublicationCompletion.CommittedCleanupPending;

    public Task RollbackPublishedGenerationIfStaleAsync(
        Audiobook audiobook,
        IAudiobookFileRegistrationLease registrationLease) =>
        RollbackPublishedGenerationIfStaleAsync(
            audiobook,
            registrationLease,
            basePathMutation: null);

    private async Task RollbackPublishedGenerationIfStaleAsync(
        Audiobook audiobook,
        IAudiobookFileRegistrationLease registrationLease,
        AudiobookBasePathMutation? basePathMutation)
    {
        ArgumentNullException.ThrowIfNull(audiobook);
        ArgumentNullException.ThrowIfNull(registrationLease);
        if (registrationLease.MatchesCurrentPublication())
        {
            return;
        }

        var ownership = await CheckAudiobookFileOwnershipAsync(
            audiobook,
            registrationLease.PublicPath,
            basePathMutation?.ResultingBasePath
                ?? Path.GetDirectoryName(registrationLease.PublicPath),
            CancellationToken.None);
        var existingFile = ownership.Outcome
                == AudiobookFileOwnershipCheckOutcome.AlreadyOwnedByAudiobook
            ? ownership.ExistingFile
            : null;
        if (existingFile == null
            || !string.Equals(
                existingFile.PhysicalObjectIdentity,
                registrationLease.PhysicalObjectIdentity,
                StringComparison.Ordinal))
        {
            return;
        }

        var rolledBack = await filesystemMutationCoordinator.ExecuteExclusiveAsync(
            globalToken => audiobookOperationCoordinator.ExecuteExclusiveAsync(
                audiobook.Id,
                _ => DeletePhysicalGenerationClaimCoreAsync(
                    existingFile.Id,
                    audiobook.Id,
                    existingFile.Path,
                    registrationLease.PhysicalObjectIdentity,
                    basePathMutation),
                globalToken),
            CancellationToken.None);
        if (!rolledBack)
        {
            throw new InvalidOperationException(
                "A stale imported physical-generation claim could not be rolled back.");
        }

        if (basePathMutation != null)
        {
            audiobook.BasePath = basePathMutation.ExpectedCurrentBasePath;
        }
    }

    private Task<BasePathRegistrationOutcome> ApplyAuthoritativeBasePathAsync(
        int audiobookId,
        string authoritativeBasePath,
        CancellationToken cancellationToken) =>
        filesystemMutationCoordinator.ExecuteExclusiveAsync(
            globalToken => audiobookOperationCoordinator.ExecuteExclusiveAsync(
                audiobookId,
                async token =>
                {
                    var current = await audiobookRepository.GetByIdSnapshotAsync(
                        audiobookId,
                        token);
                    if (current == null)
                    {
                        return new BasePathRegistrationOutcome(false, null);
                    }

                    if (string.Equals(
                            current.BasePath,
                            authoritativeBasePath,
                            StringComparison.Ordinal))
                    {
                        return new BasePathRegistrationOutcome(true, null);
                    }

                    var mutation = new AudiobookBasePathMutation(
                        audiobookId,
                        current.BasePath,
                        authoritativeBasePath);
                    var applied = await audiobookFileRepository.ApplyBasePathAsync(
                        mutation,
                        token);
                    return new BasePathRegistrationOutcome(
                        applied,
                        applied ? mutation : null);
                },
                globalToken),
            cancellationToken);

    private static void ApplyCommittedBasePath(
        Audiobook audiobook,
        AudiobookBasePathMutation? mutation)
    {
        if (mutation != null)
        {
            audiobook.BasePath = mutation.ResultingBasePath;
        }
    }
}
