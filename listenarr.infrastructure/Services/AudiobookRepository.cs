// csharp
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Models;

namespace Listenarr.Api.Services
{
    /// <summary>
    /// Moved AudiobookRepository implementation into the Infrastructure project.
    /// Keeps the original namespace Listenarr.Api.Services so existing code and DI registrations
    /// don't need to change.
    /// </summary>
    public class AudiobookRepository : IAudiobookRepository
    {
        private readonly ListenArrDbContext _db;
        public AudiobookRepository(ListenArrDbContext db)
        {
            _db = db;
        }

        public async Task<List<Audiobook>> GetAllAsync()
        {
            // Include Files so callers that fetch the full library will receive file records
            return await _db.Audiobooks
                .Include(a => a.Files)
                .OrderBy(a => a.Title)
                .ToListAsync();
        }

        public async Task<Audiobook?> GetByAsinAsync(string asin)
        {
            var normalizedAsin = NormalizeAsin(asin);
            if (string.IsNullOrWhiteSpace(normalizedAsin)) return null;

            return await _db.Audiobooks
                .Include(a => a.ExternalIdentifiers)
                .FirstOrDefaultAsync(a =>
                    (a.Asin != null && a.Asin.ToUpper() == normalizedAsin) ||
                    (a.ExternalIdentifiers != null && a.ExternalIdentifiers.Any(i =>
                        i.Type == AudiobookExternalIdentifierType.Asin &&
                        i.ValueNormalized == normalizedAsin)));
        }

        public async Task<Audiobook?> GetByIsbnAsync(string isbn)
        {
            var normalizedIsbn = NormalizeIsbn(isbn);
            if (string.IsNullOrWhiteSpace(normalizedIsbn)) return null;

            var audiobooks = await _db.Audiobooks
                .Include(a => a.ExternalIdentifiers)
                .ToListAsync();

            return audiobooks.FirstOrDefault(a =>
                (a.Isbn != null && a.Isbn.Any(i => NormalizeIsbn(i) == normalizedIsbn)) ||
                (a.ExternalIdentifiers != null && a.ExternalIdentifiers.Any(i =>
                    i.Type == AudiobookExternalIdentifierType.Isbn &&
                    string.Equals(i.ValueNormalized, normalizedIsbn, StringComparison.OrdinalIgnoreCase))));
        }

        public async Task<Audiobook?> GetByIdAsync(int id)
        {
            // Include QualityProfile and Files for callers that need full audiobook details
            return await _db.Audiobooks
                .Include(a => a.QualityProfile)
                .Include(a => a.Files)
                .Include(a => a.ExternalIdentifiers)
                .FirstOrDefaultAsync(a => a.Id == id);
        }

        public async Task AddAsync(Audiobook audiobook)
        {
            _db.Audiobooks.Add(audiobook);
            await _db.SaveChangesAsync();
        }

        public async Task<bool> UpdateAsync(Audiobook audiobook)
        {
            // Defensive: preserve existing BasePath if the incoming audiobook doesn't provide one
            try
            {
                var existing = await _db.Audiobooks.AsNoTracking().FirstOrDefaultAsync(a => a.Id == audiobook.Id);
                if (existing != null && string.IsNullOrEmpty(audiobook.BasePath) && !string.IsNullOrEmpty(existing.BasePath))
                {
                    audiobook.BasePath = existing.BasePath;
                }
            }
            catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException) {
                // If anything goes wrong reading existing record, fall back to update behavior
                            System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
            }

            _db.Audiobooks.Update(audiobook);
            await _db.SaveChangesAsync();
            return true;
        }

        public async Task<bool> DeleteAsync(Audiobook audiobook)
        {
            _db.Audiobooks.Remove(audiobook);
            await _db.SaveChangesAsync();
            return true;
        }

        public async Task<bool> DeleteByIdAsync(int id)
        {
            var audiobook = await GetByIdAsync(id);
            if (audiobook == null)
                return false;

            return await DeleteAsync(audiobook);
        }

        public async Task<int> DeleteBulkAsync(List<int> ids)
        {
            var audiobooks = await _db.Audiobooks
                .Where(a => ids.Contains(a.Id))
                .ToListAsync();

            if (!audiobooks.Any())
                return 0;

            _db.Audiobooks.RemoveRange(audiobooks);
            await _db.SaveChangesAsync();
            return audiobooks.Count;
        }

        public async Task<string?> GetAuthorAsinByNameAsync(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;

            static string Normalize(string s)
            {
                if (string.IsNullOrWhiteSpace(s)) return string.Empty;
                // Remove punctuation, collapse whitespace, and lowercase
                var cleaned = new string(s.Where(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c)).ToArray());
                var parts = cleaned.Split(new[] { ' ', '\t', '\n', '\r' }, System.StringSplitOptions.RemoveEmptyEntries);
                return string.Join(' ', parts).ToLowerInvariant();
            }

            var target = Normalize(name);

            // Query audibooks that have author ASINs to limit work
            var candidates = await _db.Audiobooks
                .Where(a => a.AuthorAsins != null && a.AuthorAsins.Count > 0 && a.Authors != null && a.Authors.Count > 0)
                .ToListAsync();

            foreach (var b in candidates)
            {
                foreach (var author in b.Authors ?? new List<string>())
                {
                    if (Normalize(author) == target)
                    {
                        var asin = b.AuthorAsins?.FirstOrDefault();
                        if (!string.IsNullOrWhiteSpace(asin)) return asin;
                    }
                }
            }

            return null;
        }

        private static string NormalizeAsin(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            return new string(value.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        }

        private static string NormalizeIsbn(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            return new string(value.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        }
    }
}
