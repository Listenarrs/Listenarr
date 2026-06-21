/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Persistence.Repositories
{
    public class EfDownloadRepository : IDownloadRepository
    {
        private readonly IDbContextFactory<ListenArrDbContext> _dbFactory;
        private readonly ILogger<EfDownloadRepository> _logger;

        public EfDownloadRepository(IDbContextFactory<ListenArrDbContext> dbFactory, ILogger<EfDownloadRepository> logger)
        {
            _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<Download> AddAsync(Download download)
        {
            ApplyActiveDeduplicationKey(download);
            await using var ctx = await _dbFactory.CreateDbContextAsync();
            ctx.Downloads.Add(download);
            await ctx.SaveChangesAsync();
            return download;
        }

        public async Task<Download?> FindAsync(string id)
        {
            await using var ctx = await _dbFactory.CreateDbContextAsync();
            return await ctx.Downloads.FindAsync(id);
        }

        public async Task UpdateAsync(Download download)
        {
            ApplyActiveDeduplicationKey(download);
            await using var ctx = await _dbFactory.CreateDbContextAsync();
            ctx.Downloads.Update(download);
            await ctx.SaveChangesAsync();
        }

        public async Task UpdateMetadataAsync(string id, string key, object? value)
        {
            await using var ctx = await _dbFactory.CreateDbContextAsync();
            var d = await ctx.Downloads.FindAsync(id);
            if (d == null) return;
            if (d.Metadata == null) d.Metadata = new Dictionary<string, object>();
            d.Metadata[key] = value ?? string.Empty;
            ctx.Downloads.Update(d);
            await ctx.SaveChangesAsync();
        }

        public async Task RemoveAsync(string id)
        {
            await using var ctx = await _dbFactory.CreateDbContextAsync();
            var d = await ctx.Downloads.FindAsync(id);
            if (d == null) return;
            ctx.Downloads.Remove(d);
            await ctx.SaveChangesAsync();
        }

        public async Task<List<Download>> GetAllAsync()
        {
            await using var ctx = await _dbFactory.CreateDbContextAsync();
            return await ctx.Downloads.AsNoTracking().ToListAsync();
        }

        public async Task<List<Download>> GetQueueDisplayCandidatesAsync()
        {
            await using var ctx = await _dbFactory.CreateDbContextAsync();
            var ddl = await ctx.Downloads
                .AsNoTracking()
                .Where(d => d.DownloadClientId == "DDL" && d.Status != DownloadStatus.Moved)
                .ToListAsync();
            var nonDdl = await ctx.Downloads
                .AsNoTracking()
                .Where(d => d.DownloadClientId != "DDL")
                .Where(d => d.Status != DownloadStatus.Moved && d.Status != DownloadStatus.Failed)
                .Where(d => d.Status != DownloadStatus.Completed || string.IsNullOrEmpty(d.FinalPath))
                .ToListAsync();
            return ddl.Concat(nonDdl).ToList();
        }

        public async Task<List<Download>> GetQueueMatchingCandidatesAsync()
        {
            await using var ctx = await _dbFactory.CreateDbContextAsync();
            return await ctx.Downloads
                .AsNoTracking()
                .Where(d => d.DownloadClientId != "DDL" && d.Status != DownloadStatus.Failed)
                .ToListAsync();
        }

        public async Task<List<string>> GetKnownClientItemIdsAsync()
        {
            await using var ctx = await _dbFactory.CreateDbContextAsync();
            var metadataEntries = await ctx.Downloads
                .AsNoTracking()
                .Select(d => d.Metadata)
                .ToListAsync();

            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var metadata in metadataEntries)
            {
                if (metadata == null) continue;
                if (TryGetMetadataString(metadata, "ClientDownloadId", out var clientDownloadId))
                    ids.Add(clientDownloadId);
                if (TryGetMetadataString(metadata, "TorrentHash", out var torrentHash))
                    ids.Add(torrentHash);
            }

            return ids.ToList();
        }

        public async Task<List<Download>> GetByClientAsync(string clientId)
        {
            await using var ctx = await _dbFactory.CreateDbContextAsync();
            return await ctx.Downloads
                .AsNoTracking()
                .Where(d => d.DownloadClientId == clientId)
                .ToListAsync();
        }

        public async Task<Download?> GetByIdAsync(string id)
        {
            await using var ctx = await _dbFactory.CreateDbContextAsync();
            try
            {
                return await ctx.Downloads
                    .AsNoTracking()
                    .FirstAsync(d => d.Id == id);
            }
            catch (InvalidOperationException)
            {
                _logger.LogError($"Trying to get download {id} but no download has that ID");
                return null;
            }
        }

        public async Task<List<Download>> GetByIdsAsync(IEnumerable<string> ids)
        {
            var idSet = ids?.ToList() ?? new List<string>();
            if (!idSet.Any()) return new List<Download>();
            await using var ctx = await _dbFactory.CreateDbContextAsync();
            return await ctx.Downloads
                .AsNoTracking()
                .Where(d => idSet.Contains(d.Id))
                .ToListAsync();
        }

        public async Task<List<Download>> GetByAudiobookIdAsync(int audiobookId, System.Threading.CancellationToken ct = default)
        {
            await using var ctx = await _dbFactory.CreateDbContextAsync();
            return await ctx.Downloads
                .AsNoTracking()
                .Where(d => d.AudiobookId == audiobookId)
                .ToListAsync(ct);
        }

        public async Task<List<Download>> GetCompletionCandidatesAsync(int limit)
        {
            await using var ctx = await _dbFactory.CreateDbContextAsync();
            return await ctx.Downloads
                .AsNoTracking()
                .Where(d => d.Status == DownloadStatus.Completed
                         || d.Status == DownloadStatus.ImportPending
                         || d.Status == DownloadStatus.Processing)
                .OrderByDescending(d => d.CompletedAt)
                .Take(limit)
                .ToListAsync();
        }

        public async Task<List<Download>> GetActiveAsync()
        {
            await using var ctx = await _dbFactory.CreateDbContextAsync();
            return await ctx.Downloads
                .AsNoTracking()
                .Where(d => d.Status == DownloadStatus.Queued
                         || d.Status == DownloadStatus.Downloading
                         || d.Status == DownloadStatus.Paused
                         || d.Status == DownloadStatus.Processing
                         || d.Status == DownloadStatus.Completed
                         || d.Status == DownloadStatus.ImportPending
                         || d.Status == DownloadStatus.Moved)
                .ToListAsync();
        }

        public async Task<List<Download>> GetRecentAsync(int count)
        {
            await using var ctx = await _dbFactory.CreateDbContextAsync();
            return await ctx.Downloads
                .AsNoTracking()
                .OrderByDescending(d => d.StartedAt)
                .Take(count)
                .ToListAsync();
        }

        public async Task<List<int>> GetActiveAudiobookIdsAsync(IEnumerable<DownloadStatus> statuses)
        {
            var statusList = statuses.ToList();
            await using var ctx = await _dbFactory.CreateDbContextAsync();
            return await ctx.Downloads
                .AsNoTracking()
                .Where(d => d.AudiobookId.HasValue && statusList.Contains(d.Status))
                .Select(d => d.AudiobookId!.Value)
                .Distinct()
                .ToListAsync();
        }

        private static bool TryGetMetadataString(Dictionary<string, object>? metadata, string key, out string value)
        {
            value = string.Empty;
            if (metadata == null || !metadata.TryGetValue(key, out var raw) || raw == null)
                return false;
            value = raw.ToString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(value);
        }

        private static void ApplyActiveDeduplicationKey(Download download)
        {
            download.ActiveAudiobookDeduplicationKey =
                download.AudiobookId.HasValue && IsActive(download.Status)
                    ? download.AudiobookId
                    : null;
        }

        private static bool IsActive(DownloadStatus status) =>
            status is DownloadStatus.Queued
                or DownloadStatus.Downloading
                or DownloadStatus.Paused
                or DownloadStatus.Completed
                or DownloadStatus.Processing
                or DownloadStatus.Ready
                or DownloadStatus.ImportPending;
    }
}
