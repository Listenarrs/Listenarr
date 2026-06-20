/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library;

public sealed class LibraryQueryWorkflow(
    IAudiobookRepository audiobookRepository,
    IAudiobookFileRepository audiobookFileRepository)
{
    public async Task<IActionResult> GetByAsinAsync(string asin)
    {
        var audiobook = await audiobookRepository.GetByAsinAsync(asin);
        return audiobook == null
            ? new NotFoundResult()
            : new OkObjectResult(audiobook);
    }

    public async Task<IActionResult> GetByIsbnAsync(string isbn)
    {
        var audiobook = await audiobookRepository.GetByIsbnAsync(isbn);
        return audiobook == null
            ? new NotFoundResult()
            : new OkObjectResult(audiobook);
    }

    public async Task<ActionResult<Audiobook>> GetByIdAsync(int id)
    {
        var audiobook = await audiobookRepository.GetByIdAsync(id);
        if (audiobook == null)
        {
            return new NotFoundObjectResult(new { message = "Audiobook not found" });
        }

        return new OkObjectResult(MapDetails(audiobook));
    }

    public async Task<IActionResult> GetFilesDebugAsync(int id) =>
        new OkObjectResult(await audiobookFileRepository.GetByAudiobookIdAsync(id));

    private static object MapDetails(Audiobook audiobook) => new
    {
        id = audiobook.Id,
        title = audiobook.Title,
        subtitle = audiobook.Subtitle,
        authors = audiobook.Authors,
        narrators = audiobook.Narrators,
        description = audiobook.Description,
        genres = audiobook.Genres,
        isbn = audiobook.Isbn?.FirstOrDefault(),
        isbns = audiobook.Isbn,
        asin = audiobook.Asin,
        openLibraryId = audiobook.OpenLibraryId,
        identifiers = AudiobookIdentifierMapper.GetEffectiveIdentifiers(audiobook)
            .Select(AudiobookIdentifierMapper.ToIdentifierResponse)
            .ToList(),
        imageUrl = audiobook.ImageUrl,
        publishYear = audiobook.PublishYear,
        publisher = audiobook.Publisher,
        language = audiobook.Language,
        filePath = audiobook.FilePath,
        fileSize = audiobook.FileSize,
        basePath = audiobook.BasePath,
        runtime = audiobook.Runtime,
        edition = audiobook.Edition,
        version = audiobook.Version,
        @explicit = audiobook.Explicit,
        abridged = audiobook.Abridged,
        monitored = audiobook.Monitored,
        quality = audiobook.Quality,
        qualityProfileId = audiobook.QualityProfileId,
        authorAsins = audiobook.AuthorAsins,
        series = audiobook.Series,
        seriesNumber = audiobook.SeriesNumber,
        publishedDate = audiobook.PublishedDate,
        seriesMemberships = audiobook.SeriesMemberships?
            .OrderByDescending(membership => membership.IsPrimary)
            .ThenBy(membership => membership.SortOrder)
            .Select(membership => new
            {
                id = membership.Id,
                seriesName = membership.SeriesName,
                seriesNumber = membership.SeriesNumber,
                seriesAsin = membership.SeriesAsin,
                isPrimary = membership.IsPrimary,
                sortOrder = membership.SortOrder
            })
            .ToList(),
        tags = audiobook.Tags,
        files = audiobook.Files?.Select(file => new
        {
            id = file.Id,
            path = file.Path,
            size = file.Size,
            durationSeconds = file.DurationSeconds,
            format = file.Format,
            container = file.Container,
            codec = file.Codec,
            bitrate = file.Bitrate,
            sampleRate = file.SampleRate,
            channels = file.Channels,
            source = file.Source,
            createdAt = file.CreatedAt
        }).ToList(),
        wanted = AudiobookWantedEvaluator.Compute(audiobook)
    };
}
