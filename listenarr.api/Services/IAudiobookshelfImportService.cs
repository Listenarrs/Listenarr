using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Listenarr.Api.Services
{
    public interface IAudiobookshelfImportService
    {
        Task<IReadOnlyList<AudiobookshelfImportPreviewDto>> PreviewImportAsync(
            string libraryId,
            CancellationToken ct = default);

        Task<AudiobookshelfImportResultDto> ImportAsync(
            AudiobookshelfImportRequestDto request,
            CancellationToken ct = default);
    }

    

    public class AudiobookshelfImportPreviewDto
    {
        public string ItemId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Author { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public int? ExistingAudiobookId { get; set; }
        public bool WillImport { get; set; }
        public string Reason { get; set; } = string.Empty;
        public string? Asin { get; set; }
        public List<string> Isbn { get; set; } = new();
    }

    public class AudiobookshelfImportRequestDto
    {
        public string LibraryId { get; set; } = string.Empty;
        public List<string> ItemIds { get; set; } = new();
        public int? QualityProfileId { get; set; }
        public bool Monitored { get; set; } = true;
        public bool SkipExisting { get; set; } = true;
    }

    public class AudiobookshelfImportResultDto
    {
        public int ImportedCount { get; set; }
        public int SkippedCount { get; set; }
        public List<string> Messages { get; set; } = new();
    }
}