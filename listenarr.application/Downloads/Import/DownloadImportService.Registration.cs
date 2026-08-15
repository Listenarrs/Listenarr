namespace Listenarr.Application.Downloads.Import;

public partial class DownloadImportService
{
    private Task<bool> RegisterPublishedImportAsync(
        Audiobook audiobook,
        AudiobookFileOwnershipCheckResult initialOwnership,
        IAudiobookFileRegistrationLease registrationLease,
        string source,
        CancellationToken cancellationToken)
    {
        return audiobookFileService.RegisterPublishedGenerationAsync(
            audiobook,
            initialOwnership,
            registrationLease,
            source,
            cancellationToken);
    }
}
