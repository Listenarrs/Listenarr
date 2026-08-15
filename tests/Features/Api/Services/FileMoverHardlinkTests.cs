using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Api.Services;

[Trait("Area", "FileSystem")]
[Trait("Name", "FileMoverHardlinkTests")]
[Trait("Category", "FileSystem")]
public sealed class FileMoverHardlinkTests : BaseTests
{
    [Fact]
    public async Task PerformActionOn_HardlinkCopy_CreatesHardlinkOnSameVolume()
    {
        var root = FileService.GetTempDirectory("markerless-hardlink");
        var source = await FileService.GetFileAsync(root, "source.mp3", "audio content");
        var destination = Path.Join(root, "destination.mp3");
        var operationId = Guid.NewGuid();

        Assert.True(await CreateMover().PerformActionOn(
            FileAction.HardlinkCopy,
            source,
            destination,
            operationId));

        Assert.True(File.Exists(source));
        Assert.True(File.Exists(destination));
        await File.WriteAllTextAsync(destination, "linked mutation");
        Assert.Equal("linked mutation", await File.ReadAllTextAsync(source));
        await AssertCompletedJournalAsync(operationId, FileAction.HardlinkCopy);
        AssertNoLibraryArtifacts(root);
    }

    [Fact]
    public async Task PerformActionOn_HardlinkCopy_MissingDestinationDirectoryFailsClosed()
    {
        var root = FileService.GetTempDirectory("markerless-hardlink-missing-parent");
        var source = await FileService.GetFileAsync(root, "source.mp3", "audio content");
        var destinationDirectory = Path.Join(root, "missing");
        var destination = Path.Join(destinationDirectory, "destination.mp3");

        Assert.False(await CreateMover().PerformActionOn(
            FileAction.HardlinkCopy,
            source,
            destination,
            Guid.NewGuid()));

        Assert.False(Directory.Exists(destinationDirectory));
        Assert.False(File.Exists(destination));
        Assert.Equal("audio content", await File.ReadAllTextAsync(source));
        AssertNoLibraryArtifacts(root);
    }

    [Theory]
    [InlineData(FileAction.Copy)]
    [InlineData(FileAction.HardlinkCopy)]
    public async Task PerformActionOn_DifferentExistingDestinationFailsClosed(
        FileAction action)
    {
        var root = FileService.GetTempDirectory("markerless-existing-destination");
        var source = await FileService.GetFileAsync(root, "source.mp3", "new content");
        var destination = await FileService.GetFileAsync(root, "destination.mp3", "old content");

        Assert.False(await CreateMover().PerformActionOn(
            action,
            source,
            destination,
            Guid.NewGuid()));

        Assert.Equal("new content", await File.ReadAllTextAsync(source));
        Assert.Equal("old content", await File.ReadAllTextAsync(destination));
        AssertNoLibraryArtifacts(root);
    }

    [Fact]
    public async Task PerformActionOn_HardlinkCopy_FallsBackToByteCopyWhenHardlinkFails()
    {
        var root = FileService.GetTempDirectory("markerless-hardlink-fallback");
        var source = await FileService.GetFileAsync(root, "source.mp3", "content");
        var destination = Path.Join(root, "destination.mp3");
        var operationId = Guid.NewGuid();
        var mover = CreateMover(() =>
            Task.FromException(new IOException("forced hardlink failure")));

        Assert.True(await mover.PerformActionOn(
            FileAction.HardlinkCopy,
            source,
            destination,
            operationId));

        Assert.Equal("content", await File.ReadAllTextAsync(destination));
        await File.WriteAllTextAsync(destination, "destination changed");
        Assert.Equal("content", await File.ReadAllTextAsync(source));
        await AssertCompletedJournalAsync(operationId, FileAction.HardlinkCopy);
        AssertNoLibraryArtifacts(root);
    }

