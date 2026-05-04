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
        /// Called just before import to get the most accurate path.
        /// </summary>
        Task<QueueItem> ResolveImportItemAsync(Download download, CancellationToken cancellationToken = default);

        /// <summary>
        /// List files related to a given download
        /// </summary>
        /// <param name="download">Download to which we want to match files</param>
        /// <returns>List of files that are from a given download</returns>
        /// <exception cref="DownloadProcessingException">Thrown when we are technicaly unable to perform the filtering based on download client retrieved informations</exception>
        Task<List<string>> GetDownloadedFiles(Download download, CancellationToken cancellationToken = default);
    }
}
