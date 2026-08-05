using Listenarr.Infrastructure.Persistence.Repositories;
using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Application.Audiobooks.RootFolders;

[Trait("Area", "RootFolders")]
[Trait("Name", "RootFolderActiveMoveBoundaryTests")]
[Trait("Category", "Application")]
public sealed class RootFolderActiveMoveBoundaryTests : BaseTests
{
    [Fact]
    public async Task DeleteAsync_ActiveMoveEndpointContainsRoot_IsBlocked()
    {
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        var factory = new TestDbContextFactory(options);
        var repository = new EfRootFolderRepository(
            factory,
            Mock.Of<ILogger<EfRootFolderRepository>>());
        var moveParent = Path.GetFullPath(Path.Join(Path.GetTempPath(), $"move-parent-{Guid.NewGuid():N}"));
        var rootPath = Path.Join(moveParent, "nested-root");
        var root = new RootFolder
        {
            Name = "Nested",
            Path = rootPath
        };
        await repository.AddAsync(root);
        var moveQueue = new Mock<IMoveQueueService>();
        moveQueue.Setup(service => service.GetFilesystemBlockingJobsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new MoveJob
                {
                    Id = Guid.NewGuid(),
                    SourcePath = moveParent,
                    RequestedPath = Path.Join(Path.GetTempPath(), $"move-target-{Guid.NewGuid():N}"),
                    Status = MoveJobStatus.Running
                }
            ]);
        var semantics = FileSystemPathSemantics.CurrentHostDefault;
        var resolver = new Mock<IFileSystemSemanticsResolver>();
        resolver.Setup(service => service.ResolveAsync(
                It.IsAny<string>(),
                It.IsAny<FileSystemCaseSensitivityMode>(),
                It.IsAny<CancellationToken>()))
            .Returns((string path, FileSystemCaseSensitivityMode _, CancellationToken _) =>
                ValueTask.FromResult(new FileSystemSemanticsResolution(
                    semantics,
                    PathIdentityState.Valid,
                    Path.GetPathRoot(path) ?? path,
                    CanonicalPath: Path.GetFullPath(path))));
        var relocationService = new Mock<IRootFolderRelocationService>();
        relocationService.Setup(service => service.GetActiveForRootAsync(
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RootFolderRelocation?)null);
        var service = new RootFolderService(
            repository,
            Mock.Of<ILogger<RootFolderService>>(),
            resolver.Object,
            moveQueue.Object,
            relocationService.Object,
            new FilesystemMutationCoordinator(),
            new AudiobookOperationCoordinator());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeleteAsync(root.Id));

        Assert.Contains("unresolved move", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(await repository.GetByIdAsync(root.Id));
    }

    [Fact]
    public async Task ReassignAudiobooksAndRemoveAsync_ActiveMoveEndpointContainsRoot_IsBlockedInTransaction()
    {
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        var factory = new TestDbContextFactory(options);
        var repository = new EfRootFolderRepository(
            factory,
            Mock.Of<ILogger<EfRootFolderRepository>>());
        var moveParent = Path.GetFullPath(Path.Join(Path.GetTempPath(), $"move-parent-{Guid.NewGuid():N}"));
        var source = new RootFolder
        {
            Name = "Source",
            Path = Path.Join(moveParent, "nested-source")
        };
        var target = new RootFolder
        {
            Name = "Target",
            Path = Path.GetFullPath(Path.Join(Path.GetTempPath(), $"target-{Guid.NewGuid():N}"))
        };
        await repository.AddAsync(source);
        await repository.AddAsync(target);
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.MoveJobs.Add(new MoveJob
            {
                Id = Guid.NewGuid(),
                SourcePath = moveParent,
                RequestedPath = Path.GetFullPath(Path.Join(Path.GetTempPath(), $"move-target-{Guid.NewGuid():N}")),
                Status = MoveJobStatus.RetryScheduled,
                ActiveDeduplicationKey = $"active:{Guid.NewGuid():N}"
            });
            await db.SaveChangesAsync();
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.ReassignAudiobooksAndRemoveAsync(
                source.Id,
                target.Id,
                FileSystemPathSemantics.CurrentHostDefault,
                FileSystemPathSemantics.CurrentHostDefault));

        Assert.Contains("active move", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(await repository.GetByIdAsync(source.Id));
        Assert.NotNull(await repository.GetByIdAsync(target.Id));
    }

    private sealed class TestDbContextFactory(DbContextOptions<ListenArrDbContext> options)
        : IDbContextFactory<ListenArrDbContext>
    {
        public ListenArrDbContext CreateDbContext() => new(options);

        public Task<ListenArrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
