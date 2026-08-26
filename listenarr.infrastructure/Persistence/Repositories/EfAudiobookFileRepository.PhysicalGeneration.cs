using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence.Repositories;

public partial class EfAudiobookFileRepository
{
    public async Task<bool> ReplacePhysicalGenerationAsync(
        int fileId,
        int audiobookId,
        string? expectedPath,
        string? expectedPhysicalObjectIdentity,
        AudiobookFile replacement,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);

        var query = _db.AudiobookFiles.Where(candidate =>
            candidate.Id == fileId
            && candidate.AudiobookId == audiobookId
            && candidate.Path == expectedPath
            && candidate.PhysicalObjectIdentity == expectedPhysicalObjectIdentity);
        if (_db.Database.IsRelational())
        {
            var completionToken =
                RequestCancellationBoundary.EnterNonCancelablePhase(ct);
            var updated = await query.ExecuteUpdateAsync(
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
                completionToken);
            if (updated != 1)
            {
                return false;
            }

            SynchronizeTrackedPhysicalGeneration(fileId, replacement);
            return true;
        }

        var existing = await query.SingleOrDefaultAsync(ct);
        if (existing == null)
        {
            return false;
        }

        ApplyPhysicalGeneration(existing, replacement);
        var nonRelationalCompletionToken =
            RequestCancellationBoundary.EnterNonCancelablePhase(ct);
        await _db.SaveChangesAsync(nonRelationalCompletionToken);
        return true;
    }

    public async Task<bool> DeletePhysicalGenerationAsync(
        int fileId,
        int audiobookId,
        string? expectedPath,
        string? expectedPhysicalObjectIdentity,
        CancellationToken ct = default)
    {
        var query = _db.AudiobookFiles.Where(candidate =>
            candidate.Id == fileId
            && candidate.AudiobookId == audiobookId
            && candidate.Path == expectedPath
            && candidate.PhysicalObjectIdentity
                == expectedPhysicalObjectIdentity);
        if (_db.Database.IsRelational())
        {
            var completionToken =
                RequestCancellationBoundary.EnterNonCancelablePhase(ct);
            var deleted = await query.ExecuteDeleteAsync(completionToken);
            if (deleted != 1)
            {
                return false;
            }

            var tracked = _db.ChangeTracker.Entries<AudiobookFile>()
                .FirstOrDefault(entry => entry.Entity.Id == fileId);
            if (tracked != null)
            {
                tracked.State = EntityState.Detached;
            }

            return true;
        }

        var existing = await query.SingleOrDefaultAsync(ct);
        if (existing == null)
        {
            return false;
        }

        _db.AudiobookFiles.Remove(existing);
        var nonRelationalCompletionToken =
            RequestCancellationBoundary.EnterNonCancelablePhase(ct);
        await _db.SaveChangesAsync(nonRelationalCompletionToken);
        return true;
    }

    private void SynchronizeTrackedPhysicalGeneration(
        int fileId,
        AudiobookFile replacement)
    {
        var trackedEntry = _db.ChangeTracker.Entries<AudiobookFile>()
            .FirstOrDefault(entry => entry.Entity.Id == fileId);
        if (trackedEntry == null)
        {
            return;
        }

        Synchronize(nameof(AudiobookFile.Size), replacement.Size);
        Synchronize(
            nameof(AudiobookFile.DurationSeconds),
            replacement.DurationSeconds);
        Synchronize(nameof(AudiobookFile.Format), replacement.Format);
        Synchronize(nameof(AudiobookFile.Container), replacement.Container);
        Synchronize(nameof(AudiobookFile.Codec), replacement.Codec);
        Synchronize(nameof(AudiobookFile.Bitrate), replacement.Bitrate);
        Synchronize(nameof(AudiobookFile.SampleRate), replacement.SampleRate);
        Synchronize(nameof(AudiobookFile.Channels), replacement.Channels);
        Synchronize(nameof(AudiobookFile.Source), replacement.Source);
        Synchronize(
            nameof(AudiobookFile.PhysicalObjectIdentity),
            replacement.PhysicalObjectIdentity);
        Synchronize(
            nameof(AudiobookFile.PhysicalIdentityVersion),
            replacement.PhysicalIdentityVersion);
        Synchronize(
            nameof(AudiobookFile.PhysicalIdentityObservedAtUtc),
            replacement.PhysicalIdentityObservedAtUtc);

        void Synchronize(string propertyName, object? value)
        {
            var property = trackedEntry.Property(propertyName);
            property.CurrentValue = value;
            property.OriginalValue = value;
            property.IsModified = false;
        }
    }

    private static void ApplyPhysicalGeneration(
        AudiobookFile target,
        AudiobookFile source)
    {
        target.Size = source.Size;
        target.DurationSeconds = source.DurationSeconds;
        target.Format = source.Format;
        target.Container = source.Container;
        target.Codec = source.Codec;
        target.Bitrate = source.Bitrate;
        target.SampleRate = source.SampleRate;
        target.Channels = source.Channels;
        target.Source = source.Source;
        if (string.IsNullOrWhiteSpace(source.PhysicalObjectIdentity)
            || !source.PhysicalIdentityObservedAtUtc.HasValue)
        {
            target.ClearPhysicalObjectIdentity();
            return;
        }

        // The source row may have been materialized from the database, where
        // the UTC-by-contract observation time round-trips as Unspecified.
        target.ApplyPhysicalObjectIdentity(
            source.PhysicalObjectIdentity,
            DateTime.SpecifyKind(
                source.PhysicalIdentityObservedAtUtc.Value,
                DateTimeKind.Utc));
    }
}
