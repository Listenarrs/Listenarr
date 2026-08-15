using System.Data;
using Listenarr.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence.Repositories;

public partial class EfAudiobookFileRepository
{
    public async Task<AudiobookFileClaimResult> ClaimWithBasePathAsync(
        AudiobookFile file,
        AudiobookBasePathMutation basePathMutation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ValidateBasePathMutation(file.AudiobookId, basePathMutation);
        if (file.PathIdentityState != PathIdentityState.Valid
            || string.IsNullOrWhiteSpace(file.PathIdentityLookupKey)
            || string.IsNullOrWhiteSpace(file.PathOwnershipKey))
        {
            return new AudiobookFileClaimResult(
                AudiobookFileClaimOutcome.IdentityUnavailable,
                Reason: file.PathIdentityReason
                    ?? "A valid filesystem identity is required before claiming an audiobook file.");
        }

        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)
            : null;
        var audiobook = await _db.Audiobooks
            .SingleOrDefaultAsync(candidate => candidate.Id == file.AudiobookId, ct);
        if (audiobook == null
            || !string.Equals(
                audiobook.BasePath,
                basePathMutation.ExpectedCurrentBasePath,
                StringComparison.Ordinal))
        {
            return new AudiobookFileClaimResult(
                AudiobookFileClaimOutcome.IdentityConflict,
                Reason: "The audiobook BasePath changed before the file registration could be committed.");
        }

        var ownership = await CheckOwnershipAsync(
            file.AudiobookId,
            file.Id == 0 ? null : file.Id,
            ToIdentity(file),
            ct);
        var existingResult = ToClaimResult(ownership);
        if (existingResult != null)
        {
            return existingResult;
        }

