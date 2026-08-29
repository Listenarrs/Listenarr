using System.ComponentModel;
using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.FileSystem;

internal sealed class DirectoryObjectIdentityResolver(
    Func<PinnedDirectoryCreation.PinnedDirectoryAnchor, string>?
        nativeIdentityResolver = null,
    Func<PinnedDirectoryCreation.PinnedDirectoryAnchor, IReadOnlyList<string>>?
        nativeIdentityCandidatesResolver = null) : IDirectoryObjectIdentityResolver
{
    private readonly Func<PinnedDirectoryCreation.PinnedDirectoryAnchor, IReadOnlyList<string>>
        _nativeIdentityCandidatesResolver = nativeIdentityCandidatesResolver
            ?? (nativeIdentityResolver == null
                ? static anchor => anchor.GetDirectoryObjectIdentityCandidates()
                : anchor => [nativeIdentityResolver(anchor)]);

    public Task<DirectoryObjectIdentityResolution> ResolveAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        ResolvePinnedAsync(
            path,
            cancellationToken,
            nativeIdentities => new DirectoryObjectIdentityResolution(
                ManagedDirectoryIdentity.CurrentVersion,
                ManagedDirectoryIdentity.CreateMarkerless(nativeIdentities[0]),
                null));

    public Task<DirectoryObjectIdentityResolution> ResolveExistingAsync(
        string path,
        int expectedVersion,
        string expectedValue,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedValue);
        if (expectedVersion != ManagedDirectoryIdentity.CurrentVersion)
        {
            return Task.FromResult(
                DirectoryObjectIdentityResolution.Unavailable(
                    $"Directory identity version {expectedVersion} is unsupported.",
                    DirectoryObjectIdentityFailureKind.IdentityUnsupported));
        }

        return ResolvePinnedAsync(
            path,
            cancellationToken,
            nativeIdentities =>
            {
                if (nativeIdentities.Any(nativeIdentity =>
                        ManagedDirectoryIdentity.MatchesNativeIdentity(
                            expectedVersion,
                            expectedValue,
                            nativeIdentity)))
                {
                    return new DirectoryObjectIdentityResolution(
                        expectedVersion,
                        expectedValue,
                        null);
                }

                if (MatchesLegacyLinuxBirthTimeIdentity(
                        expectedVersion,
                        expectedValue,
                        nativeIdentities))
                {
                    return DirectoryObjectIdentityResolution.Unavailable(
                        "The persisted Linux directory identity uses birth-time-only evidence that is no longer sufficient for destructive filesystem authority.",
                        DirectoryObjectIdentityFailureKind.LegacyWeakIdentity);
                }

                return DirectoryObjectIdentityResolution.Unavailable(
                    "The live directory no longer matches its persisted physical identity.",
                    DirectoryObjectIdentityFailureKind.IdentityMismatch);
            });
    }

    private Task<DirectoryObjectIdentityResolution> ResolvePinnedAsync(
        string path,
        CancellationToken cancellationToken,
        Func<IReadOnlyList<string>, DirectoryObjectIdentityResolution> resolve)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!FileSystemPathIdentity.TryCanonicalizeStoredAbsolutePathForHost(
                path,
                out var canonicalPath,
                out var pathReason))
        {
            var failureKind = FileSystemPathIdentity.TryDetectAbsoluteSyntax(path, out _)
                && !FileSystemPathIdentity.TryDetectAbsoluteSyntaxForHost(path, out _)
                    ? DirectoryObjectIdentityFailureKind.ForeignPathSyntax
                    : DirectoryObjectIdentityFailureKind.InvalidPath;
            return Task.FromResult(
                DirectoryObjectIdentityResolution.Unavailable(pathReason, failureKind));
        }

        try
        {
            using var anchor = PinnedDirectoryCreation.OpenPinnedBoundary(canonicalPath);
            var nativeIdentities = _nativeIdentityCandidatesResolver(anchor);
            if (nativeIdentities.Count == 0)
            {
                throw new PlatformNotSupportedException(
                    "The filesystem did not expose a durable directory identity candidate.");
            }
            if (!anchor.VisiblePathMatches())
            {
                return Task.FromResult(
                    DirectoryObjectIdentityResolution.Unavailable(
                        "The directory changed while its physical identity was captured.",
                        DirectoryObjectIdentityFailureKind.IdentityUnstable));
            }

            return Task.FromResult(resolve(nativeIdentities));
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or Win32Exception
                or PlatformNotSupportedException or InvalidOperationException)
        {
            return Task.FromResult(
                DirectoryObjectIdentityResolution.Unavailable(
                    exception.Message,
                    ClassifyFailure(exception)));
        }
    }

    private static bool MatchesLegacyLinuxBirthTimeIdentity(
        int expectedVersion,
        string expectedValue,
        IReadOnlyList<string> nativeIdentities)
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        foreach (var nativeIdentity in nativeIdentities)
        {
            if (PinnedDirectoryCreation.TryGetLinuxBirthTimeIdentityPrefix(
                    nativeIdentity,
                    out var birthTimeIdentity)
                && ManagedDirectoryIdentity.MatchesNativeIdentity(
                    expectedVersion,
                    expectedValue,
                    birthTimeIdentity))
            {
                return true;
            }
        }

        return false;
    }

    private static DirectoryObjectIdentityFailureKind ClassifyFailure(Exception exception)
    {
        return exception switch
        {
            DirectoryNotFoundException or FileNotFoundException =>
                DirectoryObjectIdentityFailureKind.Missing,
            UnauthorizedAccessException => DirectoryObjectIdentityFailureKind.AccessDenied,
            Win32Exception win32 when OperatingSystem.IsWindows()
                && win32.NativeErrorCode is 2 or 3 =>
                DirectoryObjectIdentityFailureKind.Missing,
            Win32Exception win32 when !OperatingSystem.IsWindows()
                && win32.NativeErrorCode == 2 =>
                DirectoryObjectIdentityFailureKind.Missing,
            Win32Exception win32 when OperatingSystem.IsWindows()
                && win32.NativeErrorCode == 5 =>
                DirectoryObjectIdentityFailureKind.AccessDenied,
            Win32Exception win32 when !OperatingSystem.IsWindows()
                && win32.NativeErrorCode is 1 or 13 =>
                DirectoryObjectIdentityFailureKind.AccessDenied,
            Win32Exception win32 when OperatingSystem.IsLinux()
                && win32.NativeErrorCode is 38 or 95 =>
                DirectoryObjectIdentityFailureKind.IdentityUnsupported,
            PlatformNotSupportedException => DirectoryObjectIdentityFailureKind.IdentityUnsupported,
            InvalidOperationException => DirectoryObjectIdentityFailureKind.IdentityUnstable,
            _ => DirectoryObjectIdentityFailureKind.Unknown
        };
    }
}
