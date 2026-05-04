namespace Listenarr.Application.Interfaces
{
    public interface IDownloadClientAdapterFactory
    {
        IDownloadClientAdapter GetByIdOrType(string id);
    }
}
