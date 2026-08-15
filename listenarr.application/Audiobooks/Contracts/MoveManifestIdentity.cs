using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Listenarr.Domain.Common;

namespace Listenarr.Application.Audiobooks.Contracts;

public static partial class MoveManifestIdentity
{
    public const int Version = MoveIdentityProtocol.Current;
    private const string SourceBoundaryAuthorizationDomain =
        "LISTENARR-MOVE-SOURCE-BOUNDARY";
    private const string TargetBoundaryAuthorizationDomain =
        "LISTENARR-MOVE-TARGET-BOUNDARY";

    public static string CreateDeduplicationKey(
        int audiobookId,
        string source,
        PathIdentitySnapshot sourceIdentity,
        string target,
        PathIdentitySnapshot targetIdentity,
        IEnumerable<MoveJobEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var persistedEntries = entries.ToList();
        if (!TryGetSourceBoundaryAuthorization(
                persistedEntries,
                out _,
                out _,
                out _)
            || !TryGetTargetBoundaryAuthorization(
                persistedEntries,
                out _,
                out _,
                out _))
        {
            throw new InvalidOperationException(
                "A durable move identity requires source- and target-boundary physical-generation authorization.");
        }

        return CreateDeduplicationKeyCore(
            audiobookId,
            source,
            sourceIdentity,
            target,
            targetIdentity,
            persistedEntries.Select(ToIdentityEntry));
    }

    public static string CreateDeduplicationKey(
        int audiobookId,
        string source,
        PathIdentitySnapshot sourceIdentity,
        string target,
        PathIdentitySnapshot targetIdentity,
        IEnumerable<MoveSourceManifestEntry> entries) =>
        CreateDeduplicationKeyCore(
            audiobookId,
            source,
            sourceIdentity,
            target,
            targetIdentity,
            entries.Select(ToIdentityEntry));

    public static string CreateReconciliationKey(
        int audiobookId,
        string source,
        PathIdentitySnapshot sourceIdentity,
        string target,
        PathIdentitySnapshot targetIdentity,
        IEnumerable<MoveJobEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return CreateDeduplicationKeyCore(
            audiobookId,
            source,
            sourceIdentity,
            target,
            targetIdentity,
            entries.Select(ToIdentityEntry));
    }

    public static bool SourceManifestsMatch(
        IEnumerable<MoveSourceManifestEntry> currentEntries,
        IEnumerable<MoveJobEntry> persistedEntries,
        FileSystemPathSemantics semantics)
    {
        ArgumentNullException.ThrowIfNull(currentEntries);
        ArgumentNullException.ThrowIfNull(persistedEntries);
        return string.Equals(
            ComputeManifestDigest(
                currentEntries.Select(ToIdentityEntry),
                semantics),
            ComputeManifestDigest(
                persistedEntries
                    .Where(entry => !IsBoundaryAuthorization(entry))
                    .Select(ToIdentityEntry),
                semantics),
            StringComparison.Ordinal);
    }

    public static bool SourceManifestShapesMatch(
        IEnumerable<MoveSourceManifestEntry> currentEntries,
        IEnumerable<MoveJobEntry> persistedEntries,
        FileSystemPathSemantics semantics)
    {
        ArgumentNullException.ThrowIfNull(currentEntries);
        ArgumentNullException.ThrowIfNull(persistedEntries);
        return string.Equals(
            ComputeManifestDigest(
                currentEntries
                    .Select(ToIdentityEntry)
                    .Select(entry => entry with { Sha256 = null }),
                semantics),
            ComputeManifestDigest(
                persistedEntries
                    .Where(entry => !IsBoundaryAuthorization(entry))
                    .Select(ToIdentityEntry)
                    .Select(entry => entry with { Sha256 = null }),
                semantics),
            StringComparison.Ordinal);
    }

