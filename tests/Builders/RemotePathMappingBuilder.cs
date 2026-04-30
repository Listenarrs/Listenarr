using Listenarr.Domain.Models;

namespace Listenarr.Tests.Builders
{
    public class RemotePathMappingBuilder
    {
        private readonly RemotePathMapping _remotePathMapping = new();

        public RemotePathMappingBuilder WithDownloadClientConfiguration(DownloadClientConfiguration value)
        {
            _remotePathMapping.DownloadClientId = value.Id;
            return this;
        }

        public RemotePathMappingBuilder WithRemotePath(string value)
        {
            _remotePathMapping.RemotePath = value;
            return this;
        }

        public RemotePathMappingBuilder WithLocalPath(string value)
        {
            _remotePathMapping.LocalPath = value;
            return this;
        }

        public RemotePathMappingBuilder WithName(string value)
        {
            _remotePathMapping.Name = value;
            return this;
        }

        public RemotePathMapping Build()
        {
            return _remotePathMapping;
        }
    }
}
