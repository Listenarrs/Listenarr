
namespace Listenarr.Application.Configuration.Contracts
{
    /// <summary>
    /// Manages application configuration including APIs, download clients, and settings
    /// </summary>
    public interface IConfigurationService
    {
        /// <summary>
        /// Gets all API configurations (indexers and metadata sources)
        /// </summary>
        /// <returns>List of API configurations</returns>
        Task<List<ApiConfiguration>> GetApiConfigurationsAsync();

        /// <summary>
        /// Gets a specific API configuration by ID
        /// </summary>
        /// <param name="id">The API configuration ID</param>
        /// <returns>API configuration or null if not found</returns>
        Task<ApiConfiguration?> GetApiConfigurationAsync(string id);

        /// <summary>
        /// Saves or updates an API configuration
        /// </summary>
        /// <param name="config">The API configuration to save</param>
        /// <returns>The configuration ID</returns>
        Task<string> SaveApiConfigurationAsync(ApiConfiguration config);

        /// <summary>
        /// Deletes an API configuration
        /// </summary>
        /// <param name="id">The API configuration ID to delete</param>
        /// <returns>True if deleted successfully, false otherwise</returns>
        Task<bool> DeleteApiConfigurationAsync(string id);

        /// <summary>
        /// Gets all download client configurations
        /// </summary>
        /// <returns>List of download client configurations</returns>
        Task<List<DownloadClientConfiguration>> GetDownloadClientConfigurationsAsync();

        /// <summary>
        /// Gets a specific download client configuration by ID
        /// </summary>
        /// <param name="id">The download client configuration ID</param>
        /// <returns>Download client configuration or null if not found</returns>
        Task<DownloadClientConfiguration?> GetDownloadClientConfigurationAsync(string id);

        /// <summary>
        /// Saves or updates a download client configuration
        /// </summary>
        /// <param name="config">The download client configuration to save</param>
        /// <returns>The configuration ID</returns>
        Task<string> SaveDownloadClientConfigurationAsync(DownloadClientConfiguration config);

        /// <summary>
        /// Deletes a download client configuration
        /// </summary>
        /// <param name="id">The download client configuration ID to delete</param>
        /// <returns>True if deleted successfully, false otherwise</returns>
        Task<bool> DeleteDownloadClientConfigurationAsync(string id);

        /// <summary>
        /// Gets the application settings
        /// </summary>
        /// <returns>Application settings</returns>
        Task<ApplicationSettings> GetApplicationSettingsAsync();

        /// <summary>
        /// Saves the application settings
        /// </summary>
        /// <param name="settings">The settings to save</param>
        Task SaveApplicationSettingsAsync(ApplicationSettings settings);

        /// <summary>
        /// Gets the saved Prowlarr import connection settings.
        /// </summary>
        /// <param name="includeSecret">When true, includes the decrypted API key for server-side use.</param>
        Task<ProwlarrImportConnectionSettings> GetProwlarrImportSettingsAsync(bool includeSecret = false);

        /// <summary>
        /// Saves the Prowlarr import connection settings.
        /// </summary>
        /// <param name="settings">The connection settings to save.</param>
        Task<ProwlarrImportConnectionSettings> SaveProwlarrImportSettingsAsync(ProwlarrImportConnectionSettings settings);

        /// <summary>
        /// Gets the startup configuration
        /// </summary>
        /// <returns>Startup configuration</returns>
        Task<StartupConfig> GetStartupConfigAsync();

        /// <summary>
        /// Saves the startup configuration
        /// </summary>
        /// <param name="config">The startup configuration to save</param>
        Task SaveStartupConfigAsync(StartupConfig config);

        /// <summary>
        /// Gets all webhook configurations
        /// </summary>
        /// <returns>List of webhook configurations</returns>
        Task<List<WebhookConfiguration>> GetWebhookConfigurationsAsync();
    }
}
