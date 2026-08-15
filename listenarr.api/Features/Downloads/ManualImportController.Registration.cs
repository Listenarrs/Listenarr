namespace Listenarr.Api.Features.Downloads;

public partial class ManualImportController
{
    private Task<bool> RegisterPublishedManualImportAsync(
        Audiobook audiobook,
        AudiobookFileOwnershipCheckResult initialOwnership,
        IAudiobookFileRegistrationLease registrationLease,
        string authoritativeBasePath,
        CancellationToken cancellationToken)
    {
        return _audiobookFileService.RegisterPublishedGenerationWithBasePathAsync(
            audiobook,
            initialOwnership,
            registrationLease,
            authoritativeBasePath,
            "manual-import",
            cancellationToken);
    }
}
