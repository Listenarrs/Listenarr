namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

public sealed partial class PinnedDirectoryCreationTests
{
    [Fact]
    public async Task PublishMigrationTargetAsync_DifferentPhysicalGeneration_DoesNotGrantOwnership()
    {
        // Given
        var root = FileService.GetTempDirectory("ownership-migration-generation");
        var sourceDirectory = Path.Join(root, "source", "Book");
        var targetDirectory = Path.Join(root, "target", "Book");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(targetDirectory);
        var ownershipToken = Guid.NewGuid().ToString("N");
        using var sourceAnchor = PinnedDirectoryCreation.OpenPinnedBoundary(sourceDirectory);
        using var targetAnchor = PinnedDirectoryCreation.OpenPinnedBoundary(targetDirectory);
        Assert.NotEqual(
            sourceAnchor.GetDirectoryObjectIdentity(),
            targetAnchor.GetDirectoryObjectIdentity());
        var source = CreateOwnership(
            sourceDirectory,
            ownershipToken,
            sourceAnchor.GetDirectoryObjectIdentity());
        var target = CreateOwnership(
            targetDirectory,
            ownershipToken,
            sourceAnchor.GetDirectoryObjectIdentity());
        using var targetParent = PinnedDirectoryCreation.OpenPinnedBoundary(
            Path.GetDirectoryName(targetDirectory)!);

        // When
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PinnedLibraryDirectoryOwnershipMarker.PublishMigrationTargetAsync(
                source,
                target,
                targetParent,
                CancellationToken.None));

        // Then
        Assert.Contains(
            "different physical directory generation",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Join(
            targetDirectory,
            LibraryDirectoryOwnershipMarker.FileName)));
        Assert.False(File.Exists(Path.Join(
            Path.GetDirectoryName(targetDirectory)!,
            $".listenarr-directory-owner-{ownershipToken}.json")));
    }

    [Fact]
    public async Task PublishMigrationTargetAsync_SamePhysicalGeneration_CanPublishOwnership()
    {
        // Given
        var directory = Path.Join(
            FileService.GetTempDirectory("ownership-migration-same-generation"),
            "Book");
        Directory.CreateDirectory(directory);
        var ownershipToken = Guid.NewGuid().ToString("N");
        using var directoryAnchor = PinnedDirectoryCreation.OpenPinnedBoundary(directory);
        var nativeIdentity = directoryAnchor.GetDirectoryObjectIdentity();
        var source = CreateOwnership(directory, ownershipToken, nativeIdentity);
        var target = CreateOwnership(directory, ownershipToken, nativeIdentity);
        using var parent = PinnedDirectoryCreation.OpenPinnedBoundary(
            Path.GetDirectoryName(directory)!);

        // When
        await PinnedLibraryDirectoryOwnershipMarker.PublishMigrationTargetAsync(
            source,
            target,
            parent,
            CancellationToken.None);

        // Then
        LibraryDirectoryOwnershipMarker.Validate(target, directory);
        Assert.True(ManagedDirectoryIdentity.Matches(
            target.DirectoryObjectIdentityVersion,
            target.DirectoryObjectIdentity,
            ownershipToken,
            nativeIdentity));
    }

    private static LibraryDirectoryOwnership CreateOwnership(
        string path,
        string ownershipToken,
        string nativeIdentity)
    {
        var semantics = FileSystemPathSemantics.CurrentHostDefault;
        return new LibraryDirectoryOwnership
        {
            Path = path,
            CanonicalPath = path,
            PathSyntax = semantics.Syntax,
            PathCaseSensitivity = semantics.CaseSensitivity,
            PathCaseSensitivityMode = FileSystemCaseSensitivityMode.Auto,
            PathIdentityBoundary = path,
            PathIdentityLookupKey = FileSystemPathIdentity.CreateLookupKey(
                "library-directory",
                path,
                semantics.Syntax),
            PathOwnershipKey = FileSystemPathIdentity.CreateKey(
                "library-directory",
                path,
                semantics),
            OwnershipToken = ownershipToken,
            State = LibraryDirectoryOwnershipState.Owned,
            CreationWorkflow = "Test",
            ManagedRootFolderId = 1,
            DirectoryObjectIdentityVersion = ManagedDirectoryIdentity.CurrentVersion,
            DirectoryObjectIdentity = ManagedDirectoryIdentity.Create(
                ownershipToken,
                nativeIdentity)
        };
    }
}
