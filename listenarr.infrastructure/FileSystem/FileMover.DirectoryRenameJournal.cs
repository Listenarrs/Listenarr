using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private const string DirectoryRenameJournalPrefix =
        ".listenarr-directory-rename-";

    private sealed record DirectoryRenameJournalPayload(
        int Version,
        Guid OperationId,
        string SourcePath,
        string DestinationPath,
        string SourceObjectIdentity,
        string SourceParentIdentity,
        string DestinationParentIdentity);

    private sealed record DirectoryRenameJournalEnvelope(
        string PayloadBase64,
        string Sha256);

    private PinnedDirectoryCreation.PinnedFileEntry PublishDirectoryRenameJournal(
        PinnedDirectoryCreation.PinnedDirectoryAnchor sourceParent,
        PinnedDirectoryCreation.PinnedDirectoryAnchor destinationParent,
        string source,
        string destination,
        string sourceObjectIdentity)
    {
        var payload = new DirectoryRenameJournalPayload(
            Version: 1,
            Guid.NewGuid(),
            Path.GetFullPath(source),
            Path.GetFullPath(destination),
            sourceObjectIdentity,
            sourceParent.GetDirectoryObjectIdentity(),
            destinationParent.GetDirectoryObjectIdentity());
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var envelope = new DirectoryRenameJournalEnvelope(
            Convert.ToBase64String(payloadBytes),
            Convert.ToHexString(SHA256.HashData(payloadBytes)));
        var journalBytes = JsonSerializer.SerializeToUtf8Bytes(envelope);
        var journalName =
            $"{GetDirectoryRenameJournalStem(source, destination)}-{payload.OperationId:N}.journal";
        PinnedDirectoryCreation.PinnedFileEntry? journal = null;
        try
        {
            journal = sourceParent.CreateNewFile(
                journalName,
                hiddenFile: true);
            using (var stream = journal.OpenWriteStream(
                4096,
                asynchronous: false))
            {
                stream.Write(journalBytes);
                stream.Flush(flushToDisk: true);
            }

            journal.FlushToDisk();
            sourceParent.FlushDirectoryEntry();
            return journal;
        }
        catch
        {
            if (journal != null)
            {
                try
                {
                    journal.Delete(immediateWindows: true);
                    sourceParent.FlushDirectoryEntry();
                }
                catch
                {
                    // Preserve any complete journal when cleanup is uncertain.
                }
                finally
                {
                    journal.Dispose();
                }
            }

            throw;
        }
    }

    private static void TryRetireDirectoryRenameJournal(
        string sourceDirectory,
        PinnedDirectoryCreation.PinnedFileEntry journal)
    {
        try
        {
            var sourceParentPath = Path.GetDirectoryName(
                Path.GetFullPath(sourceDirectory));
            if (string.IsNullOrWhiteSpace(sourceParentPath))
            {
                return;
            }

            using var sourceParent =
                PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                    sourceParentPath,
                    createMissing: false);
            using var current = sourceParent.OpenExistingFile(
                journal.FileName,
                requireDeleteAccess: true);
            if (!current.IdentifiesSameEntry(journal)
                || !current.VisiblePathMatches())
            {
                return;
            }

            current.Delete(immediateWindows: true);
            sourceParent.FlushDirectoryEntry();
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            // The durable journal remains available for a later recovery pass.
        }
    }

    private PinnedDirectoryMoveOutcome? TryRecoverPinnedDirectoryRename(
        string sourceDirectory,
        string destinationDirectory)
    {
        var source = Path.GetFullPath(sourceDirectory);
        var destination = Path.GetFullPath(destinationDirectory);
        var sourceParentPath = Path.GetDirectoryName(source);
        if (string.IsNullOrWhiteSpace(sourceParentPath)
            || !Directory.Exists(sourceParentPath))
        {
            return null;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        using var sourceParent =
            PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                sourceParentPath,
                createMissing: false);
        var matches = new List<(string Name, DirectoryRenameJournalPayload Payload)>();
        var journalPattern =
            $"{GetDirectoryRenameJournalStem(source, destination)}-*.journal";
        foreach (var path in Directory.EnumerateFiles(
            sourceParent.FullPath,
            journalPattern,
            SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            using var journal = sourceParent.OpenExistingFile(
                name,
                requireDeleteAccess: false);
            var payload = ReadDirectoryRenameJournal(journal);
            if (payload == null
                || !FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    payload.SourcePath,
                    out var payloadSource,
                    out _)
                || !FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    payload.DestinationPath,
                    out var payloadDestination,
                    out _)
                || !string.Equals(
                    payloadSource,
                    source,
                    comparison)
                || !string.Equals(
                    payloadDestination,
                    destination,
                    comparison))
            {
                return PinnedDirectoryMoveOutcome.Indeterminate;
            }

            matches.Add((name, payload with
            {
                SourcePath = payloadSource,
                DestinationPath = payloadDestination
            }));
        }

        if (matches.Count == 0)
        {
            return null;
        }
        if (matches.Count != 1)
        {
            return PinnedDirectoryMoveOutcome.Indeterminate;
        }

        var match = matches[0];
        if (!string.Equals(
                sourceParent.GetDirectoryObjectIdentity(),
                match.Payload.SourceParentIdentity,
                StringComparison.Ordinal))
        {
            return PinnedDirectoryMoveOutcome.Indeterminate;
        }

        var destinationParentPath = Path.GetDirectoryName(destination);
        if (string.IsNullOrWhiteSpace(destinationParentPath)
            || !Directory.Exists(destinationParentPath))
        {
            return PinnedDirectoryMoveOutcome.Indeterminate;
        }
        using var destinationParent =
            PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                destinationParentPath,
                createMissing: false);
        if (!string.Equals(
                destinationParent.GetDirectoryObjectIdentity(),
                match.Payload.DestinationParentIdentity,
                StringComparison.Ordinal))
        {
            return PinnedDirectoryMoveOutcome.Indeterminate;
        }

        var sourceIdentity = TryGetPinnedDirectoryIdentity(source);
        var destinationIdentity = TryGetPinnedDirectoryIdentity(destination);
        var sourceOwnsGeneration = string.Equals(
            sourceIdentity,
            match.Payload.SourceObjectIdentity,
            StringComparison.Ordinal);
        var destinationOwnsGeneration = string.Equals(
            destinationIdentity,
            match.Payload.SourceObjectIdentity,
            StringComparison.Ordinal);
        var outcome = (sourceOwnsGeneration, destinationOwnsGeneration) switch
        {
            (true, false) => PinnedDirectoryMoveOutcome.NotMoved,
            (false, true) => PinnedDirectoryMoveOutcome.Moved,
            _ => PinnedDirectoryMoveOutcome.Indeterminate
        };
        if (outcome == PinnedDirectoryMoveOutcome.Indeterminate)
        {
            return outcome;
        }

        using var journalForDelete = sourceParent.OpenExistingFile(
            match.Name,
            requireDeleteAccess: true);
        var revalidated = ReadDirectoryRenameJournal(journalForDelete);
        if (revalidated == null
            || revalidated.OperationId != match.Payload.OperationId
            || !journalForDelete.VisiblePathMatches())
        {
            return PinnedDirectoryMoveOutcome.Indeterminate;
        }

        journalForDelete.Delete(immediateWindows: true);
        sourceParent.FlushDirectoryEntry();
        return outcome;
    }

    private static string GetDirectoryRenameJournalStem(
        string source,
        string destination)
    {
        var normalizedSource = Path.GetFullPath(source);
        var normalizedDestination = Path.GetFullPath(destination);
        if (OperatingSystem.IsWindows())
        {
            normalizedSource = normalizedSource.ToUpperInvariant();
            normalizedDestination = normalizedDestination.ToUpperInvariant();
        }

        var keyBytes = Encoding.UTF8.GetBytes(
            normalizedSource + "\0" + normalizedDestination);
        var key = Convert.ToHexString(SHA256.HashData(keyBytes))[..24];
        return DirectoryRenameJournalPrefix + key;
    }

    private static DirectoryRenameJournalPayload? ReadDirectoryRenameJournal(
        PinnedDirectoryCreation.PinnedFileEntry journal)
    {
        try
        {
            using var stream = journal.OpenReadStream(
                4096,
                asynchronous: false);
            if (stream.Length is <= 0 or > 64 * 1024)
            {
                return null;
            }

            var bytes = new byte[stream.Length];
            var offset = 0;
            while (offset < bytes.Length)
            {
                var read = stream.Read(bytes, offset, bytes.Length - offset);
                if (read == 0)
                {
                    return null;
                }

                offset += read;
            }

            var envelope = JsonSerializer.Deserialize<DirectoryRenameJournalEnvelope>(
                bytes);
            if (envelope == null)
            {
                return null;
            }

            var payloadBytes = Convert.FromBase64String(envelope.PayloadBase64);
            if (!string.Equals(
                    Convert.ToHexString(SHA256.HashData(payloadBytes)),
                    envelope.Sha256,
                    StringComparison.Ordinal))
            {
                return null;
            }

            var payload = JsonSerializer.Deserialize<DirectoryRenameJournalPayload>(
                payloadBytes);
            return payload is { Version: 1 }
                && payload.OperationId != Guid.Empty
                ? payload
                : null;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException
                or JsonException or FormatException
                or InvalidOperationException)
        {
            return null;
        }
    }
}
