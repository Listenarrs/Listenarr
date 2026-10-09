using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

public partial class AudiobookContentMoveServiceTests
{
    [Fact]
    public async Task MoveContentsAsync_SourceReplacedBetweenCopyAndCleanup_RetainsReplacement()
    {
        var root = FileService.GetTempDirectory("move-source-reopened-replacement");
        var source = Directory.CreateDirectory(Path.Join(root, "source")).FullName;
        var file = Path.Join(source, "book.m4b");
        File.WriteAllText(file, "audio");
        var target = Path.Join(root, "destination", "Book");
        var request = (await CreateLeasedMoveRequestAsync(source, target)) with
        {
            CommitOwnerMetadataAsync = (_, _) =>
            {
                File.Move(file, file + ".original");
                File.WriteAllText(file, "audio");
                return Task.CompletedTask;
            }
        };
        var service = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(), TimeProvider.System,
            new DisableMarkerlessFileRename());

        var result = await service.MoveContentsAsync(request, CancellationToken.None);
        using var verification = result.TargetVerificationLease;
        using var ancestors = result.SourceAncestorRetirementLease;
        Assert.True(result.SourceRetained);
        Assert.Equal("audio", File.ReadAllText(file));
        Assert.Equal("audio", File.ReadAllText(file + ".original"));
        Assert.Equal("audio", File.ReadAllText(Path.Join(target, "book.m4b")));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public Task MoveContentsAsync_LargeManifest_BoundsHandles(
        bool retainSource, bool nativeRename, bool siblingDirectories) =>
        MoveLargeManifestAsync(FileService.GetTempDirectory("move-bounded-handles"),
            600, retainSource, nativeRename, siblingDirectories, false);

    [NetworkStorageTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MoveContentsAsync_LargeMountedManifest_BoundsHandles(bool siblingDirectories)
    {
        var mount = Environment.GetEnvironmentVariable(NetworkStorageTheoryAttribute.PathEnvironmentVariable)!;
        var root = Directory.CreateDirectory(Path.Join(mount, "move-bounded-" + Guid.NewGuid().ToString("N"))).FullName;
        try { await MoveLargeManifestAsync(root, 240, true, false, siblingDirectories, true); }
        finally { Directory.Delete(root, recursive: true); }
    }

    private async Task MoveLargeManifestAsync(string root, int count, bool retainSource,
        bool nativeRename, bool siblingDirectories, bool allowMissingDiagnostics)
    {
        var source = Directory.CreateDirectory(Path.Join(root, "source")).FullName;
        for (var index = 0; index < count; index++)
        {
            var parent = siblingDirectories
                ? Directory.CreateDirectory(Path.Join(source, $"Disc {index:D4}")).FullName : source;
            File.WriteAllText(Path.Join(parent, $"{index:D4}.m4b"), "audio");
        }
        var target = Path.Join(root, "destination", "Book");
        var request = await CreateLeasedMoveRequestAsync(source, target,
            sourceCleanupBoundary: root, executionProtocolVersion: MoveExecutionProtocol.MarkerlessDatabaseState,
            allowMissingDiagnostics: allowMissingDiagnostics);
        var observed = 0;
        void ObserveHandles()
        {
            if (!OperatingSystem.IsLinux()) return;
            observed = Math.Max(observed, Directory.EnumerateFiles("/proc/self/fd")
                .Count(path => new FileInfo(path).LinkTarget is { } name
                    && (name == root || name.StartsWith(root + "/", StringComparison.Ordinal))));
            Assert.InRange(observed, 0, 48);
        }
        var commit = request.CommitOwnerMetadataAsync!;
        request = request with
        {
            ForceCopyAndRetainSource = retainSource,
            ProgressReporter = (_, _, _) => { ObserveHandles(); return Task.CompletedTask; },
            CommitOwnerMetadataAsync = async (result, token) =>
            {
                ObserveHandles();
                Assert.Equal(count, Directory.GetFiles(target, "*.m4b", SearchOption.AllDirectories).Length);
                if (!nativeRename)
                    Assert.Equal(count, Directory.GetFiles(source, "*.m4b", SearchOption.AllDirectories).Length);
                await commit(result, token);
            }
        };
        var service = new AudiobookContentMoveService(
            _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
            _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(), TimeProvider.System,
            nativeRename ? null : new DisableMarkerlessFileRename());

        var result = await service.MoveContentsAsync(request, CancellationToken.None);
        using (result.TargetVerificationLease)
        using (result.SourceAncestorRetirementLease)
        {
            ObserveHandles();
            Assert.Equal(retainSource, result.SourceRetained);
            Assert.Equal(RegistrationPublicationMatchOutcome.Match,
                await result.TargetVerificationLease!.ProbeCurrentPublicationsAsync(CancellationToken.None));
            Assert.Equal(count, Directory.GetFiles(target, "*.m4b", SearchOption.AllDirectories).Length);
            Assert.All(Directory.GetFiles(target, "*.m4b", SearchOption.AllDirectories),
                path => Assert.Equal("audio", File.ReadAllText(path)));
            if (retainSource)
                Assert.Equal(count, Directory.GetFiles(source, "*.m4b", SearchOption.AllDirectories).Length);
            else
                Assert.False(Directory.Exists(source));
        }
    }
}
