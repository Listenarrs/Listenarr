using Listenarr.Api.Services.Search;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Models;
using Microsoft.Extensions.Logging;

namespace Listenarr.Api.Services.Search.Strategies;

/// <summary>
/// Fetches metadata from Audimeta API (audimeta.de).
/// </summary>
public class AudimetaStrategy : IMetadataStrategy
{
    private readonly AudimetaService _audimetaService;
    private readonly MetadataConverters _metadataConverters;
    private readonly ILogger<AudimetaStrategy> _logger;

    public string SourceName => "Audimeta";

    public AudimetaStrategy(
        AudimetaService audimetaService,
        MetadataConverters metadataConverters,
        ILogger<AudimetaStrategy> logger)
    {
        _audimetaService = audimetaService;
        _metadataConverters = metadataConverters;
        _logger = logger;
    }

    public bool CanHandle(ApiConfiguration source)
    {
        return source.BaseUrl.Contains("audimeta.de", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<AudibleBookMetadata?> FetchMetadataAsync(string asin, ApiConfiguration source, string? originalSource)
    {
        _logger.LogDebug("Calling Audimeta service for ASIN {Asin}", asin);
        var audimetaData = await _audimetaService.GetBookMetadataAsync(asin, "us", true);

        if (audimetaData != null)
        {
            _logger.LogInformation("✓ Audimeta returned data for ASIN {Asin}. Title: {Title}", asin, audimetaData.Title ?? "null");
            var metadata = _metadataConverters.ConvertAudimetaToMetadata(audimetaData, asin, originalSource ?? "Audible");
            _logger.LogInformation("Successfully enriched ASIN {Asin} with metadata from {SourceName}", asin, source.Name);
            return metadata;
        }
        else
        {
            _logger.LogWarning("✗ Audimeta returned null for ASIN {Asin}", asin);

            // Retry once without cache (force audimeta to refresh) when cache lookup fails
            try
            {
                _logger.LogInformation("Audimeta returned null for ASIN {Asin} (cache=true); retrying without cache", asin);
                var audimetaRetry = await _audimetaService.GetBookMetadataAsync(asin, "us", false);
                if (audimetaRetry != null)
                {
                    _logger.LogInformation("✓ Audimeta returned data for ASIN {Asin} on retry (no-cache). Title: {Title}", 
                        asin, audimetaRetry.Title ?? "null");
                    var metadata = _metadataConverters.ConvertAudimetaToMetadata(audimetaRetry, asin, originalSource ?? "Audible");
                    _logger.LogInformation("Successfully enriched ASIN {Asin} with metadata from {SourceName} (no-cache)", asin, source.Name);
                    return metadata;
                }
                else
                {
                    _logger.LogWarning("✗ Audimeta returned null on retry for ASIN {Asin}", asin);
                }
            }
            catch (Exception exRetry) when (exRetry is not OperationCanceledException && exRetry is not OutOfMemoryException && exRetry is not StackOverflowException) {
                _logger.LogWarning(exRetry, "Audimeta retry without cache failed for ASIN {Asin}", asin);
            }
        }

        return null;
    }
}

