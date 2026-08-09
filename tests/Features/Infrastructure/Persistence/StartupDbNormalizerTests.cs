using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.Persistence;

[Trait("Name", "StartupDbNormalizerTests")]
[Trait("Category", "Infrastructure")]
public sealed class StartupDbNormalizerTests : BaseTests
{
    [Fact]
    public async Task FilesystemInitializationFailure_ReleasesUnrelatedJsonNormalization()
    {
        var normalized = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var repository = new Mock<IAudiobookRepository>(MockBehavior.Strict);
        repository.Setup(service => service.NormalizeJsonColumnsAsync(
                It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                normalized.TrySetResult();
                return Task.CompletedTask;
            });
        using var provider = new ServiceCollection()
            .AddScoped(_ => repository.Object)
            .BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true
            });
        var readiness = new LibraryFilesystemReadiness();
        readiness.MarkRunning("LibraryDirectoryOwnership");
        var service = new StartupDbNormalizer(
            provider,
            readiness,
            NullLogger<StartupDbNormalizer>.Instance);

        await service.StartAsync(CancellationToken.None);

        repository.Verify(
            candidate => candidate.NormalizeJsonColumnsAsync(It.IsAny<CancellationToken>()),
            Times.Never);

        readiness.MarkFailed(
            "filesystem_initialization_failed",
            "Injected filesystem failure.",
            "LibraryDirectoryOwnership");
        await normalized.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        repository.Verify(
            candidate => candidate.NormalizeJsonColumnsAsync(It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
