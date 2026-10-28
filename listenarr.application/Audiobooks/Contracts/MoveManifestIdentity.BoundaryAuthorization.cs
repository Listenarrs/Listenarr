using Listenarr.Domain.Common;

namespace Listenarr.Application.Audiobooks.Contracts;

public static partial class MoveManifestIdentity
{
    public static bool IsBoundaryAuthorization(MoveJobEntry entry) =>
        IsSourceBoundaryAuthorization(entry)
        || IsTargetBoundaryAuthorization(entry);

    public static string ComputeSourceBoundaryAuthorizationDigest(
        int directoryIdentityVersion,
        string directoryIdentity) =>
        ComputeBoundaryAuthorizationDigest(
            SourceBoundaryAuthorizationDomain,
            directoryIdentityVersion,
            directoryIdentity);
}

public static class MoveBoundaryAuthorization
{
    public static bool TryResolveSourceBoundary(
        string source,
        PathIdentitySnapshot sourceIdentity,
        string? persistedSourceBoundary,
        bool deleteEmptySource,
        out string boundary,
        out string reason)
    {
        boundary = string.Empty;
        reason = string.Empty;
        try
        {
            var candidate = persistedSourceBoundary
                ?? sourceIdentity.BoundaryPath;
            if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    candidate,
                    out boundary,
                    out var canonicalizationReason,
                    sourceIdentity.Syntax))
            {
                reason = canonicalizationReason
                    ?? "The source boundary could not be canonicalized.";
                return false;
            }

            if (!FileSystemPathIdentity.IsSameOrInside(
                    source,
                    boundary,
                    sourceIdentity.Semantics))
            {
                reason = "The source boundary does not contain the move source.";
                return false;
            }

            if (!deleteEmptySource)
            {
                return true;
            }

            var sourceParent = Path.GetDirectoryName(source);
            if (string.IsNullOrWhiteSpace(sourceParent)
                || !FileSystemPathIdentity.IsSameOrInside(
                    sourceParent,
                    boundary,
                    sourceIdentity.Semantics))
            {
                reason =
                    "The source boundary does not contain the source parent required for directory retirement.";
                return false;
            }

            return true;
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException
                or NotSupportedException or PathTooLongException
                or System.Security.SecurityException)
        {
            boundary = string.Empty;
            reason = $"The source boundary is invalid: {exception.Message}";
            return false;
        }
    }
}
