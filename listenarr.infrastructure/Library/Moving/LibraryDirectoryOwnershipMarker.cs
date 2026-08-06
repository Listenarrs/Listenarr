using System.Text.Json;

namespace Listenarr.Infrastructure.Library.Moving;

internal static partial class LibraryDirectoryOwnershipMarker
{
    internal const string FileName = ".listenarr-directory-owner.json";
    internal const int Version = 2;
    private const long MaximumBytes = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void Validate(
        LibraryDirectoryOwnership ownership,
        string directory)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        var fullDirectory = Path.GetFullPath(directory);
        var parentPath = Path.GetDirectoryName(fullDirectory)
            ?? throw new InvalidOperationException(
                "The durable directory ownership path has no parent.");
        using var parent = PinnedDirectoryCreation.OpenPinnedBoundary(parentPath);
        using var pinnedDirectory = parent.OpenExistingChild(
            Path.GetFileName(fullDirectory));
        Validate(ownership, pinnedDirectory, parent);
    }

    public static bool ContainsOnlyInsideMarker(
        LibraryDirectoryOwnership ownership,
        string directory)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        var fullDirectory = Path.GetFullPath(directory);
        var parentPath = Path.GetDirectoryName(fullDirectory)
            ?? throw new InvalidOperationException(
                "The durable directory ownership path has no parent.");
        using var parent = PinnedDirectoryCreation.OpenPinnedBoundary(parentPath);
        using var pinnedDirectory = parent.OpenExistingChild(
            Path.GetFileName(fullDirectory));
        return ContainsOnlyInsideMarker(ownership, pinnedDirectory, parent);
    }

    public static bool ContainsOnlyInsideMarker(
        LibraryDirectoryOwnership ownership,
        PinnedDirectoryCreation.PinnedDirectoryAnchor directory,
        PinnedDirectoryCreation.PinnedDirectoryAnchor parent)
    {
        Validate(ownership, directory, parent);
        var markerPath = Path.Join(directory.FullPath, FileName);
        var entries = Directory.EnumerateFileSystemEntries(directory.FullPath)
            .Take(2)
            .ToList();
        if (!directory.VisiblePathMatches() || !parent.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "The durable ownership directory changed during enumeration.");
        }

        return entries.Count == 1
            && string.Equals(entries[0], markerPath, StringComparison.Ordinal);
    }

    public static void DeleteInsideMarker(
        LibraryDirectoryOwnership ownership,
        string directory)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        var fullDirectory = Path.GetFullPath(directory);
        var parentPath = Path.GetDirectoryName(fullDirectory)
            ?? throw new InvalidOperationException(
                "The durable directory ownership path has no parent.");
        using var parent = PinnedDirectoryCreation.OpenPinnedBoundary(parentPath);
        using var pinnedDirectory = parent.OpenExistingChild(
            Path.GetFileName(fullDirectory));
        DeleteInsideMarker(ownership, pinnedDirectory, parent);
    }

    public static void DeleteInsideMarker(
        LibraryDirectoryOwnership ownership,
        PinnedDirectoryCreation.PinnedDirectoryAnchor directory,
        PinnedDirectoryCreation.PinnedDirectoryAnchor parent)
    {
        Validate(ownership, directory, parent);
        using var marker = directory.OpenExistingFile(
            FileName,
            requireDeleteAccess: true);
        ValidateMarkerFile(ownership, marker);
        if (!directory.VisiblePathMatches() || !parent.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "The durable ownership directory changed before marker retirement.");
        }

        ValidateMarkerFile(ownership, marker);
        marker.Delete();
        parent.FlushDirectoryEntry();
    }

    public static void Validate(
        LibraryDirectoryOwnership ownership,
        PinnedDirectoryCreation.PinnedDirectoryAnchor directory,
        PinnedDirectoryCreation.PinnedDirectoryAnchor parent)
    {
        try
        {
            ValidatePinnedCore(ownership, directory, parent);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException
                or System.ComponentModel.Win32Exception
                or NotSupportedException)
        {
            throw new InvalidOperationException(
                "The durable directory ownership marker could not be pinned.",
                exception);
        }
    }

    public static void ValidateSiblingMarker(
        LibraryDirectoryOwnership ownership,
        PinnedDirectoryCreation.PinnedDirectoryAnchor parent)
    {
        try
        {
            using var sibling = parent.OpenExistingFile(
                Path.GetFileName(GetSiblingPath(ownership)),
                requireDeleteAccess: false);
            ValidateMarkerFile(ownership, sibling);
            if (!parent.VisiblePathMatches() || !sibling.VisiblePathMatches())
            {
                throw new InvalidOperationException(
                    "The durable ownership sibling marker changed during validation.");
            }

            ValidateMarkerFile(ownership, sibling);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException
                or System.ComponentModel.Win32Exception
                or NotSupportedException)
        {
            throw new InvalidOperationException(
                "The durable directory ownership sibling marker could not be pinned.",
                exception);
        }
    }

    private static void ValidatePinnedCore(
        LibraryDirectoryOwnership ownership,
        PinnedDirectoryCreation.PinnedDirectoryAnchor directory,
        PinnedDirectoryCreation.PinnedDirectoryAnchor parent)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        using var inside = directory.OpenExistingFile(
            FileName,
            requireDeleteAccess: false);
        using var sibling = parent.OpenExistingFile(
            Path.GetFileName(GetSiblingPath(ownership)),
            requireDeleteAccess: false);
        ValidateMarkerFile(ownership, inside);
        ValidateMarkerFile(ownership, sibling);
        if (!directory.VisiblePathMatches() || !parent.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "The durable ownership directory changed during marker validation.");
        }

        ValidateMarkerFile(ownership, inside);
        ValidateMarkerFile(ownership, sibling);
    }

    public static void DeleteSiblingMarker(LibraryDirectoryOwnership ownership)
    {
        DeleteValidatedMarker(ownership, GetSiblingPath(ownership));
    }

    public static bool TryDeleteRetiredSiblingMarker(
        LibraryDirectoryOwnership ownership,
        out string? reason)
    {
        var markerPath = GetSiblingPath(ownership);
        try
        {
            DeleteValidatedMarker(ownership, markerPath);
            reason = null;
            return true;
        }
        catch (Exception exception) when (exception is
            ArgumentException or IOException or UnauthorizedAccessException
                or InvalidOperationException or NotSupportedException
                or System.ComponentModel.Win32Exception)
        {
            if (!File.Exists(markerPath))
            {
                reason = null;
                return true;
            }

            reason = exception.Message;
            return false;
        }
    }

    public static bool TryRetireMatchingSiblingArtifacts(
        LibraryDirectoryOwnership ownership,
        PinnedDirectoryCreation.PinnedDirectoryAnchor parent,
        out string? reason)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        ArgumentNullException.ThrowIfNull(parent);
        try
        {
            var siblingName = Path.GetFileName(GetSiblingPath(ownership));
            RetireMatchingMarkerIfPresent(
                ownership,
                parent,
                siblingName);
            RetireMatchingMarkerIfPresent(
                ownership,
                parent,
                siblingName + ".v2.tmp");
            RetireMatchingMarkerIfPresent(
                ownership,
                parent,
                siblingName + ".migration.tmp");
            RetireMatchingMarkerIfPresent(
                ownership,
                parent,
                PinnedDirectoryCreation.GetConditionalReplacementBackupName(
                    siblingName));
            parent.FlushDirectoryEntry();
            reason = null;
            return true;
        }
        catch (Exception exception) when (exception is
            ArgumentException or IOException or UnauthorizedAccessException
                or InvalidOperationException or NotSupportedException
                or System.ComponentModel.Win32Exception)
        {
            reason = exception.Message;
            return false;
        }
    }

    public static bool TryRetireMatchingMarkers(
        LibraryDirectoryOwnership ownership,
        PinnedDirectoryCreation.PinnedDirectoryAnchor directory,
        PinnedDirectoryCreation.PinnedDirectoryAnchor parent,
        out string? reason)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(parent);
        try
        {
            RetireMatchingMarkerIfPresent(
                ownership,
                directory,
                FileName);
            RetireMatchingMarkerIfPresent(
                ownership,
                directory,
                FileName + ".v2.tmp");
            RetireMatchingMarkerIfPresent(
                ownership,
                directory,
                FileName + ".migration.tmp");
            RetireMatchingMarkerIfPresent(
                ownership,
                directory,
                PinnedDirectoryCreation.GetConditionalReplacementBackupName(
                    FileName));
            if (!TryRetireMatchingSiblingArtifacts(
                    ownership,
                    parent,
                    out reason))
            {
                return false;
            }
            directory.FlushDirectoryEntry();
            reason = null;
            return true;
        }
        catch (Exception exception) when (exception is
            ArgumentException or IOException or UnauthorizedAccessException
                or InvalidOperationException or NotSupportedException
                or System.ComponentModel.Win32Exception)
        {
            reason = exception.Message;
            return false;
        }
    }

    private static void RetireMatchingMarkerIfPresent(
        LibraryDirectoryOwnership ownership,
        PinnedDirectoryCreation.PinnedDirectoryAnchor parent,
        string fileName)
    {
        using var marker = parent.TryOpenExistingFile(
            fileName,
            requireDeleteAccess: true);
        if (marker == null)
        {
            return;
        }

        var payload = ReadPayload(marker);
        if (!MatchesCurrentPayload(ownership, payload)
            && !MatchesLegacyPayload(ownership, payload))
        {
            throw new InvalidOperationException(
                "A legacy directory ownership artifact does not match the persisted ownership claim.");
        }
        if (!parent.VisiblePathMatches() || !marker.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "A legacy directory ownership artifact changed before retirement.");
        }

        var verifiedPayload = ReadPayload(marker);
        if (!MatchesCurrentPayload(ownership, verifiedPayload)
            && !MatchesLegacyPayload(ownership, verifiedPayload))
        {
            throw new InvalidOperationException(
                "A legacy directory ownership artifact changed before retirement.");
        }

        marker.Delete();
    }

    public static IReadOnlyList<string> GetMarkerPaths(
        LibraryDirectoryOwnership ownership) =>
        [GetInsidePath(ownership.CanonicalPath), GetSiblingPath(ownership)];

    public static bool HasValidSiblingMarker(LibraryDirectoryOwnership ownership)
    {
        try
        {
            var siblingPath = GetSiblingPath(ownership);
            var parentPath = Path.GetDirectoryName(siblingPath)
                ?? throw new InvalidOperationException(
                    "The durable directory ownership sibling marker has no parent.");
            using var parent = PinnedDirectoryCreation.OpenPinnedBoundary(parentPath);
            ValidateSiblingMarker(ownership, parent);
            return true;
        }
        catch (Exception exception) when (exception is
            ArgumentException or IOException or UnauthorizedAccessException
                or InvalidOperationException or NotSupportedException
                or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static void DeleteValidatedMarker(
        LibraryDirectoryOwnership ownership,
        string markerPath)
    {
        var parentPath = Path.GetDirectoryName(Path.GetFullPath(markerPath))
            ?? throw new InvalidOperationException(
                "The durable directory ownership marker has no parent.");
        using var parent = PinnedDirectoryCreation.OpenPinnedBoundary(
            parentPath);
        using var marker = parent.OpenExistingFile(
            Path.GetFileName(markerPath),
            requireDeleteAccess: true);
        ValidateMarkerFile(ownership, marker);
        if (!parent.VisiblePathMatches() || !marker.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "The durable directory ownership marker changed before retirement.");
        }

        ValidateMarkerFile(ownership, marker);
        marker.Delete();
    }

    internal static void ValidateMarkerFile(
        LibraryDirectoryOwnership ownership,
        PinnedDirectoryCreation.PinnedFileEntry markerEntry)
    {
        using var stream = markerEntry.OpenReadStream(
            bufferSize: 4096,
            asynchronous: false);
        if (stream.Length <= 0 || stream.Length > MaximumBytes)
        {
            throw new InvalidOperationException(
                "The durable directory ownership marker has an invalid size.");
        }

        MarkerPayload? marker;
        try
        {
            stream.Position = 0;
            marker = JsonSerializer.Deserialize<MarkerPayload>(stream, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "The durable directory ownership marker is invalid.",
                exception);
        }

        var expected = new MarkerPayload(
            Version,
            ownership.OwnershipToken,
            ownership.CanonicalPath,
            ownership.ManagedRootFolderId,
            ownership.DirectoryObjectIdentityVersion,
            ownership.DirectoryObjectIdentity);
        var semantics = ownership.GetIdentity().Semantics;
        var pathsMatch = marker != null
            && MarkerPathMatches(
                marker.CanonicalPath,
                expected.CanonicalPath,
                semantics);
        if (marker == null
            || marker.Version != Version
            || !string.Equals(
                marker.OwnershipToken,
                expected.OwnershipToken,
                StringComparison.Ordinal)
            || marker.ManagedRootFolderId != expected.ManagedRootFolderId
            || marker.DirectoryObjectIdentityVersion
                != expected.DirectoryObjectIdentityVersion
            || !string.Equals(
                marker.DirectoryObjectIdentity,
                expected.DirectoryObjectIdentity,
                StringComparison.Ordinal)
            || !pathsMatch)
        {
            throw new InvalidOperationException(
                "The durable directory ownership marker does not match the persisted ownership claim.");
        }
    }

    private static string GetInsidePath(string directory) => Path.Join(directory, FileName);

    internal static void ValidateOwnershipToken(string ownershipToken)
    {
        if (!Guid.TryParseExact(ownershipToken, "N", out _))
        {
            throw new InvalidOperationException(
                "The durable directory ownership token is invalid.");
        }
    }

    private static string GetSiblingPath(LibraryDirectoryOwnership ownership)
    {
        ValidateOwnershipToken(ownership.OwnershipToken);
        var parent = Path.GetDirectoryName(ownership.CanonicalPath)
            ?? throw new InvalidOperationException(
                "The durable directory ownership path has no parent directory.");
        return Path.Join(
            parent,
            $".listenarr-directory-owner-{ownership.OwnershipToken}.json");
    }

    internal static string SerializePayload(LibraryDirectoryOwnership ownership) =>
        SerializePayload(
            new MarkerPayload(
                Version,
                ownership.OwnershipToken,
                ownership.CanonicalPath,
                ownership.ManagedRootFolderId,
                ownership.DirectoryObjectIdentityVersion,
                ownership.DirectoryObjectIdentity));

    internal static string SerializePayload(MarkerPayload payload) =>
        JsonSerializer.Serialize(payload, JsonOptions);

    internal static MarkerPayload ReadPayload(
        PinnedDirectoryCreation.PinnedFileEntry markerEntry)
    {
        using var stream = markerEntry.OpenReadStream(
            bufferSize: 4096,
            asynchronous: false);
        if (stream.Length <= 0 || stream.Length > MaximumBytes)
        {
            throw new InvalidOperationException(
                "The durable directory ownership marker has an invalid size.");
        }

        try
        {
            return JsonSerializer.Deserialize<MarkerPayload>(stream, JsonOptions)
                ?? throw new InvalidOperationException(
                    "The durable directory ownership marker is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "The durable directory ownership marker is invalid.",
                exception);
        }
    }

}
