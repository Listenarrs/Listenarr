using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal static partial class LibraryDirectoryOwnershipMarker
{
    internal static bool MatchesLegacyPayload(
        LibraryDirectoryOwnership ownership,
        MarkerPayload payload) =>
        payload.Version == 1
        && string.Equals(
            payload.OwnershipToken,
            ownership.OwnershipToken,
            StringComparison.Ordinal)
        && MarkerPathMatches(
            payload.CanonicalPath,
            ownership.CanonicalPath,
            ownership.GetIdentity().Semantics);

    internal static bool MatchesCurrentPayload(
        LibraryDirectoryOwnership ownership,
        MarkerPayload payload) =>
        payload.Version == Version
        && string.Equals(
            payload.OwnershipToken,
            ownership.OwnershipToken,
            StringComparison.Ordinal)
        && MarkerPathMatches(
            payload.CanonicalPath,
            ownership.CanonicalPath,
            ownership.GetIdentity().Semantics)
        && payload.ManagedRootFolderId == ownership.ManagedRootFolderId
        && payload.DirectoryObjectIdentityVersion
            == ownership.DirectoryObjectIdentityVersion
        && string.Equals(
            payload.DirectoryObjectIdentity,
            ownership.DirectoryObjectIdentity,
            StringComparison.Ordinal);

    private static bool MarkerPathMatches(
        string persistedPath,
        string expectedPath,
        FileSystemPathSemantics semantics) =>
        FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
            persistedPath,
            out var canonicalPath,
            out _,
            semantics.Syntax)
        && FileSystemPathIdentity.AreEquivalent(
            canonicalPath,
            expectedPath,
            semantics);

    internal sealed record MarkerPayload(
        int Version,
        string OwnershipToken,
        string CanonicalPath,
        int? ManagedRootFolderId = null,
        int? DirectoryObjectIdentityVersion = null,
        string? DirectoryObjectIdentity = null);
}
