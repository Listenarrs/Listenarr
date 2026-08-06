using System.Text.Json;

namespace Listenarr.Infrastructure.FileSystem;

// Compatibility reader/retirer for the short-lived marker-backed root identity
// format. New code must never publish this file; root physical identity is persisted
// in SQLite and verified against the pinned OS-native directory generation.
internal static class ManagedDirectoryEnrollment
{
    internal const string FileName = ".listenarr-root-enrollment.json";
    private const int MarkerVersion = 1;
    private const long MaximumBytes = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    internal static DirectoryObjectIdentityResolution ResolveExisting(
        PinnedDirectoryCreation.PinnedDirectoryAnchor anchor,
        string nativeIdentity)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentException.ThrowIfNullOrWhiteSpace(nativeIdentity);

        var existing = TryRead(anchor, nativeIdentity, out var markerMissing);
        return existing
            ?? DirectoryObjectIdentityResolution.Unavailable(
                markerMissing
                    ? "The legacy managed-directory enrollment marker is missing."
                    : "The legacy managed-directory enrollment marker is invalid or identifies a different physical directory.");
    }

    internal static void RetireValidMarker(
        PinnedDirectoryCreation.PinnedDirectoryAnchor anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        var nativeIdentity = anchor.GetDirectoryObjectIdentity();
        var current = TryRead(anchor, nativeIdentity, out var markerMissing);
        if (markerMissing)
        {
            return;
        }
        if (current == null)
        {
            throw new InvalidOperationException(
                "The legacy managed-directory enrollment marker is invalid and was preserved.");
        }

        RetireVerifiedMarker(anchor, nativeIdentity, current.Value!);
    }

    internal static bool TryRetireMatchingLegacyMarker(
        PinnedDirectoryCreation.PinnedDirectoryAnchor anchor,
        int? expectedVersion,
        string? expectedValue)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        if (expectedVersion != ManagedDirectoryIdentity.CurrentVersion
            || string.IsNullOrWhiteSpace(expectedValue))
        {
            return false;
        }

        var nativeIdentity = anchor.GetDirectoryObjectIdentity();
        var current = TryRead(anchor, nativeIdentity, out var markerMissing);
        if (markerMissing)
        {
            return false;
        }
        if (current == null
            || current.Version != expectedVersion
            || !string.Equals(current.Value, expectedValue, StringComparison.Ordinal))
        {
            return false;
        }

        RetireVerifiedMarker(anchor, nativeIdentity, expectedValue);
        return true;
    }

    private static void RetireVerifiedMarker(
        PinnedDirectoryCreation.PinnedDirectoryAnchor anchor,
        string nativeIdentity,
        string expectedValue)
    {
        using var marker = anchor.OpenExistingFile(
            FileName,
            requireDeleteAccess: true);
        var current = TryRead(anchor, nativeIdentity, out _);
        if (current == null
            || !string.Equals(current.Value, expectedValue, StringComparison.Ordinal)
            || !marker.VisiblePathMatches()
            || !anchor.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "The legacy managed-directory enrollment marker changed before retirement.");
        }

        marker.Delete();
        anchor.FlushDirectoryEntry();
    }

    private static DirectoryObjectIdentityResolution? TryRead(
        PinnedDirectoryCreation.PinnedDirectoryAnchor anchor,
        string nativeIdentity,
        out bool markerMissing)
    {
        markerMissing = false;
        using var marker = anchor.TryOpenExistingFile(
            FileName,
            requireDeleteAccess: false);
        if (marker == null)
        {
            markerMissing = true;
            return null;
        }

        try
        {
            if (!anchor.VisiblePathMatches() || !marker.VisiblePathMatches())
            {
                return null;
            }

            using var stream = marker.OpenReadStream(
                bufferSize: 4096,
                asynchronous: false);
            if (stream.Length <= 0 || stream.Length > MaximumBytes)
            {
                return null;
            }

            var payload = JsonSerializer.Deserialize<EnrollmentPayload>(
                stream,
                JsonOptions);
            if (payload == null
                || payload.Version != MarkerVersion
                || !Guid.TryParseExact(payload.Token, "N", out _)
                || string.IsNullOrWhiteSpace(payload.NativeIdentity)
                || !string.Equals(
                    payload.NativeIdentity,
                    nativeIdentity,
                    StringComparison.Ordinal)
                || !anchor.VisiblePathMatches()
                || !marker.VisiblePathMatches())
            {
                return null;
            }

            return new DirectoryObjectIdentityResolution(
                ManagedDirectoryIdentity.CurrentVersion,
                ManagedDirectoryIdentity.Create(
                    payload.Token,
                    nativeIdentity),
                null);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or JsonException
                or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    private sealed record EnrollmentPayload(
        int Version,
        string Token,
        string NativeIdentity,
        DateTimeOffset CreatedAtUtc);
}
