using System.Text.RegularExpressions;
using Listenarr.Infrastructure.Models;
using Microsoft.Extensions.Logging;

namespace Listenarr.Api.Services.Search;

/// <summary>
/// Handles merging and population of metadata from various sources (search results, scraped data, API responses).
/// </summary>
public class MetadataMerger
{
    private readonly ILogger<MetadataMerger> _logger;

    public MetadataMerger(ILogger<MetadataMerger> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Merges data from source metadata into target metadata, filling in missing fields.
    /// </summary>
    public void MergeMetadata(AudibleBookMetadata source, AudibleBookMetadata target)
    {
        _logger.LogInformation("Merging metadata: source.PublishYear={SourceYear}, target.PublishYear={TargetYear}, target.Asin={Asin}", 
            source.PublishYear, target.PublishYear, target.Asin);
            
        // Only merge fields that are missing in target
        if (string.IsNullOrEmpty(target.Title)) target.Title = source.Title;
        if (target.Authors == null || !target.Authors.Any()) target.Authors = source.Authors;
        if (target.Narrators == null || !target.Narrators.Any()) target.Narrators = source.Narrators;
        if (string.IsNullOrEmpty(target.Publisher)) target.Publisher = source.Publisher;
        if (string.IsNullOrEmpty(target.Description)) target.Description = source.Description;
        if (target.Genres == null || !target.Genres.Any()) target.Genres = source.Genres;
        if (string.IsNullOrEmpty(target.Language)) target.Language = source.Language;
        if (string.IsNullOrEmpty(target.ImageUrl)) target.ImageUrl = source.ImageUrl;
        if (!target.Runtime.HasValue && source.Runtime.HasValue) target.Runtime = source.Runtime;
        if (string.IsNullOrEmpty(target.PublishYear)) target.PublishYear = source.PublishYear;
        if (string.IsNullOrEmpty(target.Subtitle)) target.Subtitle = source.Subtitle;
        
        _logger.LogInformation("After merge: target.PublishYear={TargetYear}, target.Asin={Asin}", target.PublishYear, target.Asin);
    }

}
