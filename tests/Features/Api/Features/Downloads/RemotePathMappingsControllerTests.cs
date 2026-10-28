using Listenarr.Tests.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Tests.Features.Api.Features.Downloads;

[Trait("Name", "RemotePathMappingsControllerTests")]
[Trait("Category", "Api")]
public sealed class RemotePathMappingsControllerTests : BaseTests
{
    [Fact]
    public async Task Create_HostInvalidLocalPath_ReturnsGenericBadRequest()
    {
        var service = new Mock<IRemotePathMappingService>(MockBehavior.Strict);
        service
            .Setup(candidate => candidate.CreateAsync(It.IsAny<RemotePathMapping>()))
            .ThrowsAsync(new ArgumentException("The persisted path uses Unix filesystem syntax, but this host uses Windows syntax."));
        var controller = CreateController(service.Object);
        var mapping = CreateMapping();

        var action = await controller.Create(mapping);

        var result = Assert.IsType<BadRequestObjectResult>(action.Result);
        var serialized = System.Text.Json.JsonSerializer.Serialize(result.Value);
        Assert.Contains("Remote path mapping is invalid for this host.", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("Unix filesystem syntax", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenericEndpointFailures_DoNotExposeInternalExceptionDetails()
    {
        const string sensitiveDetail = "SENSITIVE_INTERNAL_DATABASE_DETAIL";
        const string clientId = "client-1";
        var client = new DownloadClientConfiguration { Id = clientId, Name = "Client" };
        var mapping = CreateMapping();
        mapping.Id = 17;
        var service = new Mock<IRemotePathMappingService>(MockBehavior.Strict);
        var clients = new Mock<IDownloadClientConfigurationRepository>(MockBehavior.Strict);
        var failure = new InvalidOperationException(sensitiveDetail);
        service.Setup(candidate => candidate.GetAllAsync()).ThrowsAsync(failure);
        service.Setup(candidate => candidate.GetByIdAsync(mapping.Id)).ThrowsAsync(failure);
        service.Setup(candidate => candidate.GetPathMappingByClientAsync(client)).ThrowsAsync(failure);
        service.Setup(candidate => candidate.CreateAsync(mapping)).ThrowsAsync(failure);
        service.Setup(candidate => candidate.UpdateAsync(mapping)).ThrowsAsync(failure);
        service.Setup(candidate => candidate.DeleteAsync(mapping.Id)).ThrowsAsync(failure);
        service.Setup(candidate => candidate.TranslatePathAsync(client, "/downloads/book.m4b"))
            .ThrowsAsync(failure);
        clients.Setup(candidate => candidate.GetByIdAsync(clientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(client);
        var controller = new RemotePathMappingsController(
            service.Object,
            clients.Object,
            Mock.Of<ILogger<RemotePathMappingsController>>());

        AssertNoSensitiveDetails((await controller.GetAll()).Result!, sensitiveDetail);
        AssertNoSensitiveDetails((await controller.GetById(mapping.Id)).Result!, sensitiveDetail);
        AssertNoSensitiveDetails((await controller.GetByClientId(clientId)).Result!, sensitiveDetail);
        AssertNoSensitiveDetails((await controller.Create(mapping)).Result!, sensitiveDetail);
        AssertNoSensitiveDetails((await controller.Update(mapping.Id, mapping)).Result!, sensitiveDetail);
        AssertNoSensitiveDetails(await controller.Delete(mapping.Id), sensitiveDetail);
        AssertNoSensitiveDetails(
            (await controller.TranslatePath(new RemotePathMappingsController.TranslatePathRequest
            {
                DownloadClientId = clientId,
                RemotePath = "/downloads/book.m4b"
            })).Result!,
            sensitiveDetail);
    }

    [Fact]
    public async Task Update_HostInvalidLocalPath_ReturnsGenericBadRequest()
    {
        var mapping = CreateMapping();
        mapping.Id = 17;
        var service = new Mock<IRemotePathMappingService>(MockBehavior.Strict);
        service
            .Setup(candidate => candidate.UpdateAsync(mapping))
            .ThrowsAsync(new ArgumentException("The persisted path uses Unix filesystem syntax, but this host uses Windows syntax."));
        var controller = CreateController(service.Object);

        var action = await controller.Update(mapping.Id, mapping);

        var result = Assert.IsType<BadRequestObjectResult>(action.Result);
        var serialized = System.Text.Json.JsonSerializer.Serialize(result.Value);
        Assert.Contains("Remote path mapping is invalid for this host.", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("Unix filesystem syntax", serialized, StringComparison.Ordinal);
    }

    private static void AssertNoSensitiveDetails(IActionResult action, string sensitiveDetail)
    {
        var result = Assert.IsType<ObjectResult>(action);
        Assert.Equal(StatusCodes.Status500InternalServerError, result.StatusCode);
        var serialized = System.Text.Json.JsonSerializer.Serialize(result.Value);
        Assert.DoesNotContain(sensitiveDetail, serialized, StringComparison.Ordinal);
    }

    private static RemotePathMappingsController CreateController(IRemotePathMappingService service) =>
        new(
            service,
            Mock.Of<IDownloadClientConfigurationRepository>(),
            Mock.Of<ILogger<RemotePathMappingsController>>());

    private static RemotePathMapping CreateMapping() =>
        new()
        {
            DownloadClientId = "client-1",
            Name = "mapping",
            RemotePath = "/downloads",
            LocalPath = "/imports"
        };
}
