using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Scanning;

internal sealed partial class AudiobookScanService
{
    private static PinnedScanAuthority OpenPinnedScanAuthority(
        AudiobookScanCommand command)
    {
        var boundaryPath = FileSystemPathIdentity.Canonicalize(
            command.ScanIdentity.BoundaryPath,
            command.ScanIdentity.Syntax);
        var scanRoot = FileSystemPathIdentity.Canonicalize(
            command.ScanRoot,
            command.ScanIdentity.Syntax);
        var anchors = new List<PinnedDirectoryState>();
        PinnedDirectoryCreation.PinnedDirectoryAnchor? current = null;
        try
        {
            current = PinnedDirectoryCreation.OpenPinnedBoundary(boundaryPath);
            var requireDurableGenerationProof =
                command.ScanPhysicalIdentity.HasDurableGenerationProof;
            anchors.Add(PinnedDirectoryState.Capture(
                current,
                requireDurableGenerationProof));
            var relative = Path.GetRelativePath(boundaryPath, scanRoot);
            if (relative != ".")
            {
                foreach (var segment in relative.Split(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries))
                {
                    if (segment is "." or "..")
                    {
                        throw new InvalidOperationException(
                            "The scan path escaped its configured physical boundary.");
                    }

                    var next = current.OpenExistingChild(segment);
                    current = next;
                    anchors.Add(PinnedDirectoryState.Capture(
                        next,
                        requireDurableGenerationProof));
                }
            }

            var authority = new PinnedScanAuthority(anchors);
            authority.Validate(command);
            return authority;
        }
        catch
        {
            foreach (var state in anchors)
            {
                state.Anchor.Dispose();
            }

            if (current != null
                && anchors.All(state => !ReferenceEquals(state.Anchor, current)))
            {
                current.Dispose();
            }

            throw;
        }
    }

    private static void ValidateDiscoverySnapshot(
        AudiobookScanCommand command,
        PinnedScanAuthority authority,
        ScanDiscoveryResult discovery)
    {
        authority.Validate(command);
        foreach (var snapshot in discovery.DirectoryObjectIdentities)
        {
            ValidateDirectorySnapshot(
                command,
                authority,
                snapshot.Key,
                snapshot.Value);
        }
    }

    private static void ValidateDiscoveredPathParent(
        AudiobookScanCommand command,
        PinnedScanAuthority authority,
        ScanDiscoveryResult discovery,
        string path)
    {
        authority.Validate(command);
        var parent = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException(
                "A discovered scan path has no parent directory.");
        var canonicalParent = FileSystemPathIdentity.Canonicalize(
            parent,
            command.ScanIdentity.Syntax);
        if (!discovery.DirectoryObjectIdentities.TryGetValue(
                canonicalParent,
                out var expectedObjectIdentity))
        {
            throw new InvalidOperationException(
                "The discovered file parent was not part of the pinned directory snapshot.");
        }

        ValidateDirectorySnapshot(
            command,
            authority,
            canonicalParent,
            expectedObjectIdentity);
        var canonicalPath = FileSystemPathIdentity.Canonicalize(
            path,
            command.ScanIdentity.Syntax);
        if (!discovery.FileObjectIdentities.TryGetValue(
                canonicalPath,
                out var expectedFileIdentity)
            || !PinnedFileIdentityMatches(
                command,
                authority,
                canonicalPath,
                expectedFileIdentity))
        {
            throw new InvalidOperationException(
                "A discovered file changed or disappeared before it could be claimed.");
        }
    }

