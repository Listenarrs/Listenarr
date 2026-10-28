using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

public partial class MoveJobProcessorTests
{
    [Fact]
    public async Task ProcessJobAsync_Copy_CommitsOwnerPathsBeforeSourceRetirement()
    {
        // Given: the injector disables native rename so this exercises copy retirement.
        var source = FileService.GetTempDirectory("move-owner-before-retirement-src");
        var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = Path.Join(FileService.GetTempPath(), $"move-owner-before-retirement-dst-{Guid.NewGuid():N}");
        var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
            .WithTitle("Publication ordering")
            .WithBasePath(source)
            .WithFilePath(sourceFile)
            .Build());
        var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, target, source);
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        var observer = new ObserveOwnerBeforeSourceRetirement(factory, audiobook.Id, sourceFile);
        var service = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            factory,
            TimeProvider.System,
            observer);
        var processor = ActivatorUtilities.CreateInstance<MoveJobProcessor>(_provider, service);

        // When
        await processor.ProcessJobAsync(job, CancellationToken.None);

        // Then: inspect a fresh context at the destructive transition itself.
        Assert.True(observer.ObservedRetirement);
        Assert.Equal(Path.GetFullPath(target), Path.GetFullPath(observer.OwnerBasePath!));
        Assert.Equal(Path.GetFullPath(Path.Join(target, "book.m4b")), Path.GetFullPath(observer.OwnerFilePath!));
        Assert.Equal(MoveJobStatus.Completed, (await queue.GetJobAsync(job.Id))?.Status);
        Assert.False(File.Exists(sourceFile));
        Assert.Equal("audio", await File.ReadAllTextAsync(Path.Join(target, "book.m4b")));
    }

    [LinuxFact]
    public async Task ProcessJobAsync_Copy_SourceReplacedAfterPublication_RetainsReplacement()
    {
        var source = FileService.GetTempDirectory("move-live-source-substitution-src");
        var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = Path.Join(FileService.GetTempPath(), $"move-live-source-substitution-dst-{Guid.NewGuid():N}");
        var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
            .WithTitle("Live source substitution")
            .WithBasePath(source)
            .WithFilePath(sourceFile)
            .Build());
        var (queue, job) = await CreateQueuedMoveJobAsync(audiobook, target, source);
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        var service = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            factory, TimeProvider.System, new ReplaceSourceAfterPublication(sourceFile));
        var processor = ActivatorUtilities.CreateInstance<MoveJobProcessor>(_provider, service);

        await processor.ProcessJobAsync(job, CancellationToken.None);

        var completed = Assert.IsType<MoveJob>(await queue.GetJobAsync(job.Id));
        Assert.Equal(MoveJobStatus.Completed, completed.Status);
        Assert.True(MoveJobPublicProjection.IsSourceRetained(completed));
        Assert.Equal("audio", await File.ReadAllTextAsync(sourceFile));
        Assert.Equal("audio", await File.ReadAllTextAsync(sourceFile + ".original"));
        Assert.Equal("audio", await File.ReadAllTextAsync(Path.Join(target, "book.m4b")));
    }

    private sealed class ReplaceSourceAfterPublication(string sourceFile) : IMoveFaultInjector
    {
        public async Task AfterPublishedAsync(Guid jobId, CancellationToken cancellationToken)
        {
            File.Move(sourceFile, sourceFile + ".original");
            await File.WriteAllTextAsync(sourceFile, "audio", cancellationToken);
        }
    }

    private sealed class ObserveOwnerBeforeSourceRetirement(
        IDbContextFactory<ListenArrDbContext> factory,
        int audiobookId,
        string sourceFile) : IMoveFaultInjector
    {
        public bool ObservedRetirement { get; private set; }
        public string? OwnerBasePath { get; private set; }
        public string? OwnerFilePath { get; private set; }

        public void OnSourceCleanupMutation(Guid jobId, SourceCleanupFaultPoint faultPoint)
        {
            if (faultPoint != SourceCleanupFaultPoint.AfterMarkerlessSourceDeleteAuthorizedState)
            {
                return;
            }

            using var db = factory.CreateDbContext();
            var owner = db.Audiobooks.AsNoTracking().Single(book => book.Id == audiobookId);
            OwnerBasePath = owner.BasePath;
            OwnerFilePath = owner.FilePath;
            Assert.True(File.Exists(sourceFile));
            ObservedRetirement = true;
        }
    }
}
