/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 */

namespace Listenarr.Api.Features.Images
{
    internal sealed partial class ImageCandidateLookupWorkflow
    {
        private async Task<string?> TryResolveAuthorFallbackAsync(
            string identifier,
            string region,
            string? relativePath,
            Action<string?, string> addCandidateUrl,
            Func<string?> getCandidateUrl)
        {
            // If no image found from book metadata, attempt author lookups (treating identifier as author name/asin)
            if (string.IsNullOrWhiteSpace(getCandidateUrl()))
            {
                try
                {
                    // First: try to find a stored author ASIN in the DB and serve its cached image if available
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(identifier))
                        {
                            var authorAsin = await _audiobookRepository.GetAuthorAsinByNameAsync(identifier);
                            if (!string.IsNullOrWhiteSpace(authorAsin))
                            {
                                var diskPath = await _imageCacheService.GetCachedImagePathAsync(authorAsin);
                                if (!string.IsNullOrWhiteSpace(diskPath))
                                {
                                    // Use cached author image by ASIN (prefer authors storage path)
                                    relativePath = "/" + diskPath.TrimStart('/');
                                    _logger.LogInformation("Found cached author image for identifier {Identifier} via stored ASIN {Asin}: {Path}", LogRedaction.SanitizeText(identifier), LogRedaction.SanitizeText(authorAsin), LogRedaction.SanitizeText(relativePath));
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
                        _logger.LogDebug(ex, "Failed to lookup stored author ASIN for identifier {Identifier}", LogRedaction.SanitizeText(identifier));
                    }

                    // If we didn't find a cached author image via stored ASIN, fallback to Audible lookup by name
                    if (string.IsNullOrWhiteSpace(relativePath))
                    {
                        var authorLookup = await _audibleService.LookupAuthorAsync(identifier, region);
                        if (authorLookup != null && !string.IsNullOrWhiteSpace(authorLookup.Image) && (authorLookup.Image.StartsWith("http://") || authorLookup.Image.StartsWith("https://")))
                        {
                            addCandidateUrl(authorLookup.Image, "AudibleAuthor");
                            _logger.LogInformation("Found author image from Audible for identifier {Identifier}: {Url}", LogRedaction.SanitizeText(identifier), LogRedaction.SanitizeText(getCandidateUrl()));
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ImageIdentifierHelper.IsRecoverableImageLookupException(ex))
                {
                    _logger.LogDebug(ex, "Audible author lookup failed for {Identifier}", LogRedaction.SanitizeText(identifier));
                }

                // 2) Audnexus author search fallback
                if (string.IsNullOrWhiteSpace(getCandidateUrl()))
                {
                    try
                    {
                        // If identifier looks like an ASIN, prefer GetAuthorAsync to fetch the author directly
                        if (identifier != null && identifier.Length >= 10 && (identifier.StartsWith("B", StringComparison.OrdinalIgnoreCase) || identifier.All(char.IsLetterOrDigit)))
                        {
                            try
                            {
                                var authorResp = await _audnexusService.GetAuthorAsync(identifier, region, update: false);
                                if (authorResp != null && !string.IsNullOrWhiteSpace(authorResp.Image) && (authorResp.Image.StartsWith("http://") || authorResp.Image.StartsWith("https://")))
                                {
                                    addCandidateUrl(authorResp.Image, "AudnexusAuthorByAsin");
                                    _logger.LogInformation("Found author image from Audnexus (by ASIN) for identifier {Identifier}: {Url}", LogRedaction.SanitizeText(identifier), LogRedaction.SanitizeText(getCandidateUrl()));
                                }
                            }
                            catch (OperationCanceledException)
                            {
                                throw;
                            }
                            catch (Exception ex) when (ImageIdentifierHelper.IsRecoverableImageLookupException(ex))
                            {
                                _logger.LogDebug(ex, "Audnexus GetAuthorAsync failed for ASIN {Identifier}", LogRedaction.SanitizeText(identifier));
                            }
                        }

                        // If still not found, fallback to searching by name
                        if (string.IsNullOrWhiteSpace(getCandidateUrl()))
                        {
                            // Try to find stored author ASIN in database (match by author name) and prefer direct GET
                            try
                            {
                                if (!string.IsNullOrWhiteSpace(identifier))
                                {
                                    var authorAsin = await _audiobookRepository.GetAuthorAsinByNameAsync(identifier);
                                    if (!string.IsNullOrWhiteSpace(authorAsin))
                                    {
                                        try
                                        {
                                            var authorResp = await _audnexusService.GetAuthorAsync(authorAsin, region, update: false);
                                            if (authorResp != null && !string.IsNullOrWhiteSpace(authorResp.Image) && (authorResp.Image.StartsWith("http://") || authorResp.Image.StartsWith("https://")))
                                            {
                                                addCandidateUrl(authorResp.Image, "AudnexusAuthorByStoredAsin");
                                                _logger.LogInformation("Found author image from Audnexus by stored ASIN {Asin} for identifier {Identifier}: {Url}", LogRedaction.SanitizeText(authorAsin), LogRedaction.SanitizeText(identifier), LogRedaction.SanitizeText(getCandidateUrl()));
                                            }
                                        }
                                        catch (OperationCanceledException)
                                        {
                                            throw;
                                        }
                                        catch (Exception ex) when (ImageIdentifierHelper.IsRecoverableImageLookupException(ex))
                                        {
                                            _logger.LogDebug(ex, "Audnexus GetAuthorAsync failed for ASIN {Asin}", LogRedaction.SanitizeText(authorAsin));
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
                                _logger.LogDebug(ex, "Failed to lookup author ASINs in database for identifier {Identifier}", LogRedaction.SanitizeText(identifier));
                            }

                            // If still not found, fallback to searching by name
                            if (string.IsNullOrWhiteSpace(getCandidateUrl()))
                            {
                                var authors = await _audnexusService.SearchAuthorsAsync(identifier!, region);
                                var first = authors?.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.Image));
                                if (first != null && !string.IsNullOrWhiteSpace(first.Image) && (first.Image.StartsWith("http://") || first.Image.StartsWith("https://")))
                                {
                                    addCandidateUrl(first.Image, "AudnexusAuthorSearch");
                                    _logger.LogInformation("Found author image from Audnexus (search) for identifier {Identifier}: {Url}", LogRedaction.SanitizeText(identifier), LogRedaction.SanitizeText(getCandidateUrl()));
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
                        _logger.LogDebug(ex, "Audnexus author search failed for {Identifier}", LogRedaction.SanitizeText(identifier));
                    }
                }
            }
            return relativePath;
        }
    }
}
