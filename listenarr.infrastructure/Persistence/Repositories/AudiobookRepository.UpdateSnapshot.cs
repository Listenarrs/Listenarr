using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence.Repositories;

public partial class AudiobookRepository
{
    public Task<Audiobook?> GetForUpdateSnapshotAsync(
        int id,
        CancellationToken ct = default) =>
        _db.Audiobooks
            .AsNoTracking()
            .FirstOrDefaultAsync(audiobook => audiobook.Id == id, ct);
}
