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
using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.Persistence;

[Trait("Name", "FileRegistrationRecoveryProtocolTests")]
[Trait("Category", "Infrastructure")]
public sealed class FileRegistrationRecoveryProtocolTests : BaseTests
{
    // A journal left on an older protocol version disables filesystem mutations for the whole
    // application, and there is no in-app route to clear it, so this exception message is the
    // entire brief an operator gets. Naming only the first of several turns one repair into one
    // restart per journal, with no way to know how many remain.
    [Fact]
    public async Task ReconcileAsync_LegacyJournals_NamesEveryOneAndHowMany()
    {
        Init();
        var factory = _provider.GetRequiredService<
            IDbContextFactory<ListenArrDbContext>>();

        var operationIds = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        await using (var seed = await factory.CreateDbContextAsync())
        {
            var created = DateTime.UtcNow.AddMinutes(-30);
            foreach (var (operationId, index) in operationIds.Select((id, i) => (id, i)))
            {
                seed.FileMutationJournals.Add(new FileMutationJournal
                {
                    OperationId = operationId,
                    Action = FileAction.HardlinkCopy,
                    State = FileMutationJournalState.Planned,
                    ProtocolVersion = FileMutationProtocol.Current - 1,
                    SourcePath = $"/incoming/book-{index}.m4b",
                    DestinationPath = $"/library/book-{index}.m4b",
                    CreatedAt = created.AddSeconds(index),
                    UpdatedAt = created.AddSeconds(index)
                });
            }
            await seed.SaveChangesAsync();
        }

        var service = new FileRegistrationRecoveryService(
            factory,
            Mock.Of<IFileMover>(),
            TimeProvider.System,
            NullLogger<FileRegistrationRecoveryService>.Instance);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ReconcileAsync());

