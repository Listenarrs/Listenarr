using Microsoft.Extensions.Options;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.FileSystem;

[Trait("Name", "FilePublicationCapabilityResolverTests")]
[Trait("Category", "Infrastructure")]
public sealed class FilePublicationCapabilityResolverTests : BaseTests
{
    [Fact]
    public async Task ResolveAsync_WeakWritableDestination_DowngradesMoveToCopyAndRetain()
    {
        var root = BuildRoot();
        var repository = new Mock<IRootFolderRepository>(MockBehavior.Strict);
        repository.Setup(candidate => candidate.GetAllAsync())
            .ReturnsAsync([root]);
        var health = new Mock<IRootFolderStorageHealthResolver>(MockBehavior.Strict);
        health.Setup(candidate => candidate.ResolveAsync(
                root,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(WeakWritableObservation());
        var resolver = new FilePublicationCapabilityResolver(
            repository.Object,
            health.Object);

        var plan = await resolver.ResolveAsync(
            FileAction.Move,
            Path.Join(FileService.GetTempDirectory("publication-source"), "source.m4b"),
            Path.Join(root.Path, "book", "target.m4b"),
            DurableProof());

        Assert.True(plan.IsAllowed);
        Assert.Equal(
            FilePublicationExecutionMode.AdditiveCopyRetainSource,
            plan.Mode);
        Assert.Equal(FileAction.Copy, plan.EffectiveAction);
        Assert.Equal(
            FilePublicationSourceDisposition.Retained,
            plan.SourceDisposition);
        repository.VerifyAll();
        health.VerifyAll();
    }

    [Fact]
    public async Task ResolveAsync_WeakModeDisabled_BlocksWithoutGrantingMutation()
    {
        var root = BuildRoot();
        var repository = new Mock<IRootFolderRepository>(MockBehavior.Strict);
        repository.Setup(candidate => candidate.GetAllAsync())
            .ReturnsAsync([root]);
        var health = new Mock<IRootFolderStorageHealthResolver>(MockBehavior.Strict);
        health.Setup(candidate => candidate.ResolveAsync(
                root,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(WeakWritableObservation());
        var resolver = new FilePublicationCapabilityResolver(
            repository.Object,
            health.Object,
            Options.Create(new FileMoverOptions
            {
                WeakPublicationMode = WeakPublicationMode.Disabled
            }));

        var plan = await resolver.ResolveAsync(
            FileAction.Move,
            Path.Join(FileService.GetTempDirectory("publication-disabled-source"), "source.m4b"),
            Path.Join(root.Path, "book", "target.m4b"),
            DurableProof());

        Assert.False(plan.IsAllowed);
        Assert.Equal(FilePublicationExecutionMode.Blocked, plan.Mode);
        Assert.Equal(
            "compatibility_publication_disabled",
            plan.ReasonCode);
        repository.VerifyAll();
        health.VerifyAll();
    }

    private RootFolder BuildRoot()
    {
        var path = FileService.GetTempDirectory("publication-capability-root");
        return new RootFolder
        {
            Id = 91,
            Name = "Weak storage",
            Path = path,
            PathIdentityState = PathIdentityState.Valid,
            ResolvedCaseSensitivity =
                FileSystemPathSemantics.CurrentHostDefault.CaseSensitivity,
            CaseSensitivityMode =
                FileSystemPathSemantics.CurrentHostDefault.CaseSensitivity
                    == FileSystemCaseSensitivity.Sensitive
                    ? FileSystemCaseSensitivityMode.Sensitive
                    : FileSystemCaseSensitivityMode.Insensitive
        };
    }

    private static RootFolderStorageObservation WeakWritableObservation() =>
        new(
            RootFolderStorageState.Limited,
            RootFolderStorageReason.IdentityUnsupported,
            "Durable identity is unavailable.",
            CanConfirmCurrentFolder: false,
            CanChangePath: true,
            CanMutateFilesystem: false,
            ConfirmationToken: null,
            CanPublishNewFiles: true);

    private static FilePublicationSourceProof DurableProof() =>
        new(
            "durable:test",
            5,
            new string('A', 64));
}
