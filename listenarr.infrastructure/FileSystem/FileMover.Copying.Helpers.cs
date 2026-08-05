namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private static void CapturePublishedRegistrationLease(
        IAudiobookFileRegistrationLease registrationLease,
        Action<IAudiobookFileRegistrationLease> capturePublication)
    {
        IAudiobookFileRegistrationLease? ownedLease = registrationLease;
        try
        {
            capturePublication(ownedLease);
            ownedLease = null;
        }
        finally
        {
            ownedLease?.Dispose();
        }
    }

}
