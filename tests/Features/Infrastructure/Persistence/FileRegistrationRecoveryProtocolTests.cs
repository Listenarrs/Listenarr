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
