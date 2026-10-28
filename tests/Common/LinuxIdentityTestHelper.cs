namespace Listenarr.Tests.Common;

internal static class LinuxIdentityTestHelper
{
    internal static string ToMergedV1AugmentedIdentity(
        string strongIdentity,
        long birthTimeSeconds = 1,
        uint birthTimeNanoseconds = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strongIdentity);
        var parts = strongIdentity.Split(':');
        if (parts.Length < 6
            || !string.Equals(parts[0], "linux-generation", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A Linux strong-generation identity is required.",
                nameof(strongIdentity));
        }

        var suffix = string.Join(':', parts.Skip(4));
        return FormattableString.Invariant(
            $"linux:{parts[1]}:{parts[2]}:{parts[3]}:{birthTimeSeconds:x16}:{birthTimeNanoseconds:x8}:{suffix}");
    }
}
