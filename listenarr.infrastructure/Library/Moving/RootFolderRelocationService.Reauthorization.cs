using Listenarr.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationService
{
    internal Action? AfterLegacyTargetAuthorizationCommitForTest
    {
        get;
        set;
    }

    public async Task<RootFolderPathChangeResult> ReauthorizeLegacyTargetAsync(
        Guid relocationId,
        string confirmedTargetPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmedTargetPath);
        var result = await _mutationCoordinator.ExecuteExclusiveAsync(
            token => ExecuteWithAllAudiobookLocksAsync(
                async lockedToken =>
                {
                    await ReauthorizeLegacyTargetCoreAsync(
                        relocationId,
                        confirmedTargetPath,
                        lockedToken);
                    AfterLegacyTargetAuthorizationCommitForTest?.Invoke();
                    return await RetryCoreAsync(
                        relocationId,
                        CancellationToken.None);
                },
                token),
            cancellationToken);
        await BroadcastAsync(result, cancellationToken);
        return result;
    }

    private async Task ReauthorizeLegacyTargetCoreAsync(
        Guid relocationId,
        string confirmedTargetPath,
        CancellationToken cancellationToken)
    {
        await using var db =
            await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);
        var relocation = await db.RootFolderRelocations
            .Include(candidate => candidate.MoveJobs)
                .ThenInclude(job => job.Entries)
            .SingleOrDefaultAsync(
                candidate => candidate.Id == relocationId,
                cancellationToken)
            ?? throw new KeyNotFoundException(
                "Root folder relocation not found");
        if (relocation.Status != RootFolderRelocationStatus.NeedsAttention
            || relocation.TargetIdentityEnrollmentState
                != TargetIdentityEnrollmentState.LegacyUnenrolled)
        {
            throw new InvalidOperationException(
                "Only a legacy-unenrolled relocation needing attention can be reauthorized.");
        }
        if (!string.Equals(
                confirmedTargetPath,
                relocation.TargetPath,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The confirmed target path must exactly match the pending relocation target.",
                nameof(confirmedTargetPath));
        }

        var targetPathAvailable =
            FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                relocation.TargetPath,
                out var canonicalTargetPath,
                out var targetPathReason);
        var sourcePathAvailable =
            FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                relocation.SourcePath,
                out var canonicalSourcePath,
                out var sourcePathReason);
        if (!targetPathAvailable || !sourcePathAvailable)
        {
            throw new InvalidOperationException(
                $"The relocation paths are unavailable for reauthorization: {targetPathReason}{sourcePathReason}");
        }

        var targetResolution = await semanticsResolver.ResolveAsync(
            canonicalTargetPath,
            relocation.TargetCaseSensitivityMode,
            cancellationToken);
        if (targetResolution.State != PathIdentityState.Valid)
        {
            throw new InvalidOperationException(
                targetResolution.Reason
                    ?? "The relocation target filesystem identity is unavailable.");
        }
        var sourceResolution = await semanticsResolver.ResolveAsync(
            canonicalSourcePath,
            relocation.SourceCaseSensitivityMode,
            cancellationToken);
        if (sourceResolution.State != PathIdentityState.Valid)
        {
            throw new InvalidOperationException(
                sourceResolution.Reason
                    ?? "The relocation source filesystem identity is unavailable.");
        }

        using var target =
            PinnedDirectoryCreation.OpenPinnedBoundary(canonicalTargetPath);
        if (!target.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "The relocation target changed while it was being reauthorized.");
        }

        ValidateLegacyReauthorizationEvidence(
            relocation,
            sourceResolution.Semantics,
            targetResolution.Semantics);
        cancellationToken.ThrowIfCancellationRequested();
        var targetNativeIdentity = target.GetDirectoryObjectIdentity();
        var targetObjectIdentity = new DirectoryObjectIdentityResolution(
            ManagedDirectoryIdentity.CurrentVersion,
            ManagedDirectoryIdentity.CreateMarkerless(targetNativeIdentity),
            null);
        if (!target.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "The relocation target changed while its physical identity was captured.");
        }

        foreach (var job in relocation.MoveJobs)
        {
            job.Entries.Add(
                MoveManifestIdentity.CreateTargetBoundaryAuthorization(
                    targetObjectIdentity.Version!.Value,
                    targetObjectIdentity.Value!));
        }

        relocation.TargetDirectoryObjectIdentityVersion =
            targetObjectIdentity.Version;
        relocation.TargetDirectoryObjectIdentity = targetObjectIdentity.Value;
        relocation.TargetDirectoryObjectIdentityUnavailableReason =
            targetObjectIdentity.UnavailableReason;
        relocation.TargetIdentityEnrollmentState =
            TargetIdentityEnrollmentState.Authorized;
        relocation.Error = null;
        relocation.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
    }

    private static void ValidateLegacyReauthorizationEvidence(
        RootFolderRelocation relocation,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics targetSemantics)
    {
        foreach (var job in relocation.MoveJobs)
        {
            if (MoveManifestIdentity.TryGetTargetBoundaryAuthorization(
                    job.Entries,
                    out _,
                    out _))
            {
                throw new InvalidOperationException(
                    "A legacy move job already contains target-boundary authorization evidence and cannot be reauthorized automatically.");
            }

            if (string.IsNullOrWhiteSpace(job.SourcePath)
                || string.IsNullOrWhiteSpace(job.RequestedPath)
                || !job.TryGetSourceIdentity(out var sourceIdentity)
                || !job.TryGetTargetIdentity(out var targetIdentity)
                || job.Entries.Count == 0
                || job.Entries.All(entry =>
                    entry.EntryType != MoveJobEntryType.File))
            {
                throw new InvalidOperationException(
                    "A persisted move job lacks authoritative endpoint or manifest evidence.");
            }

            sourceIdentity.ValidateForPath(job.SourcePath);
            targetIdentity.ValidateForPath(job.RequestedPath);
            var sourceRelationship =
                FileSystemPathIdentity.EvaluateBoundaryConflict(
                    job.SourcePath,
                    sourceIdentity.Semantics,
                    relocation.SourcePath,
                    sourceSemantics);
            if (sourceRelationship is not (
                FileSystemPathBoundaryConflict.Equivalent
                    or FileSystemPathBoundaryConflict.FirstInsideSecond))
            {
                throw new InvalidOperationException(
                    "A persisted move source is outside the relocation source boundary.");
            }

            var targetRelationship =
                FileSystemPathIdentity.EvaluateBoundaryConflict(
                    job.RequestedPath,
                    targetIdentity.Semantics,
                    relocation.TargetPath,
                    targetSemantics);
            if (targetRelationship is not (
                FileSystemPathBoundaryConflict.Equivalent
                    or FileSystemPathBoundaryConflict.FirstInsideSecond))
            {
                throw new InvalidOperationException(
                    "A persisted move target is outside the relocation target boundary.");
            }
        }
    }
}
