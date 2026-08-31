using Listenarr.Tests.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Persistence;

[Trait("Area", "Library")]
[Trait("Name", "AudiobookFilePhysicalIdentityTimestampTests")]
[Trait("Category", "Persistence")]
public sealed class AudiobookFilePhysicalIdentityTimestampTests : BaseTests
{
    [Fact]
    public async Task PhysicalIdentityObservedAtUtc_SqliteRoundTripThroughFreshContext_PreservesUtcContract()
    {
        // SQLite has no DateTimeKind; without a materialization conversion a
        // reloaded entity carries Unspecified and violates its own contract.
        // The fresh context is essential: reloading through the saving context
        // returns the already-tracked UTC object and hides the defect.
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite(connection)
            .Options;

        int fileId;
        var observedAt = DateTime.UtcNow;
        await using (var savingContext = new ListenArrDbContext(options))
        {
            await savingContext.Database.EnsureCreatedAsync();
            var audiobook = new Audiobook
            {
                Title = "Persisted Identity Book",
                BasePath = "/library/persisted-identity-book"
            };
            savingContext.Audiobooks.Add(audiobook);
            await savingContext.SaveChangesAsync();

            var file = AudiobookFile.CreateUnresolved(
                "/library/persisted-identity-book/part-01.m4b");
            file.AudiobookId = audiobook.Id;
            file.ApplyPhysicalObjectIdentity(
                "linux-generation:00000001:00000002:0000000000000003:gen:00000004",
                observedAt);
            savingContext.AudiobookFiles.Add(file);
            await savingContext.SaveChangesAsync();
            fileId = file.Id;
        }

        await using var freshContext = new ListenArrDbContext(options);
        var reloaded = await freshContext.AudiobookFiles.SingleAsync(
            candidate => candidate.Id == fileId);

        Assert.NotNull(reloaded.PhysicalIdentityObservedAtUtc);
        Assert.Equal(
            DateTimeKind.Utc,
            reloaded.PhysicalIdentityObservedAtUtc!.Value.Kind);

        // The loaded value must satisfy the entity's own UTC contract: re-applying
        // it through the guarded domain method is exactly what physical-generation
        // snapshots do with database-loaded rows.
        var snapshot = AudiobookFile.CreateUnresolved(reloaded.Path!);
        snapshot.ApplyPhysicalObjectIdentity(
            reloaded.PhysicalObjectIdentity!,
            reloaded.PhysicalIdentityObservedAtUtc.Value);
    }
}
