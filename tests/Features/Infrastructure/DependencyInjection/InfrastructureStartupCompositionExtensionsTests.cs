/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
using System.Data.Common;
using Listenarr.Infrastructure.DependencyInjection;
using Listenarr.Tests.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Listenarr.Tests.Features.Infrastructure.DependencyInjection;

[Trait("Name", "InfrastructureStartupCompositionExtensionsTests")]
[Trait("Category", "Infrastructure")]
public sealed class InfrastructureStartupCompositionExtensionsTests : BaseTests
{
    [Fact]
    [Trait("Scenario", "CanaryDataPreflight")]
    public void ApplyListenarrDatabaseMigrations_NormalizesLegacyCanaryDataBeforeConstraints()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var baselineOptions = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite(connection, sqlite =>
                sqlite.MigrationsAssembly(
                    typeof(ListenArrDbContext).Assembly.GetName().Name))
            .Options;
        var moveJobId = Guid.NewGuid();
        using (var baseline = new ListenArrDbContext(baselineOptions))
        {
            baseline.GetService<IMigrator>().Migrate(
                "20260621002226_AddApplicationSettingsConcurrency");
            baseline.Database.ExecuteSqlRaw(
                """
                INSERT INTO "RootFolders" ("Id", "Name", "Path", "IsDefault")
                VALUES (10, 'First', '/library/first', 1),
                       (20, 'Second', '/library/second', 1);
                """);
            baseline.Database.ExecuteSqlInterpolated(
                $"""
                INSERT INTO "MoveJobs" (
                    "Id", "AudiobookId", "RequestedPath", "SourcePath",
                    "EnqueuedAt", "Status", "AttemptCount", "ActiveDeduplicationKey")
                VALUES (
                    {moveJobId}, {501}, {"/library/target"}, {"/library/source"},
                    {DateTime.UtcNow}, {"Processing"}, {0}, {"legacy-canary-key"});
                """);
            baseline.Database.ExecuteSqlRaw(
                """
                INSERT INTO "Audiobooks" ("Id", "Explicit", "Abridged", "Monitored")
                VALUES (601, 0, 0, 1);
                INSERT INTO "AudiobookFiles" ("AudiobookId", "Path", "CreatedAt")
                VALUES (601, '/library/legacy.m4b', CURRENT_TIMESTAMP);
                """);
        }

        var services = new ServiceCollection();
        services.AddDbContextFactory<ListenArrDbContext>(options =>
            options
                .UseSqlite(connection, sqlite =>
                    sqlite.MigrationsAssembly(
                        typeof(ListenArrDbContext).Assembly.GetName().Name))
                .ConfigureWarnings(warnings => warnings.Throw(
                    RelationalEventId.NonTransactionalMigrationOperationWarning)));
        using var provider = services.BuildServiceProvider();

        provider.ApplyListenarrDatabaseMigrations();

        using var verification = provider
            .GetRequiredService<IDbContextFactory<ListenArrDbContext>>()
            .CreateDbContext();
        var moveJob = verification.MoveJobs.AsNoTracking().Single(job => job.Id == moveJobId);
        Assert.Equal(MoveJobStatus.NeedsAttention, moveJob.Status);
        Assert.Equal(MoveFailureKind.Verification, moveJob.FailureKind);
        Assert.Equal(MoveExecutionProtocol.PreDurableReleased, moveJob.ExecutionProtocolVersion);
        Assert.Equal(0, moveJob.IdentityKeyVersion);
        Assert.Null(moveJob.ActiveDeduplicationKey);
        Assert.Contains(
            "pre-durable released version",
            moveJob.Error,
            StringComparison.Ordinal);
        Assert.Equal([10], verification.RootFolders
            .AsNoTracking()
            .Where(root => root.IsDefault)
            .Select(root => root.Id)
            .ToList());
        var audiobookFile = verification.AudiobookFiles.AsNoTracking().Single();
        Assert.Equal(FileSystemCaseSensitivity.Unknown, audiobookFile.PathCaseSensitivity);
        Assert.Equal(FileSystemCaseSensitivityMode.Auto, audiobookFile.PathCaseSensitivityMode);
        Assert.Equal(1, audiobookFile.PathIdentityVersion);
        Assert.Equal(PathIdentityState.Unavailable, audiobookFile.PathIdentityState);
        Assert.Throws<SqliteException>(() => verification.Database.ExecuteSqlRaw(
            "UPDATE \"RootFolders\" SET \"IsDefault\" = 1 WHERE \"Id\" = 20"));
    }

    [Fact]
    [Trait("Scenario", "RepeatedMigrationStartup")]
    public void ApplyListenarrDatabaseMigrations_RepeatedStartupPreservesCurrentMoveIdentity()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var services = new ServiceCollection();
        services.AddDbContextFactory<ListenArrDbContext>(options =>
            options.UseSqlite(connection, sqlite =>
                sqlite.MigrationsAssembly(
                    typeof(ListenArrDbContext).Assembly.GetName().Name)));
        using var provider = services.BuildServiceProvider();

        provider.ApplyListenarrDatabaseMigrations();

        var moveJobId = Guid.NewGuid();
        const string currentKey = "v5:current-startup-regression";
        using (var setup = provider
            .GetRequiredService<IDbContextFactory<ListenArrDbContext>>()
            .CreateDbContext())
        {
            setup.MoveJobs.Add(new MoveJob
            {
                Id = moveJobId,
                AudiobookId = 701,
                RequestedPath = "/library/current-target",
                SourcePath = "/library/current-source",
                EnqueuedAt = DateTime.UtcNow,
                Status = MoveJobStatus.Running,
                IdentityKeyVersion = MoveManifestIdentity.Version,
                ActiveDeduplicationKey = currentKey
            });
            setup.SaveChanges();
        }

        provider.ApplyListenarrDatabaseMigrations();

        using var verification = provider
            .GetRequiredService<IDbContextFactory<ListenArrDbContext>>()
            .CreateDbContext();
        var moveJob = verification.MoveJobs.AsNoTracking().Single(job => job.Id == moveJobId);
        Assert.Equal(MoveJobStatus.Running, moveJob.Status);
        Assert.Equal(MoveExecutionProtocol.Current, moveJob.ExecutionProtocolVersion);
        Assert.Equal(MoveManifestIdentity.Version, moveJob.IdentityKeyVersion);
        Assert.Equal(currentKey, moveJob.ActiveDeduplicationKey);
    }

    [Fact]
    [Trait("Scenario", "MigrationFailureFailsStartupClosed")]
    public void ApplyListenarrDatabaseMigrations_MigrationFailurePropagates()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        var services = new ServiceCollection();
        services.AddDbContextFactory<ListenArrDbContext>(options =>
            options
                .UseSqlite(connection, sqlite =>
                    sqlite.MigrationsAssembly(
                        typeof(ListenArrDbContext).Assembly.GetName().Name))
                .AddInterceptors(new ThrowingMigrationCommandInterceptor()));
        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            provider.ApplyListenarrDatabaseMigrations());

        Assert.Equal("Injected migration failure.", exception.Message);
    }

    private sealed class ThrowingMigrationCommandInterceptor : DbCommandInterceptor
    {
        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            throw new InvalidOperationException("Injected migration failure.");
        }
    }
}
