using Listenarr.Tests.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.Persistence;

[Trait("Name", "FileRegistrationReviewListPagingTests")]
[Trait("Category", "FileRegistrationRecovery")]
public sealed class FileRegistrationReviewListPagingTests : BaseTests
{
    private const int NeedsAttentionRows = 260;
    private const int ResolvedRows = 40;
    private const int TotalRows = NeedsAttentionRows + ResolvedRows;

    private sealed class Factory(DbContextOptions<ListenArrDbContext> options) : IDbContextFactory<ListenArrDbContext>
    {
        public ListenArrDbContext CreateDbContext() => new(options);
    }

    private async Task<FileRegistrationRecoveryService> SeedAsync()
    {
        var factory = new Factory(new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite($"Data Source={Path.Join(FileService.GetTempPath(), "paging.db")};Pooling=False").Options);
        await using var db = factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < TotalRows; i++)
        {
            db.FileMutationJournals.Add(Journal(i < NeedsAttentionRows
                ? FileMutationJournalState.NeedsAttention
                : FileMutationJournalState.OperatorResolved, i, start.AddMinutes(i)));
        }
        // Rows outside the reviewable set must never be counted or returned.
        var linked = Journal(FileMutationJournalState.NeedsAttention, TotalRows, start);
        linked.AudiobookFileId = 1;
        db.FileMutationJournals.Add(linked);
        var otherState = Journal(FileMutationJournalState.Completed, TotalRows + 1, start);
        db.FileMutationJournals.Add(otherState);
        await db.SaveChangesAsync();
        return new FileRegistrationRecoveryService(factory,
            new FileMover(NullLogger<FileMover>.Instance, dbContextFactory: factory),
            TimeProvider.System, NullLogger<FileRegistrationRecoveryService>.Instance);
    }

    private static FileMutationJournal Journal(FileMutationJournalState state, int index, DateTime updatedAt) => new()
    {
        OperationId = Guid.NewGuid(),
        ProtocolVersion = -1,
        Action = FileAction.HardlinkCopy,
        SourcePath = $"/library/source-{index}.m4b",
        DestinationPath = $"/library/target-{index}.m4b",
        AudiobookId = index + 1,
        State = state,
        UpdatedAt = updatedAt
    };

    private static FileRegistrationRecoveryController ControllerFor(FileRegistrationRecoveryService service) =>
        new(service, null!) { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };

    [Fact]
    public async Task List_DefaultsToTwoHundredRows_AndReportsTheFullTotal()
    {
        var page = await (await SeedAsync()).ListReviewsAsync();
        Assert.Equal(TotalRows, page.TotalCount);
        Assert.Equal(200, page.Items.Count);
        Assert.All(page.Items, r => Assert.Equal("NeedsAttention", r.State));
    }

    [Fact]
    public async Task List_SkipPagesTheRemainder_WithNeedsAttentionBeforeResolved()
    {
        var service = await SeedAsync();
        var first = await service.ListReviewsAsync(200, 0);
        var rest = await service.ListReviewsAsync(200, 200);
        Assert.Equal(TotalRows - 200, rest.Items.Count);
        Assert.Equal(NeedsAttentionRows - 200, rest.Items.Count(r => r.State == "NeedsAttention"));
        Assert.Equal(ResolvedRows, rest.Items.Count(r => r.State == "OperatorResolved"));
        Assert.Equal("OperatorResolved", rest.Items[^1].State);
        Assert.Equal("NeedsAttention", rest.Items[0].State);
        var ids = first.Items.Concat(rest.Items).Select(r => r.OperationId).ToList();
        Assert.Equal(TotalRows, ids.Distinct().Count());
        Assert.Equal(TotalRows, rest.TotalCount);
    }

    [Fact]
    public async Task List_OrdersNewestFirstWithinEachState()
    {
        var page = await (await SeedAsync()).ListReviewsAsync(5, 0);
        // The newest NeedsAttention row was seeded last among them (audiobook id = index + 1).
        Assert.Equal([NeedsAttentionRows, NeedsAttentionRows - 1, NeedsAttentionRows - 2,
            NeedsAttentionRows - 3, NeedsAttentionRows - 4], page.Items.Select(r => r.AudiobookId!.Value));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1, 1)]
    [InlineData(5000, 200)]
    public async Task List_ClampsTake(int requested, int expected)
    {
        var page = await (await SeedAsync()).ListReviewsAsync(requested, 0);
        Assert.Equal(expected, page.Items.Count);
        Assert.Equal(TotalRows, page.TotalCount);
    }

    [Fact]
    public async Task List_NegativeSkipIsTreatedAsZero_AndSkipPastTheEndIsEmptyWithTotal()
    {
        var service = await SeedAsync();
        var negative = await service.ListReviewsAsync(3, -10);
        Assert.Equal(await IdsAsync(service, 3, 0), negative.Items.Select(r => r.OperationId));
        var beyond = await service.ListReviewsAsync(200, 10_000);
        Assert.Empty(beyond.Items);
        Assert.Equal(TotalRows, beyond.TotalCount);
    }

    private static async Task<List<Guid>> IdsAsync(FileRegistrationRecoveryService service, int take, int skip) =>
        (await service.ListReviewsAsync(take, skip)).Items.Select(r => r.OperationId).ToList();

    [Fact]
    public async Task Controller_SetsTotalCountHeader_AndKeepsTheBodyAsAnArray()
    {
        var controller = ControllerFor(await SeedAsync());
        var body = await controller.List();
        Assert.Equal(200, body.Count);
        Assert.Equal(TotalRows.ToString(), controller.Response.Headers["X-Total-Count"].ToString());
        var rest = await controller.List(take: 200, skip: 200);
        Assert.Equal(TotalRows - 200, rest.Count);
        Assert.Equal(TotalRows.ToString(), controller.Response.Headers["X-Total-Count"].ToString());
    }
}
