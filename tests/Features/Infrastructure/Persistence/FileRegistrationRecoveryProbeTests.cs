using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Persistence;

[Trait("Name", "FileRegistrationRecoveryProbeTests")]
[Trait("Category", "Infrastructure")]
public sealed class FileRegistrationRecoveryProbeTests : BaseTests
{
    [Theory]
    [InlineData(FileAction.Move)]
    [InlineData(FileAction.Copy)]
    [InlineData(FileAction.HardlinkCopy)]
    public async Task HasBlockingAsync_AllRegistrationPublicationActions_Block(
        FileAction action)
    {
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using (var db = new ListenArrDbContext(options))
        {
            db.FileMutationJournals.Add(new FileMutationJournal
            {
                OperationId = Guid.NewGuid(),
                ProtocolVersion = FileMutationProtocol.Current,
                Action = action,
                SourcePath = Path.Join(Path.GetTempPath(), "incoming.m4b"),
                DestinationPath = Path.Join(Path.GetTempPath(), "library.m4b"),
                SourceParentDirectoryObjectIdentity = "source-parent",
                DestinationParentDirectoryObjectIdentity = "destination-parent",
                SourcePhysicalObjectIdentity = "source-generation",
                TargetPhysicalObjectIdentity = "target-generation",
                SourceLength = 5,
                State = FileMutationJournalState.RegistrationCommitted,
                AudiobookId = 42,
                AudiobookFileId = null
            });
            await db.SaveChangesAsync();
        }

        var probe = new FileRegistrationRecoveryProbe(new TestDbFactory(options));

        Assert.True(await probe.HasBlockingAsync(42));
    }

    [Fact]
    public async Task HasBlockingBoundaryAsync_AnonymousVerifiedPublicationUnderBoundary_Blocks()
    {
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var root = Path.Join(
            Path.GetTempPath(),
            $"registration-probe-boundary-{Guid.NewGuid():N}");
        var source = Path.Join(Path.GetDirectoryName(root)!, "incoming", "book.m4b");
        var destination = Path.Join(root, "Author", "Book", "book.m4b");

        await using (var db = new ListenArrDbContext(options))
        {
            db.FileMutationJournals.Add(new FileMutationJournal
            {
                OperationId = Guid.NewGuid(),
                ProtocolVersion = FileMutationProtocol.Current,
                Action = FileAction.Copy,
                SourcePath = source,
                DestinationPath = destination,
                SourceParentDirectoryObjectIdentity = "source-parent",
                DestinationParentDirectoryObjectIdentity = "destination-parent",
                SourcePhysicalObjectIdentity = "source-generation",
                TargetPhysicalObjectIdentity = "target-generation",
                SourceLength = 5,
                State = FileMutationJournalState.TargetVerified,
                AudiobookId = null,
                AudiobookFileId = null
            });
            await db.SaveChangesAsync();
        }

        var probe = new FileRegistrationRecoveryProbe(new TestDbFactory(options));

        var blockers = await probe.GetBlockingBoundaryAsync(
            root,
            FileSystemPathSemantics.CurrentHostDefault);

        var blocker = Assert.Single(blockers);
        Assert.Equal(FileMutationJournalState.TargetVerified, blocker.JournalState);
        Assert.Equal(FileRegistrationRecoveryDisposition.AutomaticRecovery, blocker.Recoverability);
        Assert.False(blocker.SourceTouchesBoundary);
        Assert.True(blocker.DestinationTouchesBoundary);
    }

    [Theory]
    [InlineData(FileMutationJournalState.Completed)]
    [InlineData(FileMutationJournalState.RolledBack)]
    public async Task HasBlockingBoundaryAsync_TerminalAnonymousPublication_DoesNotBlock(
        FileMutationJournalState state)
    {
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var root = Path.Join(
            Path.GetTempPath(),
            $"registration-probe-completed-{Guid.NewGuid():N}");
        var destination = Path.Join(root, "Author", "Book", "book.m4b");

        await using (var db = new ListenArrDbContext(options))
        {
            db.FileMutationJournals.Add(new FileMutationJournal
            {
                OperationId = Guid.NewGuid(),
                ProtocolVersion = FileMutationProtocol.Current,
                Action = FileAction.Copy,
                SourcePath = Path.Join(Path.GetDirectoryName(root)!, "incoming", "book.m4b"),
                DestinationPath = destination,
                SourceParentDirectoryObjectIdentity = "source-parent",
                DestinationParentDirectoryObjectIdentity = "destination-parent",
                SourcePhysicalObjectIdentity = "source-generation",
                TargetPhysicalObjectIdentity = "target-generation",
                SourceLength = 5,
                State = state,
                AudiobookId = 42,
                AudiobookFileId = null
            });
            await db.SaveChangesAsync();
        }

        var probe = new FileRegistrationRecoveryProbe(new TestDbFactory(options));

        Assert.False(await probe.HasBlockingBoundaryAsync(
            root,
            FileSystemPathSemantics.CurrentHostDefault));
    }

    private sealed class TestDbFactory(DbContextOptions<ListenArrDbContext> options)
        : IDbContextFactory<ListenArrDbContext>
    {
        public ListenArrDbContext CreateDbContext() => new(options);

        public Task<ListenArrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
