using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class EfMoveExecutionStore
{
    public Task<MoveJobPhase> GetJobPhaseAsync(
        Guid jobId,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            "load the move job phase",
            async () =>
            {
                await using var db =
                    await dbContextFactory.CreateDbContextAsync(cancellationToken);
                return await db.MoveJobs
                    .Where(job => job.Id == jobId)
                    .Select(job => job.Phase)
                    .SingleAsync(cancellationToken);
            },
            cancellationToken);

    public Task<int> EnsureCurrentOrUpgradeLegacyExecutionProtocolAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            "upgrade the move execution protocol",
            async () =>
            {
                EnsureLeaseTokenProvided(jobId, leaseToken);
                var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
                await using var db =
                    await dbContextFactory.CreateDbContextAsync(cancellationToken);
                var job = await db.MoveJobs.SingleOrDefaultAsync(
                    candidate => candidate.Id == jobId,
                    cancellationToken);
                if (job == null
                    || !string.Equals(
                        job.LeaseOwner,
                        leaseToken.Owner,
                        StringComparison.Ordinal)
                    || job.LeaseGeneration != leaseToken.Generation
                    || !job.LeaseExpiresAt.HasValue
                    || job.LeaseExpiresAt.Value <= nowUtc)
                {
                    throw new MoveLeaseLostException(
                        jobId,
                        leaseToken.Generation);
                }

                if (MoveExecutionProtocol.IsCurrent(
                        job.ExecutionProtocolVersion))
                {
                    return job.ExecutionProtocolVersion;
                }

                if (job.ExecutionProtocolVersion is
                    MoveExecutionProtocol.TargetBoundaryMarkerlessDatabaseState
                    or MoveExecutionProtocol.MarkerlessDatabaseState)
                {
                    job.ExecutionProtocolVersion =
                        MoveExecutionProtocol.Current;
                    job.IdentityKeyVersion = MoveIdentityProtocol.Current;
                    job.UpdatedAt = nowUtc;
                    await db.SaveChangesAsync(cancellationToken);
                    return job.ExecutionProtocolVersion;
                }

                return job.ExecutionProtocolVersion;
            },
            cancellationToken);
}
