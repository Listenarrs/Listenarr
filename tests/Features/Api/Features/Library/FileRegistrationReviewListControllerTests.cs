using Listenarr.Tests.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Tests.Features.Api.Features.Library;

[Trait("Area", "Library")]
[Trait("Name", "FileRegistrationReviewListControllerTests")]
[Trait("Category", "Api")]
public sealed class FileRegistrationReviewListControllerTests : BaseTests
{
    [Fact]
    public async Task List_PassesPagingToTheService_ReturnsTheItemsAndSetsTheTotalHeader()
    {
        var items = new[] { Review(Guid.NewGuid()), Review(Guid.NewGuid()) };
        var service = new Mock<IFileRegistrationRecoveryService>(MockBehavior.Strict);
        service.Setup(s => s.ListReviewsAsync(25, 50, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileRegistrationReviewPage(items, 250));
        var controller = Controller(service);

        var body = await controller.List(take: 25, skip: 50, CancellationToken.None);

        Assert.Equal(items, body);
        Assert.Equal("250", controller.Response.Headers["X-Total-Count"].ToString());
        service.Verify(s => s.ListReviewsAsync(25, 50, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task List_WithoutPaging_AsksForTheDefaultPage()
    {
        var service = new Mock<IFileRegistrationRecoveryService>(MockBehavior.Strict);
        service.Setup(s => s.ListReviewsAsync(FileRegistrationReviewPage.DefaultTake, 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileRegistrationReviewPage([], 0));
        var controller = Controller(service);

        var body = await controller.List();

        Assert.Empty(body);
        Assert.Equal("0", controller.Response.Headers["X-Total-Count"].ToString());
    }

    private static FileRegistrationReview Review(Guid operationId) => new(operationId, 7, "HardlinkCopy", 2,
        "NeedsAttention", "reason", "/source/book.m4b", "/destination/book.m4b",
        new("Present", 1, "AB"), new("Missing", null, null), [], "Completeness cannot be verified",
        true, new string('a', 64), null, null, null);

    private static FileRegistrationRecoveryController Controller(Mock<IFileRegistrationRecoveryService> service) =>
        new(service.Object, new Mock<IFilesystemMutationCoordinator>(MockBehavior.Strict).Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
}
