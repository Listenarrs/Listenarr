using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class EfMoveExecutionStore
{
    public Task UpdateSourceEntryProofAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        string relativePath,
        string sourcePhysicalObjectIdentity,
        string? sha256,
        DateTime lastWriteTimeUtc,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            "persist markerless source-entry proof",
            async () =>
            {
                EnsureLeaseTokenProvided(jobId, leaseToken);
                ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
                ArgumentNullException.ThrowIfNull(sourcePhysicalObjectIdentity);
                var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
                await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
                var entry = await db.MoveJobEntries
                    .Include(candidate => candidate.MoveJob)
                    .SingleOrDefaultAsync(
                        candidate => candidate.MoveJobId == jobId
                            && candidate.RelativePath == relativePath,
                        cancellationToken);
                if (entry == null
                    || entry.MoveJob.Status != MoveJobStatus.Running
                    || !string.Equals(
                        entry.MoveJob.LeaseOwner,
                        leaseToken.Owner,
                        StringComparison.Ordinal)
                    || entry.MoveJob.LeaseGeneration != leaseToken.Generation
                    || entry.MoveJob.LeaseExpiresAt == null
                    || entry.MoveJob.LeaseExpiresAt <= nowUtc)
                {
                    throw new MoveLeaseLostException(jobId, leaseToken.Generation);
                }

                if (entry.EntryType == MoveJobEntryType.File
                    && sha256 != null)
                {
                    if (sha256.Length != 64
                        || !sha256.All(Uri.IsHexDigit))
                    {
                        throw new ArgumentException(
                            "A markerless source-file proof contains an invalid SHA-256 digest.",
                            nameof(sha256));
                    }

                    sha256 = sha256.ToUpperInvariant();
                }
                else if (entry.EntryType != MoveJobEntryType.File
                    && sha256 != null)
                {
                    throw new ArgumentException(
                        "A markerless source-directory proof cannot contain a SHA-256 digest.",
                        nameof(sha256));
                }

                if (!string.IsNullOrWhiteSpace(entry.Sha256)
                    && sha256 != null
                    && !string.Equals(
                        entry.Sha256,
                        sha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new MoveNeedsAttentionException(
                        "The source entry content changed before markerless execution.");
                }

                if (sourcePhysicalObjectIdentity == string.Empty)
                {
                    if (!db.Database.IsRelational())
                    {
                        entry.MoveJob.ForceCopyAndRetainSource = true;
                        entry.MoveJob.SourceCleanupMode = MoveSourceCleanupMode.RetainSource;
                    }
                    else
                    {
                        var retained = await db.MoveJobs.Where(job => job.Id == jobId
                            && job.Status == MoveJobStatus.Running
                            && job.LeaseOwner == leaseToken.Owner
                            && job.LeaseGeneration == leaseToken.Generation
                            && job.LeaseExpiresAt != null && job.LeaseExpiresAt > nowUtc)
                            .ExecuteUpdateAsync(updates => updates
                                .SetProperty(job => job.ForceCopyAndRetainSource, true)
                                .SetProperty(job => job.SourceCleanupMode, MoveSourceCleanupMode.RetainSource),
                                cancellationToken);
                        if (retained != 1) throw new MoveLeaseLostException(jobId, leaseToken.Generation);
                    }
                }
                var observedIdentity = entry.SourcePhysicalObjectIdentity;
                var observedSha256 = entry.Sha256;
                var observedLastWriteTimeUtc = entry.LastWriteTimeUtc;
                // The observed identity is diagnostic only and may change after
                // a remount. Content proof remains durable operation evidence.
                var desiredIdentity = sourcePhysicalObjectIdentity;
                var desiredSha256 = observedSha256 ?? sha256;
                if (!db.Database.IsRelational())
                {
                    entry.SourcePhysicalObjectIdentity = desiredIdentity;
                    entry.Sha256 = desiredSha256;
                    entry.LastWriteTimeUtc = lastWriteTimeUtc;
                    entry.MoveJob.UpdatedAt = nowUtc;
                    await db.SaveChangesAsync(cancellationToken);
                    return;
                }

                db.Entry(entry).State = EntityState.Detached;
                db.Entry(entry.MoveJob).State = EntityState.Detached;
                var affected = await db.MoveJobEntries
                    .Where(candidate => candidate.MoveJobId == jobId
                        && candidate.RelativePath == relativePath
                        && candidate.SourcePhysicalObjectIdentity == observedIdentity
                        && candidate.Sha256 == observedSha256
                        && candidate.LastWriteTimeUtc == observedLastWriteTimeUtc
                        && candidate.MoveJob.Status == MoveJobStatus.Running
                        && candidate.MoveJob.LeaseOwner == leaseToken.Owner
                        && candidate.MoveJob.LeaseGeneration == leaseToken.Generation
                        && candidate.MoveJob.LeaseExpiresAt != null
                        && candidate.MoveJob.LeaseExpiresAt > nowUtc)
                    .ExecuteUpdateAsync(
                        updates => updates
                            .SetProperty(
                                candidate => candidate.SourcePhysicalObjectIdentity,
                                desiredIdentity)
                            .SetProperty(candidate => candidate.Sha256, desiredSha256)
                            .SetProperty(
                                candidate => candidate.LastWriteTimeUtc,
                                lastWriteTimeUtc),
                        cancellationToken);
                if (affected != 1)
                {
                    throw new MoveLeaseLostException(jobId, leaseToken.Generation);
                }

                _ = await db.MoveJobs
                    .Where(candidate => candidate.Id == jobId
                        && candidate.Status == MoveJobStatus.Running
                        && candidate.LeaseOwner == leaseToken.Owner
                        && candidate.LeaseGeneration == leaseToken.Generation
                        && candidate.LeaseExpiresAt != null
                        && candidate.LeaseExpiresAt > nowUtc)
                    .ExecuteUpdateAsync(
                        updates => updates.SetProperty(
                            candidate => candidate.UpdatedAt,
                            nowUtc),
                        cancellationToken);
            },
            cancellationToken);
}
