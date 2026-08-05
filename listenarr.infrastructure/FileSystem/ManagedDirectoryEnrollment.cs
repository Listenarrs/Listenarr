using System.ComponentModel;
using System.Text.Json;

namespace Listenarr.Infrastructure.FileSystem;

internal static class ManagedDirectoryEnrollment
{
    internal const string FileName = ".listenarr-root-enrollment.json";
    private const int MarkerVersion = 1;
    private const long MaximumBytes = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    internal static async Task<DirectoryObjectIdentityResolution> ResolveAsync(
        PinnedDirectoryCreation.PinnedDirectoryAnchor anchor,
        string nativeIdentity,
        bool enrollIfMissing,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentException.ThrowIfNullOrWhiteSpace(nativeIdentity);
        cancellationToken.ThrowIfCancellationRequested();

        var existing = TryRead(anchor, nativeIdentity, out var markerMissing);
        if (existing != null || !markerMissing || !enrollIfMissing)
        {
            return existing
                ?? DirectoryObjectIdentityResolution.Unavailable(
                    markerMissing
                        ? "The managed directory enrollment marker is missing."
                        : "The managed directory enrollment marker is invalid or identifies a different physical directory.");
        }

        var token = Guid.NewGuid().ToString("N");
        var payload = new EnrollmentPayload(
            MarkerVersion,
            token,
            nativeIdentity,
            DateTimeOffset.UtcNow);
        var temporaryName =
            $"{FileName}.{Guid.NewGuid():N}.tmp";
        try
        {
            await anchor.PublishNewFileAsync(
                temporaryName,
                FileName,
                beforeCreateAsync: () => Task.CompletedTask,
                writeAndFlushAsync: async stream =>
                {
                    await JsonSerializer.SerializeAsync(
                        stream,
                        payload,
                        JsonOptions,
                        cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                    stream.Flush(flushToDisk: true);
                },
                beforePublicationAsync: () => Task.CompletedTask,
                preserveTemporaryFileOnFailure: _ => false);
            anchor.FlushDirectoryEntry();
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or Win32Exception
                or InvalidOperationException)
        {
            var raced = TryRead(anchor, nativeIdentity, out _);
            if (raced != null)
            {
                return raced;
            }

            return DirectoryObjectIdentityResolution.Unavailable(
                $"The managed directory could not be enrolled safely: {exception.Message}");
        }

        var enrolled = TryRead(anchor, nativeIdentity, out _);
        return enrolled == null
            ? DirectoryObjectIdentityResolution.Unavailable(
                "The managed directory enrollment could not be verified after publication.")
            : enrolled with { EnrollmentCreated = true };
    }

    internal static async Task<string> RequireMatchingEnrollmentAsync(
        PinnedDirectoryCreation.PinnedDirectoryAnchor anchor,
        int? expectedVersion,
        string? expectedValue,
        string? unavailableReason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        if (expectedVersion != ManagedDirectoryIdentity.CurrentVersion
            || string.IsNullOrWhiteSpace(expectedValue)
            || !string.IsNullOrWhiteSpace(unavailableReason))
        {
            throw new InvalidOperationException(
                "The managed directory has no usable Listenarr enrollment identity.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var nativeIdentity = anchor.GetDirectoryObjectIdentity();
        var current = await ResolveAsync(
            anchor,
            nativeIdentity,
            enrollIfMissing: false,
            cancellationToken);
        if (!current.IsAvailable
            || current.Version != expectedVersion
            || !string.Equals(
                current.Value,
                expectedValue,
                StringComparison.Ordinal)
            || !anchor.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "The managed directory no longer identifies its enrolled physical generation.");
        }

        return nativeIdentity;
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
                "The managed directory enrollment marker is invalid and was preserved.");
        }

        using var marker = anchor.OpenExistingFile(
            FileName,
            requireDeleteAccess: true);
        if (TryRead(anchor, nativeIdentity, out _) == null
            || !marker.VisiblePathMatches()
            || !anchor.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "The managed directory enrollment marker changed before retirement.");
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
