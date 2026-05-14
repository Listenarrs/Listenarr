using Listenarr.Api.Controllers;
using Listenarr.Api.Models;
using Listenarr.Domain.Models;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Listenarr.Tests.Features.Api.Controllers
{
    [Trait("Name", "LibraryController_PathPreviewTests")]
    [Trait("Category", "LibraryController")]
    public class LibraryController_PathPreviewTests : BaseTests
    {
        /// <summary>
        /// The frontend depends on this endpoint as the public contract for backend path previews.
        /// </summary>
        [Fact]
        public async Task PreviewPath_ReturnsBackendPreviewForSelectedDestinationRoot()
        {
            var outputRoot = FileService.GetTempDirectory("listenarr-preview-settings-root");
            var destinationRoot = FileService.GetTempDirectory("listenarr-preview-selected-root");

            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(outputRoot)
                .WithFolderNamingPattern("{Author}/{Subtitle}/{Title}")
                .WithFileNamingPattern("{Title}")
                .Build());

            var controller = _provider.GetRequiredService<LibraryController>();
            var result = await controller.PreviewPath(new PreviewPathRequest
            {
                DestinationRoot = destinationRoot,
                Metadata = new AudibleBookMetadata
                {
                    Title = "Detail Book",
                    Subtitle = "A Useful Subtitle",
                    Authors = ["Author One"]
                }
            });

            var ok = Assert.IsType<OkObjectResult>(result);
            var response = Assert.IsType<PreviewPathResponse>(ok.Value);

            Assert.Equal(
                Path.Join(destinationRoot, "Author One", "A Useful Subtitle", "Detail Book"),
                response.FullPath);
            Assert.Equal(Path.Join("Author One", "A Useful Subtitle", "Detail Book"), response.RelativePath);
            Assert.Equal(destinationRoot, response.Root);
        }
    }
}
