/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */


namespace Listenarr.Api.Features.Images
{
    internal sealed partial class ImageCandidateLookupWorkflow
    {
        private readonly IImageCacheService _imageCacheService;
        private readonly IAudiobookMetadataService _audiobookMetadataService;
        private readonly AudibleService _audibleService;
        private readonly IAudnexusService _audnexusService;
        private readonly IAudiobookRepository _audiobookRepository;
        private readonly IOpenLibraryService? _openLibraryService;
        private readonly ImageFallbackDownloadWorkflow _fallbackDownloadWorkflow;
        private readonly ILogger<ImageCandidateLookupWorkflow> _logger;

        public ImageCandidateLookupWorkflow(
            IImageCacheService imageCacheService,
            IAudiobookMetadataService audiobookMetadataService,
            AudibleService audibleService,
            IAudnexusService audnexusService,
            IAudiobookRepository audiobookRepository,
            IOpenLibraryService? openLibraryService,
            ImageFallbackDownloadWorkflow fallbackDownloadWorkflow,
            ILogger<ImageCandidateLookupWorkflow> logger)
        {
            _imageCacheService = imageCacheService;
            _audiobookMetadataService = audiobookMetadataService;
            _audibleService = audibleService;
            _audnexusService = audnexusService;
            _audiobookRepository = audiobookRepository;
            _openLibraryService = openLibraryService;
            _fallbackDownloadWorkflow = fallbackDownloadWorkflow;
            _logger = logger;
        }

