using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Api.Services;

[Trait("Area", "FileSystem")]
[Trait("Name", "FileMoverDestinationHierarchyOwnershipTests")]
[Trait("Category", "FileSystem")]
public sealed class FileMoverDestinationHierarchyOwnershipTests : BaseTests
{
    [Fact]
    public async Task PerformActionOn_DestinationParentRemovedAfterResolution_DoesNotRecreateHierarchy()
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
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        var mover = new FileMover(
            new NullLogger<FileMover>(),
            semanticsResolver: new FileSystemSemanticsResolver(),
            dbContextFactory: factory,
            timeProvider: TimeProvider.System)
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
        var copied = await mover.PerformActionOn(
            FileAction.Copy,
            source,
            destination,
            Guid.NewGuid());

        // Then
        Assert.True(removed);
        Assert.False(copied);
        Assert.True(File.Exists(source));
        Assert.False(Directory.Exists(destinationParent));
        Assert.False(File.Exists(destination));
    }
}
