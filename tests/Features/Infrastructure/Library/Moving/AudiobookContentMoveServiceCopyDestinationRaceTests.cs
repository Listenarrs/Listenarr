using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

public partial class AudiobookContentMoveServiceTests
{
    [Fact]
    public async Task MoveContentsAsync_ValidPersistedPartial_DoesNotConsumeSourceDuringPublication()
    {
        var source = FileService.GetTempDirectory("content-move-persisted-partial-source-check");
        var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "verified audio");
        var target = FileService.GetTempDirectory("content-move-persisted-partial-target-check");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        await PersistFileManifestAsync(request.JobId, "book.m4b", sourceFile);
        var partial = Path.Join(
            target,
            $"book.m4b.listenarr-{request.JobId:N}.partial");
        await File.WriteAllTextAsync(partial, "verified audio");
        await WriteRecoveryMarkerAsync(
            target,
            request.JobId,
            source,
            target,
            "copy-started");
        var stopAfterPublished = new StopAfterPublished(source);
        var service = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            TimeProvider.System,
            stopAfterPublished);

        await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            service.MoveContentsAsync(request, CancellationToken.None));

        Assert.True(File.Exists(sourceFile));
        Assert.Null(stopAfterPublished.DeleteAccessError);
        Assert.Equal("verified audio", await File.ReadAllTextAsync(sourceFile));
        Assert.True(File.Exists(Path.Join(target, "book.m4b")));
    }

    [DirectoryLinkFact]
    public async Task MoveContentsAsync_CopyParentReplacedBeforePartialCreation_DoesNotWriteExternalPartial()
    {
        var root = FileService.GetTempDirectory("content-move-copy-parent-race-root");
        var targetParent = Path.Join(root, "destination-parent");
        var external = FileService.GetTempDirectory("content-move-copy-parent-race-external");
        var probe = Path.Join(root, "link-probe");
        Directory.CreateDirectory(targetParent);
        Assert.True(
            TryCreateTempDirectoryLink(probe, external),
            "The required directory link could not be created.");
        Directory.Delete(probe);

        var source = FileService.GetTempDirectory("content-move-copy-parent-race-source");
        var nestedSource = Path.Join(source, "extras");
        Directory.CreateDirectory(nestedSource);
        var sourceFile = await FileService.GetFileAsync(
            nestedSource,
            "book.m4b",
            "verified audio");
        var target = Path.Join(targetParent, "Book");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        var tempRoot = Path.Join(
            targetParent,
            Path.GetFileName(target) + ".tmp-" + request.JobId.ToString("N"));
        var destinationParent = Path.Join(tempRoot, "extras");
        var displacedDestinationParent = destinationParent + ".original";
        var externalPartial = Path.Join(
            external,
            $"book.m4b.listenarr-{request.JobId:N}.partial");
        var injector = new ReplaceCopyParentBeforePartialCreation(
            destinationParent,
            displacedDestinationParent,
            external);
        var service = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            TimeProvider.System,
            injector);

        try
        {
            await Assert.ThrowsAnyAsync<MoveNeedsAttentionException>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.True(injector.ReplacementRan);
            Assert.True(File.Exists(sourceFile));
            Assert.False(File.Exists(externalPartial));
        }
        finally
        {
            TryDeleteTempDirectoryLink(destinationParent);
            if (Directory.Exists(displacedDestinationParent)
                && !Directory.Exists(destinationParent))
            {
                Directory.Move(displacedDestinationParent, destinationParent);
            }
        }
    }

    [DirectoryLinkFact]
    public async Task MoveContentsAsync_DirectCopyParentReplacedAfterHandleOpen_DoesNotCreateExternalTarget()
    {
        var source = FileService.GetTempDirectory("content-move-direct-root-race-source");
        var displacedSource = source + ".original";
        var external = FileService.GetTempDirectory("content-move-direct-root-race-external");
        var probe = Path.Join(Path.GetDirectoryName(source)!, $"link-probe-{Guid.NewGuid():N}");
        Assert.True(
            TryCreateTempDirectoryLink(probe, external),
            "The required directory link could not be created.");
        Directory.Delete(probe);

        var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "verified audio");
        var target = Path.Join(source, "published");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        var hookRan = false;
        void ReplaceParent(string path)
        {
            if (hookRan || !string.Equals(path, target, StringComparison.Ordinal))
            {
                return;
            }

            hookRan = true;
            Directory.Move(source, displacedSource);
            if (!TryCreateTempDirectoryLink(source, external))
            {
                throw new IOException("The direct-copy parent replacement link could not be created.");
            }
        }

        using var hook = ExclusiveDirectoryCreator.PushBeforeCreateHook(ReplaceParent);
        try
        {
            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            await Assert.ThrowsAnyAsync<Exception>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.True(hookRan);
            Assert.True(File.Exists(Path.Join(displacedSource, Path.GetFileName(sourceFile))));
            Assert.Empty(Directory.EnumerateFileSystemEntries(external));
        }
        finally
        {
            TryDeleteTempDirectoryLink(source);
            if (Directory.Exists(displacedSource) && !Directory.Exists(source))
            {
                Directory.Move(displacedSource, source);
            }
        }
    }

    [Fact]
    public async Task MoveContentsAsync_SourceGenerationReplacedAfterManifestValidation_FailsClosed()
    {
        var source = FileService.GetTempDirectory("content-move-source-generation-race");
        var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "verified audio");
        var target = Path.Join(
            FileService.GetTempPath(),
            $"content-move-source-generation-target-{Guid.NewGuid():N}");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        string physicalIdentity;
        using (var parent = PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
            source,
            createMissing: false))
        using (var file = parent.OpenExistingFileForStableRead("book.m4b"))
        {
            physicalIdentity = file.GetObjectIdentity();
        }
        request = request with
        {
            SourcePhysicalObjectIdentities = new Dictionary<string, string>(
                request.SourceSemantics.Comparer)
            {
                ["book.m4b"] = physicalIdentity
            }
        };
        var displaced = sourceFile + ".original";
        var injector = new ReplaceSourceBeforeCopy(
            sourceFile,
            displaced,
            "verified audio");
        var service = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            TimeProvider.System,
            injector);

        await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            service.MoveContentsAsync(request, CancellationToken.None));

        Assert.True(injector.ReplacementRan);
        Assert.True(File.Exists(sourceFile));
        Assert.True(File.Exists(displaced));
        Assert.Equal("verified audio", await File.ReadAllTextAsync(sourceFile));
        Assert.Equal("verified audio", await File.ReadAllTextAsync(displaced));
    }

    [Fact]
    public async Task MoveContentsAsync_OwnedTempDisappearsBeforeCopy_DoesNotRecreateUnmarkedDirectory()
    {
        var source = FileService.GetTempDirectory("content-move-temp-disappears-source");
        var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "verified audio");
        var target = Path.Join(
            FileService.GetTempPath(),
            $"content-move-temp-disappears-target-{Guid.NewGuid():N}");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        var tempRoot = Path.Join(
            Path.GetDirectoryName(target)!,
            Path.GetFileName(target) + ".tmp-" + request.JobId.ToString("N"));
        var injector = new DeleteOwnedTempBeforeCopyRootValidation(tempRoot);
        var service = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
            TimeProvider.System,
            injector);

        var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            service.MoveContentsAsync(request, CancellationToken.None));

        Assert.True(injector.DeletionRan);
        Assert.Contains("disappeared", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(sourceFile));
        Assert.False(Directory.Exists(tempRoot));
        Assert.False(Directory.Exists(target));
    }

    private sealed class StopAfterPublished(string source) : IMoveFaultInjector
    {
        public Exception? DeleteAccessError { get; private set; }

        public Task AfterPublishedAsync(
            Guid jobId,
            CancellationToken cancellationToken)
        {
            try
            {
                using var sourceAnchor = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(source);
                using var sourceEntry = sourceAnchor.OpenExistingFile(
                    "book.m4b",
                    requireDeleteAccess: true);
            }
            catch (Exception exception)
            {
                DeleteAccessError = exception;
            }

            throw new MoveNeedsAttentionException("Stop after publication for source inspection.");
        }
    }

    private sealed class DeleteOwnedTempBeforeCopyRootValidation(string tempRoot)
        : IMoveFaultInjector
    {
        public bool AllowAtomicRename => false;

        public bool DeletionRan { get; private set; }

        public void OnCopyMutation(Guid jobId, CopyMutationFaultPoint faultPoint)
        {
            if (DeletionRan || faultPoint != CopyMutationFaultPoint.BeforeCopyRootValidation)
            {
                return;
            }

            Assert.True(File.Exists(Path.Join(tempRoot, ".listenarr-temp-owner.json")));
            Directory.Delete(tempRoot, recursive: true);
            DeletionRan = true;
        }
    }

    private sealed class ReplaceSourceBeforeCopy(
        string sourceFile,
        string displaced,
        string contents) : IMoveFaultInjector
    {
        public bool AllowAtomicRename => false;

        public bool ReplacementRan { get; private set; }

        public void OnCopyMutation(Guid jobId, CopyMutationFaultPoint faultPoint)
        {
            if (ReplacementRan
                || faultPoint != CopyMutationFaultPoint.BeforeCopyRootValidation)
            {
                return;
            }

            File.Move(sourceFile, displaced);
            File.WriteAllText(sourceFile, contents);
            ReplacementRan = true;
        }
    }

    private sealed class ReplaceCopyParentBeforePartialCreation(
        string destinationParent,
        string displacedDestinationParent,
        string external) : IMoveFaultInjector
    {
        private bool _replaced;

        public bool AllowAtomicRename => false;

        public bool ReplacementRan => _replaced;

        public void OnCopyMutation(
            Guid jobId,
            CopyMutationFaultPoint faultPoint)
        {
            if (!_replaced && faultPoint == CopyMutationFaultPoint.BeforePartialFileCreation)
            {
                Directory.Move(destinationParent, displacedDestinationParent);
                if (!TryCreateTempDirectoryLink(destinationParent, external))
                {
                    throw new IOException("The copy destination replacement link could not be created.");
                }

                _replaced = true;
                return;
            }

            if (_replaced && faultPoint == CopyMutationFaultPoint.AfterChunkWritten)
            {
                throw new MoveNeedsAttentionException(
                    "Stop after the first copy chunk for external-path inspection.");
            }
        }
    }
}
