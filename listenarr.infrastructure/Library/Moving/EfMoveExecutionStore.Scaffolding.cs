using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class EfMoveExecutionStore
{
    public Task<IReadOnlyList<MoveJobCreatedDirectory>> GetCreatedDirectoriesAsync(
        Guid jobId,
        CancellationToken cancellationToken) =>
        ExecuteAsync<IReadOnlyList<MoveJobCreatedDirectory>>(
            "load move-created target directories",
            async () =>
            {
                await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
                return await db.MoveJobCreatedDirectories
                    .AsNoTracking()
                    .Where(directory => directory.MoveJobId == jobId)
                    .OrderBy(directory => directory.Id)
                    .ToListAsync(cancellationToken);
            },
            cancellationToken);

    public Task PersistCreatedDirectoriesAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        IReadOnlyCollection<string> paths,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            "persist move-created target directories",
            async () =>
            {
                if (paths.Count == 0)
                {
                    return;
                }

                EnsureLeaseTokenProvided(jobId, leaseToken);
                var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
                await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
                await using var transaction = db.Database.IsRelational()
                    ? await db.Database.BeginTransactionAsync(cancellationToken)
                    : null;
                if (!await IsLeaseActiveAsync(db, jobId, leaseToken, nowUtc, cancellationToken))
                {
                    throw new MoveLeaseLostException(jobId, leaseToken.Generation);
                }

                var existing = await db.MoveJobCreatedDirectories
                    .Where(directory => directory.MoveJobId == jobId)
                    .Select(directory => directory.Path)
                    .ToListAsync(cancellationToken);
                foreach (var path in paths.Except(existing, StringComparer.Ordinal))
                {
                    db.MoveJobCreatedDirectories.Add(new MoveJobCreatedDirectory
                    {
                        MoveJobId = jobId,
                        Path = path,
                        State = MoveCreatedDirectoryState.Planned
                    });
                }

                await db.SaveChangesAsync(cancellationToken);
                if (transaction != null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await transaction.CommitAsync(CancellationToken.None);
                }
            },
            cancellationToken);

    public Task UpdateCreatedDirectoryStateAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        string path,
        MoveCreatedDirectoryState state,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            "persist move-created directory state",
            async () =>
            {
                EnsureLeaseTokenProvided(jobId, leaseToken);
                var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
                await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
                var directory = await db.MoveJobCreatedDirectories
                    .Include(candidate => candidate.MoveJob)
                    .SingleOrDefaultAsync(
                        candidate => candidate.MoveJobId == jobId
                            && candidate.Path == path,
                        cancellationToken);
                if (directory == null
                    || directory.MoveJob.Status != MoveJobStatus.Running
                    || !string.Equals(
                        directory.MoveJob.LeaseOwner,
                        leaseToken.Owner,
                        StringComparison.Ordinal)
                    || directory.MoveJob.LeaseGeneration != leaseToken.Generation
                    || directory.MoveJob.LeaseExpiresAt == null
                    || directory.MoveJob.LeaseExpiresAt <= nowUtc)
                {
                    throw new MoveLeaseLostException(jobId, leaseToken.Generation);
                }

                var observedState = directory.State;
                var desiredState = AdvanceCreatedDirectoryState(observedState, state);
                if (!db.Database.IsRelational())
                {
                    directory.State = desiredState;
                    await db.SaveChangesAsync(cancellationToken);
                    return;
                }

                db.Entry(directory).State = EntityState.Detached;
                db.Entry(directory.MoveJob).State = EntityState.Detached;
                var affected = await db.MoveJobCreatedDirectories
                    .Where(candidate => candidate.MoveJobId == jobId
                        && candidate.Path == path
                        && candidate.State == observedState
                        && candidate.MoveJob.Status == MoveJobStatus.Running
                        && candidate.MoveJob.LeaseOwner == leaseToken.Owner
                        && candidate.MoveJob.LeaseGeneration == leaseToken.Generation
                        && candidate.MoveJob.LeaseExpiresAt != null
                        && candidate.MoveJob.LeaseExpiresAt > nowUtc)
                    .ExecuteUpdateAsync(
                        updates => updates.SetProperty(
                            candidate => candidate.State,
                            desiredState),
                        cancellationToken);
                if (affected != 1)
                {
                    throw new MoveLeaseLostException(jobId, leaseToken.Generation);
                }
            },
            cancellationToken);
}
