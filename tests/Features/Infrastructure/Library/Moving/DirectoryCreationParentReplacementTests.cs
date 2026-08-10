using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

[Trait("Area", "Library")]
[Trait("Name", "DirectoryCreationParentReplacementTests")]
[Trait("Category", "Infrastructure")]
public sealed class DirectoryCreationParentReplacementTests : BaseTests
{
    [DirectoryLinkFact]
    public Task EnsureCreatedHierarchyAsync_ParentReplacedBeforeHandleOpen_DoesNotCreateOutsideBoundary() =>
        AssertParentReplacementBlockedAsync(replaceBeforeOpen: true);

    [DirectoryLinkFact]
    public Task EnsureCreatedHierarchyAsync_ParentReplacedAfterHandleOpen_DoesNotCreateOutsideBoundary() =>
        AssertParentReplacementBlockedAsync(replaceBeforeOpen: false);

    [DirectoryLinkFact]
    public async Task EnsureCreatedHierarchyAsync_LinkedManagedBoundary_CreatesInsidePinnedTarget()
    {
        var root = FileService.GetTempDirectory("directory-create-linked-boundary");
        var physicalBoundary = Path.Join(root, "physical");
        var linkedBoundary = Path.Join(root, "linked");
        Directory.CreateDirectory(physicalBoundary);
        RequireDirectoryLinkCapability();
        Directory.CreateSymbolicLink(linkedBoundary, physicalBoundary);
        await AddAuthorizedRootAsync(linkedBoundary, "Linked Boundary Test Root");

        var destination = Path.Join(linkedBoundary, "Author", "Book");
        var semantics = FileSystemPathSemantics.CurrentHostDefault;
        var store = _provider.GetRequiredService<ILibraryDirectoryOwnershipStore>();
        try
        {
            var created = await store.EnsureCreatedHierarchyAsync(
                destination,
                linkedBoundary,
                semantics,
                "linked-boundary-regression");

            Assert.Equal(OperatingSystem.IsWindows() ? 2 : 0, created.Count);
            Assert.True(Directory.Exists(Path.Join(physicalBoundary, "Author", "Book")));
            var resolution = await store.ResolveOwnedAsync(
                destination,
                semantics,
                CancellationToken.None);
            Assert.Equal(
                OperatingSystem.IsWindows()
                    ? LibraryDirectoryOwnershipResolutionState.Owned
                    : LibraryDirectoryOwnershipResolutionState.Unowned,
                resolution.State);
            Assert.DoesNotContain(
                Directory.EnumerateFileSystemEntries(
                    physicalBoundary,
                    "*",
                    SearchOption.AllDirectories),
                path => Path.GetFileName(path).StartsWith(
                    ".listenarr-",
                    StringComparison.Ordinal));
        }
        finally
        {
            TryDeleteDirectoryLink(linkedBoundary);
        }
    }

    [DirectoryLinkFact]
    public async Task EnsureCreatedHierarchyAsync_LinkedManagedBoundary_TopLevelUsesPlatformOwnershipContract()
    {
        var root = FileService.GetTempDirectory("directory-create-linked-top-level");
        var physicalBoundary = Path.Join(root, "physical");
        var linkedBoundary = Path.Join(root, "linked");
        Directory.CreateDirectory(physicalBoundary);
        RequireDirectoryLinkCapability();
        Directory.CreateSymbolicLink(linkedBoundary, physicalBoundary);
        await AddAuthorizedRootAsync(linkedBoundary, "Linked Top-Level Test Root");

        var destination = Path.Join(linkedBoundary, "Book");
        var semantics = FileSystemPathSemantics.CurrentHostDefault;
        var store = _provider.GetRequiredService<ILibraryDirectoryOwnershipStore>();
        try
        {
            var created = await store.EnsureCreatedHierarchyAsync(
                destination,
                linkedBoundary,
                semantics,
                "linked-top-level-regression");

            Assert.Equal(OperatingSystem.IsWindows() ? 1 : 0, created.Count);
            Assert.True(Directory.Exists(Path.Join(physicalBoundary, "Book")));
            var resolution = await store.ResolveOwnedAsync(
                destination,
                semantics,
                CancellationToken.None);
            Assert.Equal(
                OperatingSystem.IsWindows()
                    ? LibraryDirectoryOwnershipResolutionState.Owned
                    : LibraryDirectoryOwnershipResolutionState.Unowned,
                resolution.State);
            Assert.DoesNotContain(
                Directory.EnumerateFileSystemEntries(physicalBoundary),
                path => Path.GetFileName(path).StartsWith(
                    ".listenarr-",
                    StringComparison.Ordinal));
        }
        finally
        {
            TryDeleteDirectoryLink(linkedBoundary);
        }
    }

