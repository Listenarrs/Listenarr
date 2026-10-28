using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Listenarr.Infrastructure.FileSystem;

internal sealed partial class PinnedDirectoryCreation
{
    private static IReadOnlyList<string> GetLinuxObjectIdentityCandidates(
        SafeFileHandle handle)
    {
        var evidence = ReadLinuxObjectIdentityEvidence(handle);
        return CreateLinuxObjectIdentityCandidatesFromEvidence(
            evidence.DeviceMajor,
            evidence.DeviceMinor,
            evidence.Inode,
            evidence.HasBirthTime,
            evidence.BirthTimeSeconds,
            evidence.BirthTimeNanoseconds,
            evidence.GenerationIdentities);
    }

    private static IReadOnlyList<string> GetLinuxLegacyWeakObjectIdentityCandidates(
        SafeFileHandle handle)
    {
        var evidence = ReadLinuxObjectIdentityEvidence(handle);
        return CreateLinuxLegacyWeakObjectIdentityCandidatesFromEvidence(
            evidence.DeviceMajor,
            evidence.DeviceMinor,
            evidence.Inode,
            evidence.HasBirthTime,
            evidence.BirthTimeSeconds,
            evidence.BirthTimeNanoseconds,
            evidence.GenerationIdentities);
    }

    private static LinuxObjectIdentityEvidence ReadLinuxObjectIdentityEvidence(
        SafeFileHandle handle)
    {
        const uint statxInode = 0x00000100;
        const uint statxBirthTime = 0x00000800;
        const uint requestedMask = statxInode | statxBirthTime;
        if (Statx(
                handle.DangerousGetHandle().ToInt32(),
                string.Empty,
                0x1000,
                requestedMask,
                out var information) != 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        if ((information.Mask & statxInode) == 0)
        {
            throw new PlatformNotSupportedException(
                "The filesystem does not expose an inode for durable object identity.");
        }

        return new LinuxObjectIdentityEvidence(
            information.DeviceMajor,
            information.DeviceMinor,
            information.Inode,
            (information.Mask & statxBirthTime) != 0,
            information.BirthTime.Seconds,
            information.BirthTime.Nanoseconds,
            GetLinuxGenerationIdentityCandidates(handle));
    }

    private sealed record LinuxObjectIdentityEvidence(
        uint DeviceMajor,
        uint DeviceMinor,
        ulong Inode,
        bool HasBirthTime,
        long BirthTimeSeconds,
        uint BirthTimeNanoseconds,
        IReadOnlyList<string> GenerationIdentities);

    internal static string CreateLinuxObjectIdentityFromEvidence(
        uint deviceMajor,
        uint deviceMinor,
        ulong inode,
        bool hasBirthTime,
        long birthTimeSeconds,
        uint birthTimeNanoseconds,
        string? generationIdentity) =>
        CreateLinuxObjectIdentityCandidatesFromEvidence(
            deviceMajor,
            deviceMinor,
            inode,
            hasBirthTime,
            birthTimeSeconds,
            birthTimeNanoseconds,
            string.IsNullOrWhiteSpace(generationIdentity)
                ? Array.Empty<string>()
                : [generationIdentity])[0];

    internal static IReadOnlyList<string> CreateLinuxObjectIdentityCandidatesFromEvidence(
        uint deviceMajor,
        uint deviceMinor,
        ulong inode,
        bool hasBirthTime,
        long birthTimeSeconds,
        uint birthTimeNanoseconds,
        IReadOnlyList<string> generationIdentities)
    {
        ArgumentNullException.ThrowIfNull(generationIdentities);
        var strongGenerations = generationIdentities
            .Where(IsDurableLinuxGenerationIdentity)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (strongGenerations.Length == 0)
        {
            // A birth-time + inode tuple can be reused by a rapid delete/recreate on
            // Linux filesystems (observed on overlayfs under parallel mutation). It is
            // useful compatibility evidence, but it is not sufficient destructive
            // authority by itself.
            throw new PlatformNotSupportedException(
                "The filesystem does not expose a durable file handle or inode generation for this object.");
        }

        var candidates = new List<string>(strongGenerations.Length * 2);
        foreach (var generationIdentity in strongGenerations)
        {
            // Persist a representation whose authority is the strong generation
            // primitive itself. This remains safe if birth-time support later appears
            // or disappears.
            candidates.Add(FormattableString.Invariant(
                $"linux-generation:{deviceMajor:x8}:{deviceMinor:x8}:{inode:x16}:{generationIdentity}"));
        }

        if (hasBirthTime)
        {
            // #717 could persist a birth-time spelling augmented with the same strong
            // generation evidence. Keep that exact representation as a compatibility
            // candidate, but never emit or accept the raw birth-time-only spelling as
            // destructive authority.
            var legacyIdentity = FormattableString.Invariant(
                $"linux:{deviceMajor:x8}:{deviceMinor:x8}:{inode:x16}:{birthTimeSeconds:x16}:{birthTimeNanoseconds:x8}");
            foreach (var generationIdentity in strongGenerations)
            {
                candidates.Add($"{legacyIdentity}:{generationIdentity}");
            }
        }

        return candidates.ToArray();
    }

    internal static bool ArePersistedObjectIdentitiesDurablyEquivalent(
        string left,
        string right)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(left);
        ArgumentException.ThrowIfNullOrWhiteSpace(right);
        if (PhysicalObjectIdentitySafety.IsKnownWeak(left)
            || PhysicalObjectIdentitySafety.IsKnownWeak(right))
        {
            return false;
        }
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return true;
        }
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        return TryGetLinuxStrongGenerationKey(left, out var leftKey)
            && TryGetLinuxStrongGenerationKey(right, out var rightKey)
            && string.Equals(leftKey, rightKey, StringComparison.Ordinal);
    }

    internal static bool TryGetLinuxBirthTimeIdentityPrefix(
        string identity,
        out string birthTimeIdentity)
    {
        birthTimeIdentity = string.Empty;
        if (!identity.StartsWith("linux:", StringComparison.Ordinal))
        {
            return false;
        }

        var parts = identity.Split(':');
        if (parts.Length < 8
            || !IsFixedHex(parts[1], 8)
            || !IsFixedHex(parts[2], 8)
            || !IsFixedHex(parts[3], 16)
            || !IsFixedHex(parts[4], 16)
            || !IsFixedHex(parts[5], 8))
        {
            return false;
        }

        var hasKnownMergedV1Suffix = parts[6] switch
        {
            "gen" => parts.Length == 8 && IsFixedHex(parts[7], 8),
            "fh" => parts.Length == 9
                && IsFixedHex(parts[7], 8)
                && parts[8].Length > 0
                && parts[8].Length % 2 == 0
                && parts[8].All(Uri.IsHexDigit),
            _ => false
        };
        if (!hasKnownMergedV1Suffix)
        {
            return false;
        }

        birthTimeIdentity = string.Join(':', parts.Take(6));
        return true;
    }

    private static bool TryGetLinuxStrongGenerationKey(
        string identity,
        out string key)
    {
        key = string.Empty;
        var parts = identity.Split(':');
        if (parts.Length >= 6
            && string.Equals(parts[0], "linux-generation", StringComparison.Ordinal)
            && IsFixedHex(parts[1], 8)
            && IsFixedHex(parts[2], 8)
            && IsFixedHex(parts[3], 16)
            && TryValidateLinuxGenerationSuffix(parts, 4))
        {
            key = string.Join(':', parts.Skip(1));
            return true;
        }

        if (parts.Length >= 8
            && string.Equals(parts[0], "linux", StringComparison.Ordinal)
            && IsFixedHex(parts[1], 8)
            && IsFixedHex(parts[2], 8)
            && IsFixedHex(parts[3], 16)
            && IsFixedHex(parts[4], 16)
            && IsFixedHex(parts[5], 8)
            && TryValidateLinuxGenerationSuffix(parts, 6))
        {
            key = string.Join(':', parts[1], parts[2], parts[3])
                + ":"
                + string.Join(':', parts.Skip(6));
            return true;
        }

        return false;
    }

    internal static IReadOnlyList<string> CreateLinuxLegacyWeakObjectIdentityCandidatesFromEvidence(
        uint deviceMajor,
        uint deviceMinor,
        ulong inode,
        bool hasBirthTime,
        long birthTimeSeconds,
        uint birthTimeNanoseconds,
        IReadOnlyList<string> generationIdentities)
    {
        ArgumentNullException.ThrowIfNull(generationIdentities);
        var weakGenerations = generationIdentities
            .Where(IsLegacyWeakLinuxGenerationIdentity)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (weakGenerations.Length == 0)
        {
            return Array.Empty<string>();
        }

        var candidates = new List<string>(weakGenerations.Length * 2);
        foreach (var generationIdentity in weakGenerations)
        {
            candidates.Add(FormattableString.Invariant(
                $"linux-generation:{deviceMajor:x8}:{deviceMinor:x8}:{inode:x16}:{generationIdentity}"));
        }

        if (hasBirthTime)
        {
            var legacyIdentity = FormattableString.Invariant(
                $"linux:{deviceMajor:x8}:{deviceMinor:x8}:{inode:x16}:{birthTimeSeconds:x16}:{birthTimeNanoseconds:x8}");
            foreach (var generationIdentity in weakGenerations)
            {
                candidates.Add($"{legacyIdentity}:{generationIdentity}");
            }
        }

        return candidates.ToArray();
    }

    private static bool IsDurableLinuxGenerationIdentity(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        var parts = candidate.Split(':');
        return parts[0] switch
        {
            "gen" => parts.Length == 2 && IsFixedHex(parts[1], 8),
            "fh" => TryValidateLinuxFileHandle(parts, 0, requireDurable: true),
            _ => false
        };
    }

    private static bool IsLegacyWeakLinuxGenerationIdentity(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        var parts = candidate.Split(':');
        return parts.Length == 3
            && string.Equals(parts[0], "fh", StringComparison.Ordinal)
            && TryValidateLinuxFileHandle(parts, 0, requireDurable: false)
            && string.Equals(parts[1], "00000081", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryValidateLinuxGenerationSuffix(
        string[] parts,
        int suffixIndex) =>
        parts[suffixIndex] switch
        {
            "gen" => parts.Length == suffixIndex + 2
                && IsFixedHex(parts[suffixIndex + 1], 8),
            "fh" => TryValidateLinuxFileHandle(parts, suffixIndex, requireDurable: true),
            _ => false
        };

    private static bool TryValidateLinuxFileHandle(
        string[] parts,
        int prefixIndex,
        bool requireDurable)
    {
        if (parts.Length != prefixIndex + 3
            || !string.Equals(parts[prefixIndex], "fh", StringComparison.Ordinal)
            || !IsFixedHex(parts[prefixIndex + 1], 8)
            || parts[prefixIndex + 2].Length == 0
            || parts[prefixIndex + 2].Length % 2 != 0
            || !parts[prefixIndex + 2].All(Uri.IsHexDigit))
        {
            return false;
        }

        return !requireDurable
            || !string.Equals(
                parts[prefixIndex + 1],
                "00000081",
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFixedHex(string value, int length) =>
        value.Length == length && value.All(Uri.IsHexDigit);
}
