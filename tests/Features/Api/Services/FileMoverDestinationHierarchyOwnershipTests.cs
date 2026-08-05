using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Api.Services;

[Trait("Area", "FileSystem")]
[Trait("Name", "FileMoverDestinationHierarchyOwnershipTests")]
[Trait("Category", "FileSystem")]
public sealed class FileMoverDestinationHierarchyOwnershipTests : BaseTests
{
    [Fact]
    public async Task CopyFileAsync_DestinationParentRemovedAfterResolution_DoesNotRecreateHierarchy()
    {
        // Given
        var root = FileService.GetTempDirectory("file-mover-owned-file-parent-race");
        var sourceParent = Path.Join(root, "source");
        var destinationParent = Path.Join(root, "owned", "destination");
        Directory.CreateDirectory(sourceParent);
        Directory.CreateDirectory(destinationParent);
        var source = Path.Join(sourceParent, "book.m4b");
        var destination = Path.Join(destinationParent, "book.m4b");
        await File.WriteAllTextAsync(source, "audio");
        var removed = false;
        var mover = new FileMover(
            new NullLogger<FileMover>(),
            semanticsResolver: new FileSystemSemanticsResolver())
        {
            AfterFileMoveEndpointsResolvedForTestAsync = (_, observedDestination) =>
            {
                if (!removed
                    && string.Equals(
                        Path.GetFullPath(observedDestination),
                        Path.GetFullPath(destination),
                        StringComparison.OrdinalIgnoreCase))
                {
                    Directory.Delete(destinationParent);
                    removed = true;
                }

                return Task.CompletedTask;
            }
        };

        // When
        var copied = await mover.CopyFileAsync(source, destination);

        // Then
        Assert.True(removed);
        Assert.False(copied);
        Assert.True(File.Exists(source));
        Assert.False(Directory.Exists(destinationParent));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task MoveDirectoryAsync_DestinationParentRemovedAtMutationBoundary_DoesNotRecreateHierarchy()
    {
        // Given
        var root = FileService.GetTempDirectory("file-mover-owned-directory-parent-race");
        var source = Path.Join(root, "source");
        var destinationParent = Path.Join(root, "owned", "destination");
        var destination = Path.Join(destinationParent, "book");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destinationParent);
        await File.WriteAllTextAsync(Path.Join(source, "book.m4b"), "audio");
        var removed = false;
        var mover = new FileMover(
            new NullLogger<FileMover>(),
            semanticsResolver: new FileSystemSemanticsResolver())
        {
            BeforeDirectoryMoveAttemptForTest = () =>
            {
                if (!removed)
                {
                    Directory.Delete(destinationParent);
                    removed = true;
                }
            }
        };

        // When
        var moved = await mover.MoveDirectoryAsync(source, destination);

        // Then
        Assert.True(removed);
        Assert.False(moved);
        Assert.True(Directory.Exists(source));
        Assert.Equal("audio", await File.ReadAllTextAsync(Path.Join(source, "book.m4b")));
        Assert.False(Directory.Exists(destinationParent));
        Assert.False(Directory.Exists(destination));
    }
}
