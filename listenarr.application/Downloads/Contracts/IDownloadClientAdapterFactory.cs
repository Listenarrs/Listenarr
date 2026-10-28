namespace Listenarr.Application.Downloads.Contracts
{
    public interface IDownloadClientAdapterFactory
    {
        /// <summary>
        /// Retrieve a download client adapter by client type/software
        /// </summary>
        /// <param name="type">Actual name of the download client (qBittorent, Transmission, Slskd, ...)</param>
        /// <returns>Download client adapter to use for the given type</returns>
        /// <exception cref="InvalidOperationException">Exception thrown when no adapter is defined for the given type</exception>
        IDownloadClientAdapter GetByType(string type);

        /// <summary>
        /// Retrieve a download client adapter by protocol
        /// </summary>
        /// <param name="protocol">Protocol it supports (Torrent, Usenet, Soulseek, ...)</param>
        /// <returns>Available download clients</returns>
        /// <exception cref="InvalidOperationException">Exception thrown when no adapter is defined for the given protocol</exception>
        List<IDownloadClientAdapter> GetByProtocol(DownloadProtocol protocol);

        /// <summary>
        /// Retrieve the list of client type that are compatible with the given protocol
        /// </summary>
        /// <param name="protocol">Protocol we want to check</param>
        /// <returns>A list of client type that supports the given protocol</returns>
        List<string> GetClientTypeSupportingProtocol(DownloadProtocol protocol);
    }
}
