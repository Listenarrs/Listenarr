using Microsoft.EntityFrameworkCore;
using Listenarr.Domain.Audiobooks.Enumerations;
using Listenarr.Infrastructure.Persistence;

namespace Listenarr.Infrastructure.FileSystem;

internal sealed record CompatibilityFilePublicationClaim(
    Guid OperationId,
    FileAction RequestedAction,
    string SourcePath,
    string DestinationPath,
    long SourceLength,
    string SourceSha256,
    bool IsCompanionFile);

internal sealed class CompatibilityFilePublicationJournalStore(
    IDbContextFactory<ListenArrDbContext> dbContextFactory,
    TimeProvider timeProvider)
{
    public CompatibilityFilePublicationJournal? Get(Guid operationId) =>
        GetAsync(operationId, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

    public async Task<CompatibilityFilePublicationJournal?> GetAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        await using var context = await dbContextFactory.CreateDbContextAsync(
            cancellationToken);
        return await context.CompatibilityFilePublicationJournals
            .AsNoTracking()
            .SingleOrDefaultAsync(
                journal => journal.OperationId == operationId,
                cancellationToken);
    }

    public async Task<CompatibilityFilePublicationJournal> GetOrCreateAsync(
        CompatibilityFilePublicationClaim claim,
        CancellationToken cancellationToken)
    {
        var existing = await GetAsync(claim.OperationId, cancellationToken);
        if (existing != null)
        {
            ValidateClaim(existing, claim);
            return existing;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var journal = new CompatibilityFilePublicationJournal
        {
            OperationId = claim.OperationId,
            RequestedAction = claim.RequestedAction,
            EffectiveAction = FileAction.Copy,
            SourceDisposition = CompatibilitySourceDisposition.Retained,
            SourcePath = Path.GetFullPath(claim.SourcePath),
            DestinationPath = Path.GetFullPath(claim.DestinationPath),
            SourceLength = claim.SourceLength,
            SourceSha256 = claim.SourceSha256,
            IsCompanionFile = claim.IsCompanionFile,
            CreatedAt = now,
            UpdatedAt = now
        };

        await using var context = await dbContextFactory.CreateDbContextAsync(
            cancellationToken);
        context.CompatibilityFilePublicationJournals.Add(journal);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return journal;
        }
        catch (UniqueConstraintViolationException)
        {
            var raced = await GetAsync(claim.OperationId, cancellationToken)
                ?? throw new InvalidOperationException(
                    "The compatibility publication claim raced but could not be reloaded.");
            ValidateClaim(raced, claim);
            return raced;
        }
    }

    public CompatibilityFilePublicationJournal Advance(
        Guid operationId,
        CompatibilityFilePublicationState state,
        long? targetLength = null,
        string? targetSha256 = null,
        int? audiobookId = null,
        string? error = null) =>
        AdvanceAsync(
            operationId,
            state,
            targetLength,
            targetSha256,
            audiobookId,
            error,
            CancellationToken.None).GetAwaiter().GetResult();

    public async Task<CompatibilityFilePublicationJournal> AdvanceAsync(
        Guid operationId,
        CompatibilityFilePublicationState state,
        long? targetLength,
        string? targetSha256,
        int? audiobookId,
        string? error,
        CancellationToken cancellationToken)
    {
        await using var context = await dbContextFactory.CreateDbContextAsync(
            cancellationToken);
        var journal = await context.CompatibilityFilePublicationJournals
            .SingleOrDefaultAsync(
                candidate => candidate.OperationId == operationId,
                cancellationToken)
            ?? throw new InvalidOperationException(
                "The compatibility publication journal no longer exists.");

        if (journal.ProtocolVersion != CompatibilityFilePublicationProtocol.Current)
        {
            throw new InvalidOperationException(
                "The compatibility publication journal protocol is unsupported.");
        }
        if (journal.State == CompatibilityFilePublicationState.NeedsAttention
            && state != CompatibilityFilePublicationState.NeedsAttention)
        {
            throw new InvalidOperationException(
                "A compatibility publication requiring attention cannot advance.");
        }
        if (state != CompatibilityFilePublicationState.NeedsAttention
            && state < journal.State)
        {
            throw new InvalidOperationException(
                "A compatibility publication cannot move to an earlier state.");
        }

        journal.State = state;
        journal.TargetLength = targetLength ?? journal.TargetLength;
        journal.TargetSha256 = targetSha256 ?? journal.TargetSha256;
        journal.AudiobookId = audiobookId ?? journal.AudiobookId;
        journal.Error = error;
        journal.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        await context.SaveChangesAsync(cancellationToken);
        return journal;
    }

    private static void ValidateClaim(
        CompatibilityFilePublicationJournal journal,
        CompatibilityFilePublicationClaim claim)
    {
        if (journal.ProtocolVersion != CompatibilityFilePublicationProtocol.Current
            || journal.RequestedAction != claim.RequestedAction
            || !string.Equals(
                journal.SourcePath,
                Path.GetFullPath(claim.SourcePath),
                StringComparison.Ordinal)
            || !string.Equals(
                journal.DestinationPath,
                Path.GetFullPath(claim.DestinationPath),
                StringComparison.Ordinal)
            || journal.SourceLength != claim.SourceLength
            || journal.IsCompanionFile != claim.IsCompanionFile
            || !string.Equals(
                journal.SourceSha256,
                claim.SourceSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The compatibility publication operation ID is already bound to another claim.");
        }
    }
}
