/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using System.Data.Common;
using System.Text;
using System.Text.RegularExpressions;
using Listenarr.Infrastructure.Persistence.Repositories;
using Listenarr.Tests.Common;
using Listenarr.Tests.Features.Infrastructure.Persistence.LargeLibrary;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.Persistence;

/// <summary>
/// Runs EXPLAIN QUERY PLAN on the SQL EF really generates for the rescan
/// candidate page, captured from a command interceptor rather than retyped,
/// on a representative large library.
/// </summary>
[Trait("Name", "MetadataRescanQueryPlanTests")]
[Trait("Category", "Persistence")]
public sealed partial class MetadataRescanQueryPlanTests : BaseTests
{
    private const string PartialIndexName = "IX_AudiobookFiles_MetadataRescanDue";
    private const int PageSize = 128;
    private const int ExcludedIdCount = 20;
    private static readonly DateTime ClockUtc = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly ITestOutputHelper _output;

    public MetadataRescanQueryPlanTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData(LargeLibraryVariant.Main)]
    [InlineData(LargeLibraryVariant.ForeignSyntaxOldest)]
    [InlineData(LargeLibraryVariant.ForeignBaseOldest)]
    public async Task CandidatePage_UsesPartialIndex_BeforeAndAfterAnalyze(LargeLibraryVariant variant)
    {
        await using var fixture = await new LargeLibraryFixtureBuilder(ClockUtc, variant).BuildAsync();
        var firstPage = await CapturePageAsync(fixture, []);
        Assert.Equal(PageSize, firstPage.Rows);
        int[] excludedIds = [.. firstPage.Ids.Take(ExcludedIdCount)];
        var failures = new List<string>();

        foreach (var analyzed in new[] { false, true })
        {
            if (analyzed)
            {
                await ExecuteAsync(fixture, "ANALYZE;");
            }

            foreach (var excluded in new[] { Array.Empty<int>(), excludedIds })
            {
                var page = await CapturePageAsync(fixture, excluded);
                var label = $"{variant}, {(analyzed ? "after" : "before")} ANALYZE, "
                    + $"{excluded.Length} excluded ids";
                _output.WriteLine($"== {label}\n{page.Sql}\n-- plan\n{page.Plan}");
                failures.AddRange(DescribePlanProblems(page).Select(problem => $"{label}: {problem}"));
                Assert.Equal(PageSize, page.Rows);
                Assert.Empty(page.Ids.Intersect(excluded));
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Theory]
    [InlineData(LargeLibraryVariant.ForeignSyntaxOldest)]
    [InlineData(LargeLibraryVariant.ForeignBaseOldest)]
    public async Task LargeLibrary_HasTheStatedMix(LargeLibraryVariant variant)
    {
        await using var fixture = await new LargeLibraryFixtureBuilder(ClockUtc, variant).BuildAsync();
        var books = LargeLibraryFixtureBuilder.LoadBooks();
        var host = LargeLibraryFixtureBuilder.HostSyntax.ToString();
        var foreign = LargeLibraryFixtureBuilder.ForeignSyntax.ToString();
        var due = $"\"MetadataRescanDueUtc\" <= '{ClockUtc:yyyy-MM-dd HH:mm:ss}'";
        var missing = "(\"DurationSeconds\" IS NULL OR \"Format\" IS NULL OR \"SampleRate\" IS NULL OR \"Size\" IS NULL OR \"Size\" = 64)";

        Assert.Equal(LargeLibraryFixtureBuilder.AudiobookCount, await ScalarAsync(fixture, "SELECT COUNT(*) FROM \"Audiobooks\""));
        Assert.Equal(LargeLibraryFixtureBuilder.FileCount, await ScalarAsync(fixture, "SELECT COUNT(*) FROM \"AudiobookFiles\""));
        Assert.Equal(books.Count, await ScalarAsync(fixture, "SELECT COUNT(DISTINCT \"Asin\") FROM \"Audiobooks\""));
        Assert.Equal(10_000, await ScalarAsync(fixture, "SELECT COUNT(*) FROM \"Audiobooks\" WHERE NOT \"Monitored\""));
        Assert.Equal(3_000, await ScalarAsync(fixture, $"SELECT COUNT(*) FROM \"AudiobookFiles\" WHERE \"PathSyntax\" = '{foreign}' AND {missing}"));
        Assert.InRange(
            await ScalarAsync(fixture, $"SELECT COUNT(*) FROM \"AudiobookFiles\" WHERE \"PathSyntax\" = '{host}' AND {missing}"),
            13_000,
            16_000);
        Assert.InRange(
            await ScalarAsync(fixture, $"SELECT COUNT(*) FROM \"AudiobookFiles\" WHERE \"PathSyntax\" = '{host}' AND {missing} AND {due}"),
            6_500,
            8_000);
        // The builder's own tallies agree with what the database holds.
        Assert.Equal(fixture.Counts.MissingMetadata, await ScalarAsync(fixture, $"SELECT COUNT(*) FROM \"AudiobookFiles\" WHERE {missing}"));
        Assert.Equal(fixture.Counts.DueMissingMetadata, await ScalarAsync(fixture, $"SELECT COUNT(*) FROM \"AudiobookFiles\" WHERE {missing} AND {due}"));
        Assert.Equal(fixture.Counts.DueMissingForeignSyntax, await ScalarAsync(fixture, $"SELECT COUNT(*) FROM \"AudiobookFiles\" WHERE \"PathSyntax\" = '{foreign}' AND {missing} AND {due}"));
        Assert.Equal(fixture.Counts.DueMissingForeignBase, await ScalarAsync(fixture, $"SELECT COUNT(*) FROM \"AudiobookFiles\" WHERE \"PathSyntax\" IS NULL AND {missing} AND {due}"));
        // The unrescannable rows hold every due time older than any host row.
        var oldestHostDue = $"(SELECT MIN(\"MetadataRescanDueUtc\") FROM \"AudiobookFiles\" WHERE \"PathSyntax\" = '{host}')";
        var oldRows = variant == LargeLibraryVariant.ForeignSyntaxOldest
            ? $"\"PathSyntax\" = '{foreign}'"
            : "\"PathSyntax\" IS NULL";
        Assert.Equal(
            variant == LargeLibraryVariant.ForeignSyntaxOldest ? 3_000 : 1_500,
            await ScalarAsync(fixture, $"SELECT COUNT(*) FROM \"AudiobookFiles\" WHERE {oldRows} AND \"MetadataRescanDueUtc\" < {oldestHostDue}"));
    }

    [Fact]
    public async Task CandidatePage_WithoutThePartialIndex_FailsThePlanAssertions()
    {
        // Control for the harness: with the index gone the same captured SQL
        // must be reported as a scan or a sort, or the assertions prove nothing.
        await using var fixture = await new LargeLibraryFixtureBuilder(ClockUtc).BuildAsync();
        await ExecuteAsync(fixture, $"DROP INDEX \"{PartialIndexName}\";");

        var page = await CapturePageAsync(fixture, []);
        _output.WriteLine($"{page.Sql}\n-- plan without the index\n{page.Plan}");

        Assert.NotEmpty(DescribePlanProblems(page));
    }

    [Fact]
    public async Task ScheduleAgnosticQuery_DoesNotReadThePartialIndex()
    {
        // The id-ordered manual-batch query keeps its own plan; its captured
        // plan is recorded as the "before" side of the comparison.
        await using var fixture = await new LargeLibraryFixtureBuilder(ClockUtc).BuildAsync();
        var capture = new CommandCapture();
        await using var context = new ListenArrDbContext(fixture.CreateOptions(capture));
        await context.Database.OpenConnectionAsync();
        Assert.Equal(20, (await new EfAudiobookFileRepository(context).GetMissingMetadataAsync(20)).Count);

        var command = Assert.Single(capture.Commands);
        var plan = await ExplainAsync(context.Database.GetDbConnection(), command);
        _output.WriteLine($"{command.Sql}\n-- plan\n{plan}");

        Assert.DoesNotContain(PartialIndexName, plan, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CapturedPredicate_CarriesTheIndexFilterTermForTerm()
    {
        // SQLite uses a partial index only when the query repeats its filter,
        // so the configured filter must appear verbatim in EF's WHERE clause.
        await using var fixture = await new LargeLibraryFixtureBuilder(ClockUtc).BuildAsync();
        var page = await CapturePageAsync(fixture, []);
        string filter;
        await using (var context = new ListenArrDbContext(fixture.CreateOptions()))
        {
            filter = context.Model.FindEntityType(typeof(AudiobookFile))!
                .GetIndexes()
                .Single(index => index.GetDatabaseName() == PartialIndexName)
                .GetFilter()!;
        }

        var unqualified = page.Sql.Replace($"\"{page.FileAlias}\".", string.Empty, StringComparison.Ordinal);
        Assert.Contains($"({filter})", unqualified, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuePage_FutureRowsAreExcludedInSql()
    {
        // 10,000 rows due in the future sort after 5 due rows; only the due
        // rows may leave the database, so the due comparison must be in SQL.
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var capture = new CommandCapture();
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(capture)
            .Options;
        await using var context = new ListenArrDbContext(options);
        await context.Database.EnsureCreatedAsync();
        var book = LargeLibraryFixtureBuilder.LoadBooks()[0];
        var audiobook = new Audiobook { Title = book.Title, Authors = [.. book.Authors], BasePath = HostLibraryPath("book") };
        context.Audiobooks.Add(audiobook);
        var dueFiles = new List<AudiobookFile>();
        for (var index = 0; index < 10_005; index++)
        {
            var file = AudiobookFile.CreateUnresolved($"file-{index:D5}.m4b");
            file.Audiobook = audiobook;
            file.MetadataRescanDueUtc = index < 5
                ? ClockUtc.AddMinutes(-index - 1)
                : ClockUtc.AddMinutes(index);
            context.AudiobookFiles.Add(file);
            if (index < 5)
            {
                dueFiles.Add(file);
            }
        }

        await context.SaveChangesAsync();
        capture.Commands.Clear();

        var page = await new EfAudiobookFileRepository(context)
            .GetMetadataRescanDuePageAsync(ClockUtc, [], PageSize);

        Assert.Equal(
            dueFiles.Select(file => file.Id).Order(),
            page.Select(candidate => candidate.File.Id).Order());
        var command = Assert.Single(capture.Commands);
        Assert.Matches(@"""MetadataRescanDueUtc"" <= @\w+", command.Sql);
        Assert.Equal((long)dueFiles.Count, await CountRowsAsync(connection, command));
    }

    private static string HostLibraryPath(string name) =>
        OperatingSystem.IsWindows() ? $"C:\\library\\{name}" : $"/library/{name}";

    // Re-runs the captured statement to count the rows it hands to EF.
    private static async Task<long> CountRowsAsync(DbConnection connection, CapturedCommand captured)
    {
        await using var command = CreateCommand(connection, $"SELECT COUNT(*) FROM ({captured.Sql})", captured);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static IEnumerable<string> DescribePlanProblems(CapturedPage page)
    {
        if (!page.Plan.Contains($"USING INDEX {PartialIndexName}", StringComparison.Ordinal))
        {
            yield return $"the plan does not use {PartialIndexName}";
        }

        var fileScan = new Regex($@"\bSCAN ({Regex.Escape(page.FileAlias)}|AudiobookFiles)\b");
        if (fileScan.IsMatch(page.Plan))
        {
            yield return "the plan scans AudiobookFiles";
        }

        if (page.Plan.Contains("USE TEMP B-TREE FOR ORDER BY", StringComparison.Ordinal))
        {
            yield return "the plan sorts in a temp B-tree";
        }
    }

    private static async Task<CapturedPage> CapturePageAsync(
        LargeLibraryFixture fixture,
        IReadOnlyCollection<int> excludedIds)
    {
        var capture = new CommandCapture();
        await using var context = new ListenArrDbContext(fixture.CreateOptions(capture));
        // Keep EF's connection open so the plan is explained on it.
        await context.Database.OpenConnectionAsync();
        var page = await new EfAudiobookFileRepository(context)
            .GetMetadataRescanDuePageAsync(fixture.ClockUtc, excludedIds, PageSize);

        var command = Assert.Single(capture.Commands);
        var alias = FileAliasPattern().Match(command.Sql);
        Assert.True(alias.Success, $"No AudiobookFiles alias in:\n{command.Sql}");
        return new CapturedPage(
            command.Sql,
            await ExplainAsync(context.Database.GetDbConnection(), command),
            alias.Groups["alias"].Value,
            page.Count,
            page.Select(candidate => candidate.File.Id).ToList());
    }

    private static async Task<string> ExplainAsync(DbConnection connection, CapturedCommand captured)
    {
        await using var command = CreateCommand(connection, "EXPLAIN QUERY PLAN " + captured.Sql, captured);
        var depthById = new Dictionary<long, int> { [0] = -1 };
        var plan = new StringBuilder();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var depth = depthById.GetValueOrDefault(reader.GetInt64(1), -1) + 1;
            depthById[reader.GetInt64(0)] = depth;
            plan.Append(' ', depth * 2).AppendLine(reader.GetString(3));
        }

        return plan.ToString().TrimEnd();
    }

    private static DbCommand CreateCommand(DbConnection connection, string sql, CapturedCommand captured)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in captured.Parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        return command;
    }

    private static async Task<int> ScalarAsync(LargeLibraryFixture fixture, string sql)
    {
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task ExecuteAsync(LargeLibraryFixture fixture, string sql)
    {
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    [GeneratedRegex("FROM \"AudiobookFiles\" AS \"(?<alias>[^\"]+)\"")]
    private static partial Regex FileAliasPattern();

    private sealed record CapturedPage(
        string Sql,
        string Plan,
        string FileAlias,
        int Rows,
        IReadOnlyList<int> Ids);

    internal sealed record CapturedCommand(
        string Sql,
        IReadOnlyList<(string Name, object? Value)> Parameters);

    /// <summary>Records the text and parameter values of every query EF runs.</summary>
    internal sealed class CommandCapture : DbCommandInterceptor
    {
        public List<CapturedCommand> Commands { get; } = [];

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Record(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }

        private void Record(DbCommand command) => Commands.Add(new CapturedCommand(
            command.CommandText,
            command.Parameters
                .Cast<DbParameter>()
                .Select(parameter => (parameter.ParameterName, (object?)parameter.Value))
                .ToList()));
    }
}
