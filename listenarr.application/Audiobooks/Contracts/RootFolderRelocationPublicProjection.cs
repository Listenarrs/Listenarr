namespace Listenarr.Application.Audiobooks.Contracts;

public static class RootFolderRelocationPublicProjection
{
    public static RootFolderPathChangeResult Sanitize(
        RootFolderPathChangeResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var publicError = ToPublicError(
            result.Status,
            result.TargetIdentityEnrollmentState,
            result.Mode,
            result.Error);
        return string.Equals(
                publicError,
                result.Error,
                StringComparison.Ordinal)
            ? result
            : result with { Error = publicError };
    }

    public static string? ToPublicError(
        RootFolderRelocationStatus status,
        TargetIdentityEnrollmentState enrollmentState,
        RootFolderRelocationMode mode,
        string? internalError)
    {
        if (string.IsNullOrWhiteSpace(internalError))
        {
            return null;
        }

        return enrollmentState switch
        {
            TargetIdentityEnrollmentState.Unavailable
                when mode == RootFolderRelocationMode.Relocate =>
                "The relocation target identity is unavailable. Review the target and retry.",
            _ => status switch
            {
                RootFolderRelocationStatus.NeedsAttention =>
                    "The relocation requires attention. Review the affected items and retry after resolving the underlying issue.",
                RootFolderRelocationStatus.Failed =>
                    "The relocation failed. Review the server logs and retry after resolving the underlying issue.",
                RootFolderRelocationStatus.Pending or
                    RootFolderRelocationStatus.Running =>
                    "The relocation encountered an issue and may require attention.",
                RootFolderRelocationStatus.Completed => null,
                _ => "The relocation encountered an issue."
            }
        };
    }
}
