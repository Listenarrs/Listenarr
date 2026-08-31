using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.FileSystem;

public sealed partial class CompatibilitySourceCleanupCoordinator
{
    private void TryRemoveEmptyOwnedQuarantine(string path, Guid batchId)
    {
        try
        {
            var markerPath = Path.Join(path, OwnershipMarkerName);
            var expectedMarker = JsonSerializer.Serialize(new
            {
                ProtocolVersion = CompatibilityFilePublicationProtocol.Current,
                BatchId = batchId
            });
            if (!File.Exists(markerPath)
                || !string.Equals(
                    File.ReadAllText(markerPath),
                    expectedMarker,
                    StringComparison.Ordinal)
                || Directory.EnumerateFileSystemEntries(path)
                    .Any(entry => !string.Equals(entry, markerPath, StringComparison.Ordinal)))
            {
                return;
            }

            File.Delete(markerPath);
            Directory.Delete(path, recursive: false);
        }
        catch (Exception exception) when (exception is not (
            OutOfMemoryException or StackOverflowException))
        {
            logger.LogDebug(
                exception,
                "Could not remove empty compatibility quarantine {QuarantinePath}",
                path);
        }
    }
}