    private static void ValidateNearestDirectorySnapshot(
        AudiobookScanCommand command,
        PinnedScanAuthority authority,
        ScanDiscoveryResult discovery,
        string path)
    {
        authority.Validate(command);
        var current = Path.GetDirectoryName(path);
        while (!string.IsNullOrWhiteSpace(current)
            && FileSystemPathIdentity.IsSameOrInside(
                current,
                command.ScanRoot,
                command.ScanIdentity.Semantics))
        {
            var canonical = FileSystemPathIdentity.Canonicalize(
                current,
                command.ScanIdentity.Syntax);
            if (discovery.DirectoryObjectIdentities.TryGetValue(
                    canonical,
                    out var expectedObjectIdentity))
            {
                ValidateDirectorySnapshot(
                    command,
                    authority,
                    canonical,
                    expectedObjectIdentity);
                return;
            }

            if (FileSystemPathIdentity.AreEquivalent(
                    current,
                    command.ScanRoot,
                    command.ScanIdentity.Semantics))
            {
                break;
            }

            current = Path.GetDirectoryName(current);
        }

        throw new InvalidOperationException(
            "No pinned directory snapshot proves the tracked file scope.");
    }

    private static IAudiobookFileRegistrationLease OpenPinnedMetadataFile(
        AudiobookScanCommand command,
        PinnedScanAuthority authority,
        ScanDiscoveryResult discovery,
        string path)
    {
        var canonicalPath = FileSystemPathIdentity.Canonicalize(
            path,
            command.ScanIdentity.Syntax);
        if (!discovery.FileObjectIdentities.TryGetValue(
                canonicalPath,
                out var expectedIdentity))
        {
            throw new InvalidOperationException(
                "The metadata candidate was not part of the pinned file snapshot.");
        }

        return OpenPinnedRegistrationFile(
            command,
            authority,
            canonicalPath,
            expectedIdentity);
    }

    private static IAudiobookFileRegistrationLease OpenPinnedRegistrationFile(
        AudiobookScanCommand command,
        PinnedScanAuthority authority,
        string path,
        string? expectedIdentity = null)
    {
        var canonicalPath = FileSystemPathIdentity.Canonicalize(
            path,
            command.ScanIdentity.Syntax);
        if (!FileSystemPathIdentity.IsSameOrInside(
                canonicalPath,
                command.ScanRoot,
                command.ScanIdentity.Semantics))
        {
            throw new InvalidOperationException(
                "The registration candidate is outside the authorized scan root.");
        }

        var relative = Path.GetRelativePath(command.ScanRoot, canonicalPath);
        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            throw new InvalidOperationException(
                "The registration candidate contains invalid navigation segments.");
        }

