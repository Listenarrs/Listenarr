using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence;

public sealed class AudiobookDeletionIntentStore(
    IDbContextFactory<ListenArrDbContext> dbContextFactory,
    TimeProvider timeProvider) : IAudiobookDeletionIntentStore
{
    public async Task<AudiobookDeletionIntent> GetOrCreateAsync(
        int audiobookId,
        bool deleteFolder,
        CancellationToken cancellationToken = default)
    {
        if (audiobookId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(audiobookId));
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.AudiobookDeletionIntents
            .Where(intent => intent.AudiobookId == audiobookId
                && intent.State != AudiobookDeletionIntentState.Completed)
            .OrderByDescending(intent => intent.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (existing != null)
        {
            ValidateRetry(existing, deleteFolder);
            return existing;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var intent = new AudiobookDeletionIntent
        {
            Id = Guid.NewGuid(),
            AudiobookId = audiobookId,
            DeleteFolder = deleteFolder,
            State = AudiobookDeletionIntentState.Planned,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.AudiobookDeletionIntents.Add(intent);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return intent;
        }
        catch (UniqueConstraintViolationException)
        {
            db.Entry(intent).State = EntityState.Detached;
            existing = await db.AudiobookDeletionIntents
                .SingleAsync(candidate => candidate.AudiobookId == audiobookId
                    && candidate.State != AudiobookDeletionIntentState.Completed,
                    cancellationToken);
            ValidateRetry(existing, deleteFolder);
            return existing;
        }
    }

    public Task MarkFilesystemCleanupCompletedAsync(
        Guid intentId,
        CancellationToken cancellationToken = default) =>
        AdvanceAsync(
            intentId,
            AudiobookDeletionIntentState.FilesystemCleanupCompleted,
            error: null,
            cancellationToken);

    public Task MarkCompletedAsync(
        Guid intentId,
        CancellationToken cancellationToken = default) =>
        AdvanceAsync(
            intentId,
            AudiobookDeletionIntentState.Completed,
            error: null,
            cancellationToken);

    public async Task RecordErrorAsync(
        Guid intentId,
        string error,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        if (intentId == Guid.Empty)
        {
            throw new ArgumentException(
                "A deletion intent ID must not be empty.",
                nameof(intentId));
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var intent = await db.AudiobookDeletionIntents
            .SingleAsync(candidate => candidate.Id == intentId, cancellationToken);
        if (intent.State is AudiobookDeletionIntentState.Completed
            or AudiobookDeletionIntentState.NeedsAttention)
        {
            throw new InvalidOperationException(
                "A terminal audiobook deletion intent cannot record a retryable error.");
        }

        intent.Error = error;
        intent.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task MarkNeedsAttentionAsync(
        Guid intentId,
        string error,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        return AdvanceAsync(
            intentId,
            AudiobookDeletionIntentState.NeedsAttention,
            error,
            cancellationToken);
    }

    public async Task<IReadOnlyList<AudiobookDeletionIntent>> GetActiveAsync(
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.AudiobookDeletionIntents
            .AsNoTracking()
            .Where(intent => intent.State != AudiobookDeletionIntentState.Completed)
            .OrderBy(intent => intent.CreatedAt)
            .ThenBy(intent => intent.Id)
            .ToListAsync(cancellationToken);
    }

    private async Task AdvanceAsync(
        Guid intentId,
        AudiobookDeletionIntentState state,
        string? error,
        CancellationToken cancellationToken)
    {
        if (intentId == Guid.Empty)
        {
            throw new ArgumentException(
                "A deletion intent ID must not be empty.",
                nameof(intentId));
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var intent = await db.AudiobookDeletionIntents
            .SingleAsync(candidate => candidate.Id == intentId, cancellationToken);
        ValidateTransition(intent.State, state);

        intent.State = state;
        intent.Error = error;
        intent.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static void ValidateRetry(
        AudiobookDeletionIntent intent,
        bool deleteFolder)
    {
        if (intent.State == AudiobookDeletionIntentState.NeedsAttention)
        {
            throw new InvalidOperationException(
                "An earlier audiobook deletion requires operator attention before it can be retried.");
        }
        if (intent.DeleteFolder != deleteFolder)
        {
            throw new InvalidOperationException(
                "An active audiobook deletion was created with different folder-cleanup semantics.");
        }
    }

    private static void ValidateTransition(
        AudiobookDeletionIntentState current,
        AudiobookDeletionIntentState requested)
    {
        if (current == requested)
        {
            return;
        }
        if (current is AudiobookDeletionIntentState.Completed
            or AudiobookDeletionIntentState.NeedsAttention)
        {
            throw new InvalidOperationException(
                "A terminal audiobook deletion intent cannot be advanced.");
        }
        if (requested == AudiobookDeletionIntentState.NeedsAttention)
        {
            return;
        }
        if (current == AudiobookDeletionIntentState.Planned
            && requested == AudiobookDeletionIntentState.FilesystemCleanupCompleted)
        {
            return;
        }
        if (current == AudiobookDeletionIntentState.FilesystemCleanupCompleted
            && requested == AudiobookDeletionIntentState.Completed)
        {
            return;
        }

        throw new InvalidOperationException(
            "The requested audiobook deletion state transition skips or regresses durable recovery authority.");
    }
}
