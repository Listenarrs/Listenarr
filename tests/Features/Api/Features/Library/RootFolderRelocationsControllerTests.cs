using Listenarr.Application.Common.Exceptions;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Api.Features.Library;

[Trait("Name", "RootFolderRelocationsControllerTests")]
[Trait("Category", "Api")]
public sealed class RootFolderRelocationsControllerTests : BaseTests
{
    [Fact]
    public async Task Retry_MetadataOnlyRequiresMetadataRepairReadiness()
    {
        var relocationId = Guid.NewGuid();
        var service = new Mock<IRootFolderRelocationService>();
        var attention = new RootFolderPathChangeResult(
            relocationId,
            1,
            "D:\\Library",
            "D:\\Library",
            RootFolderRelocationStatus.NeedsAttention,
            2,
            1,
            "1 audiobook requires attention.",
            TargetIdentityEnrollmentState.Authorized,
            [32],
            RootFolderRelocationMode.MetadataOnly);
        service.Setup(candidate => candidate.GetAsync(
                relocationId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(attention);
        var readiness = new TestLibraryFilesystemReadiness();
        readiness.SetFailed("Injected filesystem initialization failure.");
        var controller = new Listenarr.Api.Features.Library.RootFolderRelocationsController(
            service.Object,
            readiness,
            readiness);

        var exception = await Assert.ThrowsAsync<ApplicationUnavailableException>(() =>
            controller.Retry(relocationId, CancellationToken.None));

        Assert.Equal("metadata_repair_initialization_failed", exception.Code);
        service.Verify(candidate => candidate.RetryAsync(
            It.IsAny<Guid>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Retry_FailedMetadataOnlyRepair_Ready_AllowsRecoveryWithoutPhysicalTargetIdentity()
    {
        var relocationId = Guid.NewGuid();
        var service = new Mock<IRootFolderRelocationService>();
        var failed = new RootFolderPathChangeResult(
            relocationId,
            1,
            "D:\\Library",
            "D:\\Library",
            RootFolderRelocationStatus.Failed,
            1,
            0,
            "Metadata recovery failed.",
            TargetIdentityEnrollmentState.Unavailable,
            null,
            RootFolderRelocationMode.MetadataOnly);
        var completed = failed with
        {
            Status = RootFolderRelocationStatus.Completed,
            CompletedJobs = 1,
            Error = null,
            TargetIdentityEnrollmentState = TargetIdentityEnrollmentState.NotRequired
        };
        service.Setup(candidate => candidate.GetAsync(
                relocationId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(failed);
        service.Setup(candidate => candidate.RetryAsync(
                relocationId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(completed);
        var readiness = TestLibraryFilesystemReadiness.Ready();
        var controller = new Listenarr.Api.Features.Library.RootFolderRelocationsController(
            service.Object,
            readiness,
            readiness);

        var result = await controller.Retry(relocationId, CancellationToken.None);

        var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result);
        var payload = Assert.IsType<RootFolderPathChangeResult>(ok.Value);
        Assert.Equal(RootFolderRelocationStatus.Completed, payload.Status);
        Assert.Equal(TargetIdentityEnrollmentState.NotRequired, payload.TargetIdentityEnrollmentState);
        service.Verify(candidate => candidate.RetryAsync(
            relocationId,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetSkippedMetadataRepair_ReturnsSafeRepairDetails()
    {
        var relocationId = Guid.NewGuid();
        var service = new Mock<IRootFolderRelocationService>();
        var details = new RootFolderMetadataRepairDetails(
            relocationId,
            32,
            "Powerless",
            RootFolderRelocationSkipReasonCode.TargetIdentityCollision,
            [
                new RootFolderMetadataRepairCollisionGroup(
                    "Author/Powerless/book.mp3",
                    [
                        new RootFolderMetadataRepairCollisionFile(100, 32, "book.mp3", true),
                        new RootFolderMetadataRepairCollisionFile(101, 32, "book.MP3", true)
                    ])
            ]);
        service.Setup(candidate => candidate.GetSkippedMetadataRepairDetailsAsync(
                relocationId,
                32,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(details);
        var readiness = TestLibraryFilesystemReadiness.Ready();
        var controller = new Listenarr.Api.Features.Library.RootFolderRelocationsController(
            service.Object,
            readiness,
            readiness);

        var result = await controller.GetSkippedMetadataRepair(
            relocationId,
            32,
            CancellationToken.None);

        var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result);
        Assert.Same(details, ok.Value);
    }

    [Fact]
    public async Task RemoveSkippedMetadataRepairFile_Ready_UsesNarrowRepairService()
    {
        var relocationId = Guid.NewGuid();
        var service = new Mock<IRootFolderRelocationService>();
        var details = new RootFolderMetadataRepairDetails(
            relocationId,
            32,
            "Powerless",
            RootFolderRelocationSkipReasonCode.TargetIdentityCollision,
            []);
        service.Setup(candidate => candidate.RemoveSkippedMetadataRepairFileAsync(
                relocationId,
                32,
                101,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(details);
        var readiness = TestLibraryFilesystemReadiness.Ready();
        var controller = new Listenarr.Api.Features.Library.RootFolderRelocationsController(
            service.Object,
            readiness,
            readiness);

        var result = await controller.RemoveSkippedMetadataRepairFile(
            relocationId,
            32,
            101,
            CancellationToken.None);

        var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result);
        Assert.Same(details, ok.Value);
        service.Verify(candidate => candidate.RemoveSkippedMetadataRepairFileAsync(
            relocationId,
            32,
            101,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RemoveSkippedMetadataRepairFile_StartupFailed_BlocksBeforeMutation()
    {
        var relocationId = Guid.NewGuid();
        var service = new Mock<IRootFolderRelocationService>(MockBehavior.Strict);
        var readiness = new TestLibraryFilesystemReadiness();
        readiness.SetFailed("Injected startup failure.");
        var controller = new Listenarr.Api.Features.Library.RootFolderRelocationsController(
            service.Object,
            readiness,
            readiness);

        var exception = await Assert.ThrowsAsync<ApplicationUnavailableException>(() =>
            controller.RemoveSkippedMetadataRepairFile(
                relocationId,
                32,
                101,
                CancellationToken.None));

        Assert.Equal("metadata_repair_initialization_failed", exception.Code);
        service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AbandonUnpublished_Ready_UsesFilesystemMutationGateAndReturnsSafeResult()
    {
        var relocationId = Guid.NewGuid();
        var service = new Mock<IRootFolderRelocationService>();
        service.Setup(candidate => candidate.AbandonUnpublishedAsync(
                relocationId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RootFolderPathChangeResult(
                relocationId,
                1,
                "D:\\Source",
                "D:\\Target",
                RootFolderRelocationStatus.Failed,
                1,
                0,
                "Internal abandonment detail.",
                TargetIdentityEnrollmentState.NotRequired,
                null,
                RootFolderRelocationMode.Relocate,
                null,
                CanAbandon: false));
        var readiness = TestLibraryFilesystemReadiness.Ready();
        var controller = new Listenarr.Api.Features.Library.RootFolderRelocationsController(
            service.Object,
            readiness,
            readiness);

        var result = await controller.AbandonUnpublished(
            relocationId,
            CancellationToken.None);

        var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result);
        var payload = Assert.IsType<RootFolderPathChangeResult>(ok.Value);
        Assert.Equal(RootFolderRelocationStatus.Failed, payload.Status);
        Assert.False(payload.CanAbandon);
        Assert.DoesNotContain("Internal abandonment detail", payload.Error);
        service.Verify(candidate => candidate.AbandonUnpublishedAsync(
            relocationId,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AbandonUnpublished_StartupFailed_BlocksBeforeMutation()
    {
        var relocationId = Guid.NewGuid();
        var service = new Mock<IRootFolderRelocationService>(MockBehavior.Strict);
        var readiness = new TestLibraryFilesystemReadiness();
        readiness.SetFailed("Injected startup failure.");
        var controller = new Listenarr.Api.Features.Library.RootFolderRelocationsController(
            service.Object,
            readiness,
            readiness);

        var exception = await Assert.ThrowsAsync<ApplicationUnavailableException>(() =>
            controller.AbandonUnpublished(
                relocationId,
                CancellationToken.None));

        Assert.Equal("filesystem_initialization_failed", exception.Code);
        service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AbandonUnpublished_UnsafeState_ReturnsConflictCode()
    {
        var relocationId = Guid.NewGuid();
        var service = new Mock<IRootFolderRelocationService>();
        service.Setup(candidate => candidate.AbandonUnpublishedAsync(
                relocationId,
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ApplicationConflictException(
                "root_folder_relocation_cannot_abandon",
                "This relocation already published durable move state."));
        var readiness = TestLibraryFilesystemReadiness.Ready();
        var controller = new Listenarr.Api.Features.Library.RootFolderRelocationsController(
            service.Object,
            readiness,
            readiness);

        var result = await controller.AbandonUnpublished(
            relocationId,
            CancellationToken.None);

        var conflict = Assert.IsType<Microsoft.AspNetCore.Mvc.ConflictObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(conflict.Value);
        Assert.Contains("root_folder_relocation_cannot_abandon", json, StringComparison.Ordinal);
        Assert.Contains("already published durable move state", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Retry_RelocateStillRequiresFilesystemMutationReadiness()
    {
        var relocationId = Guid.NewGuid();
        var service = new Mock<IRootFolderRelocationService>();
        service.Setup(candidate => candidate.GetAsync(
                relocationId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RootFolderPathChangeResult(
                relocationId,
                1,
                "D:\\Source",
                "D:\\Target",
                RootFolderRelocationStatus.NeedsAttention,
                1,
                0,
                "Retry required.",
                TargetIdentityEnrollmentState.Authorized,
                null,
                RootFolderRelocationMode.Relocate));
        var readiness = new TestLibraryFilesystemReadiness();
        readiness.SetFailed("Injected filesystem initialization failure.");
        var controller = new Listenarr.Api.Features.Library.RootFolderRelocationsController(
            service.Object,
            readiness,
            readiness);

        await Assert.ThrowsAsync<ApplicationUnavailableException>(() =>
            controller.Retry(relocationId, CancellationToken.None));

        service.Verify(candidate => candidate.RetryAsync(
            It.IsAny<Guid>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}