        var current = authority.Root.Duplicate();
        try
        {
            for (var index = 0; index < segments.Length - 1; index++)
            {
                var next = current.OpenExistingChild(segments[index]);
                current.Dispose();
                current = next;
            }

            var file = current.OpenExistingFileForStableRead(segments[^1]);
            if (!file.VisiblePathMatches() || !file.IsRegularFile())
            {
                file.Dispose();
                throw new InvalidOperationException(
                    "The registration candidate changed or is no longer a regular file before stable extraction.");
            }

            if (!command.ScanPhysicalIdentity.HasDurableGenerationProof)
            {
                return PinnedAudiobookFileRegistrationLease.CreatePinnedPathOnly(
                    file,
                    canonicalPath);
            }

            if (!string.IsNullOrWhiteSpace(expectedIdentity)
                && !file.MatchesObjectIdentity(expectedIdentity))
            {
                file.Dispose();
                throw new InvalidOperationException(
                    "The registration candidate changed before stable extraction.");
            }

            return PinnedAudiobookFileRegistrationLease.Create(
                file,
                canonicalPath,
                expectedIdentity);
        }
        finally
        {
            current.Dispose();
        }
    }

    private static bool PinnedFileIdentityMatches(
        AudiobookScanCommand command,
        PinnedScanAuthority authority,
        string path,
        string expectedIdentity)
    {
        if (!FileSystemPathIdentity.IsSameOrInside(
                path,
                command.ScanRoot,
                command.ScanIdentity.Semantics))
        {
            return false;
        }

        var relative = Path.GetRelativePath(command.ScanRoot, path);
        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            return false;
        }

        var current = authority.Root.Duplicate();
        try
        {
            for (var index = 0; index < segments.Length - 1; index++)
            {
                var next = current.OpenExistingChild(segments[index]);
                current.Dispose();
                current = next;
            }

            var outcome = current.TryOpenExistingFileWithOutcome(
                segments[^1],
                requireDeleteAccess: false,
                out var opened);
            using (opened)
            {
                return outcome == PinnedFileOpenOutcome.Opened
                    && opened != null
                    && opened.VisiblePathMatches()
                    && (!command.ScanPhysicalIdentity.HasDurableGenerationProof
                        || opened.MatchesObjectIdentity(expectedIdentity));
            }
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception)
        {
            return false;
        }
        finally
        {
            current.Dispose();
        }
    }

    private static bool PinnedFileExists(
        AudiobookScanCommand command,
        PinnedScanAuthority authority,
        string path)
    {
        if (!FileSystemPathIdentity.IsSameOrInside(
                path,
                command.ScanRoot,
                command.ScanIdentity.Semantics))
        {
            throw new InvalidOperationException(
                "A pinned file lookup escaped the authorized scan root.");
        }

        var relative = Path.GetRelativePath(command.ScanRoot, path);
        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            throw new InvalidOperationException(
                "A pinned file lookup contains invalid navigation segments.");
        }

        var current = authority.Root.Duplicate();
        try
        {
            for (var index = 0; index < segments.Length - 1; index++)
            {
                try
                {
                    var next = current.OpenExistingChild(segments[index]);
                    current.Dispose();
                    current = next;
                }
                catch (System.ComponentModel.Win32Exception exception) when (
                    exception.NativeErrorCode is 2 or 3)
                {
                    return false;
                }
                catch (DirectoryNotFoundException)
                {
                    return false;
                }
            }

            var outcome = current.TryOpenExistingFileWithOutcome(
                segments[^1],
                requireDeleteAccess: false,
                out var opened);
            using (opened)
            {
                return outcome switch
                {
                    PinnedFileOpenOutcome.Opened => opened!.VisiblePathMatches(),
                    PinnedFileOpenOutcome.NotFound => false,
                    _ => throw new IOException(
                        "The pinned scan file is temporarily unavailable.")
                };
            }
        }
        finally
        {
            current.Dispose();
        }
    }

    private static void ValidateDirectorySnapshot(
        AudiobookScanCommand command,
        PinnedScanAuthority authority,
        string directory,
        string expectedObjectIdentity)
    {
        using var current = OpenRelativeDirectory(
            authority.Root,
            command.ScanRoot,
            directory,
            command.ScanIdentity.Semantics);
        if (!current.VisiblePathMatches()
            || (command.ScanPhysicalIdentity.HasDurableGenerationProof
                && !current.MatchesDirectoryObjectIdentity(
                    expectedObjectIdentity)))
        {
            throw new InvalidOperationException(
                "A directory generation changed after scan discovery.");
        }
    }

    private static PinnedDirectoryCreation.PinnedDirectoryAnchor
        OpenRelativeDirectory(
            PinnedDirectoryCreation.PinnedDirectoryAnchor scanRoot,
            string scanRootPath,
            string directory,
            FileSystemPathSemantics semantics)
    {
        if (!FileSystemPathIdentity.IsSameOrInside(
                directory,
                scanRootPath,
                semantics))
        {
            throw new InvalidOperationException(
                "A scan directory snapshot escaped the authorized root.");
        }

        var current = scanRoot.Duplicate();
        try
        {
            var relative = Path.GetRelativePath(scanRootPath, directory);
            if (relative == ".")
            {
                return current;
            }

            foreach (var segment in relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment is "." or "..")
                {
                    throw new InvalidOperationException(
                        "A scan directory snapshot contains navigation segments.");
                }

                var next = current.OpenExistingChild(segment);
                current.Dispose();
                current = next;
            }

            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

}
