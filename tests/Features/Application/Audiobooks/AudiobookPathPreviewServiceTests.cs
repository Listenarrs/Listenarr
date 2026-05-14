using Listenarr.Application.Interfaces;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Listenarr.Tests.Features.Application.Audiobooks
{
    [Trait("Name", "AudiobookPathPreviewServiceTests")]
    [Trait("Category", "AudiobookPathPreviewService")]
    public class AudiobookPathPreviewServiceTests : BaseTests
    {
        [Fact]
        public async Task PreviewAsync_NonSeriesBook_RemovesSeriesComponent()
        {
            var outputRoot = FileService.GetTempDirectory("listenarr-preview-output");
            await SaveSettingsAsync(outputRoot, "{Author}/{Series}/{Title}");

            var audiobook = new AudiobookBuilder()
                .WithTitle("The Buffalo Hunter Hunter")
                .WithAuthor("Stephen Graham Jones")
                .WithYear("2025")
                .Build();

            var result = await PreviewService.PreviewAsync(audiobook);

            Assert.Equal(
                Path.Join(outputRoot, "Stephen Graham Jones", "The Buffalo Hunter Hunter"),
                result.FullPath);
            Assert.Equal(Path.Join("Stephen Graham Jones", "The Buffalo Hunter Hunter"), result.RelativePath);
        }

        [Fact]
        public async Task PreviewAsync_SeriesBook_IncludesSeriesComponent()
        {
            var outputRoot = FileService.GetTempDirectory("listenarr-preview-series-output");
            await SaveSettingsAsync(outputRoot, "{Author}/{Series}/{Title}");

            var audiobook = new AudiobookBuilder()
                .WithTitle("The Gunslinger")
                .WithAuthor("Stephen King")
                .WithYear("1982")
                .WithSeries("The Dark Tower")
                .Build();

            var result = await PreviewService.PreviewAsync(audiobook);

            Assert.Equal(
                Path.Join(outputRoot, "Stephen King", "The Dark Tower", "The Gunslinger"),
                result.FullPath);
            Assert.Equal(Path.Join("Stephen King", "The Dark Tower", "The Gunslinger"), result.RelativePath);
        }

        /// <summary>
        /// Missing optional tokens should collapse path separators instead of creating "Unknown" folders.
        /// </summary>
        [Fact]
        public async Task PreviewAsync_EmptySubtitle_RemovesSubtitleComponent()
        {
            var outputRoot = FileService.GetTempDirectory("listenarr-preview-subtitle-output");
            var destinationRoot = FileService.GetTempDirectory("listenarr-preview-library");
            await SaveSettingsAsync(outputRoot, "{Author}/{Subtitle}/{Title}");

            var audiobook = new AudiobookBuilder()
                .WithTitle("Detail Book")
                .WithAuthor("Author One")
                .Build();

            var result = await PreviewService.PreviewAsync(audiobook, destinationRoot);

            Assert.Equal(Path.Join(destinationRoot, "Author One", "Detail Book"), result.FullPath);
            Assert.DoesNotContain("Unknown", result.FullPath, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Base path previews should apply folder patterns exactly as configured.
        /// </summary>
        [Fact]
        public async Task PreviewAsync_SeriesBook_DoesNotAddSeriesUnlessPatternIncludesIt()
        {
            var outputRoot = FileService.GetTempDirectory("listenarr-preview-title-only-output");
            await SaveSettingsAsync(outputRoot, "{Author}/{Title}");

            var audiobook = new AudiobookBuilder()
                .WithTitle("The Gunslinger")
                .WithAuthor("Stephen King")
                .WithSeries("The Dark Tower")
                .Build();

            var result = await PreviewService.PreviewAsync(audiobook);

            Assert.Equal(Path.Join(outputRoot, "Stephen King", "The Gunslinger"), result.FullPath);
            Assert.DoesNotContain("The Dark Tower", result.FullPath, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// An empty folder pattern means the audiobook lives directly under the selected root.
        /// </summary>
        [Fact]
        public async Task PreviewAsync_EmptyFolderPattern_ReturnsRootWithoutUnknownSubdirectory()
        {
            var outputRoot = FileService.GetTempDirectory("listenarr-preview-empty-pattern-output");
            await SaveSettingsAsync(outputRoot, string.Empty);

            var audiobook = new AudiobookBuilder()
                .WithTitle("Detail Book")
                .WithAuthor("Author One")
                .Build();

            var result = await PreviewService.PreviewAsync(audiobook);

            Assert.Equal(outputRoot, result.FullPath);
            Assert.Equal(string.Empty, result.RelativePath);
            Assert.DoesNotContain("Unknown", result.FullPath, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// File naming patterns affect imported files, not the audiobook base directory.
        /// </summary>
        [Fact]
        public async Task PreviewAsync_FileNamingPatternIsIgnoredForBasePath()
        {
            var outputRoot = FileService.GetTempDirectory("listenarr-preview-file-pattern-ignored-output");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(outputRoot)
                .WithFolderNamingPattern(string.Empty)
                .WithFileNamingPattern("{Author}/{Title}")
                .Build());

            var audiobook = new AudiobookBuilder()
                .WithTitle("Detail Book")
                .WithAuthor("Author One")
                .Build();

            var result = await PreviewService.PreviewAsync(audiobook);

            Assert.Equal(outputRoot, result.FullPath);
            Assert.Equal(string.Empty, result.RelativePath);
            Assert.DoesNotContain("Author One", result.FullPath, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Detail Book", result.FullPath, StringComparison.OrdinalIgnoreCase);
        }

        private IAudiobookPathPreviewService PreviewService =>
            _provider.GetRequiredService<IAudiobookPathPreviewService>();

        private async Task SaveSettingsAsync(string outputRoot, string folderNamingPattern)
        {
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(outputRoot)
                .WithFolderNamingPattern(folderNamingPattern)
                .WithFileNamingPattern("{Title}")
                .Build());
        }
    }
}
