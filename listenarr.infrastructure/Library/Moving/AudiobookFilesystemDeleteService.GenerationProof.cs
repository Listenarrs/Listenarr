namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class AudiobookFilesystemDeleteService
{
    private static bool VerifyTrackedFileCleanupComplete(
        IReadOnlyDictionary<string, string> trackedPhysicalObjectIdentities)
    {
        foreach (var tracked in trackedPhysicalObjectIdentities)
        {
            if (!File.Exists(tracked.Key))
            {
                continue;
            }

            try
            {
                using var lease = PinnedAudiobookFileRegistrationLease.Open(tracked.Key);
                if (string.Equals(
                        lease.PhysicalObjectIdentity,
                        tracked.Value,
                        StringComparison.Ordinal))
                {
                    return false;
                }
            }
            catch (Exception exception) when (exception is
                IOException or UnauthorizedAccessException
                    or ArgumentException or InvalidOperationException
                    or NotSupportedException or PathTooLongException
                    or System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }

        return true;
    }
}
