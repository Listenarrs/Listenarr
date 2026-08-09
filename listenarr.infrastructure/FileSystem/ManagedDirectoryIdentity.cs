using System.Security.Cryptography;
using System.Text;

namespace Listenarr.Infrastructure.FileSystem;

internal static class ManagedDirectoryIdentity
{
    internal const int CurrentVersion = 1;
    private const string Prefix = "listenarr-directory-v1";

    internal static bool Matches(
        int? version,
        string? value,
        string token,
        string nativeIdentity) =>
        version == CurrentVersion
        && !string.IsNullOrWhiteSpace(value)
        && string.Equals(
            value,
            Create(token, nativeIdentity),
            StringComparison.Ordinal);

    internal static string Create(string token, string nativeIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(nativeIdentity);
        return FormattableString.Invariant(
            $"{Prefix}:{token}:{HashNativeIdentity(nativeIdentity)}");
    }

    internal static string CreateMarkerless(string nativeIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nativeIdentity);
        var nativeHash = HashNativeIdentity(nativeIdentity);
        return FormattableString.Invariant(
            $"{Prefix}:{nativeHash[..32]}:{nativeHash}");
    }

    internal static bool MatchesNativeIdentity(
        int? version,
        string? value,
        string nativeIdentity)
    {
        if (version != CurrentVersion || string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Split(':');
        return parts.Length == 3
            && string.Equals(parts[0], Prefix, StringComparison.Ordinal)
            && Guid.TryParseExact(parts[1], "N", out _)
            && string.Equals(
                parts[2],
                HashNativeIdentity(nativeIdentity),
                StringComparison.Ordinal);
    }

    private static string HashNativeIdentity(string nativeIdentity) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(nativeIdentity)))
        .ToLowerInvariant();
}
