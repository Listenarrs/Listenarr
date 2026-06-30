/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

namespace Listenarr.Tests.Features.Application.Downloads.Submission;

[Trait("Name", "TrustedDownloadCandidateFactoryTests")]
[Trait("Category", "TrustedDownloadCandidateFactory")]
public sealed class TrustedDownloadCandidateFactoryTests
{
    [Fact]
    public void Create_DirectDownloadArtifactBatch_PreservesEveryArtifact()
    {
        // Given
        var result = new SearchResult
        {
            Id = "result-1",
            Title = "Book",
            Artist = "Author",
            Album = "Book",
            Source = "Internet Archive",
            DownloadType = DirectDownloadMetadataKeys.ClientId,
            IndexerId = 42,
            IndexerImplementation = "InternetArchive",
            TorrentUrl = "https://archive.org/download/book/chapter-01.mp3",
            DirectDownloadArtifacts =
            [
                new("https://archive.org/download/book/chapter-01.mp3", "chapter-01.mp3", 100, DirectDownloadArtifactPackaging.File),
                new("https://archive.org/download/book/chapter-02.mp3", "chapter-02.mp3", 200, DirectDownloadArtifactPackaging.File)
            ]
        };

        // When
        var candidate = TrustedDownloadCandidateFactory.Create(result);

        // Then
        var locators = candidate.SourceDescriptor.Locators
            .Where(locator => locator.Kind == DownloadSourceLocatorKind.DirectUrl)
            .ToList();
        Assert.Collection(
            locators,
            first => Assert.Equal("chapter-01.mp3", first.FileName),
            second => Assert.Equal("chapter-02.mp3", second.FileName));
    }
}
