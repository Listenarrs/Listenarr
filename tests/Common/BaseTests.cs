using System.Diagnostics.CodeAnalysis;
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Domain.Models;
using Listenarr.Domain.Models.Configurations;
using Listenarr.Tests.Builders;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Listenarr.Tests.Common
{
    public class BaseTests : IAsyncLifetime
    {
        protected TempFileService FileService { get; set; }

        protected ServiceCollection _services;
        protected ServiceProvider _provider;

        protected IApplicationSettingsRepository _applicationSettingsRepository;
        protected IDownloadRepository _downloadRepository;
        protected IDownloadClientConfigurationRepository _downloadClientConfigurationRepository;
        protected IRemotePathMappingRepository _remotePathMappingRepository;
        protected IHistoryRepository _historyRepository;
        protected IAudiobookRepository _audiobookRepository;
        protected IAudiobookFileRepository _audiobookFileRepository;
        protected IDownloadProcessingJobRepository _downloadProcessingJobRepository;
        protected IIndexerRepository _indexerRepository;
        protected IDownloadHistoryRepository _downloadHistoryRepository;
        protected IQualityProfileRepository _qualityProfileRepository;
        protected IMoveJobRepository _moveJobRepository;
        protected IRootFolderRepository _rootFolderRepository;

        public BaseTests()
        {
            FileService = new TempFileService();
            Init();
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
            nameof(_indexerRepository),
            nameof(_downloadHistoryRepository),
            nameof(_qualityProfileRepository),
            nameof(_moveJobRepository),
            nameof(_rootFolderRepository)
        )]
        public void Init()
        {
            _services ??= new ServiceCollectionBuilder().Build();

            _provider?.Dispose();
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
            _downloadHistoryRepository = _provider.GetRequiredService<IDownloadHistoryRepository>();
            _qualityProfileRepository = _provider.GetRequiredService<IQualityProfileRepository>();
            _moveJobRepository = _provider.GetRequiredService<IMoveJobRepository>();
            _rootFolderRepository = _provider.GetRequiredService<IRootFolderRepository>();
        }

        public virtual async Task InitializeAsync()
        {
            await FileService.InitializeAsync();
        }

        public virtual async Task DisposeAsync()
        {
            await FileService.DisposeAsync();
            await _provider.DisposeAsync();
        }

        public async Task<ApplicationSettings> CreateApplicationSettings()
        {
            return await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .Build());
        }

        public async Task<DownloadClientConfiguration> CreateDownloadClientConfiguration()
        {
            return await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfigurationBuilder()
                .Build());
        }

        public async Task<Audiobook> CreateAudiobook()
        {
            return await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithBasePath(FileService.GetTempPath())
                .Build());
        }
    }
}
