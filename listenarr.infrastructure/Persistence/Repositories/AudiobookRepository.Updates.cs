using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence.Repositories;

public partial class AudiobookRepository
{
    public async Task<bool> TryUpdateBasePathAsync(
        int audiobookId,
        string expectedBasePath,
        string newBasePath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedBasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(newBasePath);

        if (_db.Database.IsRelational())
        {
            var affected = await _db.Audiobooks
                .Where(audiobook => audiobook.Id == audiobookId
                    && audiobook.BasePath == expectedBasePath)
                .ExecuteUpdateAsync(
                    updates => updates.SetProperty(
                        audiobook => audiobook.BasePath,
                        newBasePath),
                    ct);
            if (affected == 1)
            {
                SynchronizeTrackedBasePath(audiobookId, newBasePath);
                return true;
            }

            return false;
        }

        var currentBasePath = await _db.Audiobooks
            .AsNoTracking()
            .Where(audiobook => audiobook.Id == audiobookId)
            .Select(audiobook => audiobook.BasePath)
            .SingleOrDefaultAsync(ct);
        if (!string.Equals(currentBasePath, expectedBasePath, StringComparison.Ordinal))
        {
            return false;
        }

        var entry = _db.ChangeTracker.Entries<Audiobook>()
            .FirstOrDefault(candidate => candidate.Entity.Id == audiobookId);
        if (entry == null)
        {
            entry = _db.Attach(new Audiobook
            {
                Id = audiobookId,
                BasePath = expectedBasePath
            });
        }

        entry.Property(audiobook => audiobook.BasePath).CurrentValue = newBasePath;
        entry.Property(audiobook => audiobook.BasePath).IsModified = true;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> TryUpdateImageUrlAsync(
        int audiobookId,
        string? expectedImageUrl,
        string? newImageUrl,
        CancellationToken ct = default)
    {
        if (_db.Database.IsRelational())
        {
            var affected = await _db.Audiobooks
                .Where(audiobook => audiobook.Id == audiobookId
                    && audiobook.ImageUrl == expectedImageUrl)
                .ExecuteUpdateAsync(
                    updates => updates.SetProperty(
                        audiobook => audiobook.ImageUrl,
                        newImageUrl),
                    ct);
            if (affected != 1)
            {
                return false;
            }

            SynchronizeTrackedImageUrl(audiobookId, newImageUrl);
            return true;
        }

        var existing = await _db.Audiobooks
            .FirstOrDefaultAsync(
                audiobook => audiobook.Id == audiobookId,
                ct);
        if (existing == null
            || !string.Equals(
                existing.ImageUrl,
                expectedImageUrl,
                StringComparison.Ordinal))
        {
            return false;
        }

        existing.ImageUrl = newImageUrl;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> UpdateAsync(Audiobook audiobook)
    {
        ArgumentNullException.ThrowIfNull(audiobook);

        var entry = _db.Entry(audiobook);
        if (entry.State == EntityState.Detached)
        {
            var existing = await _db.Audiobooks.FirstOrDefaultAsync(candidate => candidate.Id == audiobook.Id);
            if (existing == null)
            {
                return false;
            }

            var preservedBasePath = existing.BasePath;
            var preservedFilePath = existing.FilePath;
            var preservedFileSize = existing.FileSize;
            var preservedImageUrl = existing.ImageUrl;
            _db.Entry(existing).CurrentValues.SetValues(audiobook);
            // A detached entity has no original-value snapshot, so any path-bearing value may
            // be stale. Path changes must use a tracked entity or the dedicated expected-source
            // rewrite contract.
            existing.BasePath = preservedBasePath;
            existing.FilePath = preservedFilePath;
            existing.FileSize = preservedFileSize;
            existing.ImageUrl = preservedImageUrl;
        }

        // Tracked entities retain EF's original-value snapshot, so SaveChanges writes only
        // properties the caller actually changed. Calling Update here would mark BasePath and
        // every other property modified, allowing an unrelated stale metadata save to undo a
        // completed move from another DbContext.
        await _db.SaveChangesAsync();
        return true;
    }

    private void SynchronizeTrackedImageUrl(
        int audiobookId,
        string? newImageUrl)
    {
        var trackedEntry = _db.ChangeTracker.Entries<Audiobook>()
            .FirstOrDefault(entry => entry.Entity.Id == audiobookId);
        if (trackedEntry == null)
        {
            return;
        }

        var property = trackedEntry.Property(audiobook => audiobook.ImageUrl);
        property.CurrentValue = newImageUrl;
        property.OriginalValue = newImageUrl;
        property.IsModified = false;
    }

    private void SynchronizeTrackedBasePath(int audiobookId, string newBasePath)
    {
        var trackedEntry = _db.ChangeTracker.Entries<Audiobook>()
            .FirstOrDefault(entry => entry.Entity.Id == audiobookId);
        if (trackedEntry == null)
        {
            return;
        }

        var property = trackedEntry.Property(audiobook => audiobook.BasePath);
        property.CurrentValue = newBasePath;
        property.OriginalValue = newBasePath;
        property.IsModified = false;
    }

    public async Task<bool> UpdateWithIdentifierReplaceAsync(
        Audiobook audiobook,
        List<AudiobookExternalIdentifier> newIdentifiers,
        CancellationToken ct = default)
    {
        var existing = await _db.AudiobookExternalIdentifiers
            .Where(identifier => identifier.AudiobookId == audiobook.Id)
            .ToListAsync(ct);
        if (existing.Count > 0)
        {
            _db.AudiobookExternalIdentifiers.RemoveRange(existing);
        }

        foreach (var identifier in newIdentifiers)
        {
            identifier.AudiobookId = audiobook.Id;
        }

        if (newIdentifiers.Count > 0)
        {
            _db.AudiobookExternalIdentifiers.AddRange(newIdentifiers);
        }

        audiobook.ExternalIdentifiers = newIdentifiers;
        await _db.SaveChangesAsync(ct);
        return true;
    }
}
