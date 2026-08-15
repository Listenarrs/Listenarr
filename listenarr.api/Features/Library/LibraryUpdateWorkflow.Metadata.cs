using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library;

public sealed partial class LibraryUpdateWorkflow
{
    private async Task<IActionResult> ApplyMetadataUpdatesAsync(
        int id,
        AudiobookUpdateRequest request,
        bool basePathRewritten,
        bool suppressStaleImageUrl,
        bool metadataUpdateRequested,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
        var existingAudiobook = await repository.GetByIdAsync(id);
        cancellationToken.ThrowIfCancellationRequested();
        if (existingAudiobook == null)
        {
            return new NotFoundObjectResult(new { message = "Audiobook not found" });
        }

        var legacyIdentifierFieldsTouched = false;
        if (request.Title != null) existingAudiobook.Title = request.Title;
        if (request.Subtitle != null) existingAudiobook.Subtitle = request.Subtitle;
        if (request.Authors != null) existingAudiobook.Authors = request.Authors;
        if (request.ImageUrl != null && !suppressStaleImageUrl)
        {
            existingAudiobook.ImageUrl = request.ImageUrl;
        }
        if (request.PublishYear != null) existingAudiobook.PublishYear = request.PublishYear;
        if (request.PublishedDate != null) existingAudiobook.PublishedDate = request.PublishedDate;
        if (request.Description != null) existingAudiobook.Description = request.Description;
        if (request.Genres != null) existingAudiobook.Genres = request.Genres;
        if (request.Tags != null) existingAudiobook.Tags = request.Tags;
        if (request.Narrators != null) existingAudiobook.Narrators = request.Narrators;
        if (request.Isbn != null)
        {
            existingAudiobook.Isbn = request.Isbn;
            legacyIdentifierFieldsTouched = true;
        }

        if (request.Asin != null)
        {
            existingAudiobook.Asin = request.Asin;
            legacyIdentifierFieldsTouched = true;
        }

        if (request.OpenLibraryId != null)
        {
            existingAudiobook.OpenLibraryId = request.OpenLibraryId;
            legacyIdentifierFieldsTouched = true;
        }

        if (request.Publisher != null) existingAudiobook.Publisher = request.Publisher;
        if (request.Language != null) existingAudiobook.Language = request.Language;
        if (request.Runtime != null) existingAudiobook.Runtime = request.Runtime;
        if (request.Edition != null) existingAudiobook.Edition = request.Edition;
        if (request.Version != null) existingAudiobook.Version = request.Version;

        ApplySeriesMembershipUpdates(existingAudiobook, request);

        if (request.Explicit.HasValue) existingAudiobook.Explicit = request.Explicit.Value;
        if (request.Abridged.HasValue) existingAudiobook.Abridged = request.Abridged.Value;
        if (request.Monitored.HasValue) existingAudiobook.Monitored = request.Monitored.Value;

        if (!basePathRewritten && request.FilePath != null) existingAudiobook.FilePath = request.FilePath;
        if (request.FileSize.HasValue) existingAudiobook.FileSize = request.FileSize;
        if (request.Quality != null) existingAudiobook.Quality = request.Quality;

        await ApplyQualityProfileAsync(
            existingAudiobook,
            request,
            cancellationToken);

        if (legacyIdentifierFieldsTouched)
        {
            AudiobookIdentifierMapper.SyncImportedIdentifiersFromLegacyFields(existingAudiobook);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (metadataUpdateRequested
            && !await repository.UpdateAsync(existingAudiobook))
        {
            return new NotFoundObjectResult(new
            {
                message = "Audiobook not found"
            });
        }

        _logger.LogInformation(
            "Updated audiobook '{Title}' (ID: {Id})",
            LogRedaction.SanitizeText(existingAudiobook.Title),
            id);

        return new OkObjectResult(new
        {
            message = "Audiobook updated successfully",
            audiobook = existingAudiobook
        });
    }
}
