using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Listenarr.Infrastructure.Models;
using Listenarr.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Listenarr.Api.Repositories
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

        public async Task AddAsync(Download download)
        {
            var ctx = await _dbFactory.CreateDbContextAsync();
            ctx.Downloads.Add(download);
            await ctx.SaveChangesAsync();
        }

        public async Task<Download?> FindAsync(string id)
        {
            var ctx = await _dbFactory.CreateDbContextAsync();
            return await ctx.Downloads.FindAsync(id);
        }

        public async Task UpdateAsync(Download download)
        {
            var ctx = await _dbFactory.CreateDbContextAsync();
            ctx.Downloads.Update(download);
            await ctx.SaveChangesAsync();
        }

        public async Task UpdateMetadataAsync(string id, string key, object? value)
        {
            var ctx = await _dbFactory.CreateDbContextAsync();
            var d = await ctx.Downloads.FindAsync(id);
            if (d == null) return;
            if (d.Metadata == null) d.Metadata = new System.Collections.Generic.Dictionary<string, object>();
            d.Metadata[key] = value ?? string.Empty;
            ctx.Downloads.Update(d);
            await ctx.SaveChangesAsync();
        }

        public async Task RemoveAsync(string id)
        {
            var ctx = await _dbFactory.CreateDbContextAsync();
            var d = await ctx.Downloads.FindAsync(id);
            if (d == null) return;
            ctx.Downloads.Remove(d);
            await ctx.SaveChangesAsync();
        }

        public async Task<List<Download>> GetAllAsync()
        {
            var ctx = await _dbFactory.CreateDbContextAsync();
            return await ctx.Downloads.AsNoTracking().ToListAsync();
        }

        public async Task<List<QueueTrackedDownload>> GetQueueDisplayCandidatesAsync()
        {
            var ctx = await _dbFactory.CreateDbContextAsync();
            var ddl = await ctx.Downloads
                .AsNoTracking()
                .Where(d => d.DownloadClientId == "DDL" && d.Status != DownloadStatus.Moved)
                .Select(ToQueueTrackedDownloadProjection())
                .ToListAsync();
            var nonDdl = await ctx.Downloads
                .AsNoTracking()
                .Where(d => d.DownloadClientId != "DDL")
                .Where(d => d.Status != DownloadStatus.Moved && d.Status != DownloadStatus.Failed)
                .Where(d => d.Status != DownloadStatus.Completed || string.IsNullOrEmpty(d.FinalPath))
                .Select(ToQueueTrackedDownloadProjection())
                .ToListAsync();
            return ddl.Concat(nonDdl).ToList();
        }

        public async Task<List<QueueTrackedDownload>> GetQueueMatchingCandidatesAsync()
        {
            var ctx = await _dbFactory.CreateDbContextAsync();
            return await ctx.Downloads
                .AsNoTracking()
                .Where(d => d.DownloadClientId != "DDL" && d.Status != DownloadStatus.Failed)
                .Select(ToQueueTrackedDownloadProjection())
                .ToListAsync();
        }

        public async Task<List<string>> GetKnownClientItemIdsAsync()
        {
            var ctx = await _dbFactory.CreateDbContextAsync();
            var metadataEntries = await ctx.Downloads
                .AsNoTracking()
                .Select(d => d.Metadata)
                .ToListAsync();

            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var metadata in metadataEntries)
            {
                if (metadata == null)
                {
                    continue;
                }

                if (TryGetMetadataString(metadata, Download.METADATA_CLIENT_DOWNLOAD_ID_KEY, out var clientDownloadId))
                {
                    ids.Add(clientDownloadId);
                }

                if (TryGetMetadataString(metadata, "TorrentHash", out var torrentHash))
                {
                    ids.Add(torrentHash);
                }
            }

            return ids.ToList();
        }

        public async Task<List<Download>> GetByClientAsync(string clientId)
        {
            var ctx = await _dbFactory.CreateDbContextAsync();
            return await ctx.Downloads
                .AsNoTracking()
                .Where(d => d.DownloadClientId == clientId)
                .ToListAsync();
        }

        public async Task<List<Download>> GetByIdsAsync(IEnumerable<string> ids)
        {
            var idSet = ids?.ToList() ?? new List<string>();
            if (!idSet.Any()) return new List<Download>();
            var ctx = await _dbFactory.CreateDbContextAsync();
            return await ctx.Downloads
                .AsNoTracking()
                .Where(d => idSet.Contains(d.Id))
                .ToListAsync();
        }

        private static bool TryGetMetadataString(Dictionary<string, object>? metadata, string key, out string value)
        {
            value = string.Empty;

            if (metadata == null || !metadata.TryGetValue(key, out var raw) || raw == null)
            {
                return false;
            }

            value = raw.ToString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(value);
        }

        private static System.Linq.Expressions.Expression<Func<Download, QueueTrackedDownload>> ToQueueTrackedDownloadProjection()
        {
            return d => new QueueTrackedDownload
            {
                Id = d.Id,
                DownloadClientId = d.DownloadClientId,
                Title = d.Title,
                Artist = d.Artist,
                Status = d.Status,
                StartedAt = d.StartedAt,
                TotalSize = d.TotalSize,
                DownloadedSize = d.DownloadedSize,
                DownloadPath = d.DownloadPath,
                FinalPath = d.FinalPath,
                Metadata = d.Metadata,
                AudiobookId = d.AudiobookId,
                Language = d.Language
            };
        }
    }
}
