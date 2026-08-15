using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Tests.Features.Api.Features.Library;

[Trait("Area", "LibraryApi")]
[Trait("Name", "LibraryPreviewPathWorkflowTests")]
[Trait("Category", "LibraryController")]
public sealed class LibraryPreviewPathWorkflowTests : BaseTests
{
    [WindowsFact]
    public async Task PreviewAsync_UnavailableDestinationFilesystem_ReturnsGeneratedRelativePathWithoutStorageProbe()
    {
        var unavailableDrive = Enumerable.Range('D', 'Z' - 'D' + 1)
            .Select(letter => $"{(char)letter}:\\")
            .FirstOrDefault(path => !Directory.Exists(path));
        Assert.False(string.IsNullOrWhiteSpace(unavailableDrive));
        var destinationRoot = Path.Join(
            unavailableDrive!,
            "listenarr-preview-unavailable-root");
        var settings = new ApplicationSettingsBuilder()
            .WithFolderNamingPattern("{Author}/{Title}")
            .Build();
        var configurationService = new Mock<IConfigurationService>(MockBehavior.Strict);
        configurationService
            .Setup(service => service.GetApplicationSettingsAsync())
            .ReturnsAsync(settings);
        var workflow = new LibraryPreviewPathWorkflow(
            configurationService.Object,
            _provider.GetRequiredService<IFileNamingService>(),
            Mock.Of<ILogger<LibraryPreviewPathWorkflow>>());
        var request = new LibraryController.PreviewPathRequest
        {
            DestinationRoot = destinationRoot,
            Metadata = new AudibleBookMetadata
            {
                Title = "The Title",
                Authors = ["The Author"]
            }
        };

        var result = await workflow.PreviewAsync(request);

        var ok = Assert.IsType<OkObjectResult>(result);
        using var payload = System.Text.Json.JsonDocument.Parse(
            System.Text.Json.JsonSerializer.Serialize(ok.Value));
        Assert.Equal(
            Path.Join(destinationRoot, "The Author", "The Title"),
            payload.RootElement.GetProperty("fullPath").GetString());
        Assert.Equal(
            Path.Join("The Author", "The Title"),
            payload.RootElement.GetProperty("relativePath").GetString());
        configurationService.VerifyAll();
    }
}
