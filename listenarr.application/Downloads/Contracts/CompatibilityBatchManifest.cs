using System.Security.Cryptography;
using System.Text;

namespace Listenarr.Application.Downloads.Contracts;

public readonly record struct CompatibilityBatchManifest(
    int ExpectedMemberCount,
    string SourceManifestSha256)
{
    public static CompatibilityBatchManifest Create(
        IEnumerable<string> sourcePaths)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        var normalized = sourcePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (normalized.Length == 0)
        {
            throw new ArgumentException(
                "A compatibility batch manifest requires at least one source path.",
                nameof(sourcePaths));
        }

        var payload = Encoding.UTF8.GetBytes(string.Join('\0', normalized));
        return new CompatibilityBatchManifest(
            normalized.Length,
            Convert.ToHexString(SHA256.HashData(payload)));
    }

    public bool Matches(IEnumerable<string> sourcePaths)
    {
        var actual = Create(sourcePaths);
        return actual.ExpectedMemberCount == ExpectedMemberCount
            && string.Equals(
                actual.SourceManifestSha256,
                SourceManifestSha256,
                StringComparison.OrdinalIgnoreCase);
    }

    public void Validate()
    {
        if (ExpectedMemberCount <= 0)
        {
            throw new InvalidOperationException(
                "A compatibility batch manifest must contain at least one expected member.");
        }
        if (string.IsNullOrWhiteSpace(SourceManifestSha256)
            || SourceManifestSha256.Length != 64
            || !SourceManifestSha256.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException(
                "A compatibility batch manifest SHA-256 must contain exactly 64 hexadecimal characters.");
        }
    }
}
