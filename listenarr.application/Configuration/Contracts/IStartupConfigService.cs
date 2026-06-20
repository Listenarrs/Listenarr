
namespace Listenarr.Application.Configuration.Contracts
{
    public interface IStartupConfigService
    {
        StartupConfig? GetConfig();
        bool IsAuthenticationRequired();
        string GetEffectiveApiVersion(string? requestedApiVersion = null);
        string NormalizeApiVersion(string? configuredApiVersion, string? requestedApiVersion = null);
        Task ReloadAsync();
        Task SaveAsync(StartupConfig config);
    }
}
