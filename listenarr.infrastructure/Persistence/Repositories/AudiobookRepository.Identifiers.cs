using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence.Repositories;

public partial class AudiobookRepository
{
    public async Task<Dictionary<(int AudiobookId, string Asin), string?>> GetAsinRegionsAsync(CancellationToken ct = default)
    {
        // Batch-loaded like the series memberships: the library list has no per-row Include
        var asinIdentifiers = await _db.AudiobookExternalIdentifiers
            .AsNoTracking()
            .Where(i => i.Type == AudiobookExternalIdentifierType.Asin)
            .OrderByDescending(i => i.IsPrimary)
            .Select(i => new { i.AudiobookId, i.ValueNormalized, i.Region })
            .ToListAsync(ct);

        return asinIdentifiers
            .GroupBy(i => (i.AudiobookId, i.ValueNormalized))
            .ToDictionary(g => g.Key, g => g.First().Region);
    }
}
