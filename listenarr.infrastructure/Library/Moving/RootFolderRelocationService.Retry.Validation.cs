using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationService
{
    private static bool TryValidateRetryIdentity(
        PathIdentitySnapshot identity,
        string path,
        out string? error)
    {
        try
        {
            if (!FileSystemPathIdentity.TryCanonicalizeStoredPathWithIdentityForHost(
                    path,
                    identity,
                    out _,
                    out var reason))
            {
                error = $"The move job has an invalid persisted filesystem identity: {reason}";
                return false;
            }

            error = null;
            return true;
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException
            or NotSupportedException or PathTooLongException
            or System.Security.SecurityException)
        {
            error = $"The move job has an invalid persisted filesystem identity: {exception.Message}";
            return false;
        }
    }
}
