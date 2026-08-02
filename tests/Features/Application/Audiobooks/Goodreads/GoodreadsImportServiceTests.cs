/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

namespace Listenarr.Tests.Features.Application.Audiobooks.Goodreads;

public class GoodreadsImportServiceTests
{
    [Fact]
    public async Task ImportAsync_AddsParsedBooksThroughLibraryAddService()
    {
        var reader = new Mock<IGoodreadsListReader>();
        reader.Setup(r => r.ReadAsync(It.IsAny<GoodreadsImportRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GoodreadsImportBook>
            {
                new()
                {
                    SourceIndex = 1,
                    GoodreadsId = "123",
                    Title = "Imported Title",
                    Author = "Imported Author",
                    Isbn = ["9780441478125"],
                    Bookshelf = "to-read"
                }
            });

        var libraryAddService = new Mock<ILibraryAddService>();
        libraryAddService
            .Setup(s => s.AddToLibraryAsync(It.IsAny<LibraryAddOperationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LibraryAddOperationResult
            {
                Added = true,
                Message = "Audiobook added to library successfully",
                Audiobook = new Audiobook { Id = 42, Title = "Imported Title" }
            });

        var service = new GoodreadsImportService(
            reader.Object,
            libraryAddService.Object,
            Mock.Of<ILogger<GoodreadsImportService>>());

        var result = await service.ImportAsync(new GoodreadsImportRequest { Monitored = false });

        Assert.Equal(1, result.Total);
        Assert.Equal(1, result.AddedCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.Equal(0, result.ErrorCount);
        Assert.Equal(42, result.Items.Single().AudiobookId);

        libraryAddService.Verify(s => s.AddToLibraryAsync(
            It.Is<LibraryAddOperationRequest>(request =>
                request.HistorySource == "Goodreads"
                && request.Monitored == false
                && request.Metadata.Title == "Imported Title"
                && request.Metadata.Authors!.Contains("Imported Author")
                && request.Metadata.Isbn.Contains("9780441478125")
                && request.Metadata.Tags!.Contains("Goodreads:to-read")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ImportAsync_ReportsExistingBooksAsSkipped()
    {
        var reader = new Mock<IGoodreadsListReader>();
        reader.Setup(r => r.ReadAsync(It.IsAny<GoodreadsImportRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GoodreadsImportBook>
            {
                new() { SourceIndex = 1, Title = "Existing Title", Author = "Existing Author" }
            });

        var libraryAddService = new Mock<ILibraryAddService>();
        libraryAddService
            .Setup(s => s.AddToLibraryAsync(It.IsAny<LibraryAddOperationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LibraryAddOperationResult
            {
                AlreadyExists = true,
                Message = "Audiobook already exists in library",
                Audiobook = new Audiobook { Id = 7, Title = "Existing Title" }
            });

        var service = new GoodreadsImportService(
            reader.Object,
            libraryAddService.Object,
            Mock.Of<ILogger<GoodreadsImportService>>());

        var result = await service.ImportAsync(new GoodreadsImportRequest());

        Assert.Equal(0, result.AddedCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.Equal("Skipped", result.Items.Single().Status);
        Assert.Equal(7, result.Items.Single().AudiobookId);
    }
}
