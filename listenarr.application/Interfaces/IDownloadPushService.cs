using Listenarr.Domain.Models;

namespace Listenarr.Application.Interfaces
{
    /// <summary>
    /// Service that receives pushed download updates from clients, broadcasts them
    /// and keeps a short-lived cache of recently pushed download ids so we
    /// can avoid re-broadcasting the same updates.
    /// </summary>
    public interface IDownloadPushService
    {
        /// <summary>
        /// Accept a pushed download, broadcast it to connected clients and record it in the recent cache.
        /// </summary>
        public Task HandlePushAsync(Download download, CancellationToken cancellationToken = default);

        /// <summary>
        /// Same as HandlePushAsync with a list of downloads
        /// </summary>
        /// <see cref="HandlePushAsync"/>
        public Task HandlePushAsync(List<Download> downloads, CancellationToken cancellationToken = default);
    }
}
