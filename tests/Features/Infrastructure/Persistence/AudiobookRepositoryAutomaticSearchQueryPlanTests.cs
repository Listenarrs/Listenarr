using System.Data.Common;
using Listenarr.Infrastructure.Persistence.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Persistence;

// A collection Include makes EF add an ORDER BY. On SQLite without planner statistics that turned
// the LastSearchTime index lookup for the eligible books into a scan of every audiobook, so the
// automatic search read the whole table to find a handful of books.
[Trait("Name", "AudiobookRepositoryAutomaticSearchQueryPlanTests")]
[Trait("Category", "Infrastructure")]
public sealed class AudiobookRepositoryAutomaticSearchQueryPlanTests : BaseTests
{
    [Fact]
    public async Task GetMonitoredAudiobooksForSearchAsync_FindsEligibleBooksThroughTheSearchTimeIndex()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();
        var capture = new SelectCapture();
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(capture)
            .Options;

        var cutoff = DateTime.UtcNow;
        await using (var seed = new ListenArrDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync();
            var profile = new QualityProfile { Name = "Any" };
            seed.QualityProfiles.Add(profile);
            for (var i = 0; i < 500; i++)
            {
                seed.Audiobooks.Add(new Audiobook
                {
                    Title = $"Book {i}",
                    Monitored = true,
                    QualityProfile = profile,
                    LastSearchTime = i < 3 ? cutoff.AddDays(-2) : cutoff.AddMinutes(1),
                    SeriesMemberships = [new AudiobookSeriesMembership { SeriesName = "Ayesha", SortOrder = 0 }]
                });
            }
            await seed.SaveChangesAsync();
        }

        await using var db = new ListenArrDbContext(options);
        var selected = await new AudiobookRepository(db).GetMonitoredAudiobooksForSearchAsync(cutoff);

        Assert.Equal(3, selected.Count);
        Assert.All(selected, book => Assert.Single(book.SeriesMemberships));

        var select = Assert.Single(capture.Selects);
        var plan = new List<string>();
        await using (var explain = connection.CreateCommand())
        {
            explain.CommandText = "EXPLAIN QUERY PLAN " + select.CommandText;
            explain.Parameters.AddRange(select.Parameters);
            await using var reader = await explain.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                plan.Add(reader.GetString(3));
            }
        }

        Assert.Contains(plan, step => step.Contains("IX_Audiobooks_LastSearchTime"));
        Assert.DoesNotContain(plan, step => step.StartsWith("SCAN a") && !step.Contains("USING"));
    }

    private sealed class SelectCapture : DbCommandInterceptor
    {
        public List<(string CommandText, List<SqliteParameter> Parameters)> Selects { get; } = [];

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("SELECT") && command.CommandText.Contains("\"Audiobooks\""))
            {
                Selects.Add((command.CommandText, command.Parameters.Cast<DbParameter>()
                    .Select(p => new SqliteParameter(p.ParameterName, p.Value))
                    .ToList()));
            }
            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
