using System.ComponentModel;
using System.Security.Cryptography;
using Listenarr.Domain.Audiobooks.Enumerations;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private static RegistrationPublicationMatchOutcome ProbeMarkerlessJournalTarget(
        FileMutationJournal journal,
        string targetPhysicalObjectIdentity)
    {
        try
        {
            var destination = Path.GetFullPath(journal.DestinationPath);
            var parent = Path.GetDirectoryName(destination);
            var fileName = Path.GetFileName(destination);
            if (string.IsNullOrWhiteSpace(parent)
                || string.IsNullOrWhiteSpace(fileName))
            {
                return RegistrationPublicationMatchOutcome.Mismatch;
            }

            using var anchor = PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                parent,
                createMissing: false);
            if (journal.ProtocolVersion != FileMutationProtocol.Current
                || string.IsNullOrWhiteSpace(
                    journal.DestinationParentDirectoryObjectIdentity)
                || !anchor.MatchesDirectoryObjectIdentity(
                    journal.DestinationParentDirectoryObjectIdentity))
            {
                return RegistrationPublicationMatchOutcome.Mismatch;
            }

            var parentVisibility = anchor.ProbeVisiblePathMatch();
            if (parentVisibility != RegistrationPublicationMatchOutcome.Match)
            {
                return parentVisibility;
            }

            var openOutcome = anchor.TryOpenExistingFileWithOutcome(
                fileName,
                requireDeleteAccess: false,
                out var entry);
            using (entry)
            {
                if (openOutcome == PinnedFileOpenOutcome.Unavailable)
                {
                    return RegistrationPublicationMatchOutcome.Unavailable;
                }
                if (openOutcome != PinnedFileOpenOutcome.Opened
                    || entry == null
                    || !entry.MatchesObjectIdentity(targetPhysicalObjectIdentity))
                {
                    return RegistrationPublicationMatchOutcome.Mismatch;
                }

                var visibility = entry.ProbeVisiblePathMatch();
                if (visibility != RegistrationPublicationMatchOutcome.Match)
                {
                    return visibility;
                }

                using var stream = entry.OpenReadStream(
                    bufferSize: 128 * 1024,
                    asynchronous: false);
                if (stream.Length != journal.SourceLength)
                {
                    return RegistrationPublicationMatchOutcome.Mismatch;
                }
                if (string.IsNullOrWhiteSpace(journal.SourceSha256))
                {
                    return RegistrationPublicationMatchOutcome.Match;
                }

                stream.Position = 0;
                var hash = Convert.ToHexString(SHA256.HashData(stream));
                return string.Equals(
                    hash,
                    journal.SourceSha256,
                    StringComparison.Ordinal)
                    ? RegistrationPublicationMatchOutcome.Match
                    : RegistrationPublicationMatchOutcome.Mismatch;
            }
        }
        catch (FileNotFoundException)
        {
            return RegistrationPublicationMatchOutcome.Mismatch;
        }
        catch (DirectoryNotFoundException)
        {
            return RegistrationPublicationMatchOutcome.Mismatch;
        }
        catch (Win32Exception exception) when (
            exception.NativeErrorCode is 2 or 3)
        {
            return RegistrationPublicationMatchOutcome.Mismatch;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or Win32Exception
                or PlatformNotSupportedException)
        {
            return RegistrationPublicationMatchOutcome.Unavailable;
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException
                or NotSupportedException or PathTooLongException
                or System.Security.SecurityException)
        {
            return RegistrationPublicationMatchOutcome.Mismatch;
        }
    }

    private async Task ValidateMarkerlessRegistrationJournalAsync(
        FileMutationJournal journal,
        FileAction action,
        FileMoveGateLease gate)
    {
        if (journal.ProtocolVersion != FileMutationProtocol.Current
            || journal.Action != action
            || !await JournalPathsMatchGateAsync(journal, gate))
        {
            throw new InvalidOperationException(
                "The durable registration identity does not match the requested operation.");
        }
    }
}
