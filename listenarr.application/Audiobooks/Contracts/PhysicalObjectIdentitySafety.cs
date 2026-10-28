namespace Listenarr.Application.Audiobooks.Contracts;

public static class PhysicalObjectIdentitySafety
{
    public static bool IsKnownWeak(string? identity)
    {
        if (string.IsNullOrWhiteSpace(identity)
            || !identity.StartsWith("linux:", StringComparison.Ordinal)
                && !identity.StartsWith(
                    "linux-generation:",
                    StringComparison.Ordinal))
        {
            return false;
        }

        var parts = identity.Split(':');
        if (parts.Length == 6
            && string.Equals(parts[0], "linux", StringComparison.Ordinal)
            && IsFixedHex(parts[1], 8)
            && IsFixedHex(parts[2], 8)
            && IsFixedHex(parts[3], 16)
            && IsFixedHex(parts[4], 16)
            && IsFixedHex(parts[5], 8))
        {
            return true;
        }

        var suffixIndex = parts[0] switch
        {
            "linux-generation" when parts.Length >= 6
                && IsFixedHex(parts[1], 8)
                && IsFixedHex(parts[2], 8)
                && IsFixedHex(parts[3], 16) => 4,
            "linux" when parts.Length >= 8
                && IsFixedHex(parts[1], 8)
                && IsFixedHex(parts[2], 8)
                && IsFixedHex(parts[3], 16)
                && IsFixedHex(parts[4], 16)
                && IsFixedHex(parts[5], 8) => 6,
            _ => -1
        };

        return suffixIndex >= 0
            && parts.Length == suffixIndex + 3
            && string.Equals(parts[suffixIndex], "fh", StringComparison.Ordinal)
            && string.Equals(
                parts[suffixIndex + 1],
                "00000081",
                StringComparison.OrdinalIgnoreCase)
            && parts[suffixIndex + 2].Length > 0
            && parts[suffixIndex + 2].Length % 2 == 0
            && parts[suffixIndex + 2].All(Uri.IsHexDigit);
    }

    private static bool IsFixedHex(string value, int length) =>
        value.Length == length && value.All(Uri.IsHexDigit);
}
