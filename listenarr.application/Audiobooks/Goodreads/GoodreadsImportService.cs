/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks.Goodreads;

public sealed class GoodreadsImportService : IGoodreadsImportService
{
    private const int DefaultLimit = 500;
    private const int MaximumLimit = 5000;

    private readonly IGoodreadsListReader _reader;
    private readonly ILibraryAddService _libraryAddService;
    private readonly ILogger<GoodreadsImportService> _logger;

    public GoodreadsImportService(
        IGoodreadsListReader reader,
        ILibraryAddService libraryAddService,
        ILogger<GoodreadsImportService> logger)
    {
        _reader = reader;
        _libraryAddService = libraryAddService;
        _logger = logger;
    }

    public async Task<GoodreadsImportResult> ImportAsync(
        GoodreadsImportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var books = await _reader.ReadAsync(request, cancellationToken);
        var limit = Math.Clamp(request.Limit ?? DefaultLimit, 1, MaximumLimit);
        var selectedBooks = books.Take(limit).ToList();

        var result = new GoodreadsImportResult
        {
            Total = selectedBooks.Count
        };

        if (books.Count > selectedBooks.Count)
        {
            result.Warnings.Add($"Only the first {selectedBooks.Count} Goodreads items were imported. Increase limit to import more.");
        }

        foreach (var book in selectedBooks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(book.Title))
            {
                result.SkippedCount++;
                result.Items.Add(CreateRow(book, "Skipped", "Goodreads item did not include a title.", null));
                continue;
            }

            try
            {
                var addResult = await _libraryAddService.AddToLibraryAsync(
                    new LibraryAddOperationRequest
                    {
                        Metadata = ToMetadata(book),
                        Monitored = request.Monitored,
                        QualityProfileId = request.QualityProfileId,
                        AutoSearch = request.AutoSearch,
                        DestinationPath = request.DestinationPath,
                        HistorySource = "Goodreads",
                        HistoryMessage = $"Audiobook '{book.Title}' imported from Goodreads"
                    },
                    cancellationToken);

                if (addResult.AlreadyExists)
                {
                    result.SkippedCount++;
                    result.Items.Add(CreateRow(book, "Skipped", addResult.Message, addResult.Audiobook?.Id));
                    continue;
                }

                result.AddedCount++;
                result.Items.Add(CreateRow(book, "Added", addResult.Message, addResult.Audiobook?.Id));
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                result.ErrorCount++;
                result.Items.Add(CreateRow(book, "Error", ex.Message, null));
                _logger.LogWarning(ex, "Failed to import Goodreads item {SourceIndex}: {Title}", book.SourceIndex, book.Title);
            }
        }

        return result;
    }

    private static AudibleBookMetadata ToMetadata(GoodreadsImportBook book)
    {
        var tags = new List<string> { "Goodreads" };
        if (!string.IsNullOrWhiteSpace(book.Bookshelf))
        {
            tags.AddRange(book.Bookshelf
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(shelf => $"Goodreads:{shelf}"));
        }

        return new AudibleBookMetadata
        {
            Source = "Goodreads",
            Title = book.Title.Trim(),
            Author = book.Author?.Trim(),
            Authors = string.IsNullOrWhiteSpace(book.Author) ? [] : [book.Author.Trim()],
            Isbn = book.Isbn,
            PublishYear = book.PublishYear,
            PublishedDate = book.PublishedDate,
            Tags = tags,
            Description = string.IsNullOrWhiteSpace(book.SourceUrl)
                ? "Imported from Goodreads."
                : $"Imported from Goodreads: {book.SourceUrl}"
        };
    }

    private static GoodreadsImportRowResult CreateRow(
        GoodreadsImportBook book,
        string status,
        string message,
        int? audiobookId)
    {
        return new GoodreadsImportRowResult
        {
            SourceIndex = book.SourceIndex,
            GoodreadsId = book.GoodreadsId,
            Title = book.Title,
            Author = book.Author,
            Status = status,
            Message = message,
            AudiobookId = audiobookId
        };
    }
}
