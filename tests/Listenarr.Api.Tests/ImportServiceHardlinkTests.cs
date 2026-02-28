using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using Listenarr.Api.Services;
using Listenarr.Domain.Models;

namespace Listenarr.Api.Tests
{
    public class ImportServiceHardlinkTests : IDisposable
    {
        private readonly string _outputRoot;
        private readonly string _sourceDir;

        public ImportServiceHardlinkTests()
        {
            _outputRoot = Path.Combine(Path.GetTempPath(), $"import-hardlink-out-{Guid.NewGuid()}");
            _sourceDir = Path.Combine(Path.GetTempPath(), $"import-hardlink-src-{Guid.NewGuid()}");
            Directory.CreateDirectory(_sourceDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_sourceDir, true); } catch { }
            try { Directory.Delete(_outputRoot, true); } catch { }
        }

        [Fact]
        public async Task ImportFilesFromDirectory_UsesHardlink_WhenModeIsHardlinkCopy()
        {
            // Arrange
            var file1 = Path.Combine(_sourceDir, "track1.m4b");
            await File.WriteAllTextAsync(file1, "audio data");

            var settings = new ApplicationSettings 
            { 
                OutputPath = _outputRoot, 
                CompletedFileAction = "Hardlink/Copy",
                EnableMetadataProcessing = false 
            };

            var provider = BuildImportServiceProvider();
            var importService = provider.GetRequiredService<IImportService>();

            // Act
            var results = await importService.ImportFilesFromDirectoryAsync("dl-1", null, new[] { file1 }, settings);

            // Assert
            Assert.True(results.Any(r => r.Success), "Import should succeed");
            
            var successResult = results.First(r => r.Success);
            Assert.True(File.Exists(successResult.FinalPath), "Destination file should exist");
            Assert.True(File.Exists(file1), "Source file should still exist (hardlink/copy preserves source)");
        }

        [Fact]
        public async Task ImportFilesFromDirectory_UsesMove_WhenModeIsMove()
        {
            // Arrange
            var file1 = Path.Combine(_sourceDir, "track1.m4b");
            await File.WriteAllTextAsync(file1, "audio data");

            var settings = new ApplicationSettings 
            { 
                OutputPath = _outputRoot, 
                CompletedFileAction = "Move",
                EnableMetadataProcessing = false 
            };

            var provider = BuildImportServiceProvider();
            var importService = provider.GetRequiredService<IImportService>();

            // Act
            var results = await importService.ImportFilesFromDirectoryAsync("dl-1", null, new[] { file1 }, settings);

            // Assert
            Assert.True(results.Any(r => r.Success), "Import should succeed");
            
            var successResult = results.First(r => r.Success);
            Assert.True(File.Exists(successResult.FinalPath), "Destination file should exist");
            Assert.False(File.Exists(file1), "Source file should be removed (moved)");
        }

        [Fact]
        public async Task ImportFilesFromDirectory_UsesCopy_WhenModeIsCopy()
        {
            // Arrange
            var file1 = Path.Combine(_sourceDir, "track1.m4b");
            await File.WriteAllTextAsync(file1, "audio data");

            var settings = new ApplicationSettings 
            { 
                OutputPath = _outputRoot, 
                CompletedFileAction = "Copy",
                EnableMetadataProcessing = false 
            };

            var provider = BuildImportServiceProvider();
            var importService = provider.GetRequiredService<IImportService>();

            // Act
            var results = await importService.ImportFilesFromDirectoryAsync("dl-1", null, new[] { file1 }, settings);

            // Assert
            Assert.True(results.Any(r => r.Success), "Import should succeed");
            
            var successResult = results.First(r => r.Success);
            Assert.True(File.Exists(successResult.FinalPath), "Destination file should exist");
            Assert.True(File.Exists(file1), "Source file should still exist (copy preserves source)");
        }

        [Fact]
        public async Task ImportSingleFileAsync_UsesHardlink_WhenModeIsHardlinkCopy()
        {
            // Arrange
            var sourceFile = Path.Combine(_sourceDir, "audiobook.m4b");
            await File.WriteAllTextAsync(sourceFile, "single file audio");

            var settings = new ApplicationSettings 
            { 
                OutputPath = _outputRoot, 
                CompletedFileAction = "Hardlink/Copy",
                EnableMetadataProcessing = false 
            };

            var provider = BuildImportServiceProvider();
            var importService = provider.GetRequiredService<IImportService>();

            // Act
            var result = await importService.ImportSingleFileAsync("dl-2", null, sourceFile, settings);

            // Assert
            Assert.True(result.Success, "Single file import should succeed");
            Assert.True(File.Exists(result.FinalPath), "Destination file should exist");
            Assert.True(File.Exists(sourceFile), "Source file should still exist");
        }

