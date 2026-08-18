using Listenarr.Api.Startup;
using Listenarr.Infrastructure.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;

using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Api.Startup;

[Trait("Name", "ProductionCompositionValidationTests")]
[Trait("Category", "Api")]
public sealed class ProductionCompositionValidationTests : BaseTests
{
    [Fact]
    public void DevelopmentComposition_ValidatesCompleteProductionServiceGraph()
    {
        var contentRoot = Path.Join(
            Path.GetTempPath(),
            "listenarr-tests",
            $"development-composition-{Guid.NewGuid():N}");
        Directory.CreateDirectory(contentRoot);
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ContentRootPath = contentRoot,
                EnvironmentName = Environments.Development
            });
            var fileSystem = new LocalFileSystem();
            builder.Configuration["Listenarr:SqliteDbPath"] = Path.Join(
                contentRoot,
                "composition.db");
            builder.AddListenarrApiServices(fileSystem);
            builder.Services.AddListenarrInfrastructureComposition(
                builder.Configuration,
                builder.Environment);

            Type[] affectedSingletonServiceTypes =
            [
                typeof(TimeProvider),
                typeof(IFilesystemMutationCoordinator),
                typeof(IDirectoryObjectIdentityResolver),
                typeof(IRootFolderStorageHealthResolver),
                typeof(LibraryDirectoryOwnershipBoundaryAuthorizer),
                typeof(IAudiobookOperationCoordinator),
                typeof(IAudiobookUpdatePublisher),
                typeof(IRootFolderRelocationService),
                typeof(IMoveCleanupBoundaryResolver),
                typeof(ILibraryDirectoryOwnershipStore),
                typeof(IAudiobookDeletionIntentProbe),
                typeof(IFileRegistrationRecoveryProbe),
                typeof(IFileRenameRecoveryProbe),
                typeof(IMoveQueueService),
                typeof(IMoveQueuePersistence),
                typeof(IMoveExecutionStore),
                typeof(IMoveScanHandoffStore),
                typeof(LibraryFilesystemReadiness),
                typeof(ILibraryFilesystemReadiness),
                typeof(ILibraryFilesystemMutationGate),
                typeof(IFileSystemSemanticsResolver),
                typeof(IFileSystem),
                typeof(IStartupConfigService),
                typeof(IFfmpegService),
                typeof(IScanQueueService),
                typeof(IUnmatchedScanQueueService),
                typeof(MoveScanHandoffRecoveryService),
                typeof(ScanJobProcessor),
                typeof(IScanJobProcessor),
                typeof(AudiobookContentMoveService),
                typeof(MoveJobProcessor),
                typeof(IMoveJobProcessor),
                typeof(UnmatchedScanProcessor),
                typeof(IUnmatchedScanProcessor),
                typeof(MetadataRescanService),
                typeof(DownloadProcessingJobProcessor),
                typeof(IDownloadImportProcessor),
                typeof(UnmatchedScanBackgroundService)
            ];
            foreach (var serviceType in affectedSingletonServiceTypes)
            {
                var descriptor = Assert.Single(
                    builder.Services,
                    candidate => candidate.ServiceType == serviceType);
                Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
            }

            Type[] affectedScopedServiceTypes =
            [
                typeof(IAudiobookDeletionCommitService),
                typeof(IAudiobookDeletionIntentStore),
                typeof(IAudiobookDeletionIntentReconciler),
                typeof(IFileRenameCommitStore),
                typeof(IFileRegistrationRecoveryService),
                typeof(IFileRenameRecoveryReconciler),
                typeof(IAudiobookFileIdentityReconciler),
                typeof(IAudiobookFilesystemDeleteService),
                typeof(IRenameService),
                typeof(IRootFolderStorageConfirmationService)
            ];
            foreach (var serviceType in affectedScopedServiceTypes)
            {
                var descriptor = Assert.Single(
                    builder.Services,
                    candidate => candidate.ServiceType == serviceType);
                Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
            }

            Type[] affectedHostedServiceTypes =
            [
                typeof(LibraryFilesystemStartupReconciliationService),
                typeof(ScanBackgroundService),
                typeof(MoveBackgroundService),
                typeof(MetadataRescanService),
                typeof(DownloadProcessingJobProcessor),
                typeof(UnmatchedScanBackgroundService),
                typeof(StartupDbNormalizer)
            ];
            Assert.All(
                builder.Services.Where(descriptor =>
                    descriptor.ServiceType == typeof(IHostedService)),
                descriptor => Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime));

            using var provider = builder.Services.BuildServiceProvider(
                new ServiceProviderOptions
                {
                    ValidateScopes = true,
                    ValidateOnBuild = true
                });

            foreach (var serviceType in affectedSingletonServiceTypes)
            {
                Assert.NotNull(provider.GetRequiredService(serviceType));
            }
            using (var scope = provider.CreateScope())
            {
                foreach (var serviceType in affectedScopedServiceTypes)
                {
                    Assert.NotNull(scope.ServiceProvider.GetRequiredService(serviceType));
                }
            }

            var hostedServices = provider.GetServices<IHostedService>().ToList();
            foreach (var implementationType in affectedHostedServiceTypes)
            {
                Assert.Single(hostedServices, service =>
                    service.GetType() == implementationType);
            }

            Assert.Same(
                provider.GetRequiredService<ScanJobProcessor>(),
                provider.GetRequiredService<IScanJobProcessor>());
            Assert.Same(
                provider.GetRequiredService<MoveJobProcessor>(),
                provider.GetRequiredService<IMoveJobProcessor>());
            Assert.Same(
                provider.GetRequiredService<UnmatchedScanProcessor>(),
                provider.GetRequiredService<IUnmatchedScanProcessor>());
            Assert.Same(
                provider.GetRequiredService<LibraryFilesystemReadiness>(),
                provider.GetRequiredService<ILibraryFilesystemReadiness>());
            Assert.Same(
                provider.GetRequiredService<LibraryFilesystemReadiness>(),
                provider.GetRequiredService<ILibraryFilesystemMutationGate>());
            Assert.Same(
                provider.GetRequiredService<DownloadProcessingJobProcessor>(),
                provider.GetRequiredService<IDownloadImportProcessor>());
            Assert.Same(
                provider.GetRequiredService<MetadataRescanService>(),
                Assert.Single(hostedServices.OfType<MetadataRescanService>()));
            Assert.Same(
                provider.GetRequiredService<UnmatchedScanBackgroundService>(),
                Assert.Single(hostedServices.OfType<UnmatchedScanBackgroundService>()));
        }
        finally
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }
}
