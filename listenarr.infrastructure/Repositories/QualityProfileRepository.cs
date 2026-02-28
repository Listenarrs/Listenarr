using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Listenarr.Domain.Models;
using Listenarr.Application.Repositories;
using Listenarr.Infrastructure.Models;

namespace Listenarr.Infrastructure.Repositories
{
    public class QualityProfileRepository : IQualityProfileRepository
    {
        private readonly ListenArrDbContext _db;

        public QualityProfileRepository(ListenArrDbContext db)
        {
            _db = db;
        }

        public async Task<List<QualityProfile>> GetAllAsync()
        {
            return await _db.QualityProfiles.ToListAsync();
        }

        public async Task<QualityProfile?> FindByIdAsync(int id)
        {
            return await _db.QualityProfiles.FindAsync(id);
        }

        public async Task<QualityProfile?> GetDefaultAsync()
        {
            return await _db.QualityProfiles.FirstOrDefaultAsync(p => p.IsDefault);
        }

        public async Task<QualityProfile> AddAsync(QualityProfile profile)
        {
            _db.QualityProfiles.Add(profile);
            await _db.SaveChangesAsync();
            return profile;
        }

        public async Task<QualityProfile> UpdateAsync(QualityProfile profile)
        {
            // Avoid attaching a second instance to the DbContext which can cause tracking conflicts.
            // Qualities is stored as a JSON text column (not a navigation property), so do not use Include.
            var existing = await _db.QualityProfiles
                                    .FirstOrDefaultAsync(p => p.Id == profile.Id);

            if (existing == null)
            {
                throw new InvalidOperationException($"Quality profile with ID {profile.Id} not found");
            }

            // Manually update scalar properties to avoid EF attaching a second instance
            existing.Name = profile.Name;
            existing.Description = profile.Description;
            existing.CutoffQuality = profile.CutoffQuality;
            existing.MinimumSize = profile.MinimumSize;
            existing.MaximumSize = profile.MaximumSize;
            existing.MinimumSeeders = profile.MinimumSeeders;
            existing.MinimumScore = profile.MinimumScore;
            existing.IsDefault = profile.IsDefault;
            existing.PreferNewerReleases = profile.PreferNewerReleases;
            existing.MaximumAge = profile.MaximumAge;

            // Replace list/scalar-serialized properties safely
            // Create a new list to force EF Core to detect the change
            var newQualities = new List<QualityDefinition>();
            if (profile.Qualities != null && profile.Qualities.Count > 0)
            {
                foreach (var q in profile.Qualities)
                {
                    newQualities.Add(new QualityDefinition
                    {
                        Quality = q.Quality,
                        Allowed = q.Allowed,
                        Priority = q.Priority,
                        Codec = q.Codec,
                        Bitrate = q.Bitrate,
                        IsLossless = q.IsLossless
                    });
                }
            }
            existing.Qualities = newQualities;
            // Mark Qualities as modified so EF Core detects the change
            _db.Entry(existing).Property(p => p.Qualities).IsModified = true;

            existing.PreferredFormats = profile.PreferredFormats ?? new System.Collections.Generic.List<string>();
            existing.PreferredWords = profile.PreferredWords ?? new System.Collections.Generic.List<string>();
            existing.MustNotContain = profile.MustNotContain ?? new System.Collections.Generic.List<string>();
            existing.MustContain = profile.MustContain ?? new System.Collections.Generic.List<string>();
            existing.PreferredLanguages = profile.PreferredLanguages ?? new System.Collections.Generic.List<string>();

            // Update CustomGroupNames
            existing.CustomGroupNames = profile.CustomGroupNames;
            if (profile.CustomGroupNames != null)
            {
                _db.Entry(existing).Property(p => p.CustomGroupNames).IsModified = true;
            }

            existing.UpdatedAt = DateTime.UtcNow;

            await _db.SaveChangesAsync();
            return existing;
        }

        public async Task<bool> DeleteAsync(int id)
        {
            var existing = await _db.QualityProfiles.FindAsync(id);
            if (existing == null) return false;
            _db.QualityProfiles.Remove(existing);
            await _db.SaveChangesAsync();
            return true;
        }

        public async Task<int> CountAudiobooksUsingProfileAsync(int profileId)
        {
            return await _db.Audiobooks.CountAsync(a => a.QualityProfileId == profileId);
        }
    }
}
