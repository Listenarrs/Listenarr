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
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Application.Search.Scoring;

[Trait("Name", "SearchResultScorerProtocolTests")]
[Trait("Category", "Application")]
public sealed class SearchResultScorerProtocolTests : BaseTests
{
    [Theory]
    [InlineData("torrent", 0, true)]
    [InlineData("Torrent", 0, true)]
    [InlineData("TORRENT", 4, true)]
    [InlineData("ToRrEnT", null, true)]
    [InlineData("Torrent", 5, false)]
    [InlineData("torrent", 6, false)]
    [InlineData("Usenet", 0, false)]
    [InlineData("DDL", 0, false)]
    public async Task MinimumSeedersIsCaseInsensitiveAndOnlyAppliesToTorrents(
        string protocol, int? seeders, bool rejected)
    {
        var scorer = new SearchResultScorer(null, NullLogger.Instance);
        var result = new SearchResult
        {
            Title = "A Book",
            DownloadType = protocol,
            Seeders = seeders,
            Format = "M4B",
            Quality = "M4B",
            Language = "English"
        };
        var score = await scorer.Score(result, new QualityProfile { MinimumSeeders = 5 });
        Assert.Equal(rejected, score.IsRejected);
        if (rejected) Assert.Contains(score.RejectionReasons, r => r.StartsWith("Not enough seeders", StringComparison.Ordinal));
        else Assert.DoesNotContain(score.RejectionReasons, r => r.StartsWith("Not enough seeders", StringComparison.Ordinal));
    }
}