        // The count first, so an operator knows the size of the job before reading identifiers.
        Assert.Contains("3 file-mutation journal(s)", thrown.Message, StringComparison.Ordinal);
        foreach (var operationId in operationIds)
        {
            Assert.Contains(operationId.ToString(), thrown.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    // The listing is capped so the message stays readable, which puts the count an operator
    // acts on into a branch of its own. An off-by-one there either hides a journal from the
    // listing without admitting it, or claims a remainder that does not exist.
    [Fact]
    public async Task ReconcileAsync_MoreLegacyJournalsThanTheCap_ListsTenAndCountsTheRest()
    {
        Init();
        var factory = _provider.GetRequiredService<
            IDbContextFactory<ListenArrDbContext>>();

        var operationIds = await SeedLegacyJournalsAsync(factory, count: 13);

        var service = new FileRegistrationRecoveryService(
            factory,
            Mock.Of<IFileMover>(),
            TimeProvider.System,
            NullLogger<FileRegistrationRecoveryService>.Instance);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ReconcileAsync());

        Assert.Contains("13 file-mutation journal(s)", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("and 3 more", thrown.Message, StringComparison.Ordinal);

        // Ordered by CreatedAt, so the ten named are the ten oldest and the naming is stable
        // across restarts. An operator who repairs those ten meets the next three, rather than
        // a fresh arbitrary ten.
        var listed = operationIds
            .Select(operationId => thrown.Message.Contains(
                operationId.ToString(),
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Equal(Enumerable.Repeat(true, 10).Concat(Enumerable.Repeat(false, 3)), listed);
    }

    // The states the message names are read before the same method moves every one of those rows
    // to NeedsAttention, so an operator who looks a journal up finds a different word there. The
    // pre-update state is the useful one, because it says how far the mutation got, but only if
    // the message says that is what it is.
    [Fact]
    [Trait("Scenario", "The listing says which state each mutation reached, not the one the row now carries")]
    public async Task ReconcileAsync_LegacyJournals_NamesTheStateTheMutationReached()
    {
        Init();
        var factory = _provider.GetRequiredService<
            IDbContextFactory<ListenArrDbContext>>();

        var planned = Guid.NewGuid();
        var committed = Guid.NewGuid();
        await using (var seed = await factory.CreateDbContextAsync())
        {
            var created = DateTime.UtcNow.AddMinutes(-30);
            seed.FileMutationJournals.Add(NewLegacyJournal(planned, FileMutationJournalState.Planned, created, 0));
            seed.FileMutationJournals.Add(
                NewLegacyJournal(committed, FileMutationJournalState.RegistrationCommitted, created, 1));
            await seed.SaveChangesAsync();
        }

        var service = new FileRegistrationRecoveryService(
            factory,
            Mock.Of<IFileMover>(),
            TimeProvider.System,
            NullLogger<FileRegistrationRecoveryService>.Instance);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ReconcileAsync());

        Assert.Contains($"{planned} (interrupted at Planned)", thrown.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            $"{committed} (interrupted at RegistrationCommitted)",
            thrown.Message,
            StringComparison.OrdinalIgnoreCase);

        // The half that makes the wording load-bearing: by the time anyone reads that message,
        // neither row carries the state it names.
        await using var check = await factory.CreateDbContextAsync();
        var states = await check.FileMutationJournals
            .AsNoTracking()
            .Select(journal => journal.State)
            .ToListAsync();
        Assert.All(states, state => Assert.Equal(FileMutationJournalState.NeedsAttention, state));
    }

    // Past the cap the exception names ten and counts the rest, so the set an operator needs is
    // only complete in the log the same message tells them to read.
    [Fact]
    [Trait("Scenario", "The full set reaches the log at Debug even when the message is capped")]
    public async Task ReconcileAsync_MoreLegacyJournalsThanTheCap_LogsTheWholeSetAtDebug()
    {
        Init();
        var factory = _provider.GetRequiredService<
            IDbContextFactory<ListenArrDbContext>>();

        var operationIds = await SeedLegacyJournalsAsync(factory, count: 13);
        var logger = new CapturingLogger<FileRegistrationRecoveryService>();

        var service = new FileRegistrationRecoveryService(
            factory,
            Mock.Of<IFileMover>(),
            TimeProvider.System,
            logger);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ReconcileAsync());

        var debugRecord = Assert.Single(logger.Records, record => record.Level == LogLevel.Debug);
        foreach (var operationId in operationIds)
        {
            Assert.Contains(operationId.ToString(), debugRecord.Message, StringComparison.OrdinalIgnoreCase);
        }

        // Once, not one line per journal, and the message stays capped so the two are not the
        // same listing written twice.
        Assert.Equal(1, logger.Records.Count(record => record.Level == LogLevel.Debug));
        Assert.Contains("and 3 more", thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(operationIds[12].ToString(), thrown.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Records { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Records.Add((logLevel, formatter(state, exception)));
        }
    }

    private static FileMutationJournal NewLegacyJournal(
        Guid operationId,
        FileMutationJournalState state,
        DateTime created,
        int index)
    {
        return new FileMutationJournal
        {
            OperationId = operationId,
            Action = FileAction.HardlinkCopy,
            State = state,
            ProtocolVersion = FileMutationProtocol.Current - 1,
            SourcePath = $"/incoming/book-{index}.m4b",
            DestinationPath = $"/library/book-{index}.m4b",
            CreatedAt = created.AddSeconds(index),
            UpdatedAt = created.AddSeconds(index)
        };
    }

    private static async Task<IReadOnlyList<Guid>> SeedLegacyJournalsAsync(
        IDbContextFactory<ListenArrDbContext> factory,
        int count)
    {
        var operationIds = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToList();
        await using var seed = await factory.CreateDbContextAsync();
        var created = DateTime.UtcNow.AddMinutes(-30);
        foreach (var (operationId, index) in operationIds.Select((id, i) => (id, i)))
        {
            seed.FileMutationJournals.Add(new FileMutationJournal
            {
                OperationId = operationId,
                Action = FileAction.HardlinkCopy,
                State = FileMutationJournalState.Planned,
                ProtocolVersion = FileMutationProtocol.Current - 1,
                SourcePath = $"/incoming/book-{index}.m4b",
                DestinationPath = $"/library/book-{index}.m4b",
                CreatedAt = created.AddSeconds(index),
                UpdatedAt = created.AddSeconds(index)
            });
        }
        await seed.SaveChangesAsync();
        return operationIds;
    }
}
