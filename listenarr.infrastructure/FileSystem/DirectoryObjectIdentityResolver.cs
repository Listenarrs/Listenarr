using System.ComponentModel;
using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.FileSystem;

internal sealed class DirectoryObjectIdentityResolver(
    Func<PinnedDirectoryCreation.PinnedDirectoryAnchor, string>?
        nativeIdentityResolver = null) : IDirectoryObjectIdentityResolver
{
    private readonly Func<PinnedDirectoryCreation.PinnedDirectoryAnchor, string>
        _nativeIdentityResolver = nativeIdentityResolver
        ?? (static anchor => anchor.GetDirectoryObjectIdentity());

    public Task<DirectoryObjectIdentityResolution> ResolveAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        ResolvePinnedAsync(
            path,
            cancellationToken,
            nativeIdentity => new DirectoryObjectIdentityResolution(
                ManagedDirectoryIdentity.CurrentVersion,
                ManagedDirectoryIdentity.CreateMarkerless(nativeIdentity),
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
            nativeIdentity => ManagedDirectoryIdentity.MatchesNativeIdentity(
                    expectedVersion,
                    expectedValue,
                    nativeIdentity)
                ? new DirectoryObjectIdentityResolution(
                    expectedVersion,
                    expectedValue,
                    null)
                : DirectoryObjectIdentityResolution.Unavailable(
                    "The live directory no longer matches its persisted physical identity.",
                    DirectoryObjectIdentityFailureKind.IdentityMismatch));
    }

    private Task<DirectoryObjectIdentityResolution> ResolvePinnedAsync(
        string path,
        CancellationToken cancellationToken,
        Func<string, DirectoryObjectIdentityResolution> resolve)
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
            var nativeIdentity = _nativeIdentityResolver(anchor);
            if (!anchor.VisiblePathMatches())
            {
                return Task.FromResult(
                    DirectoryObjectIdentityResolution.Unavailable(
                        "The directory changed while its physical identity was captured.",
                        DirectoryObjectIdentityFailureKind.IdentityUnstable));
            }

            return Task.FromResult(resolve(nativeIdentity));
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

    private static DirectoryObjectIdentityFailureKind ClassifyFailure(Exception exception)
    {
        return exception switch
        {
            DirectoryNotFoundException or FileNotFoundException =>
                DirectoryObjectIdentityFailureKind.Missing,
            UnauthorizedAccessException => DirectoryObjectIdentityFailureKind.AccessDenied,
            Win32Exception win32 when win32.NativeErrorCode is 2 or 3 =>
                DirectoryObjectIdentityFailureKind.Missing,
            Win32Exception win32 when win32.NativeErrorCode == 5 =>
                DirectoryObjectIdentityFailureKind.AccessDenied,
            PlatformNotSupportedException => DirectoryObjectIdentityFailureKind.IdentityUnsupported,
            InvalidOperationException => DirectoryObjectIdentityFailureKind.IdentityUnstable,
            _ => DirectoryObjectIdentityFailureKind.Unknown
        };
    }
}
