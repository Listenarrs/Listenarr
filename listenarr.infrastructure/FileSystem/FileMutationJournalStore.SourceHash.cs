using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.FileSystem;

internal sealed partial class EfFileMutationJournalStore
{
    public async Task<FileMutationJournal> SetSourceSha256Async(
        Guid operationId,
        string expectedSourcePhysicalObjectIdentity,
        long expectedSourceLength,
        string sourceSha256,
        CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException(
                "A file-mutation operation ID must not be empty.",
                nameof(operationId));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(
            expectedSourcePhysicalObjectIdentity);
        if (expectedSourceLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedSourceLength));
        }
        ValidateSha256(sourceSha256, nameof(sourceSha256));

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await using var db =
                await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var journal = await db.FileMutationJournals
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    candidate => candidate.OperationId == operationId,
                    cancellationToken)
                ?? throw new InvalidOperationException(
                    "The durable file-mutation journal does not exist.");
            if (!string.Equals(
                    journal.SourcePhysicalObjectIdentity,
                    expectedSourcePhysicalObjectIdentity,
                    StringComparison.Ordinal)
                || journal.SourceLength != expectedSourceLength)
            {
                throw new InvalidOperationException(
                    "The file-mutation source generation changed before content hashing.");
            }
            if (!string.IsNullOrWhiteSpace(journal.SourceSha256))
            {
                if (!string.Equals(
                        journal.SourceSha256,
                        sourceSha256,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The file-mutation source content changed after hashing.");
                }

                return journal;
            }
            if (journal.State != FileMutationJournalState.Planned)
            {
                throw new InvalidOperationException(
                    "A source content hash can only be added before markerless publication.");
            }

            var now = timeProvider.GetUtcNow().UtcDateTime;
            if (!db.Database.IsRelational())
            {
                var tracked = await db.FileMutationJournals.SingleAsync(
                    candidate => candidate.OperationId == operationId,
                    cancellationToken);
                if (tracked.SourceSha256 == null
                    && tracked.State == FileMutationJournalState.Planned
                    && string.Equals(
                        tracked.SourcePhysicalObjectIdentity,
                        expectedSourcePhysicalObjectIdentity,
                        StringComparison.Ordinal)
                    && tracked.SourceLength == expectedSourceLength)
                {
                    tracked.SourceSha256 = sourceSha256;
                    tracked.UpdatedAt = now;
                    await db.SaveChangesAsync(cancellationToken);
                    return tracked;
                }

                continue;
            }

            var affected = await db.FileMutationJournals
                .Where(candidate => candidate.OperationId == operationId
                    && candidate.State == FileMutationJournalState.Planned
                    && candidate.SourceSha256 == null
                    && candidate.SourcePhysicalObjectIdentity
                        == expectedSourcePhysicalObjectIdentity
                    && candidate.SourceLength == expectedSourceLength)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(
                            candidate => candidate.SourceSha256,
                            sourceSha256)
                        .SetProperty(
                            candidate => candidate.UpdatedAt,
                            now),
                    cancellationToken);
            if (affected == 1)
            {
                journal.SourceSha256 = sourceSha256;
                journal.UpdatedAt = now;
                return journal;
            }
        }

        throw new InvalidOperationException(
            "The file-mutation source hash changed concurrently too many times.");
    }

    private static void ValidateSha256(string value, string parameterName)
    {
        if (value.Length != 64
            || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "A file-mutation SHA-256 proof must contain 64 hexadecimal characters.",
                parameterName);
        }
    }
}
