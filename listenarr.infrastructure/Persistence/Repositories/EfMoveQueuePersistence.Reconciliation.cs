using System.Data.Common;
using Listenarr.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence.Repositories;

public sealed partial class EfMoveQueuePersistence
{
    public async Task ReconcileIdentityKeysAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(cancellationToken)
                : null;
            var activeJobs = await db.MoveJobs
                .Include(job => job.Relocation)
                .Include(job => job.Entries)
                .Where(job => job.Status == MoveJobStatus.Queued
                    || job.Status == MoveJobStatus.Running
                    || job.Status == MoveJobStatus.RetryScheduled)
                .ToListAsync(cancellationToken);

            // Release the unique active-key index while every active job is rebuilt under
            // the authoritative endpoint and tracked-file manifest identities.
            foreach (var job in activeJobs)
            {
                job.ActiveDeduplicationKey = null;
            }

            await db.SaveChangesAsync(cancellationToken);

            var resolvedJobs = new List<(MoveJob Job, string Key, PathIdentitySnapshot TargetIdentity)>();
            foreach (var job in activeJobs)
            {
                if (!MoveExecutionProtocol.IsCurrent(job.ExecutionProtocolVersion))
                {
                    MarkIdentityConflict(
                        job,
                        "This move job predates the durable database execution protocol and cannot resume filesystem mutation safely.");
                    continue;
                }
                if (job.Entries.Count == 0
                    || job.Entries.All(entry => entry.EntryType != MoveJobEntryType.File))
                {
                    MarkIdentityConflict(
                        job,
                        "The move job has no persisted tracked-file source manifest and cannot be reconciled safely.");
                    continue;
                }
                if (string.IsNullOrWhiteSpace(job.SourcePath)
                    || string.IsNullOrWhiteSpace(job.RequestedPath))
                {
                    MarkIdentityConflict(
                        job,
                        "The legacy move job has no complete source and target paths and cannot be reconciled safely.");
                    continue;
                }

                try
                {
                    var sourcePath = job.SourcePath;
                    if (job.TryGetSourceIdentity(out var persistedSourceIdentity))
                    {
                        if (!FileSystemPathIdentity.TryCanonicalizeStoredPathWithIdentityForHost(
                            sourcePath,
                            persistedSourceIdentity,
                            out sourcePath,
                            out var sourceReason))
                        {
                            throw new InvalidOperationException($"Source path cannot be reconciled: {sourceReason}");
                        }
                    }
                    else if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                        sourcePath,
                        out sourcePath,
                        out var sourceReason))
                    {
                        throw new InvalidOperationException($"Source path cannot be reconciled: {sourceReason}");
                    }

