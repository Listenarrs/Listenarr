using Listenarr.Domain.Models;

namespace Listenarr.Application.Interfaces
{
    public interface IStartupConfigService
    {
        StartupConfig? GetConfig();
        Task ReloadAsync();
        Task SaveAsync(StartupConfig config);
    }
}