        public async Task<string?> TryResolveAsync(string identifier, string? relativePath, string? requestedRegion)
        {
            try
            {
                var region = requestedRegion ?? string.Empty;
                if (string.IsNullOrWhiteSpace(region)) region = "us";

                string? candidateUrl = null;
                string? candidateIsbn = null;
                string? localOpenLibraryId = null;
                string? localTitle = null;
                string? localAuthor = null;
                var localIsbnCandidates = new List<string>();
                var localOpenLibraryIds = new List<string>();
                var localAsinCandidates = new List<string>();
                var candidateUrls = new List<string>();
                var candidateUrlSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                void AddCandidateUrl(string? url, string source)
                {
                    var normalized = ImageIdentifierHelper.NormalizeHttpImageUrl(url);
                    if (string.IsNullOrWhiteSpace(normalized)) return;
                    if (candidateUrlSet.Add(normalized))
                    {
                        candidateUrls.Add(normalized);
                        _logger.LogDebug("Queued image candidate for {Identifier} from {Source}: {Url}", LogRedaction.SanitizeText(identifier), LogRedaction.SanitizeText(source), LogRedaction.SanitizeText(normalized));
                    }
                    if (string.IsNullOrWhiteSpace(candidateUrl))
                    {
                        candidateUrl = normalized;
                    }
                }

                // Seed OpenLibrary fallback inputs from the local library record when
                // this identifier is an ASIN. This helps when provider metadata is
                // missing/stale but the book already has ISBN/OLID persisted.
                try
                {
                    if (ImageIdentifierHelper.LooksLikeAsin(identifier))
                    {
                        var localBook = await _audiobookRepository.GetByAsinAsync(identifier);
                        if (localBook != null)
                        {
                            localTitle = localBook.Title;
                            localAuthor = localBook.Authors?.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));

                            // Collect identifiers from the new typed identifier model first.
                            foreach (var extId in (localBook.ExternalIdentifiers ?? Enumerable.Empty<AudiobookExternalIdentifier>())
                                .Where(extId => !string.IsNullOrWhiteSpace(extId.ValueNormalized)))
                            {
                                switch (extId.Type)
                                {
                                    case AudiobookExternalIdentifierType.Asin:
                                        if (ImageIdentifierHelper.LooksLikeAsin(extId.ValueNormalized) &&
                                            !localAsinCandidates.Contains(extId.ValueNormalized, StringComparer.OrdinalIgnoreCase))
                                        {
                                            localAsinCandidates.Add(extId.ValueNormalized);
                                        }
                                        break;
                                    case AudiobookExternalIdentifierType.Isbn:
                                        if (ImageIdentifierHelper.LooksLikeIsbn(extId.ValueNormalized) &&
                                            !localIsbnCandidates.Contains(extId.ValueNormalized, StringComparer.OrdinalIgnoreCase))
                                        {
                                            localIsbnCandidates.Add(extId.ValueNormalized);
                                        }
                                        break;
                                    case AudiobookExternalIdentifierType.OpenLibraryId:
                                        {
                                            var normalizedOlid = ImageIdentifierHelper.NormalizeOpenLibraryId(extId.ValueNormalized);
                                            if (!string.IsNullOrWhiteSpace(normalizedOlid) &&
                                                !localOpenLibraryIds.Contains(normalizedOlid, StringComparer.OrdinalIgnoreCase))
                                            {
                                                localOpenLibraryIds.Add(normalizedOlid);
                                            }
                                        }
                                        break;
                                }
                            }

                            var localIsbn = localBook.Isbn?
                                .Select(ImageIdentifierHelper.NormalizeIsbn)
                                .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v) && ImageIdentifierHelper.LooksLikeIsbn(v));
                            if (!string.IsNullOrWhiteSpace(localIsbn))
                            {
                                if (!localIsbnCandidates.Contains(localIsbn, StringComparer.OrdinalIgnoreCase))
                                {
                                    localIsbnCandidates.Add(localIsbn);
                                }
                                candidateIsbn ??= localIsbn;
                                _logger.LogDebug("Seeded candidate ISBN {Isbn} from local library record for {Identifier}", LogRedaction.SanitizeText(candidateIsbn), LogRedaction.SanitizeText(identifier));
                            }

                            if (!string.IsNullOrWhiteSpace(localBook.OpenLibraryId))
                            {
                                var normalizedLocalOlid = ImageIdentifierHelper.NormalizeOpenLibraryId(localBook.OpenLibraryId);
                                if (!string.IsNullOrWhiteSpace(normalizedLocalOlid))
                                {
                                    if (!localOpenLibraryIds.Contains(normalizedLocalOlid, StringComparer.OrdinalIgnoreCase))
                                    {
                                        localOpenLibraryIds.Add(normalizedLocalOlid);
                                    }
                                    localOpenLibraryId ??= normalizedLocalOlid;
                                }
                            }

                            if (ImageIdentifierHelper.LooksLikeAsin(localBook.Asin ?? string.Empty))
                            {
                                var normalizedLocalAsin = (localBook.Asin ?? string.Empty).Trim().ToUpperInvariant();
                                if (!localAsinCandidates.Contains(normalizedLocalAsin, StringComparer.OrdinalIgnoreCase))
                                {
                                    localAsinCandidates.Add(normalizedLocalAsin);
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ImageIdentifierHelper.IsRecoverableImageLookupException(ex))
                {
                    _logger.LogDebug(ex, "Failed to seed image fallback metadata from local library record for {Identifier}", LogRedaction.SanitizeText(identifier));
                }

                // If the requested identifier key has no cached image, reuse an existing
                // cached image from any alternate stored identifier (e.g., old primary ASIN).
                if (string.IsNullOrWhiteSpace(relativePath))
                {
                    var cacheAliasCandidates = localAsinCandidates
                        .Concat(localIsbnCandidates)
                        .Concat(localOpenLibraryIds)
                        .Where(v => !string.IsNullOrWhiteSpace(v) && !string.Equals(v, identifier, StringComparison.OrdinalIgnoreCase))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    foreach (var aliasIdentifier in cacheAliasCandidates)
                    {
                        try
                        {
                            var aliasPath = await _imageCacheService.GetCachedImagePathAsync(aliasIdentifier);
                            if (!string.IsNullOrWhiteSpace(aliasPath))
                            {
                                relativePath = aliasPath;
                                _logger.LogInformation(
                                    "Reused cached image for identifier {Identifier} via alternate identifier {AliasIdentifier}: {Path}",
                                    LogRedaction.SanitizeText(identifier),
                                    LogRedaction.SanitizeText(aliasIdentifier),
                                    LogRedaction.SanitizeText(relativePath));
                                break;
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex) when (ImageIdentifierHelper.IsRecoverableImageLookupException(ex))
                        {
                            _logger.LogDebug(ex, "Failed probing alternate cached image identifier {AliasIdentifier} for {Identifier}", LogRedaction.SanitizeText(aliasIdentifier), LogRedaction.SanitizeText(identifier));
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(relativePath))
                {
                    var audible = await _audiobookMetadataService.GetAudibleMetadataAsync(identifier, region, cache: true);

                    if (audible != null)
                    {
                        AddCandidateUrl(audible.ImageUrl, "Audible");
                        if (!string.IsNullOrWhiteSpace(audible.Isbn))
                        {
                            candidateIsbn = ImageIdentifierHelper.NormalizeIsbn(audible.Isbn);
                        }
                    }

                    // Try Audnexus for ASINs as an additional candidate source even when
                    // Audible returned an image (Audible images can be placeholders or stale).
                    if (ImageIdentifierHelper.LooksLikeAsin(identifier))
                    {
                        try
                        {
                            var audnexus = await _audnexusService.GetBookMetadataAsync(identifier, region, seedAuthors: true, update: false);
                            if (audnexus != null)
                            {
                                AddCandidateUrl(audnexus.Image, "AudnexusBook");
                                if (string.IsNullOrWhiteSpace(candidateIsbn) && !string.IsNullOrWhiteSpace(audnexus.Isbn))
                                {
                                    candidateIsbn = ImageIdentifierHelper.NormalizeIsbn(audnexus.Isbn);
                                }
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex) when (ImageIdentifierHelper.IsRecoverableImageLookupException(ex))
                        {
                            _logger.LogDebug(ex, "Audnexus ASIN lookup failed for {Identifier}", LogRedaction.SanitizeText(identifier));
                        }
                    }

                    // Try alternate stored ASIN identifiers for this audiobook when the requested
                    // ASIN is region-limited or missing from providers.
                    if (ImageIdentifierHelper.LooksLikeAsin(identifier) && localAsinCandidates.Count > 0)
                    {
                        foreach (var altAsin in localAsinCandidates
                            .Where(a => !string.Equals(a, identifier, StringComparison.OrdinalIgnoreCase))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Take(3))
                        {
                            try
                            {
                                var altAudible = await _audiobookMetadataService.GetAudibleMetadataAsync(altAsin, region, cache: true);
                                if (altAudible != null)
                                {
                                    AddCandidateUrl(altAudible.ImageUrl, "AudibleAltAsin");
                                    if (string.IsNullOrWhiteSpace(candidateIsbn) && !string.IsNullOrWhiteSpace(altAudible.Isbn))
                                    {
                                        candidateIsbn = ImageIdentifierHelper.NormalizeIsbn(altAudible.Isbn);
                                    }
                                }
                            }
                            catch (OperationCanceledException)
                            {
                                throw;
                            }
                            catch (Exception ex) when (ImageIdentifierHelper.IsRecoverableImageLookupException(ex))
                            {
                                _logger.LogDebug(ex, "Audible alternate ASIN lookup failed for {Identifier} via {AltAsin}", LogRedaction.SanitizeText(identifier), LogRedaction.SanitizeText(altAsin));
                            }

                            try
                            {
                                var altAudnexus = await _audnexusService.GetBookMetadataAsync(altAsin, region, seedAuthors: true, update: false);
                                if (altAudnexus != null)
                                {
                                    AddCandidateUrl(altAudnexus.Image, "AudnexusBookAltAsin");
                                    if (string.IsNullOrWhiteSpace(candidateIsbn) && !string.IsNullOrWhiteSpace(altAudnexus.Isbn))
                                    {
                                        candidateIsbn = ImageIdentifierHelper.NormalizeIsbn(altAudnexus.Isbn);
                                    }
                                }
                            }
                            catch (OperationCanceledException)
                            {
                                throw;
                            }
                            catch (Exception ex) when (ImageIdentifierHelper.IsRecoverableImageLookupException(ex))
                            {
                                _logger.LogDebug(ex, "Audnexus alternate ASIN lookup failed for {Identifier} via {AltAsin}", LogRedaction.SanitizeText(identifier), LogRedaction.SanitizeText(altAsin));
                            }
                        }
                    }

                    // Build an OpenLibrary ISBN candidate when we have an ISBN (identifier or metadata/local record).
                    if (string.IsNullOrWhiteSpace(candidateIsbn) && ImageIdentifierHelper.LooksLikeIsbn(identifier))
                    {
                        candidateIsbn = ImageIdentifierHelper.NormalizeIsbn(identifier);
                    }
                    if (!string.IsNullOrWhiteSpace(candidateIsbn))
                    {
                        var olIsbnCandidate = $"https://covers.openlibrary.org/b/isbn/{Uri.EscapeDataString(candidateIsbn)}-L.jpg";
                        AddCandidateUrl(olIsbnCandidate, "OpenLibraryIsbn");
                        if (candidateUrls.Count == 1)
                        {
                            _logger.LogInformation("Using OpenLibrary ISBN cover candidate for {Identifier}: ISBN={Isbn}", LogRedaction.SanitizeText(identifier), LogRedaction.SanitizeText(candidateIsbn));
                        }
                    }

                    foreach (var localIsbnCandidate in localIsbnCandidates)
                    {
                        AddCandidateUrl(
                            $"https://covers.openlibrary.org/b/isbn/{Uri.EscapeDataString(localIsbnCandidate)}-L.jpg",
                            "OpenLibraryIsbnLocalIdentifier");
                    }

                    // Legacy fallback path through configured source envelope for compatibility.
                    if (string.IsNullOrWhiteSpace(candidateUrl) || string.IsNullOrWhiteSpace(candidateIsbn))
                    {
                        _logger.LogDebug("No image found in audible, attempting fallback GetMetadataAsync for {Identifier}", LogRedaction.SanitizeText(identifier));
                        try
                        {
                            var metadataEnvelope = await _audiobookMetadataService.GetMetadataAsync(identifier, region, cache: true);
                            if (metadataEnvelope != null)
                            {
                                try
                                {
                                    // If the service returned an AudibleBookResponse directly
                                    if (metadataEnvelope is AudibleBookResponse directMeta)
                                    {
                                        AddCandidateUrl(directMeta.ImageUrl, "MetadataEnvelopeDirect");
                                    }
                                    else
                                    {
                                        // Try dynamic access
                                        dynamic env = metadataEnvelope;
                                        object? mdObj = env.metadata;

                                        // If it's already the Audible type, use it
                                        if (mdObj is AudibleBookResponse mdMeta)
                                        {
                                            AddCandidateUrl(mdMeta.ImageUrl, "MetadataEnvelopeAudible");
                                        }
                                        else if (mdObj != null)
                                        {
                                            // Try reflection for common property names
                                            var t = mdObj.GetType();
                                            var prop = t.GetProperty("ImageUrl") ?? t.GetProperty("Image") ?? t.GetProperty("image") ?? t.GetProperty("imageUrl");
                                            if (prop != null)
                                            {
                                                var v = prop.GetValue(mdObj)?.ToString();
                                                AddCandidateUrl(v, "MetadataEnvelopeReflection");
                                            }

                                            if (string.IsNullOrWhiteSpace(candidateIsbn))
                                            {
                                                var isbnProp = t.GetProperty("Isbn") ?? t.GetProperty("ISBN") ?? t.GetProperty("isbn");
                                                var isbnVal = isbnProp?.GetValue(mdObj)?.ToString();
                                                if (!string.IsNullOrWhiteSpace(isbnVal))
                                                {
                                                    candidateIsbn = ImageIdentifierHelper.NormalizeIsbn(isbnVal);
                                                }
                                            }
                                        }
                                    }

                                    if (!string.IsNullOrWhiteSpace(candidateUrl))
                                    {
                                        _logger.LogInformation("Found image URL in fallback metadata source for identifier {Identifier}: {Url}", LogRedaction.SanitizeText(identifier), LogRedaction.SanitizeText(candidateUrl));
                                    }
                                    else
                                    {
                                        _logger.LogDebug("Fallback metadata returned no image URL for {Identifier}", LogRedaction.SanitizeText(identifier));
                                    }
                                }
                                catch (Exception ex) when (ImageIdentifierHelper.IsRecoverableImageLookupException(ex))
                                {
                                    _logger.LogDebug(ex, "Failed to parse fallback metadata envelope for {Identifier}", LogRedaction.SanitizeText(identifier));
                                }
                            }
                            else
                            {
                                _logger.LogDebug("GetMetadataAsync returned null for {Identifier}", LogRedaction.SanitizeText(identifier));
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex) when (ImageIdentifierHelper.IsRecoverableImageLookupException(ex))
                        {
                            _logger.LogDebug(ex, "Fallback metadata lookup failed for {Identifier}", LogRedaction.SanitizeText(identifier));
                        }
                    }

                    // If metadata envelope yielded ISBN, queue OpenLibrary cover as a fallback candidate.
                    if (!string.IsNullOrWhiteSpace(candidateIsbn))
                    {
                        AddCandidateUrl($"https://covers.openlibrary.org/b/isbn/{Uri.EscapeDataString(candidateIsbn)}-L.jpg", "OpenLibraryIsbnPostMetadata");
                    }

                    // Final OpenLibrary fallback via persisted OLID (if available and ISBN path
                    // wasn't usable).
                    if (!string.IsNullOrWhiteSpace(localOpenLibraryId))
                    {
                        AddCandidateUrl($"https://covers.openlibrary.org/b/olid/{Uri.EscapeDataString(localOpenLibraryId)}-L.jpg", "OpenLibraryOlid");
                    }
                    foreach (var localOlid in localOpenLibraryIds)
                    {
                        AddCandidateUrl($"https://covers.openlibrary.org/b/olid/{Uri.EscapeDataString(localOlid)}-L.jpg", "OpenLibraryOlidLocalIdentifier");
                    }

                    // Final ISBN discovery fallback for ASIN requests: use local title/author to
                    // search OpenLibrary when providers/local metadata do not include ISBN/OLID.
                    if (string.IsNullOrWhiteSpace(candidateIsbn) &&
                        _openLibraryService != null &&
                        ImageIdentifierHelper.LooksLikeAsin(identifier) &&
                        !string.IsNullOrWhiteSpace(localTitle))
                    {
                        try
                        {
                            var titleIsbns = await _openLibraryService.GetIsbnsForTitleAsync(localTitle!, localAuthor);
                            var normalizedTitleIsbns = titleIsbns
                                .Select(ImageIdentifierHelper.NormalizeIsbn)
                                .Where(v => !string.IsNullOrWhiteSpace(v) && ImageIdentifierHelper.LooksLikeIsbn(v))
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .Take(5)
                                .ToList();

                            if (normalizedTitleIsbns.Count > 0)
                            {
                                _logger.LogInformation(
                                    "Derived {Count} OpenLibrary ISBN candidate(s) from local title/author for {Identifier}: Title={Title}, Author={Author}",
                                    normalizedTitleIsbns.Count,
                                    LogRedaction.SanitizeText(identifier),
                                    LogRedaction.SanitizeText(localTitle),
                                    LogRedaction.SanitizeText(localAuthor));

                                foreach (var titleIsbn in normalizedTitleIsbns)
                                {
                                    AddCandidateUrl(
                                        $"https://covers.openlibrary.org/b/isbn/{Uri.EscapeDataString(titleIsbn)}-L.jpg",
                                        "OpenLibraryTitleAuthorSearch");
                                }
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex) when (ImageIdentifierHelper.IsRecoverableImageLookupException(ex))
                        {
                            _logger.LogDebug(ex, "OpenLibrary title/author ISBN fallback failed for {Identifier}", LogRedaction.SanitizeText(identifier));
                        }
                    }

                    relativePath = await TryResolveAuthorFallbackAsync(
                        identifier!,
                        region,
                        relativePath,
                        AddCandidateUrl,
                        () => candidateUrl);

                    if (candidateUrls.Count > 0)
                    {
                        relativePath = await _fallbackDownloadWorkflow.TryDownloadFirstCachedAsync(identifier!, candidateUrls);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ImageIdentifierHelper.IsRecoverableImageLookupException(ex))
            {
                _logger.LogDebug(ex, "Metadata-driven image download failed for {Identifier}", LogRedaction.SanitizeText(identifier));
            }

            return relativePath;
        }
    }
}