    [Fact]
    public async Task PerformActionOn_HardlinkCrashBeforeTargetState_ResumesFromPhysicalAlias()
    {
        var root = FileService.GetTempDirectory("markerless-hardlink-prestate-crash");
        var source = await FileService.GetFileAsync(root, "source.mp3", "content");
        var destination = Path.Join(root, "destination.mp3");
        var operationId = Guid.NewGuid();
        var interrupted = CreateMover(
            afterTargetCreatedBeforeState: () =>
                Task.FromException(new IOException("crash before target state")));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            interrupted.PerformActionOn(
                FileAction.HardlinkCopy,
                source,
                destination,
                operationId));
        Assert.True(File.Exists(destination));
        await AssertJournalStateAsync(
            operationId,
            FileMutationJournalState.Planned);

        Assert.True(await CreateMover().PerformActionOn(
            FileAction.HardlinkCopy,
            source,
            destination,
            operationId));
        await AssertCompletedJournalAsync(operationId, FileAction.HardlinkCopy);
        AssertNoLibraryArtifacts(root);
    }

    [Fact]
    public async Task PerformActionOn_CopyCrashBeforeTargetState_FailsClosedOnRetry()
    {
        var root = FileService.GetTempDirectory("markerless-copy-prestate-crash");
        var source = await FileService.GetFileAsync(root, "source.mp3", "content");
        var destination = Path.Join(root, "destination.mp3");
        var operationId = Guid.NewGuid();
        var interrupted = CreateMover(
            afterTargetCreatedBeforeState: () =>
                Task.FromException(new IOException("crash before target state")));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            interrupted.PerformActionOn(
                FileAction.Copy,
                source,
                destination,
                operationId));
        Assert.True(File.Exists(destination));
        Assert.Equal(0, new FileInfo(destination).Length);
        await AssertJournalStateAsync(
            operationId,
            FileMutationJournalState.Planned);

        Assert.False(await CreateMover().PerformActionOn(
            FileAction.Copy,
            source,
            destination,
            operationId));
        await AssertJournalStateAsync(
            operationId,
            FileMutationJournalState.NeedsAttention);
        Assert.Equal("content", await File.ReadAllTextAsync(source));
        Assert.Equal(0, new FileInfo(destination).Length);
        AssertNoLibraryArtifacts(root);
    }

    [Fact]
    public async Task PerformActionOn_CopyCrashAfterTargetState_ResumesAndCompletes()
    {
        var root = FileService.GetTempDirectory("markerless-copy-target-state-crash");
        var source = await FileService.GetFileAsync(root, "source.mp3", "content");
        var destination = Path.Join(root, "destination.mp3");
        var operationId = Guid.NewGuid();
        var interrupted = CreateMover(
            afterTargetState: () =>
                Task.FromException(new IOException("crash after target state")));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            interrupted.PerformActionOn(
                FileAction.Copy,
                source,
                destination,
                operationId));
        await AssertJournalStateAsync(
            operationId,
            FileMutationJournalState.TargetIdentityPersisted);

        Assert.True(await CreateMover().PerformActionOn(
            FileAction.Copy,
            source,
            destination,
            operationId));
        Assert.Equal("content", await File.ReadAllTextAsync(destination));
        await AssertCompletedJournalAsync(operationId, FileAction.Copy);
        AssertNoLibraryArtifacts(root);
    }

    [Fact]
    public async Task PerformActionOn_CopyCrashAfterBytesBeforeVerifiedState_ResumesAndCompletes()
    {
        var root = FileService.GetTempDirectory("markerless-copy-written-crash");
        var source = await FileService.GetFileAsync(root, "source.mp3", "content");
        var destination = Path.Join(root, "destination.mp3");
        var operationId = Guid.NewGuid();
        var interrupted = CreateMover(
            afterTargetWrittenBeforeVerified: () =>
                Task.FromException(new IOException("crash after target write")));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            interrupted.PerformActionOn(
                FileAction.Copy,
                source,
                destination,
                operationId));
        Assert.Equal("content", await File.ReadAllTextAsync(destination));
        await AssertJournalStateAsync(
            operationId,
            FileMutationJournalState.TargetIdentityPersisted);

        Assert.True(await CreateMover().PerformActionOn(
            FileAction.Copy,
            source,
            destination,
            operationId));
        await AssertCompletedJournalAsync(operationId, FileAction.Copy);
        AssertNoLibraryArtifacts(root);
    }

    [Theory]
    [InlineData(FileAction.Copy)]
    [InlineData(FileAction.HardlinkCopy)]
    public async Task PerformActionOn_MissingSourceFailsClosed(FileAction action)
    {
        var root = FileService.GetTempDirectory("markerless-missing-source");
        var source = Path.Join(root, "missing.mp3");
        var destination = Path.Join(root, "destination.mp3");

        Assert.False(await CreateMover().PerformActionOn(
            action,
            source,
            destination,
            Guid.NewGuid()));

        Assert.False(File.Exists(destination));
        AssertNoLibraryArtifacts(root);
    }

    [Fact]
    public async Task PerformActionOn_CopyCreatesIndependentCopy()
    {
        var root = FileService.GetTempDirectory("markerless-copy");
        var source = await FileService.GetFileAsync(root, "source.mp3", "original content");
        var destination = Path.Join(root, "destination.mp3");
        var operationId = Guid.NewGuid();

        Assert.True(await CreateMover().PerformActionOn(
            FileAction.Copy,
            source,
            destination,
            operationId));

        await File.WriteAllTextAsync(destination, "modified content");
        Assert.Equal("original content", await File.ReadAllTextAsync(source));
        await AssertCompletedJournalAsync(operationId, FileAction.Copy);
        AssertNoLibraryArtifacts(root);
    }

    [Fact]
    public async Task PerformActionOn_CompletedCopyWithRecreatedSourceFailsClosed()
    {
        var root = FileService.GetTempDirectory("markerless-copy-recreated-source");
        var source = await FileService.GetFileAsync(root, "source.mp3", "original content");
        var destination = Path.Join(root, "destination.mp3");
        var operationId = Guid.NewGuid();

        Assert.True(await CreateMover().PerformActionOn(
            FileAction.Copy,
            source,
            destination,
            operationId));
        File.Delete(source);
        await File.WriteAllTextAsync(source, "replacement content");

        Assert.False(await CreateMover().PerformActionOn(
            FileAction.Copy,
            source,
            destination,
            operationId));

        Assert.Equal("replacement content", await File.ReadAllTextAsync(source));
        Assert.Equal("original content", await File.ReadAllTextAsync(destination));
        await AssertJournalStateAsync(
            operationId,
            FileMutationJournalState.NeedsAttention);
        AssertNoLibraryArtifacts(root);
    }

    [Fact]
    public async Task PerformActionOn_CompletedHardlinkWithRecreatedSourceFailsClosed()
    {
        var root = FileService.GetTempDirectory("markerless-hardlink-recreated-source");
        var source = await FileService.GetFileAsync(root, "source.mp3", "original content");
        var destination = Path.Join(root, "destination.mp3");
        var operationId = Guid.NewGuid();

        Assert.True(await CreateMover().PerformActionOn(
            FileAction.HardlinkCopy,
            source,
            destination,
            operationId));
        File.Delete(source);
        await File.WriteAllTextAsync(source, "replacement content");

        Assert.False(await CreateMover().PerformActionOn(
            FileAction.HardlinkCopy,
            source,
            destination,
            operationId));

        Assert.Equal("replacement content", await File.ReadAllTextAsync(source));
        Assert.Equal("original content", await File.ReadAllTextAsync(destination));
        await AssertJournalStateAsync(
            operationId,
            FileMutationJournalState.NeedsAttention);
        AssertNoLibraryArtifacts(root);
    }

    [Fact]
    public async Task PerformActionOn_CompletedCopyWithChangedSourceBytesFailsClosed()
    {
        var root = FileService.GetTempDirectory("markerless-copy-changed-source");
        var source = await FileService.GetFileAsync(root, "source.mp3", "original content");
        var destination = Path.Join(root, "destination.mp3");
        var operationId = Guid.NewGuid();

        Assert.True(await CreateMover().PerformActionOn(
            FileAction.Copy,
            source,
            destination,
            operationId));
        await File.WriteAllTextAsync(source, "changed source bytes");

        Assert.False(await CreateMover().PerformActionOn(
            FileAction.Copy,
            source,
            destination,
            operationId));

        Assert.Equal("changed source bytes", await File.ReadAllTextAsync(source));
        Assert.Equal("original content", await File.ReadAllTextAsync(destination));
        await AssertJournalStateAsync(
            operationId,
            FileMutationJournalState.NeedsAttention);
        AssertNoLibraryArtifacts(root);
    }

    [Theory]
    [InlineData(FileAction.Copy)]
    [InlineData(FileAction.HardlinkCopy)]
    public async Task PerformActionOn_CompletedPublicationWithReplacedTargetFailsClosed(
        FileAction action)
    {
        var root = FileService.GetTempDirectory("markerless-replaced-target");
        var source = await FileService.GetFileAsync(root, "source.mp3", "original content");
        var destination = Path.Join(root, "destination.mp3");
        var operationId = Guid.NewGuid();

        Assert.True(await CreateMover().PerformActionOn(
            action,
            source,
            destination,
            operationId));
        File.Delete(destination);
        await File.WriteAllTextAsync(destination, "replacement target");

        Assert.False(await CreateMover().PerformActionOn(
            action,
            source,
            destination,
            operationId));

        Assert.Equal("original content", await File.ReadAllTextAsync(source));
        Assert.Equal("replacement target", await File.ReadAllTextAsync(destination));
        await AssertJournalStateAsync(
            operationId,
            FileMutationJournalState.NeedsAttention);
        AssertNoLibraryArtifacts(root);
    }

    [Theory]
    [InlineData(FileAction.Copy)]
    [InlineData(FileAction.HardlinkCopy)]
    public async Task PerformActionOn_SameContentDestinationIsIdempotent(
        FileAction action)
    {
        var root = FileService.GetTempDirectory("markerless-idempotent-copy");
        var source = await FileService.GetFileAsync(root, "source.mp3", "same content");
        var destination = await FileService.GetFileAsync(root, "destination.mp3", "same content");
        var operationId = Guid.NewGuid();

        Assert.True(await CreateMover().PerformActionOn(
            action,
            source,
            destination,
            operationId));

        Assert.Equal("same content", await File.ReadAllTextAsync(source));
        Assert.Equal("same content", await File.ReadAllTextAsync(destination));
        await AssertCompletedJournalAsync(operationId, action);
        AssertNoLibraryArtifacts(root);
    }

    private FileMover CreateMover(
        Func<Task>? beforeHardlinkCreation = null,
        Func<Task>? afterTargetCreatedBeforeState = null,
        Func<Task>? afterTargetState = null,
        Func<Task>? afterTargetWrittenBeforeVerified = null)
    {
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        return new FileMover(
            new NullLogger<FileMover>(),
            dbContextFactory: factory,
            timeProvider: TimeProvider.System)
        {
            FileMoveLockDirectoryForTest = FileService.GetTempDirectory(
                "markerless-copy-locks"),
            BeforePinnedHardlinkCreationForTestAsync = beforeHardlinkCreation,
            AfterMarkerlessRegistrationTargetCreatedBeforeStateForTestAsync =
                afterTargetCreatedBeforeState,
            AfterMarkerlessRegistrationTargetStateForTestAsync = afterTargetState,
            AfterMarkerlessRegistrationTargetWrittenBeforeVerifiedStateForTestAsync =
                afterTargetWrittenBeforeVerified
        };
    }

    private async Task AssertCompletedJournalAsync(
        Guid operationId,
        FileAction action)
    {
        var journal = await GetJournalAsync(operationId);
        Assert.Equal(FileMutationProtocol.MarkerlessDatabaseState, journal.ProtocolVersion);
        Assert.Equal(action, journal.Action);
        Assert.Equal(FileMutationJournalState.Completed, journal.State);
        Assert.Null(journal.AudiobookId);
    }

    private async Task AssertJournalStateAsync(
        Guid operationId,
        FileMutationJournalState expectedState)
    {
        var journal = await GetJournalAsync(operationId);
        Assert.Equal(expectedState, journal.State);
    }

    private async Task<FileMutationJournal> GetJournalAsync(Guid operationId)
    {
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        return await db.FileMutationJournals
            .AsNoTracking()
            .SingleAsync(candidate => candidate.OperationId == operationId);
    }

    private static void AssertNoLibraryArtifacts(string root)
    {
        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories),
            path => Path.GetFileName(path).Contains(
                ".listenarr-",
                StringComparison.Ordinal));
    }
}
