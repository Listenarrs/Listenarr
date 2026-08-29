using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Listenarr.Infrastructure.FileSystem;

internal sealed partial class PinnedDirectoryCreation
{
    internal readonly record struct PinnedRenameAttempt(
        bool Published,
        int NativeErrorCode);

    internal sealed partial class PinnedFileEntry
    {
        internal PinnedFileEntry CreateHardLinkTo(
            PinnedDirectoryAnchor destinationParent,
            string destinationName,
            Action? afterLinkCreatedForTest = null)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(destinationParent);
            ValidateLeafName(destinationName);
            var sourceVisibility = ProbeVisiblePathMatch();
            var destinationVisibility = destinationParent.ProbeVisiblePathMatch();
            if (sourceVisibility == RegistrationPublicationMatchOutcome.Unavailable
                || destinationVisibility == RegistrationPublicationMatchOutcome.Unavailable)
            {
                throw new IOException(
                    "A pinned hardlink endpoint is temporarily unavailable before link creation.");
            }
            if (sourceVisibility != RegistrationPublicationMatchOutcome.Match
                || destinationVisibility != RegistrationPublicationMatchOutcome.Match)
            {
                throw new InvalidOperationException(
                    "A pinned hardlink endpoint changed before link creation.");
            }

            using var destinationHandle =
                destinationParent.DuplicateHandleForOperation();
            if (OperatingSystem.IsWindows())
            {
                CreateRelativeHardLinkWindows(
                    _fileHandle,
                    destinationHandle,
                    destinationName);
            }
            else if (LinkAt(
                    _parentHandle.DangerousGetHandle().ToInt32(),
                    _fileName,
                    destinationHandle.DangerousGetHandle().ToInt32(),
                    destinationName,
                    flags: 0) != 0)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not create a hardlink between pinned filesystem endpoints.");
            }

            afterLinkCreatedForTest?.Invoke();

            PinnedFileEntry? linked = null;
            try
            {
                linked = destinationParent.OpenExistingFile(
                    destinationName,
                    requireDeleteAccess: true);
                var linkedVisibility = linked.ProbeVisiblePathMatch();
                if (linkedVisibility == RegistrationPublicationMatchOutcome.Unavailable)
                {
                    throw new IOException(
                        "The created hardlink is temporarily unavailable before publication can be verified.");
                }
                if (linkedVisibility != RegistrationPublicationMatchOutcome.Match
                    || !IdentifiesSameEntry(linked))
                {
                    throw new InvalidOperationException(
                        "The created hardlink does not identify the pinned source generation.");
                }

                return linked;
            }
            catch
            {
                if (linked != null
                    && linked.VisiblePathMatches()
                    && IdentifiesSameEntry(linked))
                {
                    linked.Delete(immediateWindows: true);
                }
                linked?.Dispose();
                throw;
            }
        }

        internal async Task<bool> MatchesAsync(
            long expectedLength,
            string? expectedSha256,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(expectedSha256))
            {
                return false;
            }

            await using var stream = OpenReadStream(
                bufferSize: 128 * 1024,
                asynchronous: false);
            if (stream.Length != expectedLength)
            {
                return false;
            }

            stream.Position = 0;
            var hash = await SHA256.HashDataAsync(stream, cancellationToken);
            return string.Equals(
                Convert.ToHexString(hash),
                expectedSha256,
                StringComparison.Ordinal);
        }

        internal void MoveTo(
            PinnedDirectoryAnchor destinationParent,
            string destinationName)
        {
            var attempt = TryMoveToNoReplace(destinationParent, destinationName);
            if (!attempt.Published)
            {
                throw new Win32Exception(
                    attempt.NativeErrorCode,
                    "Could not publish a pinned filesystem entry relative to its owned directory.");
            }
        }

        internal PinnedRenameAttempt TryMoveToNoReplace(
            PinnedDirectoryAnchor destinationParent,
            string destinationName)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(destinationParent);
            ValidateLeafName(destinationName);
            var sourceVisibility = ProbeVisiblePathMatch();
            if (sourceVisibility == RegistrationPublicationMatchOutcome.Unavailable)
            {
                throw new IOException(
                    "The source file is temporarily unavailable before its pinned rename.");
            }
            if (sourceVisibility != RegistrationPublicationMatchOutcome.Match)
            {
                throw new InvalidOperationException(
                    "The source file changed before its pinned rename.");
            }
            var destinationVisibility = destinationParent.ProbeVisiblePathMatch();
            if (destinationVisibility == RegistrationPublicationMatchOutcome.Unavailable)
            {
                throw new IOException(
                    "The destination directory is temporarily unavailable before its pinned rename.");
            }
            if (destinationVisibility != RegistrationPublicationMatchOutcome.Match)
            {
                throw new InvalidOperationException(
                    "The destination directory changed before its pinned rename.");
            }

            using var destinationHandle = destinationParent.DuplicateHandleForOperation();
            if (OperatingSystem.IsLinux())
            {
                var nativeError = TryRenameRelativeEntryNoReplaceLinux(
                    _parentHandle,
                    _fileName,
                    destinationHandle,
                    destinationName);
                if (nativeError != 0)
                {
                    return new PinnedRenameAttempt(false, nativeError);
                }
            }
            else
            {
                RenameRelativeEntry(
                    _parentHandle,
                    _fileHandle,
                    _fileName,
                    destinationHandle,
                    destinationName);
            }

            using var published = OperatingSystem.IsWindows()
                ? OpenRelativeFileWindows(
                    destinationHandle,
                    destinationName,
                    Path.Join(destinationParent.FullPath, destinationName),
                    requireDeleteAccess: false)
                : OpenRelativeFileUnix(
                    destinationHandle,
                    destinationName,
                    Path.Join(destinationParent.FullPath, destinationName));
            if (!HandlesIdentifySameDirectory(_fileHandle, published))
            {
                throw new InvalidOperationException(
                    "The published quarantine file does not identify the opened source file.");
            }

            var newParentHandle = DuplicateSafeHandle(destinationHandle);
            _parentHandle.Dispose();
            _parentHandle = newParentHandle;
            _parentPath = destinationParent.FullPath;
            _fileName = destinationName;
            _parentFollowsVisibleFinalLink =
                destinationParent.FollowsVisibleFinalLink;
            return new PinnedRenameAttempt(true, 0);
        }

        internal void MoveWithinParent(string destinationName)
        {
            ThrowIfDisposed();
            ValidateLeafName(destinationName);
            var sourceVisibility = ProbeVisiblePathMatch();
            if (sourceVisibility == RegistrationPublicationMatchOutcome.Unavailable)
            {
                throw new IOException(
                    "The source file is temporarily unavailable before its pinned publication.");
            }
            if (sourceVisibility != RegistrationPublicationMatchOutcome.Match)
            {
                throw new InvalidOperationException(
                    "The source file changed before its pinned publication.");
            }

            RenameRelativeEntry(
                _parentHandle,
                _fileHandle,
                _fileName,
                _parentHandle,
                destinationName);
            using var published = OperatingSystem.IsWindows()
                ? OpenRelativeFileWindows(
                    _parentHandle,
                    destinationName,
                    Path.Join(_parentPath, destinationName),
                    requireDeleteAccess: false)
                : OpenRelativeFileUnix(
                    _parentHandle,
                    destinationName,
                    Path.Join(_parentPath, destinationName));
            if (!HandlesIdentifySameDirectory(_fileHandle, published))
            {
                throw new InvalidOperationException(
                    "The published file does not identify the opened partial file.");
            }

            _fileName = destinationName;
        }
    }
}
