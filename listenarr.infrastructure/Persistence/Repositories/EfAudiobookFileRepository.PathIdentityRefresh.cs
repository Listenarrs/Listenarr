using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence.Repositories;

public partial class EfAudiobookFileRepository
{
    public async Task<bool> ReconcilePathIdentityAsync(
        int fileId,
        int audiobookId,
        AudiobookFilePathState expectedPathState,
        string storedPath,
        AudiobookFilePathIdentity identity,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expectedPathState);
        ArgumentException.ThrowIfNullOrWhiteSpace(storedPath);
        ArgumentNullException.ThrowIfNull(identity);
        identity.Validate();

        var query = _db.AudiobookFiles.Where(candidate =>
            candidate.Id == fileId
            && candidate.AudiobookId == audiobookId
            && candidate.Path == expectedPathState.StoredPath
            && candidate.CanonicalPath == expectedPathState.CanonicalPath
            && candidate.PathSyntax == expectedPathState.Syntax
            && candidate.PathCaseSensitivity == expectedPathState.CaseSensitivity
            && candidate.PathCaseSensitivityMode == expectedPathState.RequestedMode
            && candidate.PathIdentityBoundary == expectedPathState.BoundaryPath
            && candidate.PathIdentityLookupKey == expectedPathState.LookupKey
            && candidate.PathOwnershipKey == expectedPathState.OwnershipKey
            && candidate.PathIdentityVersion == expectedPathState.Version
            && candidate.PathIdentityState == expectedPathState.State
            && candidate.PathIdentityReason == expectedPathState.Reason);

        var completionToken =
            RequestCancellationBoundary.EnterNonCancelablePhase(ct);
        if (_db.Database.IsRelational())
        {
            var updated = await query.ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(candidate => candidate.Path, storedPath)
                    .SetProperty(candidate => candidate.CanonicalPath, identity.CanonicalPath)
                    .SetProperty(candidate => candidate.PathSyntax, identity.Syntax)
                    .SetProperty(candidate => candidate.PathCaseSensitivity, identity.CaseSensitivity)
                    .SetProperty(candidate => candidate.PathCaseSensitivityMode, identity.RequestedMode)
                    .SetProperty(candidate => candidate.PathIdentityBoundary, identity.BoundaryPath)
                    .SetProperty(candidate => candidate.PathIdentityLookupKey, identity.LookupKey)
                    .SetProperty(candidate => candidate.PathOwnershipKey, identity.OwnershipKey)
                    .SetProperty(candidate => candidate.PathIdentityVersion, identity.Version)
                    .SetProperty(candidate => candidate.PathIdentityState, identity.State)
                    .SetProperty(candidate => candidate.PathIdentityReason, identity.Reason),
                completionToken);
            if (updated != 1)
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

        existing.ApplyPathIdentity(storedPath, identity);
        await _db.SaveChangesAsync(completionToken);
        return true;
    }
}
