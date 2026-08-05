using System.Security.Cryptography;
using System.Text;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private readonly record struct FileMoveContent(long Length, string Sha256);
    private readonly record struct FileMoveFence(
        int Version,
        Guid? OperationId,
        string SourceIdentity,
        string DestinationIdentity,
        string? SourceObjectIdentity,
        string? DestinationStageObjectIdentity,
        string? DestinationPreviousObjectIdentity,
        string? PublishedDestinationObjectIdentity,
        bool NativeRename,
        FileMoveContent Content);

    private static async Task<FileMoveContent> CaptureFileMoveContentAsync(
        string path)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = stream.Length;
        var hash = await SHA256.HashDataAsync(stream);
        return new FileMoveContent(length, Convert.ToHexString(hash));
    }

    private static async Task<FileMoveContent> CaptureFileMoveContentAsync(
        PinnedDirectoryCreation.PinnedFileEntry entry)
    {
        await using var stream = entry.OpenReadStream(
            bufferSize: 128 * 1024,
            asynchronous: false);
        var length = stream.Length;
        stream.Position = 0;
        var hash = await SHA256.HashDataAsync(stream);
        return new FileMoveContent(length, Convert.ToHexString(hash));
    }

    private static Task<bool> FileMatchesMoveContentAsync(
        PinnedDirectoryCreation.PinnedFileEntry entry,
        FileMoveContent expected) =>
        entry.MatchesAsync(
            expected.Length,
            expected.Sha256,
            CancellationToken.None);

    private static async Task<bool> FileMatchesMoveContentAsync(
        string path,
        FileMoveContent expected)
    {
        if (!File.Exists(path) || IsLinkedOrUnverifiableEntry(path))
        {
            return false;
        }

        var actual = await CaptureFileMoveContentAsync(path);
        return actual == expected;
    }

    private static async Task WriteFileMoveContentAsync(
        PinnedDirectoryCreation.PinnedFileEntry entry,
        FileMoveContent content,
        Guid? operationId,
        string sourceIdentity,
        string destinationIdentity,
        string sourceObjectIdentity,
        string? destinationStageObjectIdentity,
        string? destinationPreviousObjectIdentity,
        string? publishedDestinationObjectIdentity,
        bool nativeRename)
    {
        const int version = 2;
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceObjectIdentity);
        var body =
            $"version={version}\n"
            + $"operationId={operationId?.ToString("D") ?? string.Empty}\n"
            + $"sourceIdentity={sourceIdentity}\n"
            + $"destinationIdentity={destinationIdentity}\n"
            + $"sourceObjectIdentity={sourceObjectIdentity}\n"
            + $"destinationStageObjectIdentity={destinationStageObjectIdentity ?? string.Empty}\n"
            + $"destinationPreviousObjectIdentity={destinationPreviousObjectIdentity ?? string.Empty}\n"
            + $"publishedDestinationObjectIdentity={publishedDestinationObjectIdentity ?? string.Empty}\n"
            + $"mode={(nativeRename ? "native" : "copy")}\n"
            + $"length={content.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n"
            + $"sha256={content.Sha256}\n";
        var checksum = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(body)));
        var payload = Encoding.UTF8.GetBytes(
            body + $"checksum={checksum}\n");
        await using var stream = entry.OpenWriteStream(
            bufferSize: 4096,
            asynchronous: false);
        stream.SetLength(0);
        stream.Position = 0;
        await stream.WriteAsync(payload);
        await stream.FlushAsync();
        stream.Flush(flushToDisk: true);
    }

    private static async Task<FileMoveFence?> ReadFileMoveContentAsync(
        PinnedDirectoryCreation.PinnedFileEntry entry)
    {
        await using var stream = entry.OpenReadStream(
            bufferSize: 4096,
            asynchronous: false);
        if (stream.Length is <= 0 or > 4096)
        {
            return null;
        }

        var payload = new byte[stream.Length];
        var offset = 0;
        while (offset < payload.Length)
        {
            var read = await stream.ReadAsync(payload.AsMemory(offset));
            if (read == 0)
            {
                return null;
            }
            offset += read;
        }

        var parts = Encoding.UTF8.GetString(payload)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var version = parts.Length > 0 && parts[0] == "version=2"
            ? 2
            : parts.Length > 0 && parts[0] == "version=1"
                ? 1
                : 0;
        var modeIndex = version == 2 ? 8 : 4;
        var lengthIndex = version == 2 ? 9 : 5;
        var shaIndex = version == 2 ? 10 : 6;
        var checksumIndex = version == 2 ? 11 : 7;
        var expectedPartCount = version == 2 ? 12 : 8;
        if (version == 0
            || parts.Length != expectedPartCount
            || !parts[1].StartsWith("operationId=", StringComparison.Ordinal)
            || !parts[2].StartsWith("sourceIdentity=", StringComparison.Ordinal)
            || !parts[3].StartsWith("destinationIdentity=", StringComparison.Ordinal)
            || (version == 2
                && (!parts[4].StartsWith("sourceObjectIdentity=", StringComparison.Ordinal)
                    || !parts[5].StartsWith("destinationStageObjectIdentity=", StringComparison.Ordinal)
                    || !parts[6].StartsWith("destinationPreviousObjectIdentity=", StringComparison.Ordinal)
                    || !parts[7].StartsWith("publishedDestinationObjectIdentity=", StringComparison.Ordinal)))
            || parts[modeIndex] is not ("mode=native" or "mode=copy")
            || !parts[lengthIndex].StartsWith("length=", StringComparison.Ordinal)
            || !parts[shaIndex].StartsWith("sha256=", StringComparison.Ordinal)
            || !parts[checksumIndex].StartsWith("checksum=", StringComparison.Ordinal)
            || !long.TryParse(
                parts[lengthIndex]["length=".Length..],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var length)
            || length < 0
            || parts[shaIndex].Length != "sha256=".Length + 64
            || parts[shaIndex]["sha256=".Length..].Any(character => !Uri.IsHexDigit(character))
            || parts[checksumIndex].Length != "checksum=".Length + 64
            || parts[checksumIndex]["checksum=".Length..].Any(character => !Uri.IsHexDigit(character)))
        {
            return null;
        }

        var body = string.Join('\n', parts[..checksumIndex]) + "\n";
        var expectedChecksum = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(body)));
        if (!string.Equals(
                expectedChecksum,
                parts[checksumIndex]["checksum=".Length..],
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var operationText = parts[1]["operationId=".Length..];
        Guid? operationId = null;
        if (operationText.Length != 0)
        {
            if (!Guid.TryParseExact(operationText, "D", out var parsedOperationId))
            {
                return null;
            }
            operationId = parsedOperationId;
        }
        var sourceIdentity = parts[2]["sourceIdentity=".Length..];
        var destinationIdentity = parts[3]["destinationIdentity=".Length..];
        var sourceObjectIdentity = version == 2
            ? parts[4]["sourceObjectIdentity=".Length..]
            : null;
        var destinationStageObjectIdentity = version == 2
            ? parts[5]["destinationStageObjectIdentity=".Length..]
            : null;
        var destinationPreviousObjectIdentity = version == 2
            ? parts[6]["destinationPreviousObjectIdentity=".Length..]
            : null;
        var publishedDestinationObjectIdentity = version == 2
            ? parts[7]["publishedDestinationObjectIdentity=".Length..]
            : null;
        if (sourceIdentity.Length == 0
            || destinationIdentity.Length == 0
            || sourceIdentity.Contains('\r')
            || destinationIdentity.Contains('\r')
            || (version == 2
                && (string.IsNullOrWhiteSpace(sourceObjectIdentity)
                    || sourceObjectIdentity.Contains('\r')
                    || destinationStageObjectIdentity!.Contains('\r')
                    || destinationPreviousObjectIdentity!.Contains('\r')
                    || publishedDestinationObjectIdentity!.Contains('\r'))))
        {
            return null;
        }

        return new FileMoveFence(
            version,
            operationId,
            sourceIdentity,
            destinationIdentity,
            sourceObjectIdentity,
            string.IsNullOrEmpty(destinationStageObjectIdentity)
                ? null
                : destinationStageObjectIdentity,
            string.IsNullOrEmpty(destinationPreviousObjectIdentity)
                ? null
                : destinationPreviousObjectIdentity,
            string.IsNullOrEmpty(publishedDestinationObjectIdentity)
                ? null
                : publishedDestinationObjectIdentity,
            NativeRename: parts[modeIndex] == "mode=native",
            new FileMoveContent(
                length,
                parts[shaIndex]["sha256=".Length..].ToUpperInvariant()));
    }

    private static async Task<FileMoveContent?> ReadLegacyFileMoveContentAsync(
        PinnedDirectoryCreation.PinnedFileEntry entry)
    {
        await using var stream = entry.OpenReadStream(256, asynchronous: false);
        if (stream.Length is <= 0 or > 256)
        {
            return null;
        }
        var bytes = new byte[stream.Length];
        var read = await stream.ReadAtLeastAsync(
            bytes,
            bytes.Length,
            throwOnEndOfStream: false);
        if (read != bytes.Length)
        {
            return null;
        }
        var parts = Encoding.ASCII.GetString(bytes)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2
            || !long.TryParse(
                parts[0],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var length)
            || length < 0
            || parts[1].Length != 64
            || parts[1].Any(character => !Uri.IsHexDigit(character)))
        {
            return null;
        }
        return new FileMoveContent(length, parts[1].ToUpperInvariant());
    }
}
