using System.Text.Json;

namespace Listenarr.Api.Features.Library;

public sealed partial class LibraryMetadataRescanWorkflow
{
    private async Task<MetadataRescanApplyResult> ApplyMetadataRescanResultAsync(
        int audiobookId,
        AudibleBookMetadata metadata,
        string expectedMetadataState,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
        var audiobook = await repository.GetByIdAsync(audiobookId);
        cancellationToken.ThrowIfCancellationRequested();
        if (audiobook == null)
        {
            return new MetadataRescanApplyResult(MetadataRescanApplyStatus.NotFound);
        }

        if (!string.Equals(
                CreateMetadataStateFingerprint(audiobook),
                expectedMetadataState,
                StringComparison.Ordinal))
        {
            return new MetadataRescanApplyResult(MetadataRescanApplyStatus.Conflict);
        }

        var legacyIdentifierFieldsTouched = ApplyMetadataRescanPatch(audiobook, metadata);
        var fallbackImageUrl = audiobook.ImageUrl;
        if (!string.IsNullOrWhiteSpace(metadata.ImageUrl))
        {
            fallbackImageUrl = metadata.ImageUrl;
            audiobook.ImageUrl = fallbackImageUrl;
        }

        if (legacyIdentifierFieldsTouched)
        {
            AudiobookIdentifierMapper.SyncImportedIdentifiersFromLegacyFields(audiobook);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!await repository.UpdateAsync(audiobook))
        {
            return new MetadataRescanApplyResult(
                MetadataRescanApplyStatus.NotFound);
        }

        if (!string.IsNullOrWhiteSpace(metadata.ImageUrl))
        {
            var publishedImageUrl =
                await MoveMetadataImageToLibraryStorageAsync(
                    audiobook,
                    metadata.ImageUrl);
            if (!string.IsNullOrWhiteSpace(publishedImageUrl)
                && !string.Equals(
                    publishedImageUrl,
                    fallbackImageUrl,
                    StringComparison.Ordinal))
            {
                if (await repository.TryUpdateImageUrlAsync(
                        audiobook.Id,
                        fallbackImageUrl,
                        publishedImageUrl,
                        CancellationToken.None))
                {
                    audiobook.ImageUrl = publishedImageUrl;
                }
                else
                {
                    _logger.LogWarning(
                        "Metadata rescan committed for audiobook {AudiobookId}, but its published image URL could not be enrolled because the stored value changed",
                        audiobook.Id);
                }
            }
        }

        return new MetadataRescanApplyResult(
            MetadataRescanApplyStatus.Applied,
            audiobook);
    }

    private static string CreateMetadataStateFingerprint(Audiobook audiobook) =>
        JsonSerializer.Serialize(new
        {
            audiobook.Title,
            audiobook.Subtitle,
            audiobook.PublishYear,
            audiobook.PublishedDate,
            audiobook.Description,
            audiobook.Publisher,
            audiobook.Language,
            audiobook.Runtime,
            audiobook.Version,
            audiobook.Series,
            audiobook.SeriesNumber,
            audiobook.Authors,
            audiobook.Narrators,
            audiobook.Genres,
            audiobook.Isbn,
            audiobook.Asin,
            audiobook.OpenLibraryId,
            audiobook.ImageUrl,
            SeriesMemberships = audiobook.SeriesMemberships?
                .OrderBy(membership => membership.Id)
                .Select(membership => new
                {
                    membership.Id,
                    membership.SeriesName,
                    membership.SeriesAsin,
                    membership.SeriesNumber,
                    membership.IsPrimary,
                    membership.SortOrder
                }),
            ExternalIdentifiers = audiobook.ExternalIdentifiers?
                .OrderBy(identifier => identifier.Id)
                .Select(identifier => new
                {
                    identifier.Id,
                    identifier.Type,
                    identifier.ValueRaw,
                    identifier.ValueNormalized,
                    identifier.Region,
                    identifier.IsPrimary,
                    identifier.Source
                })
        });

    private enum MetadataRescanApplyStatus
    {
        Applied,
        NotFound,
        Conflict
    }

    private sealed record MetadataRescanApplyResult(
        MetadataRescanApplyStatus Status,
        Audiobook? Audiobook = null);
}
