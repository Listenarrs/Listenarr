using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

[Trait("Area", "Library")]
[Trait("Name", "PinnedDirectoryCreationTests")]
[Trait("Category", "Infrastructure")]
public sealed partial class PinnedDirectoryCreationTests : BaseTests
{
    [LinuxFact]
    public async Task OpenExistingFile_LinuxNamedPipe_DoesNotBlockInspection()
    {
        var parent = FileService.GetTempDirectory("pinned-file-named-pipe");
        var pipePath = Path.Join(parent, "unexpected.m4b");
        var startInfo = new ProcessStartInfo("mkfifo")
        {
            UseShellExecute = false,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(pipePath);
        using (var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start mkfifo."))
        {
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
        }

        using var anchor = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(parent);
        var openTask = Task.Run(() =>
            anchor.OpenExistingFile(Path.GetFileName(pipePath), requireDeleteAccess: false));
        var completed = await Task.WhenAny(openTask, Task.Delay(TimeSpan.FromSeconds(2)));

        Assert.Same(openTask, completed);
        using var entry = await openTask;
        Assert.True(entry.VisiblePathMatches());
        Assert.False(entry.IsRegularFile());
    }

    [Fact]
    public async Task FileVisiblePathProbe_ReplacedGeneration_IsMismatch()
    {
        var parent = FileService.GetTempDirectory("pinned-file-probe-replaced");
        var file = await FileService.GetFileAsync(parent, "book.m4b", "owned");
        var displaced = Path.Join(parent, "book.original.m4b");
        using var anchor = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(parent);
        using var entry = anchor.OpenExistingFile("book.m4b", requireDeleteAccess: false);

        File.Move(file, displaced);
        await File.WriteAllTextAsync(file, "replacement");

        Assert.Equal(
            RegistrationPublicationMatchOutcome.Mismatch,
            entry.ProbeVisiblePathMatch());
    }

    [Fact]
    public void DirectoryVisiblePathProbe_ReplacedGeneration_IsMismatch()
    {
        var parent = FileService.GetTempDirectory("pinned-directory-probe-parent");
        var directory = Path.Join(parent, "owned");
        var displaced = Path.Join(parent, "owned.original");
        Directory.CreateDirectory(directory);
        using var anchor = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(directory);

        Directory.Move(directory, displaced);
        Directory.CreateDirectory(directory);

        Assert.Equal(
            RegistrationPublicationMatchOutcome.Mismatch,
            anchor.ProbeVisiblePathMatch());
    }

    [LinuxFact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task FileVisiblePathProbe_AccessDenied_IsUnavailable()
    {
        var parent = FileService.GetTempDirectory("pinned-file-probe-unavailable");
        var file = await FileService.GetFileAsync(parent, "book.m4b", "owned");
        using var anchor = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(parent);
        using var entry = anchor.OpenExistingFile("book.m4b", requireDeleteAccess: false);
        var originalMode = File.GetUnixFileMode(file);
        File.SetUnixFileMode(file, UnixFileMode.None);
        try
        {
            if (!File.Exists(file))
            {
                Assert.Equal(
                    RegistrationPublicationMatchOutcome.Unavailable,
                    entry.ProbeVisiblePathMatch());
            }
        }
        finally
        {
            File.SetUnixFileMode(file, originalMode);
        }
    }

    [LinuxFact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task RegistrationLease_PhysicalIdentityMatch_DoesNotDependOnVisiblePathAvailability()
    {
        var parent = FileService.GetTempDirectory("registration-identity-probe-unavailable");
        var file = await FileService.GetFileAsync(parent, "book.m4b", "owned");
        using var lease = PinnedAudiobookFileRegistrationLease.Open(file);
        var originalMode = File.GetUnixFileMode(file);
        File.SetUnixFileMode(file, UnixFileMode.None);
        try
        {
            if (!File.Exists(file))
            {
                Assert.Equal(
                    RegistrationPublicationMatchOutcome.Unavailable,
                    lease.ProbeCurrentPublication());
                Assert.True(lease.MatchesPhysicalObjectIdentity(
                    lease.PhysicalObjectIdentity));
            }
        }
        finally
        {
            File.SetUnixFileMode(file, originalMode);
        }
    }

    [LinuxFact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void DirectoryVisiblePathProbe_AccessDenied_IsUnavailable()
    {
        var parent = FileService.GetTempDirectory("pinned-directory-probe-unavailable-parent");
        var directory = Path.Join(parent, "owned");
        Directory.CreateDirectory(directory);
        using var anchor = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(directory);
        var originalMode = File.GetUnixFileMode(parent);
        File.SetUnixFileMode(parent, UnixFileMode.None);
        try
        {
            if (!Directory.Exists(directory))
            {
                Assert.Equal(
                    RegistrationPublicationMatchOutcome.Unavailable,
                    anchor.ProbeVisiblePathMatch());
            }
        }
        finally
        {
            File.SetUnixFileMode(parent, originalMode);
        }
    }

    [WindowsFact]
    public void DeletePinnedEmptyDirectoryImmediately_NestedLiveAnchors_DeletesChildBeforeParent()
    {
        var parent = FileService.GetTempDirectory(
            "pinned-directory-immediate-nested-retirement");
        var statePath = Path.Join(parent, "state");
        var claimPath = Path.Join(statePath, "claim");
        using var parentAnchor = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(parent);
        using var state = parentAnchor.TryCreateChildForPublication("state");
        Assert.True(state.Created);
        using var stateAnchor = state.OpenCreatedDirectoryAnchor();
        using var claim = stateAnchor.TryCreateChildForPublication("claim");
        Assert.True(claim.Created);
        using var claimAnchor = claim.OpenCreatedDirectoryAnchor();
        Assert.True(claimAnchor.VisiblePathMatches());
        Assert.True(stateAnchor.VisiblePathMatches());

        claim.DeletePinnedEmptyDirectoryImmediately("claim");

        Assert.False(Directory.Exists(claimPath));
        Assert.True(stateAnchor.VisiblePathMatches());
        state.DeletePinnedEmptyDirectoryImmediately("state");

        Assert.False(Directory.Exists(statePath));
    }

    [Fact]
    public void PublishCreatedDirectoryAs_EmptyDirectory_PublishesWithinPinnedParent()
    {
        var parent = FileService.GetTempDirectory("pinned-directory-publication-empty");
        using var creation = PinnedDirectoryCreation.TryCreateForPublication(parent, "prepared");
        Assert.True(creation.Created);

        using var published = creation.PublishCreatedDirectoryAs("published");

        Assert.False(Directory.Exists(Path.Join(parent, "prepared")));
        Assert.True(Directory.Exists(Path.Join(parent, "published")));
        Assert.True(published.VisiblePathMatches());
    }

    [DirectoryLinkFact]
    public void RestrictToCurrentUser_PublicPathReplacedWithLink_DoesNotMutateReplacementTarget()
    {
        var parent = FileService.GetTempDirectory(
            "pinned-directory-permissions-parent");
        var replacementTarget = FileService.GetTempDirectory(
            "pinned-directory-permissions-external");
        using var creation = PinnedDirectoryCreation.TryCreateForPublication(
            parent,
            "private-state");
        Assert.True(creation.Created);
        var displaced = Path.Join(parent, "private-state-original");
        UnixFileMode? replacementMode = null;
        if (!OperatingSystem.IsWindows())
        {
            replacementMode = UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead
                | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead
                | UnixFileMode.OtherExecute;
            File.SetUnixFileMode(replacementTarget, replacementMode.Value);
        }

        Directory.Move(creation.FullPath, displaced);
        Directory.CreateSymbolicLink(creation.FullPath, replacementTarget);
        try
        {
            Assert.ThrowsAny<Exception>(() => creation.RestrictToCurrentUser());

            Assert.True(Directory.Exists(displaced));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(
                    replacementMode!.Value,
                    File.GetUnixFileMode(replacementTarget));
            }
        }
        finally
        {
            if (Directory.Exists(creation.FullPath))
            {
                Directory.Delete(creation.FullPath);
            }
        }
    }

    [Fact]
    public async Task CreateHardLinkTo_SamePinnedParent_CreatesGenerationWitness()
    {
        var parent = FileService.GetTempDirectory(
            "pinned-hardlink-same-parent");
        using var parentAnchor =
            PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(parent);
        using var source = parentAnchor.CreateNewFile("source.stage");
        await using (var stream = source.OpenWriteStream(4096, asynchronous: false))
        {
            await stream.WriteAsync("owned audio"u8.ToArray());
            await stream.FlushAsync();
            stream.Flush(flushToDisk: true);
        }

        using var claim = source.CreateHardLinkTo(
            parentAnchor,
            "destination.published.claim");

        Assert.True(source.IdentifiesSameEntry(claim));
        Assert.True(claim.VisiblePathMatches());
    }

    [Fact]
    public async Task CreateHardLinkTo_DestinationReplacedBeforeVerification_PreservesReplacementGeneration()
    {
        var parent = FileService.GetTempDirectory(
            "pinned-hardlink-verification-replacement");
        var sourcePath = await FileService.GetFileAsync(
            parent,
            "source.m4b",
            "owned audio");
        var destinationPath = Path.Join(parent, "destination.m4b");
        var displacedLinkPath = Path.Join(parent, "destination-created.m4b");
        using (var parentAnchor =
            PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(parent))
        using (var sourceEntry = parentAnchor.OpenExistingFile(
            Path.GetFileName(sourcePath),
            requireDeleteAccess: true))
        {
            Assert.ThrowsAny<Exception>(() =>
                sourceEntry.CreateHardLinkTo(
                    parentAnchor,
                    Path.GetFileName(destinationPath),
                    () =>
                    {
                        File.Move(destinationPath, displacedLinkPath);
                        File.WriteAllText(destinationPath, "replacement audio");
                    }).Dispose());
        }

        Assert.True(File.Exists(destinationPath));
        Assert.Equal(
            "replacement audio",
            await File.ReadAllTextAsync(destinationPath));
        Assert.True(File.Exists(displacedLinkPath));
        Assert.Equal(
            "owned audio",
            await File.ReadAllTextAsync(displacedLinkPath));
        Assert.Equal(
            "owned audio",
            await File.ReadAllTextAsync(sourcePath));
    }

    [Fact]
    public async Task PublishCreatedDirectoryAs_NonEmptyHierarchyWithReleasedDescendants_PublishesWithinPinnedParent()
    {
        var parent = FileService.GetTempDirectory("pinned-directory-publication-released-hierarchy");
        using var creation = PinnedDirectoryCreation.TryCreateForPublication(parent, "prepared");
        Assert.True(creation.Created);
        await File.WriteAllTextAsync(
            Path.Join(creation.FullPath, "marker.json"),
            "{}");
        using (var rootAnchor = creation.OpenCreatedDirectoryAnchor())
        {
            using var childCreation = rootAnchor.TryCreateChild("child");
            Assert.True(childCreation.Created);
            using var childAnchor = childCreation.OpenCreatedDirectoryAnchor();
            Assert.True(childAnchor.VisiblePathMatches());
        }

        using var published = creation.PublishCreatedDirectoryAs("published");

        Assert.False(Directory.Exists(Path.Join(parent, "prepared")));
        Assert.True(File.Exists(Path.Join(parent, "published", "marker.json")));
        Assert.True(Directory.Exists(Path.Join(parent, "published", "child")));
        Assert.True(published.VisiblePathMatches());
    }

    [Fact]
    public async Task PublishCreatedDirectoryAs_ExistingDestination_PreservesBothDirectories()
    {
        var parent = FileService.GetTempDirectory("pinned-directory-publication-collision");
        using var creation = PinnedDirectoryCreation.TryCreateForPublication(parent, "prepared");
        Assert.True(creation.Created);
        await File.WriteAllTextAsync(
            Path.Join(creation.FullPath, "prepared.txt"),
            "prepared");
        var published = Path.Join(parent, "published");
        Directory.CreateDirectory(published);
        await File.WriteAllTextAsync(Path.Join(published, "existing.txt"), "existing");

        Assert.ThrowsAny<Exception>(() =>
            creation.PublishCreatedDirectoryAs("published").Dispose());

        Assert.Equal("prepared", await File.ReadAllTextAsync(Path.Join(parent, "prepared", "prepared.txt")));
        Assert.Equal("existing", await File.ReadAllTextAsync(Path.Join(published, "existing.txt")));
    }

    [Fact]
    public async Task PublishCreatedDirectoryAs_NonEmptyHierarchyWithLiveRootAnchor_PublishesWithinPinnedParent()
    {
        var parent = FileService.GetTempDirectory("pinned-directory-publication-live-root");
        using var creation = PinnedDirectoryCreation.TryCreateForPublication(parent, "prepared");
        Assert.True(creation.Created);
        await File.WriteAllTextAsync(
            Path.Join(creation.FullPath, "marker.json"),
            "{}");
        using var rootAnchor = creation.OpenCreatedDirectoryAnchor();
        using (var childCreation = rootAnchor.TryCreateChild("child"))
        {
            Assert.True(childCreation.Created);
            using var childAnchor = childCreation.OpenCreatedDirectoryAnchor();
            Assert.True(childAnchor.VisiblePathMatches());
        }

        using var published = creation.PublishCreatedDirectoryAs("published");

        Assert.True(rootAnchor.VisiblePathMatches(Path.Join(parent, "published")));
        Assert.True(published.VisiblePathMatches());
        Assert.True(Directory.Exists(Path.Join(parent, "published", "child")));
    }

    [Fact]
    public async Task MoveExistingFileTo_PublishesOpenedFileBetweenPinnedParents()
    {
        var sourceParent = FileService.GetTempDirectory("pinned-file-move-source");
        var destinationParent = FileService.GetTempDirectory("pinned-file-move-destination");
        var sourceFile = await FileService.GetFileAsync(sourceParent, "book.m4b", "verified audio");
        var expectedHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sourceFile)));
        using var sourceAnchor = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(sourceParent);
        using var destinationAnchor = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(destinationParent);
        using (var sourceEntry = sourceAnchor.OpenExistingFile(
            "book.m4b",
            requireDeleteAccess: true))
        {
            Assert.True(await sourceEntry.MatchesAsync(
                new FileInfo(sourceFile).Length,
                expectedHash,
                CancellationToken.None));
            sourceEntry.MoveTo(destinationAnchor, "book.m4b");
            Assert.True(await sourceEntry.MatchesAsync(
                "verified audio"u8.Length,
                expectedHash,
                CancellationToken.None));
        }

        Assert.False(File.Exists(sourceFile));
        Assert.Equal(
            "verified audio",
            await File.ReadAllTextAsync(Path.Join(destinationParent, "book.m4b")));
    }

    [Fact]
    public async Task DeleteOpenedFile_RemovesVerifiedPinnedEntry()
    {
        var parent = FileService.GetTempDirectory("pinned-file-delete");
        var file = await FileService.GetFileAsync(parent, "book.m4b", "delete me");
        using var anchor = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(parent);
        using (var entry = anchor.OpenExistingFile(
            "book.m4b",
            requireDeleteAccess: true))
        {
            entry.Delete();
        }

        Assert.False(File.Exists(file));
    }

    [Fact]
    public void TryOpenExistingFileWithOutcome_MissingFile_IsNotFound()
    {
        var parent = FileService.GetTempDirectory("pinned-file-open-missing");
        using var anchor = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(parent);

        var outcome = anchor.TryOpenExistingFileWithOutcome(
            "missing-marker.json",
            requireDeleteAccess: true,
            out var entry);

        Assert.Equal(PinnedFileOpenOutcome.NotFound, outcome);
        Assert.Null(entry);
    }

    [WindowsFact]
    public async Task TryOpenExistingFile_WindowsSharingViolation_DoesNotReportMissing()
    {
        var parent = FileService.GetTempDirectory("pinned-file-open-locked-nullable");
        var marker = await FileService.GetFileAsync(
            parent,
            "marker.json",
            "owned marker");
        using var anchor = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(parent);
        using (File.Open(
            marker,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read))
        {
            var error = Assert.Throws<Win32Exception>(() =>
                anchor.TryOpenExistingFile(
                    "marker.json",
                    requireDeleteAccess: true));

            Assert.Equal(32, error.NativeErrorCode);
            Assert.True(File.Exists(marker));
        }
    }

    [WindowsFact]
    public async Task TryOpenExistingFileWithOutcome_WindowsSharingViolation_IsUnavailable()
    {

        var parent = FileService.GetTempDirectory("pinned-file-open-locked");
        var marker = await FileService.GetFileAsync(
            parent,
            "marker.json",
            "owned marker");
        using var anchor = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(parent);
        using (File.Open(
            marker,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read))
        {
            var lockedOutcome = anchor.TryOpenExistingFileWithOutcome(
                "marker.json",
                requireDeleteAccess: true,
                out var lockedEntry);

            Assert.Equal(PinnedFileOpenOutcome.Unavailable, lockedOutcome);
            Assert.Null(lockedEntry);
            Assert.True(File.Exists(marker));
        }

        var availableOutcome = anchor.TryOpenExistingFileWithOutcome(
            "marker.json",
            requireDeleteAccess: true,
            out var availableEntry);
        using (availableEntry)
        {
            Assert.Equal(PinnedFileOpenOutcome.Opened, availableOutcome);
            Assert.NotNull(availableEntry);
            Assert.True(availableEntry.VisiblePathMatches());
        }
    }

    [LinuxFact]
    public async Task DeleteOpenedFile_UnixReplacementBeforeFinalRevalidation_IsPreservedWithoutScratchArtifacts()
    {
        var parent = FileService.GetTempDirectory("pinned-file-delete-race");
        var file = await FileService.GetFileAsync(parent, "marker.json", "owned");
        var displaced = Path.Join(parent, "marker.original");
        using var anchor = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(parent);
        using var entry = anchor.OpenExistingFile(
            "marker.json",
            requireDeleteAccess: true);
        var replaced = false;
        using var hook = PinnedFilesystemMutationHooks.PushBeforeUnixFileDeleteRevalidation(path =>
        {
            if (replaced || !string.Equals(path, file, StringComparison.Ordinal))
            {
                return;
            }

            replaced = true;
            File.Move(file, displaced, overwrite: false);
            File.WriteAllText(file, "external");
        });

        Assert.ThrowsAny<Exception>(() => entry.Delete());

        Assert.True(replaced);
        Assert.Equal("owned", await File.ReadAllTextAsync(displaced));
        Assert.Equal("external", await File.ReadAllTextAsync(file));
        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(parent),
            path => Path.GetFileName(path).StartsWith(
                ".listenarr-",
                StringComparison.Ordinal));
    }

