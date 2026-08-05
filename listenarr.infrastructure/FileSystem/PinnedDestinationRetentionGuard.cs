using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;

namespace Listenarr.Infrastructure.FileSystem;

internal sealed class PinnedDestinationRetentionGuard : IDisposable
{
    private readonly PinnedDirectoryCreation.PinnedDirectoryAnchor _parent;
    private readonly PinnedDirectoryCreation.PinnedFileEntry _retention;
    private readonly PinnedDirectoryCreation.PinnedFileEntry? _publicTarget;
    private readonly string _publicName;
    private readonly string _retentionName;
    private readonly long _expectedLength;
    private readonly string _expectedSha256;
    private bool _linearized;
    private bool _completed;

    internal Action? AfterWindowsPublicTargetReleasedForTest { get; set; }

    private PinnedDestinationRetentionGuard(
        PinnedDirectoryCreation.PinnedDirectoryAnchor parent,
        PinnedDirectoryCreation.PinnedFileEntry retention,
        PinnedDirectoryCreation.PinnedFileEntry? publicTarget,
        string publicName,
        string retentionName,
        long expectedLength,
        string expectedSha256)
    {
        _parent = parent;
        _retention = retention;
        _publicTarget = publicTarget;
        _publicName = publicName;
        _retentionName = retentionName;
        _expectedLength = expectedLength;
        _expectedSha256 = expectedSha256;
    }

