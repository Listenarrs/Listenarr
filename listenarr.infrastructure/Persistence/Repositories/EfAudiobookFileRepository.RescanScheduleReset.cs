namespace Listenarr.Infrastructure.Persistence.Repositories;

// A replaced physical generation is a new file to the metadata rescan: it is
// due now and starts every ladder again. The last outcome is left as the
// record of the previous generation. Restore, path-identity reconcile and
// base-path moves are not a new generation and keep the schedule.
public partial class EfAudiobookFileRepository
{
    private DateTime RescanResetDueUtc() => _timeProvider.GetUtcNow().UtcDateTime;

    // Marks each property modified even when the tracked value already equals
    // the reset: a stale tracked copy can hold a value the store no longer has.
    private void ResetRescanSchedule(AudiobookFile target, DateTime dueUtc)
    {
        var entry = _db.Entry(target);
        Write(nameof(AudiobookFile.MetadataRescanDueUtc), dueUtc);
        Write(nameof(AudiobookFile.MetadataRescanFailureCount), 0);
        Write(nameof(AudiobookFile.MetadataRescanUnavailableCount), 0);
        Write(nameof(AudiobookFile.MetadataRescanBlockedCount), 0);

        void Write(string propertyName, object value)
        {
            var property = entry.Property(propertyName);
            property.CurrentValue = value;
            property.IsModified = true;
        }
    }

    // ExecuteUpdate bypasses tracking, so a tracked copy takes the reset as its
    // current and original values, as SynchronizeTrackedRescanSchedule does.
    private void SynchronizeTrackedRescanScheduleReset(int fileId, DateTime dueUtc)
    {
        var trackedEntry = _db.ChangeTracker.Entries<AudiobookFile>()
            .FirstOrDefault(entry => entry.Entity.Id == fileId);
        if (trackedEntry == null)
        {
            return;
        }

        Synchronize(nameof(AudiobookFile.MetadataRescanDueUtc), dueUtc);
        Synchronize(nameof(AudiobookFile.MetadataRescanFailureCount), 0);
        Synchronize(nameof(AudiobookFile.MetadataRescanUnavailableCount), 0);
        Synchronize(nameof(AudiobookFile.MetadataRescanBlockedCount), 0);

        void Synchronize(string propertyName, object value)
        {
            var property = trackedEntry.Property(propertyName);
            property.CurrentValue = value;
            property.OriginalValue = value;
            property.IsModified = false;
        }
    }
}