    [LinuxFact]
    public void TryCreateChildForPublication_UnixReplacementBeforeOpenIsNotGrantedCreationAuthority()
    {
        var parent = FileService.GetTempDirectory("pinned-directory-create-final-name-race");
        var child = Path.Join(parent, "Book");
        var displaced = Path.Join(parent, "Book.original");
        using var anchor = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(parent);
        var replaced = false;
        using var hook = PinnedFilesystemMutationHooks.PushAfterUnixDirectoryCreateBeforeOpen(path =>
        {
            if (replaced || !string.Equals(path, child, StringComparison.Ordinal))
            {
                return;
            }

            replaced = true;
            Directory.Move(child, displaced);
            Directory.CreateDirectory(child);
            File.WriteAllText(Path.Join(child, "external.txt"), "external");
        });

        using var creation = anchor.TryCreateChildForPublication("Book");
        using var observed = creation.OpenCreatedDirectoryAnchor();

        Assert.True(replaced);
        Assert.True(creation.Created);
        Assert.False(creation.CreationGenerationIsProvable);
        Assert.True(observed.VisiblePathMatches());
        Assert.True(Directory.Exists(displaced));
        Assert.Equal(
            "external",
            File.ReadAllText(Path.Join(child, "external.txt")));
        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(parent),
            path => Path.GetFileName(path).StartsWith(
                ".listenarr-",
                StringComparison.Ordinal));
    }

    [LinuxFact]
    public void DeletePinnedEmptyDirectoryImmediately_UnixReplacementBeforeFinalRevalidationIsPreserved()
    {
        var parent = FileService.GetTempDirectory("pinned-directory-delete-final-name-race");
        var child = Path.Join(parent, "Book");
        var displaced = Path.Join(parent, "Book.original");
        Directory.CreateDirectory(child);
        using var anchor = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(parent);
        using var publication = anchor.OpenExistingChildForPublication("Book");
        var replaced = false;
        using var hook = PinnedFilesystemMutationHooks.PushBeforeUnixDirectoryDeleteRevalidation(path =>
        {
            if (replaced || !string.Equals(path, child, StringComparison.Ordinal))
            {
                return;
            }

            replaced = true;
            Directory.Move(child, displaced);
            Directory.CreateDirectory(child);
            File.WriteAllText(Path.Join(child, "external.txt"), "external");
        });

        Assert.ThrowsAny<Exception>(() =>
            publication.DeletePinnedEmptyDirectoryImmediately("Book"));

        Assert.True(replaced);
        Assert.True(Directory.Exists(displaced));
        Assert.True(Directory.Exists(child));
        Assert.Equal(
            "external",
            File.ReadAllText(Path.Join(child, "external.txt")));
        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(parent),
            path => Path.GetFileName(path).StartsWith(
                ".listenarr-",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task MoveExistingFileTo_ExistingDestinationPreservesBothFiles()
    {
        var sourceParent = FileService.GetTempDirectory("pinned-file-move-collision-source");
        var destinationParent = FileService.GetTempDirectory("pinned-file-move-collision-destination");
        var sourceFile = await FileService.GetFileAsync(sourceParent, "book.m4b", "source");
        var destinationFile = await FileService.GetFileAsync(destinationParent, "book.m4b", "destination");
        using var sourceAnchor = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(sourceParent);
        using var destinationAnchor = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(destinationParent);
        using (var sourceEntry = sourceAnchor.OpenExistingFile(
            "book.m4b",
            requireDeleteAccess: true))
        {
            Assert.ThrowsAny<Exception>(() =>
                sourceEntry.MoveTo(destinationAnchor, "book.m4b"));
        }

        Assert.Equal("source", await File.ReadAllTextAsync(sourceFile));
        Assert.Equal("destination", await File.ReadAllTextAsync(destinationFile));
    }

    [WindowsFact]
    public async Task PublishCreatedDirectoryAs_WindowsNonEmptyHierarchyWithLiveDescendant_FailsClosed()
    {

        var parent = FileService.GetTempDirectory("pinned-directory-publication-live-hierarchy");
        using var creation = PinnedDirectoryCreation.TryCreateForPublication(parent, "prepared");
        Assert.True(creation.Created);
        await File.WriteAllTextAsync(
            Path.Join(creation.FullPath, "marker.json"),
            "{}");
        using var rootAnchor = creation.OpenCreatedDirectoryAnchor();
        using var childCreation = rootAnchor.TryCreateChild("child");
        Assert.True(childCreation.Created);
        using var childAnchor = childCreation.OpenCreatedDirectoryAnchor();

        await Assert.ThrowsAnyAsync<Exception>(() => Task.Run(() =>
            creation.PublishCreatedDirectoryAs("published").Dispose()));

        Assert.True(Directory.Exists(Path.Join(parent, "prepared")));
        Assert.False(Directory.Exists(Path.Join(parent, "published")));
    }

    [Fact]
    public async Task OpenOrCreateExclusiveLockFileAsync_ContendsAndReleasesAcrossPinnedAnchors()
    {
        var directory = FileService.GetTempDirectory(
            "pinned-exclusive-lock-file");
        using var firstAnchor =
            PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(directory);
        using var secondAnchor =
            PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(directory);
        using var firstLock =
            await firstAnchor.OpenOrCreateExclusiveLockFileAsync(
                "stripe-0001.lock");
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(250));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            secondAnchor.OpenOrCreateExclusiveLockFileAsync(
                "stripe-0001.lock",
                cancellation.Token));

        firstLock.Dispose();
        using var reacquired =
            await secondAnchor.OpenOrCreateExclusiveLockFileAsync(
                "stripe-0001.lock");
        Assert.True(reacquired.CanRead);
        Assert.True(reacquired.CanWrite);
    }

}