    public static MoveJobEntry CreateSourceBoundaryAuthorization(
        int directoryIdentityVersion,
        string directoryIdentity)
    {
        if (directoryIdentityVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(directoryIdentityVersion));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryIdentity);
        return new MoveJobEntry
        {
            RelativePath = ".",
            EntryType = MoveJobEntryType.Directory,
            Length = directoryIdentityVersion,
            LastWriteTimeUtc = DateTime.UnixEpoch,
            Sha256 = ComputeSourceBoundaryAuthorizationDigest(
                directoryIdentityVersion,
                directoryIdentity),
            SourcePhysicalObjectIdentity = directoryIdentity,
            CopyState = MoveJobEntryCopyState.Pending,
            CleanupState = MoveJobEntryCleanupState.Pending
        };
    }

    public static MoveJobEntry CreateTargetBoundaryAuthorization(
        int directoryIdentityVersion,
        string directoryIdentity)
    {
        if (directoryIdentityVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(directoryIdentityVersion));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryIdentity);
        return new MoveJobEntry
        {
            RelativePath = string.Empty,
            EntryType = MoveJobEntryType.Directory,
            Length = directoryIdentityVersion,
            LastWriteTimeUtc = DateTime.UnixEpoch,
            Sha256 = ComputeTargetBoundaryAuthorizationDigest(
                directoryIdentityVersion,
                directoryIdentity),
            TargetPhysicalObjectIdentity = directoryIdentity,
            CopyState = MoveJobEntryCopyState.Pending,
            CleanupState = MoveJobEntryCleanupState.Pending
        };
    }

    public static bool IsSourceBoundaryAuthorization(MoveJobEntry entry) =>
        entry.EntryType == MoveJobEntryType.Directory
        && string.Equals(entry.RelativePath, ".", StringComparison.Ordinal);

    public static bool IsTargetBoundaryAuthorization(MoveJobEntry entry) =>
        entry.EntryType == MoveJobEntryType.Directory
        && string.IsNullOrEmpty(entry.RelativePath);

    public static bool TryGetSourceBoundaryAuthorization(
        IEnumerable<MoveJobEntry> entries,
        out int directoryIdentityVersion,
        out string directoryIdentity,
        out string digest)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var matches = entries
            .Where(IsSourceBoundaryAuthorization)
            .Take(2)
            .ToList();
        if (matches.Count != 1
            || matches[0].Length <= 0
            || matches[0].Length > int.MaxValue
            || string.IsNullOrWhiteSpace(
                matches[0].SourcePhysicalObjectIdentity)
            || matches[0].Sha256 is not { Length: 64 } sourceDigest
            || !sourceDigest.All(Uri.IsHexDigit))
        {
            directoryIdentityVersion = 0;
            directoryIdentity = string.Empty;
            digest = string.Empty;
            return false;
        }

        directoryIdentityVersion = (int)matches[0].Length;
        directoryIdentity = matches[0].SourcePhysicalObjectIdentity!;
        digest = matches[0].Sha256!.ToUpperInvariant();
        if (!string.Equals(
                digest,
                ComputeSourceBoundaryAuthorizationDigest(
                    directoryIdentityVersion,
                    directoryIdentity),
                StringComparison.Ordinal))
        {
            directoryIdentityVersion = 0;
            directoryIdentity = string.Empty;
            digest = string.Empty;
            return false;
        }
        return true;
    }

    public static bool TryGetTargetBoundaryAuthorization(
        IEnumerable<MoveJobEntry> entries,
        out int directoryIdentityVersion,
        out string directoryIdentity,
        out string digest)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var matches = entries
            .Where(IsTargetBoundaryAuthorization)
            .Take(2)
            .ToList();
        if (matches.Count != 1
            || matches[0].Length <= 0
            || matches[0].Length > int.MaxValue
            || string.IsNullOrWhiteSpace(
                matches[0].TargetPhysicalObjectIdentity)
            || matches[0].Sha256 is not { Length: 64 } targetDigest
            || !targetDigest.All(Uri.IsHexDigit))
        {
            directoryIdentityVersion = 0;
            directoryIdentity = string.Empty;
            digest = string.Empty;
            return false;
        }

        directoryIdentityVersion = (int)matches[0].Length;
        directoryIdentity = matches[0].TargetPhysicalObjectIdentity!;
        digest = matches[0].Sha256!.ToUpperInvariant();
        if (!string.Equals(
                digest,
                ComputeTargetBoundaryAuthorizationDigest(
                    directoryIdentityVersion,
                    directoryIdentity),
                StringComparison.Ordinal))
        {
            directoryIdentityVersion = 0;
            directoryIdentity = string.Empty;
            digest = string.Empty;
            return false;
        }
        return true;
    }

    public static string ComputeTargetBoundaryAuthorizationDigest(
        int directoryIdentityVersion,
        string directoryIdentity) =>
        ComputeBoundaryAuthorizationDigest(
            TargetBoundaryAuthorizationDomain,
            directoryIdentityVersion,
            directoryIdentity);

    private static string ComputeBoundaryAuthorizationDigest(
        string domain,
        int directoryIdentityVersion,
        string directoryIdentity)
    {
        if (directoryIdentityVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(directoryIdentityVersion));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryIdentity);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendUtf8(hash, domain);
        AppendInt32(hash, directoryIdentityVersion);
        AppendUtf8(hash, directoryIdentity);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string CreateDeduplicationKeyCore(
        int audiobookId,
        string source,
        PathIdentitySnapshot sourceIdentity,
        string target,
        PathIdentitySnapshot targetIdentity,
        IEnumerable<ManifestIdentityEntry> entries)
    {
        var sourceKey = FileSystemPathIdentity.CreateKey(
            $"move-source:{audiobookId}",
            source,
            sourceIdentity.Semantics,
            Version);
        var targetKey = FileSystemPathIdentity.CreateKey(
            $"move-target:{audiobookId}",
            target,
            targetIdentity.Semantics,
            Version);
        var manifestDigest = ComputeManifestDigest(
            entries,
            sourceIdentity.Semantics);
        return $"{sourceKey}:{targetKey}:{manifestDigest}";
    }

    private static string ComputeManifestDigest(
        IEnumerable<ManifestIdentityEntry> entries,
        FileSystemPathSemantics semantics)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var normalizedEntries = entries
            .Select(entry => NormalizeEntry(entry, semantics))
            .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
            .ThenBy(entry => entry.EntryType)
            .ThenBy(entry => entry.Length)
            .ThenBy(entry => entry.LastWriteTimeUtc.Ticks)
            .ThenBy(entry => entry.Sha256, StringComparer.Ordinal)
            .ToList();

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("LISTENARR-MOVE-MANIFEST"u8);
        AppendInt32(hash, Version);
        AppendInt32(hash, normalizedEntries.Count);
        foreach (var entry in normalizedEntries)
        {
            AppendUtf8(hash, entry.RelativePath);
            AppendInt32(hash, (int)entry.EntryType);
            AppendInt64(hash, entry.Length);
            AppendInt64(hash, entry.LastWriteTimeUtc.Ticks);
            AppendHash(hash, entry.Sha256);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AppendUtf8(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        AppendInt32(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void AppendHash(IncrementalHash hash, string? sha256)
    {
        Span<byte> presence = stackalloc byte[1];
        if (sha256 == null)
        {
            presence[0] = 0;
            hash.AppendData(presence);
            return;
        }

        if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException(
                "A move manifest identity contains invalid SHA-256 evidence.");
        }

        presence[0] = 1;
        hash.AppendData(presence);
        var bytes = Convert.FromHexString(sha256);
        AppendInt32(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void AppendInt32(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendInt64(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static ManifestIdentityEntry NormalizeEntry(
        ManifestIdentityEntry entry,
        FileSystemPathSemantics semantics)
    {
        var relativePath = NormalizeRelativePath(
            entry.RelativePath,
            semantics);
        if (entry.EntryType == MoveJobEntryType.Directory)
        {
            if ((string.IsNullOrEmpty(entry.RelativePath)
                    || string.Equals(entry.RelativePath, ".", StringComparison.Ordinal))
                && entry.Length > 0
                && entry.Sha256 is { Length: 64 } authorizationDigest
                && authorizationDigest.All(Uri.IsHexDigit))
            {
                return new ManifestIdentityEntry(
                    relativePath,
                    entry.EntryType,
                    entry.Length,
                    DateTime.UnixEpoch,
                    authorizationDigest.ToUpperInvariant());
            }

            // Directory timestamps are not ownership evidence and may change when
            // unrelated content appears in a shared source tree.
            return new ManifestIdentityEntry(
                relativePath,
                entry.EntryType,
                0,
                DateTime.UnixEpoch,
                null);
        }

        return new ManifestIdentityEntry(
            relativePath,
            entry.EntryType,
            entry.Length,
            entry.LastWriteTimeUtc,
            entry.Sha256?.ToUpperInvariant());
    }

    private static string NormalizeRelativePath(
        string relativePath,
        FileSystemPathSemantics semantics)
    {
        var normalized = relativePath.Normalize(NormalizationForm.FormC);
        if (semantics.Syntax == FileSystemPathSyntax.Windows)
        {
            normalized = normalized.Replace('/', '\\');
        }

        return semantics.CaseSensitivity == FileSystemCaseSensitivity.Insensitive
            ? normalized.ToUpperInvariant()
            : normalized;
    }

    private static ManifestIdentityEntry ToIdentityEntry(
        MoveJobEntry entry) =>
        new(
            entry.RelativePath,
            entry.EntryType,
            entry.Length,
            entry.LastWriteTimeUtc,
            entry.Sha256);

    private static ManifestIdentityEntry ToIdentityEntry(
        MoveSourceManifestEntry entry) =>
        new(
            entry.RelativePath,
            entry.EntryType,
            entry.Length,
            entry.LastWriteTimeUtc,
            entry.Sha256);

    private sealed record ManifestIdentityEntry(
        string RelativePath,
        MoveJobEntryType EntryType,
        long Length,
        DateTime LastWriteTimeUtc,
        string? Sha256);
}
