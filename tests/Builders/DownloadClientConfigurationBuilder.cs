namespace Listenarr.Tests.Builders
{
    public class DownloadClientConfigurationBuilder
    {
        private readonly DownloadClientConfiguration _downloadClientConfiguration = new();
        private Dictionary<string, object> _settings = [];

        public DownloadClientConfigurationBuilder()
        {
            _downloadClientConfiguration.Id = Guid.NewGuid().ToString();
            _downloadClientConfiguration.IsEnabled = true;
            _downloadClientConfiguration.Host = "localhost";
            _downloadClientConfiguration.Port = 8080;
            _downloadClientConfiguration.Type = "qbittorrent";
        }

        public DownloadClientConfigurationBuilder WithId(string value)
        {
            _downloadClientConfiguration.Id = value;
            return this;
        }

        public DownloadClientConfigurationBuilder WithName(string value)
        {
            _downloadClientConfiguration.Name = value;
            return this;
        }

        public DownloadClientConfigurationBuilder WithType(string value)
        {
            _downloadClientConfiguration.Type = value;
            return this;
        }

        public DownloadClientConfigurationBuilder WithHost(string value)
        {
            _downloadClientConfiguration.Host = value;
            return this;
        }

        public DownloadClientConfigurationBuilder WithPort(int value)
        {
            _downloadClientConfiguration.Port = value;
            return this;
        }

        public DownloadClientConfigurationBuilder WithApiKey(string value)
        {
            _settings["apiKey"] = value;
            return this;
        }

        public DownloadClientConfigurationBuilder WithUrlBase(string value)
        {
            _settings["urlBase"] = value;
            return this;
        }

        public DownloadClientConfigurationBuilder WithSsl()
        {
            _downloadClientConfiguration.UseSSL = true;
            return this;
        }

        public DownloadClientConfigurationBuilder WithoutSsl()
        {
            _downloadClientConfiguration.UseSSL = false;
            return this;
        }

        public DownloadClientConfigurationBuilder WithSettings(string key, string value)
        {
            _settings[key] = value;
            return this;
        }

        public DownloadClientConfigurationBuilder Enabled()
        {
            _downloadClientConfiguration.IsEnabled = true;
            return this;
        }

        public DownloadClientConfigurationBuilder Disabled()
        {
            _downloadClientConfiguration.IsEnabled = false;
            return this;
        }

        public DownloadClientConfigurationBuilder WithPath(string value)
        {
            _downloadClientConfiguration.DownloadPath = value;
            return this;
        }

        public DownloadClientConfigurationBuilder WithEnabled()
        {
            _downloadClientConfiguration.IsEnabled = true;
            return this;
        }

        public DownloadClientConfigurationBuilder WithDisabled()
        {
            _downloadClientConfiguration.IsEnabled = false;
            return this;
        }

        public DownloadClientConfigurationBuilder WithUsername(string value)
        {
            _downloadClientConfiguration.Username = value;
            return this;
        }

        public DownloadClientConfigurationBuilder WithPassword(string value)
        {
            _downloadClientConfiguration.Password = value;
            return this;
        }

        public DownloadClientConfiguration Build()
        {
            _downloadClientConfiguration.Settings = _settings;
            return _downloadClientConfiguration;
        }
    }
}