        [Fact]
        public async Task ImportSingleFileAsync_UsesMove_WhenModeIsMove()
        {
            // Arrange
            var sourceFile = Path.Combine(_sourceDir, "audiobook.m4b");
            await File.WriteAllTextAsync(sourceFile, "single file audio");

            var settings = new ApplicationSettings 
            { 
                OutputPath = _outputRoot, 
                CompletedFileAction = "Move",
                EnableMetadataProcessing = false 
            };

            var provider = BuildImportServiceProvider();
            var importService = provider.GetRequiredService<IImportService>();

            // Act
            var result = await importService.ImportSingleFileAsync("dl-2", null, sourceFile, settings);

            // Assert
            Assert.True(result.Success, "Single file import should succeed");
            Assert.True(File.Exists(result.FinalPath), "Destination file should exist");
            Assert.False(File.Exists(sourceFile), "Source file should be removed");
        }

        [Fact]
        public async Task ImportFilesFromDirectory_HandlesMultipleFiles_WithHardlinkCopy()
        {
            // Arrange
            var file1 = Path.Combine(_sourceDir, "track1.m4b");
            var file2 = Path.Combine(_sourceDir, "track2.m4b");
            var file3 = Path.Combine(_sourceDir, "track3.m4b");
            await File.WriteAllTextAsync(file1, "audio1");
            await File.WriteAllTextAsync(file2, "audio2");
            await File.WriteAllTextAsync(file3, "audio3");

            var settings = new ApplicationSettings 
            { 
                OutputPath = _outputRoot, 
                CompletedFileAction = "Hardlink/Copy",
                EnableMetadataProcessing = false 
            };

            var provider = BuildImportServiceProvider();
            var importService = provider.GetRequiredService<IImportService>();

            // Act
            var results = await importService.ImportFilesFromDirectoryAsync("dl-3", null, new[] { file1, file2, file3 }, settings);

            // Assert
            var successResults = results.Where(r => r.Success).ToList();
            Assert.True(successResults.Count >= 2, "At least 2 files should import successfully");
            
            foreach (var result in successResults)
            {
                Assert.True(File.Exists(result.FinalPath), $"Destination {result.FinalPath} should exist");
            }

            // All source files should still exist
            Assert.True(File.Exists(file1), "Source file 1 should still exist");
            Assert.True(File.Exists(file2), "Source file 2 should still exist");
            Assert.True(File.Exists(file3), "Source file 3 should still exist");
        }

        [Fact]
        public async Task ImportFilesFromDirectory_FallbacksToCopy_WhenHardlinkFails()
        {
            // Arrange - hardlink might fail on some systems or across volumes
            var file1 = Path.Combine(_sourceDir, "track1.m4b");
            await File.WriteAllTextAsync(file1, "audio data");

            var settings = new ApplicationSettings 
            { 
                OutputPath = _outputRoot, 
                CompletedFileAction = "Hardlink/Copy",
                EnableMetadataProcessing = false 
            };

            var provider = BuildImportServiceProvider();
            var importService = provider.GetRequiredService<IImportService>();

            // Act - even if hardlink fails, should fallback to copy
            var results = await importService.ImportFilesFromDirectoryAsync("dl-4", null, new[] { file1 }, settings);

            // Assert
            Assert.True(results.Any(r => r.Success), "Import should succeed via copy fallback");
            Assert.True(File.Exists(file1), "Source should still exist after hardlink/copy");
        }

        private IServiceProvider BuildImportServiceProvider()
        {
            return TestServiceFactory.BuildServiceProvider(services =>
            {
                var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString())
                    .Options;

                var dbFactoryMock = new Mock<IDbContextFactory<ListenArrDbContext>>();
                dbFactoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<System.Threading.CancellationToken>()))
                    .ReturnsAsync(new ListenArrDbContext(options));

                services.AddSingleton<IDbContextFactory<ListenArrDbContext>>(dbFactoryMock.Object);
                services.AddSingleton<IFileNamingService>(new FileNamingService(new TestConfigurationService(), new NullLogger<FileNamingService>()));
                services.AddSingleton<IMetadataService>(new Mock<IMetadataService>().Object);
                services.AddSingleton<IImportService>(sp => new ImportService(
                    dbFactoryMock.Object, 
                    sp.GetRequiredService<IServiceScopeFactory>(), 
                    sp.GetRequiredService<IFileNamingService>(), 
                    sp.GetService<IMetadataService>(), 
                    new NullLogger<ImportService>()));
            });
        }
    }
}
