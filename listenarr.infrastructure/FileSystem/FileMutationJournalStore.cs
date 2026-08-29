using Listenarr.Domain.Audiobooks.Enumerations;
using Listenarr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.FileSystem;

internal sealed record FileMutationJournalClaim(
    Guid OperationId,
    FileAction Action,
    string SourcePath,
    string DestinationPath,
    string SourceParentDirectoryObjectIdentity,
    string DestinationParentDirectoryObjectIdentity,
    string SourcePhysicalObjectIdentity,
    long SourceLength,
    string? SourceSha256,
    int? AudiobookId = null,
    int? AudiobookFileId = null);

internal interface IFileMutationJournalStore
{
    Task<FileMutationJournal> GetOrCreateAsync(
        FileMutationJournalClaim claim,
        CancellationToken cancellationToken);

    Task<FileMutationJournal?> GetAsync(
        Guid operationId,
        CancellationToken cancellationToken);

    Task<FileMutationJournal> SetSourceSha256Async(
        Guid operationId,
        string expectedSourcePhysicalObjectIdentity,
        long expectedSourceLength,
        string sourceSha256,
        CancellationToken cancellationToken);

    FileMutationJournal? Get(Guid operationId);

    Task<FileMutationJournal> AdvanceAsync(
        Guid operationId,
        FileMutationJournalState state,
        string? targetPhysicalObjectIdentity,
        int? audiobookId,
        string? error,
        CancellationToken cancellationToken);

    Task<RegistrationPublicationMatchOutcome> AdvanceWithCommitValidationAsync(
        Guid operationId,
        FileMutationJournalState state,
        string? targetPhysicalObjectIdentity,
        int? audiobookId,
        string? error,
        Func<CancellationToken, Task<RegistrationPublicationMatchOutcome>> validateAsync,
        CancellationToken cancellationToken);

    FileMutationJournal Advance(
        Guid operationId,
        FileMutationJournalState state,
        string? targetPhysicalObjectIdentity,
        int? audiobookId,
        string? error);

    RegistrationPublicationMatchOutcome AdvanceWithCommitValidation(
        Guid operationId,
        FileMutationJournalState state,
        string? targetPhysicalObjectIdentity,
        int? audiobookId,
        string? error,
        Func<RegistrationPublicationMatchOutcome> validate);
}

