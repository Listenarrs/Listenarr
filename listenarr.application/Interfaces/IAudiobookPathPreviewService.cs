using Listenarr.Domain.Models;
using Listenarr.Application.Audiobooks;

namespace Listenarr.Application.Interfaces
{
    public interface IAudiobookPathPreviewService
    {
        Task<PathPreviewResult> PreviewAsync(
            Audiobook audiobook,
            string? destinationRoot = null,
            CancellationToken ct = default);
    }
}
