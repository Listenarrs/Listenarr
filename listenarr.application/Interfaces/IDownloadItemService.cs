using Listenarr.Domain.Models;

namespace Listenarr.Application.Interfaces
{
    /// <summary>
    /// Resolves download informations with accurate paths and metadata as queue items
    /// </summary>
    public interface IDownloadItemService
    {
        /// <summary>
        /// Resolves the import item by querying the download client.
        /// </summary>
        Task<QueueItem> GetImportItemAsync(Download download, CancellationToken cancellationToken = default);
    }
}
