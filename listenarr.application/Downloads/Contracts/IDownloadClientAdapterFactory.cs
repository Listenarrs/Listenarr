namespace Listenarr.Application.Downloads.Contracts
{
    public interface IDownloadClientAdapterFactory
    {
        IDownloadClientAdapter GetByIdOrType(string id);
    }
}