internal sealed partial class EfFileMutationJournalStore(
    IDbContextFactory<ListenArrDbContext> dbContextFactory,
    TimeProvider timeProvider,
    IFileSystemSemanticsResolver? semanticsResolver = null) : IFileMutationJournalStore
{
    private readonly IFileSystemSemanticsResolver _semanticsResolver =
        semanticsResolver ?? new FileSystemSemanticsResolver();

    internal Func<Task>? AfterAdvanceLoadedForTestAsync { get; set; }

    public async Task<FileMutationJournal> GetOrCreateAsync(
        FileMutationJournalClaim claim,
        CancellationToken cancellationToken)
    {
        ValidateClaim(claim);
        var canonicalSource = Path.GetFullPath(claim.SourcePath);
        var canonicalDestination = Path.GetFullPath(claim.DestinationPath);
        await using var db =
            await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.FileMutationJournals
            .SingleOrDefaultAsync(
                journal => journal.OperationId == claim.OperationId,
                cancellationToken);
        if (existing != null)
        {
            await ValidateIdentityAsync(
                existing,
                claim,
                canonicalSource,
                canonicalDestination,
                cancellationToken);
            return existing;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var journal = new FileMutationJournal
        {
            OperationId = claim.OperationId,
            ProtocolVersion = FileMutationProtocol.Current,
            Action = claim.Action,
            SourcePath = canonicalSource,
            DestinationPath = canonicalDestination,
            SourceParentDirectoryObjectIdentity =
                claim.SourceParentDirectoryObjectIdentity,
            DestinationParentDirectoryObjectIdentity =
                claim.DestinationParentDirectoryObjectIdentity,
            SourcePhysicalObjectIdentity = claim.SourcePhysicalObjectIdentity,
            SourceLength = claim.SourceLength,
            SourceSha256 = claim.SourceSha256,
            AudiobookId = claim.AudiobookId,
            AudiobookFileId = claim.AudiobookFileId,
            State = FileMutationJournalState.Planned,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.FileMutationJournals.Add(journal);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return journal;
        }
        catch (UniqueConstraintViolationException)
        {
            db.Entry(journal).State = EntityState.Detached;
            existing = await db.FileMutationJournals
                .SingleAsync(
                    candidate => candidate.OperationId == claim.OperationId,
                    cancellationToken);
            await ValidateIdentityAsync(
                existing,
                claim,
                canonicalSource,
                canonicalDestination,
                cancellationToken);
            return existing;
        }
    }

    public async Task<FileMutationJournal?> GetAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException(
                "A file-mutation operation ID must not be empty.",
                nameof(operationId));
        }

        await using var db =
            await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.FileMutationJournals
            .AsNoTracking()
            .SingleOrDefaultAsync(
                journal => journal.OperationId == operationId,
                cancellationToken);
    }

    public FileMutationJournal? Get(Guid operationId)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException(
                "A file-mutation operation ID must not be empty.",
                nameof(operationId));
        }

        using var db = dbContextFactory.CreateDbContext();
        return db.FileMutationJournals
            .AsNoTracking()
            .SingleOrDefault(journal => journal.OperationId == operationId);
    }

    public async Task<FileMutationJournal> AdvanceAsync(
        Guid operationId,
        FileMutationJournalState state,
        string? targetPhysicalObjectIdentity,
        int? audiobookId,
        string? error,
        CancellationToken cancellationToken)
    {
        ValidateAdvanceRequest(
            operationId,
            state,
            targetPhysicalObjectIdentity,
            audiobookId);
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
            var expected = CaptureMutableState(journal);
            ApplyAdvance(
                journal,
                state,
                targetPhysicalObjectIdentity,
                audiobookId,
                error);
            if (AfterAdvanceLoadedForTestAsync != null)
            {
                await AfterAdvanceLoadedForTestAsync();
            }

            if (await TryPersistAdvanceAsync(
                    db,
                    journal,
                    expected,
                    cancellationToken))
            {
                return journal;
            }
        }

        throw new InvalidOperationException(
            "The file-mutation journal changed concurrently too many times.");
    }

    public async Task<RegistrationPublicationMatchOutcome>
        AdvanceWithCommitValidationAsync(
            Guid operationId,
            FileMutationJournalState state,
            string? targetPhysicalObjectIdentity,
            int? audiobookId,
            string? error,
            Func<CancellationToken, Task<RegistrationPublicationMatchOutcome>> validateAsync,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(validateAsync);
        ValidateAdvanceRequest(
            operationId,
            state,
            targetPhysicalObjectIdentity,
            audiobookId);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await using var db =
                await dbContextFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(cancellationToken)
                : null;
            var journal = await db.FileMutationJournals
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    candidate => candidate.OperationId == operationId,
                    cancellationToken)
                ?? throw new InvalidOperationException(
                    "The durable file-mutation journal does not exist.");
            var expected = CaptureMutableState(journal);
            ApplyAdvance(
                journal,
                state,
                targetPhysicalObjectIdentity,
                audiobookId,
                error);
            if (AfterAdvanceLoadedForTestAsync != null)
            {
                await AfterAdvanceLoadedForTestAsync();
            }

            if (!await TryPersistAdvanceAsync(
                    db,
                    journal,
                    expected,
                    cancellationToken))
            {
                if (transaction != null)
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                }
                continue;
            }

            RegistrationPublicationMatchOutcome validation;
            try
            {
                validation = await validateAsync(CancellationToken.None);
            }
            catch
            {
                if (transaction != null)
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                }
                else
                {
                    await RestoreMutableStateAsync(
                        db,
                        operationId,
                        expected,
                        CancellationToken.None);
                }
                throw;
            }

            if (validation != RegistrationPublicationMatchOutcome.Match)
            {
                if (transaction != null)
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                }
                else
                {
                    await RestoreMutableStateAsync(
                        db,
                        operationId,
                        expected,
                        CancellationToken.None);
                }
                return validation;
            }

            if (transaction != null)
            {
                await transaction.CommitAsync(CancellationToken.None);
            }
            return RegistrationPublicationMatchOutcome.Match;
        }

        throw new InvalidOperationException(
            "The file-mutation journal changed concurrently too many times.");
    }

    public FileMutationJournal Advance(
        Guid operationId,
        FileMutationJournalState state,
        string? targetPhysicalObjectIdentity,
        int? audiobookId,
        string? error)
    {
        ValidateAdvanceRequest(
            operationId,
            state,
            targetPhysicalObjectIdentity,
            audiobookId);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var db = dbContextFactory.CreateDbContext();
            var journal = db.FileMutationJournals
                .AsNoTracking()
                .SingleOrDefault(candidate => candidate.OperationId == operationId)
                ?? throw new InvalidOperationException(
                    "The durable file-mutation journal does not exist.");
            var expected = CaptureMutableState(journal);
            ApplyAdvance(
                journal,
                state,
                targetPhysicalObjectIdentity,
                audiobookId,
                error);
            if (TryPersistAdvance(db, journal, expected))
            {
                return journal;
            }
        }

        throw new InvalidOperationException(
            "The file-mutation journal changed concurrently too many times.");
    }

    public RegistrationPublicationMatchOutcome AdvanceWithCommitValidation(
        Guid operationId,
        FileMutationJournalState state,
        string? targetPhysicalObjectIdentity,
        int? audiobookId,
        string? error,
        Func<RegistrationPublicationMatchOutcome> validate)
    {
        ArgumentNullException.ThrowIfNull(validate);
        ValidateAdvanceRequest(
            operationId,
            state,
            targetPhysicalObjectIdentity,
            audiobookId);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var db = dbContextFactory.CreateDbContext();
            using var transaction = db.Database.IsRelational()
                ? db.Database.BeginTransaction()
                : null;
            var journal = db.FileMutationJournals
                .AsNoTracking()
                .SingleOrDefault(candidate => candidate.OperationId == operationId)
                ?? throw new InvalidOperationException(
                    "The durable file-mutation journal does not exist.");
            var expected = CaptureMutableState(journal);
            ApplyAdvance(
                journal,
                state,
                targetPhysicalObjectIdentity,
                audiobookId,
                error);
            if (!TryPersistAdvance(db, journal, expected))
            {
                transaction?.Rollback();
                continue;
            }

            RegistrationPublicationMatchOutcome validation;
            try
            {
                validation = validate();
            }
            catch
            {
                if (transaction != null)
                {
                    transaction.Rollback();
                }
                else
                {
                    RestoreMutableState(db, operationId, expected);
                }
                throw;
            }

            if (validation != RegistrationPublicationMatchOutcome.Match)
            {
                if (transaction != null)
                {
                    transaction.Rollback();
                }
                else
                {
                    RestoreMutableState(db, operationId, expected);
                }
                return validation;
            }

            transaction?.Commit();
            return RegistrationPublicationMatchOutcome.Match;
        }

        throw new InvalidOperationException(
            "The file-mutation journal changed concurrently too many times.");
    }

}
