using System.Diagnostics.CodeAnalysis;
using Listenarr.Application.Repositories;
using Listenarr.Tests.Builders;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Listenarr.Tests.Common
{
    public class BaseTests : IAsyncLifetime
    {
        protected TempFileService FileService { get; set; }

        public ServiceCollection _services;
        public ServiceProvider _provider;

        public IApplicationSettingsRepository _applicationSettingsRepository;
        public IDownloadRepository _downloadRepository;
        public IDownloadClientConfigurationRepository _downloadClientConfigurationRepository;
        public IRemotePathMappingRepository _remotePathMappingRepository;
        public IHistoryRepository _historyRepository;
        public IAudiobookRepository _audiobookRepository;
        public IAudiobookFileRepository _audiobookFileRepository;
        public IDownloadProcessingJobRepository _downloadProcessingJobRepository;
        public IIndexerRepository _indexerRepository;

        public BaseTests(ServiceCollection? services = null)
        {
            FileService = new TempFileService();
            Init(services);
        }

        [MemberNotNull(
            nameof(_services), 
            nameof(_provider), 
            nameof(_applicationSettingsRepository),
            nameof(_downloadClientConfigurationRepository),
            nameof(_downloadRepository),
            nameof(_remotePathMappingRepository),
            nameof(_historyRepository),
            nameof(_audiobookRepository),
            nameof(_audiobookFileRepository),
            nameof(_downloadProcessingJobRepository),
            nameof(_indexerRepository)
        )]
        public void Init(ServiceCollection? services = null)
        {
            if (services != null)
            {
                _services = services;
            }
            
            _services ??= new ServiceCollectionBuilder().Build();
            _provider = _services.BuildServiceProvider();

            _applicationSettingsRepository = _provider.GetRequiredService<IApplicationSettingsRepository>();
            _downloadClientConfigurationRepository = _provider.GetRequiredService<IDownloadClientConfigurationRepository>();
            _downloadRepository = _provider.GetRequiredService<IDownloadRepository>();
            _remotePathMappingRepository = _provider.GetRequiredService<IRemotePathMappingRepository>();
            _historyRepository = _provider.GetRequiredService<IHistoryRepository>();
            _audiobookRepository = _provider.GetRequiredService<IAudiobookRepository>();
            _audiobookFileRepository = _provider.GetRequiredService<IAudiobookFileRepository>();
            _downloadProcessingJobRepository = _provider.GetRequiredService<IDownloadProcessingJobRepository>();
            _indexerRepository = _provider.GetRequiredService<IIndexerRepository>();
        }

        public virtual async Task InitializeAsync()
        {
        }

        public virtual async Task DisposeAsync()
        {
        }
    }
}