    [DirectoryLinkFact]
    public void PinnedFileEntry_DeleteUnderLinkedBoundary_DeletesThroughPinnedTarget()
    {
        var root = FileService.GetTempDirectory("pinned-file-retirement-linked-boundary");
        var physicalBoundary = Path.Join(root, "physical");
        var linkedBoundary = Path.Join(root, "linked");
        Directory.CreateDirectory(physicalBoundary);
        RequireDirectoryLinkCapability();
        Directory.CreateSymbolicLink(linkedBoundary, physicalBoundary);
        var fileName = "retire.tmp";

        try
        {
            using var boundary =
                PinnedDirectoryCreation.OpenPinnedBoundary(linkedBoundary);
            var file = boundary.CreateNewFile(fileName, hiddenFile: true);
            try
            {
                using (var stream = file.OpenWriteStream(
                    bufferSize: 4096,
                    asynchronous: false))
                {
                    stream.WriteByte(1);
                    stream.Flush(flushToDisk: true);
                }

                file.Delete();
            }
            finally
            {
                // Windows FileDispositionInfo marks the pinned handle for deletion;
                // pathname retirement becomes observable when that handle closes.
                file.Dispose();
            }

            Assert.False(File.Exists(Path.Join(physicalBoundary, fileName)));
            Assert.DoesNotContain(
                Directory.EnumerateFileSystemEntries(physicalBoundary),
                path => Path.GetFileName(path).StartsWith(
                    ".listenarr-",
                    StringComparison.Ordinal));
        }
        finally
        {
            TryDeleteDirectoryLink(linkedBoundary);
        }
    }

    private async Task AssertParentReplacementBlockedAsync(bool replaceBeforeOpen)
    {
        var suffix = replaceBeforeOpen ? "before-open" : "after-open";
        var root = FileService.GetTempDirectory($"directory-create-parent-race-root-{suffix}");
        var parent = Path.Join(root, "Author");
        var displacedParent = Path.Join(root, "Author.original");
        var external = FileService.GetTempDirectory($"directory-create-parent-race-external-{suffix}");
        Directory.CreateDirectory(parent);
        RequireDirectoryLinkCapability();
        await AddAuthorizedRootAsync(root, "Parent Replacement Test Root");

        var destination = Path.Join(parent, "Book");
        var semantics = FileSystemPathSemantics.CurrentHostDefault;
        var store = _provider.GetRequiredService<ILibraryDirectoryOwnershipStore>();
        var hookRan = false;
        Exception? hookFailure = null;
        void ReplaceParent(string path)
        {
            var expectedPath = replaceBeforeOpen ? parent : destination;
            if (hookRan || !string.Equals(path, expectedPath, StringComparison.Ordinal))
            {
                return;
            }

            hookRan = true;
            try
            {
                Directory.Move(parent, displacedParent);
                Directory.CreateSymbolicLink(parent, external);
            }
            catch (Exception exception)
            {
                hookFailure = exception;
                throw;
            }
        }

        using var hook = replaceBeforeOpen
            ? ExclusiveDirectoryCreator.PushBeforeOpenParentHook(ReplaceParent)
            : ExclusiveDirectoryCreator.PushBeforeCreateHook(ReplaceParent);

        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() =>
                store.EnsureCreatedHierarchyAsync(
                    destination,
                    root,
                    semantics,
                    "parent-replacement-regression"));

            Assert.True(hookRan);
            Assert.Null(hookFailure);
            Assert.Empty(Directory.EnumerateFileSystemEntries(external));
            Assert.False(Directory.Exists(Path.Join(external, "Book")));
            var resolution = await store.ResolveOwnedAsync(
                destination,
                semantics,
                CancellationToken.None);
            Assert.Equal(
                LibraryDirectoryOwnershipResolutionState.Unowned,
                resolution.State);
        }
        finally
        {
            TryDeleteDirectoryLink(parent);
            if (Directory.Exists(displacedParent) && !Directory.Exists(parent))
            {
                Directory.Move(displacedParent, parent);
            }
        }
    }

    private static void RequireDirectoryLinkCapability()
    {
        NativeTestCapabilityPolicy.RequireAvailable(
            NativeTestCapability.DirectorySymbolicLinks);
    }

    private static void TryDeleteDirectoryLink(string path)
    {
        try
        {
            if (Directory.Exists(path)
                && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(path);
            }
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException)
        {
            // Best effort test cleanup. The per-test temporary root is removed by BaseTests.
        }
    }
}