        audiobook.BasePath = basePathMutation.ResultingBasePath;
        _db.AudiobookFiles.Add(file);
        try
        {
            if (transaction == null)
            {
                var completionToken =
                    RequestCancellationBoundary.EnterNonCancelablePhase(ct);
                await _db.SaveChangesAsync(completionToken);
            }
            else
            {
                await _db.SaveChangesAsync(ct);
                var completionToken =
                    RequestCancellationBoundary.EnterNonCancelablePhase(ct);
                await transaction.CommitAsync(completionToken);
            }

            return new AudiobookFileClaimResult(
                AudiobookFileClaimOutcome.Created,
                file);
        }
        catch (UniqueConstraintViolationException)
        {
            if (transaction != null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }

            _db.Entry(file).State = EntityState.Detached;
            _db.Entry(audiobook).State = EntityState.Detached;
            return new AudiobookFileClaimResult(
                AudiobookFileClaimOutcome.IdentityConflict,
                Reason: "The audiobook file ownership claim conflicted with another persistence operation.");
        }
    }

    public async Task<bool> ApplyBasePathAsync(
        AudiobookBasePathMutation basePathMutation,
        CancellationToken ct = default)
    {
        ValidateBasePathMutation(basePathMutation.AudiobookId, basePathMutation);
        if (_db.Database.IsRelational())
        {
            var completionToken =
                RequestCancellationBoundary.EnterNonCancelablePhase(ct);
            var updated = await _db.Audiobooks
                .Where(candidate =>
                    candidate.Id == basePathMutation.AudiobookId
                    && candidate.BasePath == basePathMutation.ExpectedCurrentBasePath)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        candidate => candidate.BasePath,
                        basePathMutation.ResultingBasePath),
                    completionToken);
            if (updated == 1)
            {
                SynchronizeTrackedBasePath(basePathMutation);
                return true;
            }

            return await _db.Audiobooks
                .AsNoTracking()
                .AnyAsync(
                    candidate =>
                        candidate.Id == basePathMutation.AudiobookId
                        && candidate.BasePath == basePathMutation.ResultingBasePath,
                    ct);
        }

        var audiobook = await _db.Audiobooks
            .SingleOrDefaultAsync(
                candidate => candidate.Id == basePathMutation.AudiobookId,
                ct);
        if (audiobook == null)
        {
            return false;
        }
        if (string.Equals(
                audiobook.BasePath,
                basePathMutation.ResultingBasePath,
                StringComparison.Ordinal))
        {
            return true;
        }
        if (!string.Equals(
                audiobook.BasePath,
                basePathMutation.ExpectedCurrentBasePath,
                StringComparison.Ordinal))
        {
            return false;
        }

        audiobook.BasePath = basePathMutation.ResultingBasePath;
        var nonCancelableToken =
            RequestCancellationBoundary.EnterNonCancelablePhase(ct);
        await _db.SaveChangesAsync(nonCancelableToken);
        return true;
    }

    public async Task<bool> ReplacePhysicalGenerationWithBasePathAsync(
        int fileId,
        int audiobookId,
        string? expectedPath,
        string? expectedPhysicalObjectIdentity,
        AudiobookFile replacement,
        AudiobookBasePathMutation basePathMutation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        ValidateBasePathMutation(audiobookId, basePathMutation);

        if (!_db.Database.IsRelational())
        {
            var audiobook = await _db.Audiobooks
                .SingleOrDefaultAsync(candidate => candidate.Id == audiobookId, ct);
            var existing = await _db.AudiobookFiles.SingleOrDefaultAsync(
                candidate =>
                    candidate.Id == fileId
                    && candidate.AudiobookId == audiobookId
                    && candidate.Path == expectedPath
                    && candidate.PhysicalObjectIdentity == expectedPhysicalObjectIdentity,
                ct);
            if (audiobook == null
                || existing == null
                || !string.Equals(
                    audiobook.BasePath,
                    basePathMutation.ExpectedCurrentBasePath,
                    StringComparison.Ordinal))
            {
                return false;
            }

            audiobook.BasePath = basePathMutation.ResultingBasePath;
            ApplyPhysicalGeneration(existing, replacement);
            var nonRelationalCompletionToken =
                RequestCancellationBoundary.EnterNonCancelablePhase(ct);
            await _db.SaveChangesAsync(nonRelationalCompletionToken);
            return true;
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            ct);
        var basePathUpdated = await _db.Audiobooks
            .Where(candidate =>
                candidate.Id == audiobookId
                && candidate.BasePath == basePathMutation.ExpectedCurrentBasePath)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(
                    candidate => candidate.BasePath,
                    basePathMutation.ResultingBasePath),
                ct);
        if (basePathUpdated != 1)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return false;
        }

        var fileUpdated = await _db.AudiobookFiles
            .Where(candidate =>
                candidate.Id == fileId
                && candidate.AudiobookId == audiobookId
                && candidate.Path == expectedPath
                && candidate.PhysicalObjectIdentity == expectedPhysicalObjectIdentity)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(candidate => candidate.Size, replacement.Size)
                    .SetProperty(
                        candidate => candidate.DurationSeconds,
                        replacement.DurationSeconds)
                    .SetProperty(candidate => candidate.Format, replacement.Format)
                    .SetProperty(candidate => candidate.Container, replacement.Container)
                    .SetProperty(candidate => candidate.Codec, replacement.Codec)
                    .SetProperty(candidate => candidate.Bitrate, replacement.Bitrate)
                    .SetProperty(candidate => candidate.SampleRate, replacement.SampleRate)
                    .SetProperty(candidate => candidate.Channels, replacement.Channels)
                    .SetProperty(candidate => candidate.Source, replacement.Source)
                    .SetProperty(
                        candidate => candidate.PhysicalObjectIdentity,
                        replacement.PhysicalObjectIdentity)
                    .SetProperty(
                        candidate => candidate.PhysicalIdentityVersion,
                        replacement.PhysicalIdentityVersion)
                    .SetProperty(
                        candidate => candidate.PhysicalIdentityObservedAtUtc,
                        replacement.PhysicalIdentityObservedAtUtc),
                ct);
        if (fileUpdated != 1)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return false;
        }

        var completionToken =
            RequestCancellationBoundary.EnterNonCancelablePhase(ct);
        await transaction.CommitAsync(completionToken);
        SynchronizeTrackedBasePath(basePathMutation);
        SynchronizeTrackedPhysicalGeneration(fileId, replacement);
        return true;
    }

    public async Task<bool> DeletePhysicalGenerationWithBasePathAsync(
        int fileId,
        int audiobookId,
        string? expectedPath,
        string? expectedPhysicalObjectIdentity,
        AudiobookBasePathMutation basePathMutation,
        CancellationToken ct = default)
    {
        ValidateBasePathMutation(audiobookId, basePathMutation);
        if (!_db.Database.IsRelational())
        {
            var audiobook = await _db.Audiobooks
                .SingleOrDefaultAsync(candidate => candidate.Id == audiobookId, ct);
            var file = await _db.AudiobookFiles.SingleOrDefaultAsync(
                candidate =>
                    candidate.Id == fileId
                    && candidate.AudiobookId == audiobookId
                    && candidate.Path == expectedPath
                    && candidate.PhysicalObjectIdentity == expectedPhysicalObjectIdentity,
                ct);
            if (audiobook == null
                || file == null
                || !string.Equals(
                    audiobook.BasePath,
                    basePathMutation.ExpectedCurrentBasePath,
                    StringComparison.Ordinal))
            {
                return false;
            }

            audiobook.BasePath = basePathMutation.ResultingBasePath;
            _db.AudiobookFiles.Remove(file);
            var nonRelationalCompletionToken =
                RequestCancellationBoundary.EnterNonCancelablePhase(ct);
            await _db.SaveChangesAsync(nonRelationalCompletionToken);
            return true;
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            ct);
        var deleted = await _db.AudiobookFiles
            .Where(candidate =>
                candidate.Id == fileId
                && candidate.AudiobookId == audiobookId
                && candidate.Path == expectedPath
                && candidate.PhysicalObjectIdentity == expectedPhysicalObjectIdentity)
            .ExecuteDeleteAsync(ct);
        if (deleted != 1)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return false;
        }

        var basePathUpdated = await _db.Audiobooks
            .Where(candidate =>
                candidate.Id == audiobookId
                && candidate.BasePath == basePathMutation.ExpectedCurrentBasePath)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(
                    candidate => candidate.BasePath,
                    basePathMutation.ResultingBasePath),
                ct);
        if (basePathUpdated != 1)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return false;
        }

        var completionToken =
            RequestCancellationBoundary.EnterNonCancelablePhase(ct);
        await transaction.CommitAsync(completionToken);
        var trackedFile = _db.ChangeTracker.Entries<AudiobookFile>()
            .FirstOrDefault(entry => entry.Entity.Id == fileId);
        if (trackedFile != null)
        {
            trackedFile.State = EntityState.Detached;
        }
        SynchronizeTrackedBasePath(basePathMutation);
        return true;
    }

    private static void ValidateBasePathMutation(
        int audiobookId,
        AudiobookBasePathMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        if (mutation.AudiobookId != audiobookId)
        {
            throw new ArgumentException(
                "The BasePath mutation must target the owning audiobook.",
                nameof(mutation));
        }
    }

    private void SynchronizeTrackedBasePath(AudiobookBasePathMutation mutation)
    {
        var tracked = _db.ChangeTracker.Entries<Audiobook>()
            .FirstOrDefault(entry => entry.Entity.Id == mutation.AudiobookId);
        if (tracked == null)
        {
            return;
        }

        var property = tracked.Property(nameof(Audiobook.BasePath));
        property.CurrentValue = mutation.ResultingBasePath;
        property.OriginalValue = mutation.ResultingBasePath;
        property.IsModified = false;
    }
}
