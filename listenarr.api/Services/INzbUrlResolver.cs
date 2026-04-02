using System.Threading;
using System.Threading.Tasks;
using Listenarr.Domain.Models;

namespace Listenarr.Api.Services
{
    public interface INzbUrlResolver
    {
        Task<(string Url, string? IndexerApiKey)> ResolveAsync(IndexerSearchResult result, CancellationToken ct = default);
    }
}
