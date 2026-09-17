using Listenarr.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence.Repositories;

public partial class EfAudiobookFileRepository
{
    public async Task<bool> RefreshMetadataAsync(
        AudiobookFileMetadataRefreshSnapshot expectedFile,
        AudioMetadata metadata,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expectedFile);
        ArgumentNullException.ThrowIfNull(metadata);
        if (expectedFile.PathState.State != PathIdentityState.Valid
            || string.IsNullOrWhiteSpace(expectedFile.PathState.OwnershipKey))
        {
            return false;
        }

        var query = _db.AudiobookFiles.Where(file =>
            file.Id == expectedFile.FileId
            && file.AudiobookId == expectedFile.AudiobookId
            && file.Path == expectedFile.PathState.StoredPath
            && file.CanonicalPath == expectedFile.PathState.CanonicalPath
            && file.PathSyntax == expectedFile.PathState.Syntax
            && file.PathCaseSensitivity == expectedFile.PathState.CaseSensitivity
            && file.PathCaseSensitivityMode == expectedFile.PathState.RequestedMode
            && file.PathIdentityBoundary == expectedFile.PathState.BoundaryPath
            && file.PathIdentityLookupKey == expectedFile.PathState.LookupKey
            && file.PathOwnershipKey == expectedFile.PathState.OwnershipKey
            && file.PathIdentityVersion == expectedFile.PathState.Version
            && file.PathIdentityState == expectedFile.PathState.State
            && file.PhysicalObjectIdentity == expectedFile.PhysicalObjectIdentity
            && file.PhysicalIdentityVersion == expectedFile.PhysicalIdentityVersion
            && file.PhysicalIdentityObservedAtUtc == expectedFile.PhysicalIdentityObservedAtUtc
            && file.Audiobook != null
            && file.Audiobook.BasePath == expectedFile.BasePath);
        var duration = metadata.Duration.TotalSeconds;
        if (_db.Database.IsRelational())
        {
            var completionToken = RequestCancellationBoundary.EnterNonCancelablePhase(ct);
            var updated = await query.ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(file => file.DurationSeconds, file =>
                        duration > 0 ? duration : file.DurationSeconds)
                    .SetProperty(file => file.Format, file =>
                        !string.IsNullOrEmpty(metadata.Format) ? metadata.Format : file.Format)
                    .SetProperty(file => file.Container, file =>
                        !string.IsNullOrEmpty(metadata.Container) ? metadata.Container : file.Container)
                    .SetProperty(file => file.Codec, file =>
                        !string.IsNullOrEmpty(metadata.Codec) ? metadata.Codec : file.Codec)
                    .SetProperty(file => file.Bitrate, file =>
                        metadata.BitRate > 0 ? metadata.BitRate : file.Bitrate)
                    .SetProperty(file => file.SampleRate, file =>
                        metadata.SampleRate > 0 ? metadata.SampleRate : file.SampleRate)
                    .SetProperty(file => file.Channels, file =>
                        metadata.Channels > 0 ? metadata.Channels : file.Channels),
                completionToken);
            if (updated != 1)
            {
                return false;
            }

            // ExecuteUpdate bypasses tracking. Discard only this now-stale snapshot,
            // so later reads/saves cannot put its old metadata back.
            var tracked = _db.ChangeTracker.Entries<AudiobookFile>()
                .FirstOrDefault(entry => entry.Entity.Id == expectedFile.FileId);
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
        existing.DurationSeconds = duration > 0 ? duration : existing.DurationSeconds;
        existing.Format = !string.IsNullOrEmpty(metadata.Format) ? metadata.Format : existing.Format;
        existing.Container = !string.IsNullOrEmpty(metadata.Container) ? metadata.Container : existing.Container;
        existing.Codec = !string.IsNullOrEmpty(metadata.Codec) ? metadata.Codec : existing.Codec;
        existing.Bitrate = metadata.BitRate > 0 ? metadata.BitRate : existing.Bitrate;
        existing.SampleRate = metadata.SampleRate > 0 ? metadata.SampleRate : existing.SampleRate;
        existing.Channels = metadata.Channels > 0 ? metadata.Channels : existing.Channels;
        var nonRelationalCompletionToken = RequestCancellationBoundary.EnterNonCancelablePhase(ct);
        await _db.SaveChangesAsync(nonRelationalCompletionToken);
        return true;
    }
}
