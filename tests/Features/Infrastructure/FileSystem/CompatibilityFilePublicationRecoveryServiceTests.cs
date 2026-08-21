using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.FileSystem;

[Trait("Name", "CompatibilityFilePublicationRecoveryServiceTests")]
[Trait("Category", "Infrastructure")]
public sealed class CompatibilityFilePublicationRecoveryServiceTests : BaseTests
{
    [Fact]
    public async Task ReconcileAsync_PlannedJournalWithTarget_PreservesBothAndMarksAttention()
    {
        var root = FileService.GetTempDirectory("compatibility-recovery-target");
        var source = Path.Join(root, "source.m4b");
        var destination = Path.Join(root, "destination.m4b");
        await File.WriteAllTextAsync(source, "audio");
        await File.WriteAllTextAsync(destination, "partial");
        var operationId = Guid.NewGuid();
        var factory = _provider.GetRequiredService<
            IDbContextFactory<ListenArrDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.CompatibilityFilePublicationJournals.Add(
                new CompatibilityFilePublicationJournal
                {
                    OperationId = operationId,
                    RequestedAction = FileAction.Move,
                    EffectiveAction = FileAction.Copy,
                    SourcePath = source,
                    DestinationPath = destination,
                    SourceLength = 5,
                    SourceSha256 = new string('A', 64),
                    State = CompatibilityFilePublicationState.Planned
                });
            await db.SaveChangesAsync();
        }
        var service = new CompatibilityFilePublicationRecoveryService(
            factory,
            TimeProvider.System,
            NullLogger<CompatibilityFilePublicationRecoveryService>.Instance);

        await service.ReconcileAsync();

        Assert.Equal("audio", await File.ReadAllTextAsync(source));
        Assert.Equal("partial", await File.ReadAllTextAsync(destination));
        await using var verification = await factory.CreateDbContextAsync();
        var journal = await verification.CompatibilityFilePublicationJournals
            .SingleAsync(candidate => candidate.OperationId == operationId);
        Assert.Equal(
            CompatibilityFilePublicationState.NeedsAttention,
            journal.State);
    }
}