    internal static string CreateRetentionName(
        Guid operationId,
        string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var pathHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(relativePath)))[..20];
        return $".listenarr-destination-retention-{operationId:N}-{pathHash}.bin";
    }

    internal static string CreateSourceRecoveryName(
        Guid operationId,
        string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var pathHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(relativePath)))[..20];
        return $".listenarr-source-recovery-{operationId:N}-{pathHash}.bin";
    }

    internal static Task<PinnedDestinationRetentionGuard?> OpenOrCreateAsync(
        PinnedDirectoryCreation.PinnedDirectoryAnchor destinationParent,
        string publicName,
        string retentionName,
        long expectedLength,
        string expectedSha256,
        CancellationToken cancellationToken) =>
        OpenAsync(
            destinationParent,
            publicName,
            retentionName,
            expectedLength,
            expectedSha256,
            createIfMissing: true,
            repairInterruptedOwnedCopy: false,
            cancellationToken);

    internal static Task<PinnedDestinationRetentionGuard?>
        OpenOrRepairOwnedAsync(
            PinnedDirectoryCreation.PinnedDirectoryAnchor destinationParent,
            string publicName,
            string retentionName,
            long expectedLength,
            string expectedSha256,
            CancellationToken cancellationToken) =>
        OpenAsync(
            destinationParent,
            publicName,
            retentionName,
            expectedLength,
            expectedSha256,
            createIfMissing: true,
            repairInterruptedOwnedCopy: true,
            cancellationToken);

    internal static Task<PinnedDestinationRetentionGuard?> OpenExistingAsync(
        PinnedDirectoryCreation.PinnedDirectoryAnchor destinationParent,
        string publicName,
        string retentionName,
        long expectedLength,
        string expectedSha256,
        CancellationToken cancellationToken) =>
        OpenAsync(
            destinationParent,
            publicName,
            retentionName,
            expectedLength,
            expectedSha256,
            createIfMissing: false,
            repairInterruptedOwnedCopy: false,
            cancellationToken);

    private static async Task<PinnedDestinationRetentionGuard?> OpenAsync(
        PinnedDirectoryCreation.PinnedDirectoryAnchor destinationParent,
        string publicName,
        string retentionName,
        long expectedLength,
        string expectedSha256,
        bool createIfMissing,
        bool repairInterruptedOwnedCopy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destinationParent);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicName);
        ArgumentException.ThrowIfNullOrWhiteSpace(retentionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);

        var parent = destinationParent.Duplicate();
        PinnedDirectoryCreation.PinnedFileEntry? retention = null;
        PinnedDirectoryCreation.PinnedFileEntry? publicTarget = null;
        try
        {
            var retentionOutcome = parent.TryOpenExistingFileWithOutcome(
                retentionName,
                requireDeleteAccess: true,
                out retention);
            if (retentionOutcome == PinnedFileOpenOutcome.Unavailable)
            {
                return null;
            }

            if (retentionOutcome == PinnedFileOpenOutcome.NotFound)
            {
                if (!createIfMissing)
                {
                    return null;
                }

                publicTarget = parent.OpenExistingFileForStableRead(publicName);
                if (!publicTarget.VisiblePathMatches()
                    || !await publicTarget.MatchesAsync(
                        expectedLength,
                        expectedSha256,
                        cancellationToken))
                {
                    return null;
                }

                if (OperatingSystem.IsWindows())
                {
                    retention = parent.CreateNewFile(
                        retentionName,
                        hiddenFile: true);
                    await using var source = publicTarget.OpenReadStream(
                        128 * 1024,
                        asynchronous: false);
                    await using var destination = retention.OpenWriteStream(
                        128 * 1024,
                        asynchronous: false);
                    await source.CopyToAsync(destination, cancellationToken);
                    await destination.FlushAsync(cancellationToken);
                    retention.FlushToDisk();
                }
                else
                {
                    retention = publicTarget.CreateHardLinkTo(
                        parent,
                        retentionName);
                }
                parent.FlushDirectoryEntry();
            }
            else
            {
                var publicOutcome = TryOpenStablePublicTarget(
                    parent,
                    publicName,
                    out publicTarget);
                if (publicOutcome == PinnedFileOpenOutcome.Unavailable)
                {
                    return null;
                }
            }

            if (OperatingSystem.IsWindows() && retention != null)
            {
                retention.Dispose();
                retention = parent.OpenExistingFileForStableDelete(retentionName);
            }

            if (retention == null
                || !retention.VisiblePathMatches()
                || !await retention.MatchesAsync(
                    expectedLength,
                    expectedSha256,
                    cancellationToken))
            {
                if (!repairInterruptedOwnedCopy
                    || !OperatingSystem.IsWindows()
                    || retention == null
                    || publicTarget == null
                    || !publicTarget.VisiblePathMatches()
                    || !await publicTarget.MatchesAsync(
                        expectedLength,
                        expectedSha256,
                        cancellationToken))
                {
                    return null;
                }

                retention.Delete(immediateWindows: true);
                retention.Dispose();
                retention = null;
                parent.FlushDirectoryEntry();

                retention = parent.CreateNewFile(
                    retentionName,
                    hiddenFile: true);
                await using (var source = publicTarget.OpenReadStream(
                    128 * 1024,
                    asynchronous: false))
                await using (var destination = retention.OpenWriteStream(
                    128 * 1024,
                    asynchronous: false))
                {
                    await source.CopyToAsync(destination, cancellationToken);
                    await destination.FlushAsync(cancellationToken);
                }
                retention.FlushToDisk();
                retention.Dispose();
                retention = parent.OpenExistingFileForStableDelete(
                    retentionName);
                parent.FlushDirectoryEntry();

                if (!retention.VisiblePathMatches()
                    || !await retention.MatchesAsync(
                        expectedLength,
                        expectedSha256,
                        cancellationToken))
                {
                    return null;
                }
            }

            var result = new PinnedDestinationRetentionGuard(
                parent,
                retention,
                publicTarget,
                publicName,
                retentionName,
                expectedLength,
                expectedSha256);
            parent = null!;
            retention = null;
            publicTarget = null;
            return result;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or Win32Exception
                or InvalidOperationException or PlatformNotSupportedException)
        {
            return null;
        }
        finally
        {
            publicTarget?.Dispose();
            retention?.Dispose();
            parent?.Dispose();
        }
    }

    internal async Task<bool> CurrentPublicationMatchesAsync(
        CancellationToken cancellationToken)
    {
        if (_publicTarget == null
            || !_publicTarget.VisiblePathMatches()
            || !await _publicTarget.MatchesAsync(
                _expectedLength,
                _expectedSha256,
                cancellationToken))
        {
            return false;
        }

        return OperatingSystem.IsWindows()
            || _publicTarget.IdentifiesSameEntry(_retention);
    }

    internal async Task<bool> TryLinearizePublicationAsync(
        CancellationToken cancellationToken)
    {
        if (_completed || _linearized)
        {
            return _linearized && !_completed;
        }

        if (!await _retention.MatchesAsync(
                _expectedLength,
                _expectedSha256,
                cancellationToken))
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            if (!await CurrentPublicationMatchesAsync(cancellationToken))
            {
                return false;
            }

            _linearized = true;
            return true;
        }

        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        if (!_parent.TryExchangeRelativeFiles(_publicName, _retentionName))
        {
            return false;
        }

        _parent.FlushDirectoryEntry();
        PinnedDirectoryCreation.PinnedFileEntry? displaced = null;
        try
        {
            var outcome = _parent.TryOpenExistingFileWithOutcome(
                _retentionName,
                requireDeleteAccess: false,
                out displaced);
            if (outcome == PinnedFileOpenOutcome.Opened
                && displaced != null
                && displaced.IdentifiesSameEntry(_retention)
                && await displaced.MatchesAsync(
                    _expectedLength,
                    _expectedSha256,
                    cancellationToken))
            {
                _linearized = true;
                return true;
            }
        }
        finally
        {
            displaced?.Dispose();
        }

        if (!_parent.TryExchangeRelativeFiles(_publicName, _retentionName))
        {
            throw new InvalidOperationException(
                "The destination-retention exchange could not be rolled back after detecting a replacement generation.");
        }

        _parent.FlushDirectoryEntry();
        return false;
    }

    internal async Task<bool> CompleteAsync(
        CancellationToken cancellationToken)
    {
        if (!_linearized || _completed)
        {
            return _completed;
        }

        if (!_retention.VisiblePathMatches()
            || !await CurrentPublicationMatchesAsync(cancellationToken))
        {
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            var retirementName =
                $".listenarr-destination-retirement-{Guid.NewGuid():N}.bin";
            _retention.MoveWithinParent(retirementName);
            _parent.FlushDirectoryEntry();
        }

        var retainedPath = Path.Join(_parent.FullPath, _retentionName);
        _retention.Delete(immediateWindows: true);
        _retention.Dispose();
        _parent.FlushDirectoryEntry();
        if (OperatingSystem.IsWindows() && File.Exists(retainedPath))
        {
            return false;
        }

        _completed = true;
        if (OperatingSystem.IsWindows())
        {
            // The stable public handle is the Windows linearization guard. Keep it
            // alive until the recovery copy has been retired and its directory entry
            // flushed so no replacement pathname can appear inside the commit window.
            _publicTarget?.Dispose();
            AfterWindowsPublicTargetReleasedForTest?.Invoke();
        }
        return true;
    }

    public void Dispose()
    {
        _publicTarget?.Dispose();
        _retention.Dispose();
        _parent.Dispose();
    }

    private static PinnedFileOpenOutcome TryOpenStablePublicTarget(
        PinnedDirectoryCreation.PinnedDirectoryAnchor parent,
        string publicName,
        out PinnedDirectoryCreation.PinnedFileEntry? publicTarget)
    {
        publicTarget = null;
        try
        {
            publicTarget = parent.OpenExistingFileForStableRead(publicName);
            return PinnedFileOpenOutcome.Opened;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode is 2 or 3)
        {
            return PinnedFileOpenOutcome.NotFound;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or Win32Exception)
        {
            return PinnedFileOpenOutcome.Unavailable;
        }
    }
}
