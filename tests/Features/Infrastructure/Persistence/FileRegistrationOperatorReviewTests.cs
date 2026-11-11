using System.Security.Cryptography;
using Listenarr.Application.Common.Exceptions;
using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.Persistence;

public sealed class FileRegistrationOperatorReviewTests : BaseTests
{
    private sealed class Factory(DbContextOptions<ListenArrDbContext> options) : IDbContextFactory<ListenArrDbContext>
    {
        public ListenArrDbContext CreateDbContext() => new(options);
    }

    private async Task<(Factory Factory, FileRegistrationRecoveryService Service, Guid Id, string Source, string Target)> SeedAsync()
    {
        var root = FileService.GetTempPath();
        var source = Path.Join(root, "source.m4b");
        var target = Path.Join(root, "target.m4b");
        await File.WriteAllTextAsync(source, "complete original");
        await File.WriteAllTextAsync(target, "partial");
        var factory = new Factory(new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite($"Data Source={Path.Join(root, "review.db")};Pooling=False").Options);
        var id = Guid.NewGuid();
        await using var db = factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        db.FileMutationJournals.Add(new FileMutationJournal
        {
            OperationId = id,
            ProtocolVersion = -1,
            Action = FileAction.HardlinkCopy,
            SourcePath = source,
            DestinationPath = target,
            AudiobookId = 865,
            SourceLength = new FileInfo(source).Length,
            SourceSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source))),
            State = FileMutationJournalState.NeedsAttention,
            Error = "Original repair evidence"
        });
        await db.SaveChangesAsync();
        return (factory, new FileRegistrationRecoveryService(factory,
            new FileMover(NullLogger<FileMover>.Instance, dbContextFactory: factory),
            TimeProvider.System, NullLogger<FileRegistrationRecoveryService>.Instance), id, source, target);
    }

    [Fact]
    public async Task Resolution_PreservesIncompleteContentAndAudit_AcrossRecoveryAndReplay()
    {
        var (factory, service, id, source, target) = await SeedAsync();
        var review = await service.InspectAsync(id);
        Assert.Contains("completeness cannot be verified", review.ContentEvidence, StringComparison.OrdinalIgnoreCase);
        var result = await service.ResolveAsync(id, review.ReviewToken, "Retaining incomplete copy for manual repair", true, "tester");
        Assert.Equal("OperatorResolved", result.State);
        Assert.False(result.CanResolve);
        Assert.Equal("tester", result.ResolvedBy);
        Assert.Equal("Original repair evidence", result.Error);
        Assert.Equal("complete original", await File.ReadAllTextAsync(source));
        Assert.Equal("partial", await File.ReadAllTextAsync(target));
        await service.ReconcileAsync();
        await service.ResolveAsync(id, review.ReviewToken, "Retaining incomplete copy for manual repair", true, "another caller");
        Assert.False(await new FileRegistrationRecoveryProbe(factory).HasBlockingAsync(865));
        await using var db = factory.CreateDbContext();
        var row = await db.FileMutationJournals.SingleAsync();
        Assert.Equal(FileMutationJournalState.OperatorResolved, row.State);
        Assert.NotNull(row.ResolutionReviewJson);
        Assert.Equal("tester", row.ResolvedBy);
        Assert.False(FileMutationJournalLifecycle.MayRetireSource(row.State));
        var store = new EfFileMutationJournalStore(factory, TimeProvider.System);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AdvanceAsync(id,
            FileMutationJournalState.SourceDeletionAuthorized, null, 865, null, default));
        Assert.Empty(await db.AudiobookFiles.ToListAsync());
    }

    [Fact]
    public async Task MatchingFileEvidence_DoesNotClaimWholeBookCompleteness()
    {
        var (_, service, id, source, target) = await SeedAsync();
        await File.WriteAllBytesAsync(target, await File.ReadAllBytesAsync(source));
        var review = await service.InspectAsync(id);
        Assert.StartsWith("Destination matches this journal's file evidence", review.ContentEvidence);
        Assert.Contains("whole-book completeness cannot be verified", review.ContentEvidence);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Resolution_RejectsChangedFileOrOwnership(bool changeFile)
    {
        var (factory, service, id, _, target) = await SeedAsync();
        var review = await service.InspectAsync(id);
        if (changeFile) await File.WriteAllTextAsync(target, "replaced file");
        else
        {
            await using var db = factory.CreateDbContext();
            await db.FileMutationJournals.ExecuteUpdateAsync(s => s.SetProperty(j => j.AudiobookId, 999));
        }
        await Assert.ThrowsAsync<ApplicationConflictException>(() =>
            service.ResolveAsync(id, review.ReviewToken, "reviewed", true, "tester"));
        await using var verify = factory.CreateDbContext();
        Assert.Equal(FileMutationJournalState.NeedsAttention, (await verify.FileMutationJournals.SingleAsync()).State);
    }

    [Fact]
    public async Task Resolution_ReleasesOnlySelectedOperation_AndRequiresAcknowledgement()
    {
        var (factory, service, id, source, target) = await SeedAsync();
        var other = Guid.NewGuid();
        await using (var db = factory.CreateDbContext())
        {
            db.FileMutationJournals.Add(new FileMutationJournal
            {
                OperationId = other,
                AudiobookId = 865,
                SourcePath = source,
                DestinationPath = target,
                Action = FileAction.Copy,
                State = FileMutationJournalState.NeedsAttention
            });
            await db.SaveChangesAsync();
        }
        var review = await service.InspectAsync(id);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ResolveAsync(id, review.ReviewToken, "reviewed", false, "tester"));
        await service.ResolveAsync(id, review.ReviewToken, "reviewed", true, "tester");
        Assert.True(await new FileRegistrationRecoveryProbe(factory).HasBlockingAsync(865));
        var secondReview = await service.InspectAsync(other);
        await service.ResolveAsync(other, secondReview.ReviewToken, "reviewed", true, "tester");
        Assert.False(await new FileRegistrationRecoveryProbe(factory).HasBlockingAsync(865));
        await Assert.ThrowsAsync<ApplicationConflictException>(() => service.ResolveAsync(id, "bad".PadRight(64, '0'), "reviewed", true, "tester"));
    }
}
