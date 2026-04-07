using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Listenarr.Api.Services;

namespace Listenarr.Api.Services
{
    public interface IAudiobookshelfService
    {
        Task<(bool Success, string? Message)> TestConnectionAsync(CancellationToken ct = default);

        Task<IReadOnlyList<AudiobookshelfLibraryDto>> GetLibrariesAsync(CancellationToken ct = default);

        Task<(bool Success, string? Message)> TriggerLibraryScanAsync(string libraryId, CancellationToken ct = default);

        Task<IReadOnlyList<AudiobookshelfLibraryItemDto>> GetLibraryItemsAsync(
            string libraryId,
            CancellationToken ct = default);
    }

    public class AudiobookshelfLibraryDto
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string MediaType { get; set; } = string.Empty;
        public string? Path { get; set; }
    }

    public class AudiobookshelfLibraryItemDto
    {
        public string Id { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string MediaType { get; set; } = string.Empty;
        public long? Size { get; set; }
        public bool IsFile { get; set; }
        public AudiobookshelfBookMetadataDto Metadata { get; set; } = new();
    }

    public class AudiobookshelfBookMetadataDto
    {
        public string? Title { get; set; }
        public string? Subtitle { get; set; }
        public List<string> Authors { get; set; } = new();
        public List<string> Narrators { get; set; } = new();
        public string? Series { get; set; }
        public string? SeriesNumber { get; set; }
        public string? Publisher { get; set; }
        public string? Language { get; set; }
        public string? Asin { get; set; }
        public List<string> Isbn { get; set; } = new();
        public string? PublishedYear { get; set; }
        public string? PublishedDate { get; set; }
        public string? Description { get; set; }
        public string? ImageUrl { get; set; }
        public List<string> Genres { get; set; } = new();
        public List<string> Tags { get; set; } = new();
        public int? Runtime { get; set; }
        public bool Explicit { get; set; }
        public bool Abridged { get; set; }
    }
}