using Listenarr.Domain.Models;

namespace Listenarr.Application.Interfaces
{
    /// <summary>
    /// Download import responsible for processing a given download importation
    /// </summary>
    public interface IDownloadImportService
    {
        /// <summary>
        /// Import files for a given audiobook
        /// - Handles archives
        /// - Handles quality profiles
        /// - Handles naming patterns
        /// </summary>
        /// <param name="audiobook">Audiobook for which we are importing files</param>
        /// <param name="files">List of files to import as reported by the IDownloadClientGateway</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException">If arguments are not consistent (missing audiobook base path for example)</exception>
        /// <exception cref="IOException">Thrown if we are unable to process one archive</exception>
        Task<List<ImportResult>> ImportDownloadFilesAsync(Audiobook audiobook, List<string> files, CancellationToken ct = default);
    }
}