                    var targetPath = job.RequestedPath;
                    if (job.TryGetTargetIdentity(out var persistedTargetIdentity))
                    {
                        if (!FileSystemPathIdentity.TryCanonicalizeStoredPathWithIdentityForHost(
                            targetPath,
                            persistedTargetIdentity,
                            out targetPath,
                            out var targetReason))
                        {
                            throw new InvalidOperationException($"Target path cannot be reconciled: {targetReason}");
                        }
                    }
                    else if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                        targetPath,
                        out targetPath,
                        out var targetReason))
                    {
                        throw new InvalidOperationException($"Target path cannot be reconciled: {targetReason}");
                    }

                    var sourceIdentity = await ResolveJobIdentityAsync(
                        job,
                        sourcePath,
                        target: false,
                        cancellationToken);
                    var targetIdentity = await ResolveJobIdentityAsync(
                        job,
                        targetPath,
                        target: true,
                        cancellationToken);

                    // Persist endpoint rewrites only after both paths and identities have been
                    // validated. A foreign or relative legacy endpoint must remain intact as
                    // durable operator evidence instead of being partially rewritten.
                    job.SourcePath = sourcePath;
                    job.RequestedPath = targetPath;
                    job.SetSourceIdentity(sourceIdentity);
                    job.SetTargetIdentity(targetIdentity);
                    job.IdentityKeyVersion = MoveManifestIdentity.Version;
                    resolvedJobs.Add((
                        job,
                        MoveManifestIdentity.CreateReconciliationKey(
                            job.AudiobookId,
                            sourcePath,
                            sourceIdentity,
                            targetPath,
                            targetIdentity,
                            job.Entries),
                        targetIdentity));
                }
                catch (Exception exception) when (exception is
                    ArgumentException or InvalidOperationException or NotSupportedException or PathTooLongException)
                {
                    MarkIdentityConflict(
                        job,
                        $"Move path identity could not be reconciled: {exception.Message}");
                }
            }

            var activeJobIds = activeJobs.Select(job => job.Id).ToList();
            var jobIdsWithManifestExecutionState = await db.MoveJobEntries
                .Where(entry => activeJobIds.Contains(entry.MoveJobId)
                    && entry.RelativePath != string.Empty
                    && (entry.CopyState != MoveJobEntryCopyState.Pending
                        || entry.CleanupState != MoveJobEntryCleanupState.Pending))
                .Select(entry => entry.MoveJobId)
                .Distinct()
                .ToListAsync(cancellationToken);
            var jobIdsWithScaffolding = await db.MoveJobCreatedDirectories
                .Where(directory => activeJobIds.Contains(directory.MoveJobId))
                .Select(directory => directory.MoveJobId)
                .Distinct()
                .ToListAsync(cancellationToken);
            var manifestEvidence = jobIdsWithManifestExecutionState.ToHashSet();
            var scaffoldEvidence = jobIdsWithScaffolding.ToHashSet();

            foreach (var group in resolvedJobs.GroupBy(item => item.Key, StringComparer.Ordinal))
            {
                var candidates = group.ToList();
                var evidenceBearing = candidates
                    .Where(candidate => HasDurableExecutionEvidence(
                        candidate.Job,
                        manifestEvidence,
                        scaffoldEvidence))
                    .ToList();

                if (evidenceBearing.Count > 1)
                {
                    var conflictingIds = string.Join(", ", evidenceBearing.Select(item => item.Job.Id));
                    foreach (var candidate in candidates)
                    {
                        MarkIdentityConflict(
                            candidate.Job,
                            $"Multiple active move jobs own recovery evidence for the same destination: {conflictingIds}.");
                    }
                    continue;
                }

                if (evidenceBearing.Count == 0 && candidates.Count > 1)
                {
                    foreach (var candidate in candidates)
                    {
                        MarkIdentityConflict(
                            candidate.Job,
                            "Multiple active move jobs share one identity but no authoritative recovery owner can be proven.");
                    }
                    continue;
                }

                var canonical = evidenceBearing.Count == 1
                    ? evidenceBearing[0]
                    : candidates[0];
                var canonicalHasTargetAuthorization =
                    MoveManifestIdentity.TryGetTargetBoundaryAuthorization(
                        canonical.Job.Entries,
                        out _,
                        out _);
                if (canonicalHasTargetAuthorization)
                {
                    canonical.Job.ActiveDeduplicationKey = group.Key;
                    canonical.Job.IdentityKeyVersion = MoveManifestIdentity.Version;
                }
                else
                {
                    MarkIdentityConflict(
                        canonical.Job,
                        "The move job has no durable target-boundary physical-generation authorization and cannot be reconciled safely.");
                }

                foreach (var duplicate in candidates.Where(item => item.Job.Id != canonical.Job.Id))
                {
                    if (evidenceBearing.Any(item => item.Job.Id == duplicate.Job.Id))
                    {
                        MarkIdentityConflict(
                            canonical.Job,
                            $"Move job {duplicate.Job.Id} also owns recovery evidence for this destination.");
                        MarkIdentityConflict(
                            duplicate.Job,
                            $"Move job {canonical.Job.Id} also owns recovery evidence for this destination.");
                        continue;
                    }

                    duplicate.Job.Status = MoveJobStatus.Superseded;
                    duplicate.Job.Error = $"Superseded by move job {canonical.Job.Id} during identity-key reconciliation.";
                    duplicate.Job.IdentityKeyVersion = MoveManifestIdentity.Version;
                    duplicate.Job.ActiveDeduplicationKey = null;
                    duplicate.Job.LeaseOwner = null;
                    duplicate.Job.LeaseExpiresAt = null;
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            if (transaction != null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await transaction.CommitAsync(CancellationToken.None);
            }
        }
        catch (Exception exception) when (exception is DbException or DbUpdateException)
        {
            throw new PersistenceException("Failed to reconcile move job identity keys.", exception);
        }
    }

    private async Task<PathIdentitySnapshot> ResolveJobIdentityAsync(
        MoveJob job,
        string path,
        bool target,
        CancellationToken cancellationToken)
    {
        var hasPersistedIdentity = target
            ? job.TryGetTargetIdentity(out var persistedIdentity)
            : job.TryGetSourceIdentity(out persistedIdentity);
        if (hasPersistedIdentity)
        {
            persistedIdentity.ValidateForPath(path);
            if (persistedIdentity.RequestedMode == FileSystemCaseSensitivityMode.Auto)
            {
                var current = await semanticsResolver.ResolveAsync(
                    persistedIdentity.BoundaryPath,
                    FileSystemCaseSensitivityMode.Auto,
                    cancellationToken);
                if (current.State != PathIdentityState.Valid
                    || current.Semantics.Syntax != persistedIdentity.Syntax
                    || current.Semantics.CaseSensitivity != persistedIdentity.CaseSensitivity)
                {
                    throw new InvalidOperationException(
                        $"The persisted {(target ? "target" : "source")} filesystem identity changed.");
                }
            }

            return persistedIdentity;
        }

        var mode = target
            ? job.TargetCaseSensitivityMode
                ?? job.Relocation?.TargetCaseSensitivityMode
                ?? FileSystemCaseSensitivityMode.Auto
            : job.SourceCaseSensitivityMode
                ?? job.Relocation?.SourceCaseSensitivityMode
                ?? FileSystemCaseSensitivityMode.Auto;
        var resolution = await semanticsResolver.ResolveAsync(path, mode, cancellationToken);
        if (resolution.State != PathIdentityState.Valid)
        {
            throw new InvalidOperationException(
                resolution.Reason
                    ?? $"The {(target ? "target" : "source")} filesystem identity is unavailable.");
        }

        var configuredBoundary = target
            ? job.Relocation?.TargetPath
            : job.Relocation?.SourcePath;
        var boundary = !string.IsNullOrWhiteSpace(configuredBoundary)
            && FileSystemPathIdentity.IsSameOrInside(path, configuredBoundary, resolution.Semantics)
                ? configuredBoundary
                : resolution.BoundaryPath;
        return PathIdentitySnapshot.FromResolution(
            resolution.Semantics,
            mode,
            boundary,
            path);
    }

    private static void MarkIdentityConflict(MoveJob job, string error)
    {
        job.Status = MoveJobStatus.NeedsAttention;
        job.IdentityKeyVersion = MoveManifestIdentity.Version;
        job.ActiveDeduplicationKey = null;
        job.FailureKind = MoveFailureKind.Verification;
        job.Error = error;
        job.LeaseOwner = null;
        job.LeaseExpiresAt = null;
    }

}
