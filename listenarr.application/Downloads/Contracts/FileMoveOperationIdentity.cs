using System.Security.Cryptography;
using System.Text;
using Listenarr.Domain.Common;

namespace Listenarr.Application.Downloads.Contracts;

public static class FileMoveOperationIdentity
{
    public static Guid CreateForPaths(
        string scope,
        int audiobookId,
        object operationKind,
        string sourcePath,
        FileSystemPathSemantics sourceSemantics,
        string destinationPath,
        FileSystemPathSemantics destinationSemantics)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(operationKind);

        var sourceKey = FileSystemPathIdentity.CreateKey(
            "file-move-source",
            sourcePath,
            sourceSemantics);
        var destinationKey = FileSystemPathIdentity.CreateKey(
            "file-move-destination",
            destinationPath,
            destinationSemantics);
        var payload = string.Join(
            "\0",
            scope,
            audiobookId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToString(
                operationKind,
                System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            sourceKey,
            destinationKey);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return new Guid(hash.AsSpan(0, 16));
    }
}
