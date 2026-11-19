/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Listenarr.Tests.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Api.Features.Downloads;

/// <summary>
/// A blocklist with no way out is a one-way door: some of the failures that write an entry are
/// nothing to do with the release, so an entry written by a full disk or a tracker outage would
/// otherwise ban that release for that book permanently. These pin that the exits work and that
/// they stay scoped to what was asked for.
/// </summary>
[Trait("Area", "DownloadsApi")]
[Trait("Name", "BlocklistControllerTests")]
[Trait("Category", "BlocklistController")]
public sealed class BlocklistControllerTests : BaseTests
{
    private const string FirstHash = "ABCDEF1234567890ABCDEF1234567890ABCDEF12";
    private const string SecondHash = "1111111111111111111111111111111111111111";

    private BlocklistController NewController() => new(
        _provider.GetRequiredService<IBlocklistService>(),
        NullLogger<BlocklistController>.Instance);

    [Fact]
    public async Task Delete_LetsTheSearchPickTheReleaseUpAgain()
    {
        // The assertion that matters is not that a row went, it is that the filter stops
        // excluding the release. A DeleteAsync that removed nothing would still return
        // NoContent, so the filter is asked both before and after.
        var blocklist = _provider.GetRequiredService<IBlocklistService>();
        var identifier = ReleaseIdentity.For(FirstHash, null, null, null)!;
        await blocklist.BlockAsync(7, identifier, "The Only Listing", 800_000_000, "simulated failure");

        Assert.Empty(await BlockedReleaseFilter.ExcludeAsync(
            blocklist, 7, [Scored(FirstHash)], NullLogger.Instance));

        var entry = Assert.Single(await blocklist.GetForAudiobookAsync(7));
        var response = await NewController().Delete(entry.Id);

        Assert.IsType<NoContentResult>(response);
        Assert.Single(await BlockedReleaseFilter.ExcludeAsync(
            blocklist, 7, [Scored(FirstHash)], NullLogger.Instance));
    }

    [Fact]
    public async Task Delete_LeavesTheOtherEntriesForTheSameBookAlone()
    {
        var blocklist = _provider.GetRequiredService<IBlocklistService>();
        await blocklist.BlockAsync(7, ReleaseIdentity.For(FirstHash, null, null, null)!, "First", 1, "a");
        await blocklist.BlockAsync(7, ReleaseIdentity.For(SecondHash, null, null, null)!, "Second", 2, "b");

        var first = (await blocklist.GetForAudiobookAsync(7)).Single(entry => entry.Title == "First");
        await NewController().Delete(first.Id);

        var remaining = Assert.Single(await blocklist.GetForAudiobookAsync(7));
        Assert.Equal("Second", remaining.Title);
    }

    [Fact]
    public async Task Delete_UnknownId_Answers404()
    {
        var response = await NewController().Delete(4242);

        Assert.IsType<NotFoundObjectResult>(response);
    }

    [Fact]
    public async Task ClearForAudiobook_RemovesEveryEntryForThatBookAndNobodyElses()
    {
        // A clear that quietly purged the whole table would satisfy "the book has none left",
        // so the second book is the control.
        var blocklist = _provider.GetRequiredService<IBlocklistService>();
        await blocklist.BlockAsync(7, ReleaseIdentity.For(FirstHash, null, null, null)!, "First", 1, "a");
        await blocklist.BlockAsync(7, ReleaseIdentity.For(SecondHash, null, null, null)!, "Second", 2, "b");
        await blocklist.BlockAsync(9, ReleaseIdentity.For(FirstHash, null, null, null)!, "Other book", 3, "c");

        var response = Assert.IsType<OkObjectResult>(await NewController().ClearForAudiobook(7));

        Assert.Contains(
            "\"removed\":2",
            System.Text.Json.JsonSerializer.Serialize(response.Value),
            StringComparison.Ordinal);
        Assert.Empty(await blocklist.GetForAudiobookAsync(7));
        Assert.Single(await blocklist.GetForAudiobookAsync(9));
    }

    [Fact]
    public async Task ClearForAudiobook_WithNothingBlocked_IsNotAnError()
    {
        var response = Assert.IsType<OkObjectResult>(await NewController().ClearForAudiobook(7));

        Assert.Contains(
            "\"removed\":0",
            System.Text.Json.JsonSerializer.Serialize(response.Value),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetForAudiobook_ReturnsOnlyThatBooksEntries()
    {
        var blocklist = _provider.GetRequiredService<IBlocklistService>();
        await blocklist.BlockAsync(7, ReleaseIdentity.For(FirstHash, null, null, null)!, "Mine", 1, "a");
        await blocklist.BlockAsync(9, ReleaseIdentity.For(SecondHash, null, null, null)!, "Not mine", 2, "b");

        var response = Assert.IsType<OkObjectResult>((await NewController().GetForAudiobook(7)).Result);
        var entries = Assert.IsAssignableFrom<IReadOnlyList<BlockedRelease>>(response.Value);

        Assert.Equal("Mine", Assert.Single(entries).Title);
    }

    [Fact]
    public void Controller_HasNoBroadCatch_SoNo5xxBodyCanCarryAnExceptionMessage()
    {
        // The controllers this sits beside answer 500 with ex.Message, which is the house pattern
        // and a separate question. This one has no catch at all, which is what
        // NewControllerBroadCatches_AreForbiddenOutsideDocumentedLegacyControllers requires of a
        // controller added after that rule. Asserted here as well so the reason is recorded beside
        // the endpoints rather than only in an architecture list.
        var source = File.ReadAllText(Path.Join(
            TestUtils.FindRepositoryRoot(),
            "listenarr.api",
            "Features",
            "Downloads",
            "BlocklistController.cs"));

        Assert.DoesNotContain("catch (", source, StringComparison.Ordinal);
    }

    private static QualityScore Scored(string infoHash) => new()
    {
        TotalScore = 90,
        SearchResult = new SearchResult
        {
            Title = "Book",
            MagnetLink = $"magnet:?xt=urn:btih:{infoHash}&dn=book"
        }
    };
}
