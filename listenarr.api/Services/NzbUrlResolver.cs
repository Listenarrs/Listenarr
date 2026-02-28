using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Models;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Listenarr.Api.Services
{
    public class NzbUrlResolver : INzbUrlResolver
    {
        private readonly IDbContextFactory<ListenArrDbContext> _dbContextFactory;
        private readonly ILogger<NzbUrlResolver> _logger;

        public NzbUrlResolver(IDbContextFactory<ListenArrDbContext> dbContextFactory, ILogger<NzbUrlResolver> logger)
        {
            _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<(string Url, string? IndexerApiKey)> ResolveAsync(SearchResult result, CancellationToken ct = default)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));

            var nzbUrl = result.NzbUrl ?? string.Empty;
            if (string.IsNullOrWhiteSpace(nzbUrl))
            {
                return (nzbUrl, null);
            }

            try
            {
                var hasApiKey = false;
                if (Uri.TryCreate(nzbUrl, UriKind.Absolute, out var parsed))
                {
                    var query = QueryHelpers.ParseQuery(parsed.Query);
                    hasApiKey = query.Keys.Any(k => string.Equals(k, "apikey", StringComparison.OrdinalIgnoreCase));
                }
                else if (nzbUrl.Contains("apikey=", StringComparison.OrdinalIgnoreCase))
                {
                    hasApiKey = true;
                }

                if (hasApiKey)
                {
                    return (nzbUrl, null);
                }

                Indexer? indexer = null;
                await using var dbContext = await _dbContextFactory.CreateDbContextAsync(ct);
                if (result.IndexerId.HasValue)
                {
                    indexer = await dbContext.Indexers
                        .AsNoTracking()
                        .FirstOrDefaultAsync(i => i.Id == result.IndexerId.Value, ct);
                }
                else if (!string.IsNullOrWhiteSpace(result.Source))
                {
                    indexer = await dbContext.Indexers
                        .AsNoTracking()
                        .FirstOrDefaultAsync(i => i.Name == result.Source, ct);
                }

                if (indexer != null && !string.IsNullOrWhiteSpace(indexer.ApiKey))
                {
                    var updatedUrl = QueryHelpers.AddQueryString(nzbUrl, "apikey", indexer.ApiKey);
                    return (updatedUrl, indexer.ApiKey);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogDebug(ex, "Failed to append indexer API key to NZB URL for {Title}", result.Title);
            }

            return (nzbUrl, null);
        }
    }
}

