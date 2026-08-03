/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
using System.Text.Json;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Tests.Features.Api.Features.Library
{
    [Trait("Name", "LibraryController_BulkUpdateTests")]
    [Trait("Category", "LibraryController")]
    public sealed class LibraryController_BulkUpdateTests : BaseTests
    {
        [Fact]
        public async Task BulkDelete_DatabaseFailure_PreservesCachedImageAndDoesNotWriteDeletionHistory()
        {
            var audiobook = new Audiobook
            {
                Id = 8123,
                Title = "Delete Failure",
                Asin = "B000DELETE"
            };
            var repository = new Mock<IAudiobookRepository>(MockBehavior.Strict);
            repository.Setup(service => service.GetByIdAsync(audiobook.Id))
                .ReturnsAsync(audiobook);
            repository.Setup(service => service.DeleteByIdAsync(audiobook.Id))
                .ReturnsAsync(false);
            var imageCache = new Mock<IImageCacheService>(MockBehavior.Strict);
            var history = new Mock<IHistoryRepository>(MockBehavior.Strict);
            var fileSystem = new Mock<IFileSystem>(MockBehavior.Strict);
            Init(services => services
                .WithSingleton<IAudiobookRepository>(repository.Object)
                .WithSingleton<IImageCacheService>(imageCache.Object)
                .WithSingleton<IHistoryRepository>(history.Object)
                .WithSingleton<IFileSystem>(fileSystem.Object));

            var result = await _provider.GetRequiredService<LibraryController>()
                .BulkDeleteAudiobooks(new LibraryController.BulkDeleteRequest
                {
                    Ids = [audiobook.Id]
                });

            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            var json = JsonSerializer.Serialize(badRequest.Value);
            Assert.Contains(
                $"Failed to delete audiobook with ID {audiobook.Id}",
                json,
                StringComparison.Ordinal);
            repository.Verify(service => service.GetByIdAsync(audiobook.Id), Times.Once);
            repository.Verify(service => service.DeleteByIdAsync(audiobook.Id), Times.Once);
            repository.VerifyNoOtherCalls();
            imageCache.VerifyNoOtherCalls();
            history.VerifyNoOtherCalls();
            fileSystem.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task BulkUpdate_DatabaseFailure_DoesNotWriteHistoryOrExposeInternalError()
        {
            var audiobook = new Audiobook
            {
                Id = 8124,
                Title = "Update Failure",
                Monitored = false
            };
            var repository = new Mock<IAudiobookRepository>(MockBehavior.Strict);
            repository.Setup(service => service.GetByIdAsync(audiobook.Id))
                .ReturnsAsync(audiobook);
            repository.Setup(service => service.UpdateAsync(audiobook))
                .ThrowsAsync(new InvalidOperationException(
                    "C:\\private\\database worker secret"));
            var history = new Mock<IHistoryRepository>(MockBehavior.Strict);
            Init(services => services
                .WithSingleton<IAudiobookRepository>(repository.Object)
                .WithSingleton<IHistoryRepository>(history.Object));

            var result = await _provider.GetRequiredService<LibraryController>()
                .BulkUpdateAudiobooks(new LibraryController.BulkUpdateRequest
                {
                    Ids = [audiobook.Id],
                    Updates = new Dictionary<string, object>
                    {
                        ["monitored"] = true
                    }
                });

            var ok = Assert.IsType<OkObjectResult>(result);
            var json = JsonSerializer.Serialize(ok.Value);
            Assert.Contains("Unhandled bulk update error", json, StringComparison.Ordinal);
            Assert.DoesNotContain("worker secret", json, StringComparison.OrdinalIgnoreCase);
            repository.Verify(service => service.GetByIdAsync(audiobook.Id), Times.Once);
            repository.Verify(service => service.UpdateAsync(audiobook), Times.Once);
            repository.VerifyNoOtherCalls();
            history.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task BulkUpdate_UpdateReturnsFalse_DoesNotWriteHistoryOrReportMetadataUpdated()
        {
            var audiobook = new Audiobook
            {
                Id = 8126,
                Title = "Update Lost",
                Monitored = false
            };
            var repository = new Mock<IAudiobookRepository>(MockBehavior.Strict);
            repository.Setup(service => service.GetByIdAsync(audiobook.Id))
                .ReturnsAsync(audiobook);
            repository.Setup(service => service.UpdateAsync(audiobook))
                .ReturnsAsync(false);
            var history = new Mock<IHistoryRepository>(MockBehavior.Strict);
            Init(services => services
                .WithSingleton<IAudiobookRepository>(repository.Object)
                .WithSingleton<IHistoryRepository>(history.Object));

            var result = await _provider.GetRequiredService<LibraryController>()
                .BulkUpdateAudiobooks(new LibraryController.BulkUpdateRequest
                {
                    Ids = [audiobook.Id],
                    Updates = new Dictionary<string, object>
                    {
                        ["monitored"] = true
                    }
                });

            var ok = Assert.IsType<OkObjectResult>(result);
            var json = JsonSerializer.Serialize(ok.Value);
            Assert.Contains("disappeared", json, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"metadataUpdated\":false", json, StringComparison.OrdinalIgnoreCase);
            history.VerifyNoOtherCalls();
        }

        [Theory]
        [InlineData("\"false\"")]
        [InlineData("0")]
        [InlineData("null")]
        [InlineData("{}")]
        public async Task BulkUpdate_InvalidJsonMonitoredValue_DoesNotDisableMonitoring(
            string jsonValue)
        {
            var audiobook = new Audiobook
            {
                Id = 8127,
                Title = "Strict Boolean Update",
                Monitored = true
            };
            var repository = new Mock<IAudiobookRepository>(MockBehavior.Strict);
            repository.Setup(service => service.GetByIdAsync(audiobook.Id))
                .ReturnsAsync(audiobook);
            var history = new Mock<IHistoryRepository>(MockBehavior.Strict);
            Init(services => services
                .WithSingleton<IAudiobookRepository>(repository.Object)
                .WithSingleton<IHistoryRepository>(history.Object));
            using var document = JsonDocument.Parse(jsonValue);

            var result = await _provider.GetRequiredService<LibraryController>()
                .BulkUpdateAudiobooks(new LibraryController.BulkUpdateRequest
                {
                    Ids = [audiobook.Id],
                    Updates = new Dictionary<string, object>
                    {
                        ["monitored"] = document.RootElement.Clone()
                    }
                });

            var ok = Assert.IsType<OkObjectResult>(result);
            var json = JsonSerializer.Serialize(ok.Value);
            Assert.Contains("Invalid monitored value", json, StringComparison.Ordinal);
            Assert.Contains("\"success\":false", json, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"metadataUpdated\":false", json, StringComparison.OrdinalIgnoreCase);
            Assert.True(audiobook.Monitored);
            repository.Verify(service => service.GetByIdAsync(audiobook.Id), Times.Once);
            repository.VerifyNoOtherCalls();
            history.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task BulkUpdate_UndefinedPathChangeMode_IsRejectedBeforeMetadataMutation()
        {
            var audiobook = new Audiobook
            {
                Id = 8128,
                Title = "Undefined Path Mode",
                Monitored = true
            };
            var repository = new Mock<IAudiobookRepository>(MockBehavior.Strict);
            var history = new Mock<IHistoryRepository>(MockBehavior.Strict);
            Init(services => services
                .WithSingleton<IAudiobookRepository>(repository.Object)
                .WithSingleton<IHistoryRepository>(history.Object));

            var result = await _provider.GetRequiredService<LibraryController>()
                .BulkUpdateAudiobooks(new LibraryController.BulkUpdateRequest
                {
                    Ids = [audiobook.Id],
                    Updates = new Dictionary<string, object>
                    {
                        ["monitored"] = false
                    },
                    PathChange = new LibraryController.BulkPathChangeRequest
                    {
                        Mode = (LibraryController.BulkPathChangeMode)999,
                        DestinationRootOrPath = "ignored"
                    }
                });

            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            var json = JsonSerializer.Serialize(badRequest.Value);
            Assert.Contains("Invalid path change mode", json, StringComparison.Ordinal);
            Assert.True(audiobook.Monitored);
            repository.VerifyNoOtherCalls();
            history.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task BulkUpdate_HistoryFailure_DoesNotReverseCommittedMetadataUpdate()
        {
            var audiobook = new Audiobook
            {
                Id = 8125,
                Title = "History Failure",
                Monitored = false
            };
            var repository = new Mock<IAudiobookRepository>(MockBehavior.Strict);
            repository.Setup(service => service.GetByIdAsync(audiobook.Id))
                .ReturnsAsync(audiobook);
            repository.Setup(service => service.UpdateAsync(audiobook))
                .ReturnsAsync(true);
            var history = new Mock<IHistoryRepository>(MockBehavior.Strict);
            history.Setup(service => service.AddAsync(
                    It.IsAny<History>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("history unavailable"));
            Init(services => services
                .WithSingleton<IAudiobookRepository>(repository.Object)
                .WithSingleton<IHistoryRepository>(history.Object));

            var result = await _provider.GetRequiredService<LibraryController>()
                .BulkUpdateAudiobooks(new LibraryController.BulkUpdateRequest
                {
                    Ids = [audiobook.Id],
                    Updates = new Dictionary<string, object>
                    {
                        ["monitored"] = true
                    }
                });

            var ok = Assert.IsType<OkObjectResult>(result);
            var json = JsonSerializer.Serialize(ok.Value);
            Assert.Contains("\"success\":true", json, StringComparison.OrdinalIgnoreCase);
            Assert.True(audiobook.Monitored);
            repository.Verify(service => service.GetByIdAsync(audiobook.Id), Times.Once);
            repository.Verify(service => service.UpdateAsync(audiobook), Times.Once);
            history.Verify(service => service.AddAsync(
                It.Is<History>(entry =>
                    entry.AudiobookId == audiobook.Id
                    && entry.EventType == "Updated"),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task BulkUpdate_PhysicalPathChange_EnqueuesFromAuthoritativeSourceWithoutRewritingPaths()
        {
            MoveEnqueueCommand? captured = null;
            var jobId = Guid.NewGuid();
            var moveQueue = new Mock<IMoveQueueService>();
            moveQueue.Setup(service => service.EnqueueMoveAsync(
                    It.IsAny<MoveEnqueueCommand>(),
                    It.IsAny<CancellationToken>()))
                .Callback<MoveEnqueueCommand, CancellationToken>((command, _) => captured = command)
                .ReturnsAsync(jobId);
            Init(services => services.WithSingleton(moveQueue.Object));

            var destinationRoot = FileService.GetTempDirectory("bulk-physical-destination");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(destinationRoot)
                .WithFileNamingPattern("{Author}/{Title}")
                .Build());
            var sourceBasePath = FileService.GetTempDirectory("bulk-physical-source");
            var sourceFilePath = Path.Join(sourceBasePath, "book.m4b");
            await File.WriteAllTextAsync(sourceFilePath, "audio");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Physical Book",
                Authors = ["Physical Author"],
                Monitored = false,
                BasePath = sourceBasePath,
                FilePath = sourceFilePath
            });
            await AddTrackedFileAsync(audiobook, sourceFilePath);

            var actionResult = await _provider.GetRequiredService<LibraryController>()
                .BulkUpdateAudiobooks(new LibraryController.BulkUpdateRequest
                {
                    Ids = [audiobook.Id],
                    Updates = new Dictionary<string, object>
                    {
                        ["monitored"] = true
                    },
                    PathChange = new LibraryController.BulkPathChangeRequest
                    {
                        Mode = LibraryController.BulkPathChangeMode.Physical,
                        DestinationRootOrPath = destinationRoot,
                        DeleteEmptySource = false
                    }
                });

            var ok = Assert.IsType<OkObjectResult>(actionResult);
            var json = JsonSerializer.Serialize(ok.Value);
            using var document = JsonDocument.Parse(json);
            var result = Assert.Single(document.RootElement.GetProperty("results").EnumerateArray());
            Assert.True(result.GetProperty("success").GetBoolean());
            Assert.Equal(jobId, result.GetProperty("moveJobId").GetGuid());
            var expectedTarget = Path.Join(destinationRoot, "Physical Author", "Physical Book");
            Assert.Equal(
                FileUtils.NormalizeStoredPath(expectedTarget),
                result.GetProperty("resolvedDestination").GetString());

            var stored = await GetFreshAudiobookAsync(audiobook.Id);
            Assert.NotNull(stored);
            Assert.True(stored.Monitored);
            Assert.Equal(sourceBasePath, stored.BasePath);
            Assert.Equal(sourceFilePath, stored.FilePath);
            var storedFile = Assert.Single(stored.Files!);
            Assert.Equal(sourceFilePath, storedFile.Path);
            Assert.NotNull(captured);
            Assert.Equal(sourceBasePath, captured.SourcePath);
            Assert.Equal(FileUtils.NormalizeStoredPath(expectedTarget), captured.TargetPath);
            Assert.False(captured.DeleteEmptySource);
            moveQueue.Verify(service => service.EnqueueMoveAsync(
                It.IsAny<MoveEnqueueCommand>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [LinuxFact]
        public async Task BulkUpdate_PhysicalPathChange_PreservesTrailingSpaceInUnixDestinationRoot()
        {
            MoveEnqueueCommand? captured = null;
            var jobId = Guid.NewGuid();
            var moveQueue = new Mock<IMoveQueueService>();
            moveQueue.Setup(service => service.EnqueueMoveAsync(
                    It.IsAny<MoveEnqueueCommand>(),
                    It.IsAny<CancellationToken>()))
                .Callback<MoveEnqueueCommand, CancellationToken>((command, _) => captured = command)
                .ReturnsAsync(jobId);
            Init(services => services.WithSingleton(moveQueue.Object));

            var configuredRoot = FileService.GetTempDirectory("bulk-unix-byte-root");
            var destinationRoot = Path.Join(configuredRoot, "Library ");
            Directory.CreateDirectory(destinationRoot);
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(configuredRoot)
                .WithFileNamingPattern("{Author}/{Title}")
                .Build());
            var sourceBasePath = FileService.GetTempDirectory("bulk-unix-byte-source");
            var sourceFilePath = Path.Join(sourceBasePath, "book.m4b");
            await File.WriteAllTextAsync(sourceFilePath, "audio");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Byte Book",
                Authors = ["Byte Author"],
                BasePath = sourceBasePath,
                FilePath = sourceFilePath
            });
            await AddTrackedFileAsync(audiobook, sourceFilePath);

            var actionResult = await _provider.GetRequiredService<LibraryController>()
                .BulkUpdateAudiobooks(new LibraryController.BulkUpdateRequest
                {
                    Ids = [audiobook.Id],
                    PathChange = new LibraryController.BulkPathChangeRequest
                    {
                        Mode = LibraryController.BulkPathChangeMode.Physical,
                        DestinationRootOrPath = destinationRoot,
                        DeleteEmptySource = false
                    }
                });

            var ok = Assert.IsType<OkObjectResult>(actionResult);
            var json = JsonSerializer.Serialize(ok.Value);
            using var document = JsonDocument.Parse(json);
            var result = Assert.Single(document.RootElement.GetProperty("results").EnumerateArray());
            Assert.True(result.GetProperty("success").GetBoolean());
            Assert.NotNull(captured);
            var expectedTarget = Path.Join(destinationRoot, "Byte Author", "Byte Book");
            Assert.Equal(expectedTarget, captured.TargetPath);
            Assert.Contains($"{Path.DirectorySeparatorChar}Library {Path.DirectorySeparatorChar}", captured.TargetPath);
            Assert.Equal(expectedTarget, result.GetProperty("resolvedDestination").GetString());
        }

        [Fact]
        public async Task BulkUpdate_PhysicalPathChange_WithoutMetadata_EnqueuesAndReportsNoMetadataUpdate()
        {
            var jobId = Guid.NewGuid();
            var moveQueue = new Mock<IMoveQueueService>();
            moveQueue.Setup(service => service.EnqueueMoveAsync(
                    It.IsAny<MoveEnqueueCommand>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(jobId);
            Init(services => services.WithSingleton(moveQueue.Object));

            var destinationRoot = FileService.GetTempDirectory("bulk-path-only-destination");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(destinationRoot)
                .WithFileNamingPattern("{Author}/{Title}")
                .Build());
            var sourceBasePath = FileService.GetTempDirectory("bulk-path-only-source");
            var sourceFilePath = Path.Join(sourceBasePath, "book.m4b");
            await File.WriteAllTextAsync(sourceFilePath, "audio");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Path Only Book",
                Authors = ["Path Only Author"],
                BasePath = sourceBasePath,
                FilePath = sourceFilePath
            });
            await AddTrackedFileAsync(audiobook, sourceFilePath);

            var actionResult = await _provider.GetRequiredService<LibraryController>()
                .BulkUpdateAudiobooks(new LibraryController.BulkUpdateRequest
                {
                    Ids = [audiobook.Id],
                    PathChange = new LibraryController.BulkPathChangeRequest
                    {
                        Mode = LibraryController.BulkPathChangeMode.Physical,
                        DestinationRootOrPath = destinationRoot
                    }
                });

            var ok = Assert.IsType<OkObjectResult>(actionResult);
            var json = JsonSerializer.Serialize(ok.Value);
            using var document = JsonDocument.Parse(json);
            var result = Assert.Single(document.RootElement.GetProperty("results").EnumerateArray());
            Assert.True(result.GetProperty("success").GetBoolean());
            Assert.False(result.GetProperty("metadataUpdated").GetBoolean());
            Assert.Equal("enqueued", result.GetProperty("pathChangeOutcome").GetString());
            Assert.Equal(jobId, result.GetProperty("moveJobId").GetGuid());
            moveQueue.Verify(service => service.EnqueueMoveAsync(
                It.IsAny<MoveEnqueueCommand>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task BulkUpdate_PhysicalPathChange_EmptyJobIdFailsClosed()
        {
            var moveQueue = new Mock<IMoveQueueService>();
            moveQueue.Setup(service => service.EnqueueMoveAsync(
                    It.IsAny<MoveEnqueueCommand>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(Guid.Empty);
            Init(services => services.WithSingleton(moveQueue.Object));

            var destinationRoot = FileService.GetTempDirectory("bulk-empty-job-destination");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(destinationRoot)
                .WithFileNamingPattern("{Author}/{Title}")
                .Build());
            var sourceBasePath = FileService.GetTempDirectory("bulk-empty-job-source");
            var sourceFilePath = Path.Join(sourceBasePath, "book.m4b");
            await File.WriteAllTextAsync(sourceFilePath, "audio");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Empty Job Book",
                Authors = ["Empty Job Author"],
                BasePath = sourceBasePath,
                FilePath = sourceFilePath
            });
            await AddTrackedFileAsync(audiobook, sourceFilePath);

            var actionResult = await _provider.GetRequiredService<LibraryController>()
                .BulkUpdateAudiobooks(new LibraryController.BulkUpdateRequest
                {
                    Ids = [audiobook.Id],
                    PathChange = new LibraryController.BulkPathChangeRequest
                    {
                        Mode = LibraryController.BulkPathChangeMode.Physical,
                        DestinationRootOrPath = destinationRoot
                    }
                });

            var ok = Assert.IsType<OkObjectResult>(actionResult);
            var json = JsonSerializer.Serialize(ok.Value);
            using var document = JsonDocument.Parse(json);
            var result = Assert.Single(document.RootElement.GetProperty("results").EnumerateArray());
            Assert.False(result.GetProperty("success").GetBoolean());
            Assert.Equal("failed", result.GetProperty("pathChangeOutcome").GetString());
            Assert.Equal(JsonValueKind.Null, result.GetProperty("moveJobId").ValueKind);
            Assert.Contains(
                result.GetProperty("errors").EnumerateArray(),
                error => error.GetString() == "The server did not return a durable move job ID.");
        }

        [Fact]
        public async Task BulkUpdate_PhysicalPathChange_EnqueueFailureIsReturnedPerItem()
        {
            var moveQueue = new Mock<IMoveQueueService>();
            moveQueue.Setup(service => service.EnqueueMoveAsync(
                    It.IsAny<MoveEnqueueCommand>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new IOException("queue unavailable"));
            Init(services => services.WithSingleton(moveQueue.Object));

            var destinationRoot = FileService.GetTempDirectory("bulk-enqueue-failure-destination");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(destinationRoot)
                .WithFileNamingPattern("{Author}/{Title}")
                .Build());
            var sourceBasePath = FileService.GetTempDirectory("bulk-enqueue-failure-source");
            var sourceFilePath = Path.Join(sourceBasePath, "book.m4b");
            await File.WriteAllTextAsync(sourceFilePath, "audio");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Enqueue Failure Book",
                Authors = ["Enqueue Failure Author"],
                BasePath = sourceBasePath,
                FilePath = sourceFilePath
            });
            await AddTrackedFileAsync(audiobook, sourceFilePath);

            var actionResult = await _provider.GetRequiredService<LibraryController>()
                .BulkUpdateAudiobooks(new LibraryController.BulkUpdateRequest
                {
                    Ids = [audiobook.Id],
                    PathChange = new LibraryController.BulkPathChangeRequest
                    {
                        Mode = LibraryController.BulkPathChangeMode.Physical,
                        DestinationRootOrPath = destinationRoot
                    }
                });

            var ok = Assert.IsType<OkObjectResult>(actionResult);
            var json = JsonSerializer.Serialize(ok.Value);
            using var document = JsonDocument.Parse(json);
            var result = Assert.Single(document.RootElement.GetProperty("results").EnumerateArray());
            Assert.False(result.GetProperty("success").GetBoolean());
            Assert.Equal("failed", result.GetProperty("pathChangeOutcome").GetString());
            Assert.Equal(JsonValueKind.Null, result.GetProperty("moveJobId").ValueKind);
            Assert.Contains(
                result.GetProperty("errors").EnumerateArray(),
                error => error.GetString()?.Contains("Failed to enqueue move job", StringComparison.Ordinal) == true);

            var stored = await GetFreshAudiobookAsync(audiobook.Id);
            Assert.NotNull(stored);
            Assert.Equal(sourceBasePath, stored.BasePath);
            Assert.Equal(sourceFilePath, stored.FilePath);
        }

        [Fact]
        public async Task BulkUpdate_TypedMetadataOnlyFailure_DoesNotReportOverallSuccess()
        {
            var configuredRoot = FileService.GetTempDirectory("bulk-typed-configured-root");
            var outsideRoot = FileService.GetTempDirectory("bulk-typed-outside-root");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(configuredRoot)
                .WithFileNamingPattern("{Author}/{Title}")
                .Build());
            var originalBasePath = Path.Join(configuredRoot, "Existing", "Book");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Typed Metadata Only",
                Authors = ["Author"],
                Monitored = false,
                BasePath = originalBasePath
            });

            var actionResult = await _provider.GetRequiredService<LibraryController>()
                .BulkUpdateAudiobooks(new LibraryController.BulkUpdateRequest
                {
                    Ids = [audiobook.Id],
                    Updates = new Dictionary<string, object>
                    {
                        ["monitored"] = true
                    },
                    PathChange = new LibraryController.BulkPathChangeRequest
                    {
                        Mode = LibraryController.BulkPathChangeMode.MetadataOnly,
                        DestinationRootOrPath = outsideRoot
                    }
                });

            var ok = Assert.IsType<OkObjectResult>(actionResult);
            var json = JsonSerializer.Serialize(ok.Value);
            using var document = JsonDocument.Parse(json);
            var result = Assert.Single(document.RootElement.GetProperty("results").EnumerateArray());
            Assert.False(result.GetProperty("success").GetBoolean());
            Assert.True(result.GetProperty("metadataUpdated").GetBoolean());
            Assert.Equal("failed", result.GetProperty("pathChangeOutcome").GetString());
            Assert.Contains(
                result.GetProperty("errors").EnumerateArray(),
                error => error.GetString()?.Contains(
                    "configured root folder or output path",
                    StringComparison.OrdinalIgnoreCase) == true);

            var stored = await GetFreshAudiobookAsync(audiobook.Id);
            Assert.NotNull(stored);
            Assert.True(stored.Monitored);
            Assert.Equal(originalBasePath, stored.BasePath);
        }

        [Fact]
        public async Task BulkUpdate_PhysicalPathChange_DoesNotEnqueueWhenRequestedMetadataIsInvalid()
        {
            var moveQueue = new Mock<IMoveQueueService>();
            moveQueue.Setup(service => service.EnqueueMoveAsync(
                    It.IsAny<MoveEnqueueCommand>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(Guid.NewGuid());
            Init(services => services.WithSingleton(moveQueue.Object));

            var destinationRoot = FileService.GetTempDirectory("bulk-invalid-metadata-destination");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(destinationRoot)
                .WithFileNamingPattern("{Author}/{Title}")
                .Build());
            var sourceBasePath = FileService.GetTempDirectory("bulk-invalid-metadata-source");
            var sourceFilePath = Path.Join(sourceBasePath, "book.m4b");
            await File.WriteAllTextAsync(sourceFilePath, "audio");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Invalid Metadata Book",
                Authors = ["Invalid Metadata Author"],
                Monitored = false,
                BasePath = sourceBasePath,
                FilePath = sourceFilePath
            });
            await AddTrackedFileAsync(audiobook, sourceFilePath);

            var actionResult = await _provider.GetRequiredService<LibraryController>()
                .BulkUpdateAudiobooks(new LibraryController.BulkUpdateRequest
                {
                    Ids = [audiobook.Id],
                    Updates = new Dictionary<string, object>
                    {
                        ["monitored"] = "not-a-boolean"
                    },
                    PathChange = new LibraryController.BulkPathChangeRequest
                    {
                        Mode = LibraryController.BulkPathChangeMode.Physical,
                        DestinationRootOrPath = destinationRoot
                    }
                });

            var ok = Assert.IsType<OkObjectResult>(actionResult);
            var json = JsonSerializer.Serialize(ok.Value);
            using var document = JsonDocument.Parse(json);
            var result = Assert.Single(document.RootElement.GetProperty("results").EnumerateArray());
            Assert.False(result.GetProperty("success").GetBoolean());
            Assert.Contains(
                result.GetProperty("errors").EnumerateArray(),
                error => error.GetString()?.Contains("Invalid monitored value", StringComparison.Ordinal) == true);
            moveQueue.Verify(service => service.EnqueueMoveAsync(
                It.IsAny<MoveEnqueueCommand>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task BulkUpdate_InvalidRootFolderStillAppliesValidMetadataUpdates()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Partial Bulk Update",
                Monitored = false
            });

            var actionResult = await controller.BulkUpdateAudiobooks(new LibraryController.BulkUpdateRequest
            {
                Ids = [audiobook.Id],
                Updates = new Dictionary<string, object>
                {
                    ["monitored"] = true,
                    ["rootFolder"] = "   "
                }
            });

            var ok = Assert.IsType<OkObjectResult>(actionResult);
            var json = JsonSerializer.Serialize(ok.Value);
            using var document = JsonDocument.Parse(json);
            var result = Assert.Single(document.RootElement.GetProperty("results").EnumerateArray());
            Assert.True(result.GetProperty("success").GetBoolean());
            Assert.NotEmpty(result.GetProperty("errors").EnumerateArray());

            var stored = await GetFreshAudiobookAsync(audiobook.Id);
            Assert.NotNull(stored);
            Assert.True(stored.Monitored);
        }

        [Fact]
        public async Task BulkUpdate_CustomRootOutsideConfiguredBoundaries_IsRejectedWithoutPathRewrite()
        {
            var configuredRoot = FileService.GetTempDirectory("bulk-configured-root");
            var outsideRoot = FileService.GetTempDirectory("bulk-outside-root");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(configuredRoot)
                .WithFileNamingPattern("{Author}/{Title}")
                .Build());
            var originalBasePath = Path.Join(configuredRoot, "Existing", "Book");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Boundary Test",
                Authors = ["Author"],
                Monitored = false,
                BasePath = originalBasePath
            });

            var actionResult = await _provider.GetRequiredService<LibraryController>()
                .BulkUpdateAudiobooks(new LibraryController.BulkUpdateRequest
                {
                    Ids = [audiobook.Id],
                    Updates = new Dictionary<string, object>
                    {
                        ["monitored"] = true,
                        ["rootFolder"] = outsideRoot
                    }
                });

            var ok = Assert.IsType<OkObjectResult>(actionResult);
            var json = JsonSerializer.Serialize(ok.Value);
            using var document = JsonDocument.Parse(json);
            var result = Assert.Single(document.RootElement.GetProperty("results").EnumerateArray());
            Assert.True(result.GetProperty("success").GetBoolean());
            Assert.Contains(
                result.GetProperty("errors").EnumerateArray(),
                error => error.GetString()?.Contains(
                    "configured root folder or output path",
                    StringComparison.OrdinalIgnoreCase) == true);

            var stored = await GetFreshAudiobookAsync(audiobook.Id);
            Assert.NotNull(stored);
            Assert.True(stored.Monitored);
            Assert.Equal(originalBasePath, stored.BasePath);
        }

        [Fact]
        public async Task BulkUpdate_ApplyRootMonitoredQuality_ReturnsPerIdResultsAndPersistsChanges()
        {
            // Arrange
            var controller = _provider.GetRequiredService<LibraryController>();

            var tempRoot = FileService.GetTempDirectory("bulk-update");
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(tempRoot)
                .WithFileNamingPattern("{Author}/{Title}")
                .Build());

            await _qualityProfileRepository.AddAsync(new QualityProfile
            {
                Id = 42,
                Name = "Test Profile",
                Qualities = new List<QualityDefinition>(),
                PreferredFormats = new List<string>(),
                PreferredLanguages = new List<string>(),
                MustContain = new List<string>(),
                MustNotContain = new List<string>()
            });

            var sourceBasePath = Path.Join(
                FileService.GetTempPath(),
                $"bulk-update-source-{Guid.NewGuid():N}");
            var sourceFilePath = Path.Join(sourceBasePath, "book.m4b");
            var sourceImagePath = Path.Join(sourceBasePath, "cover.jpg");
            var a1 = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Book A",
                Authors = new List<string> { "Author A" },
                Monitored = false,
                QualityProfileId = null,
                BasePath = sourceBasePath,
                FilePath = sourceFilePath,
                ImageUrl = sourceImagePath,
                Files =
                [
                    new AudiobookFile
                    {
                        Path = sourceFilePath
                    }
                ]
            });

            await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Book B",
                Authors = new List<string> { "Author B" },
                Monitored = false,
                QualityProfileId = null
            });

            var request = new LibraryController.BulkUpdateRequest
            {
                Ids = new List<int> { a1.Id, 999999 },
                Updates = new Dictionary<string, object>
                {
                    { "monitored", true },
                    { "qualityProfileId", 42 },
                    { "rootFolder", tempRoot }
                }
            };

            // Act
            var actionResult = await controller.BulkUpdateAudiobooks(request);

            // Assert
            var ok = Assert.IsType<OkObjectResult>(actionResult);

            var json = JsonSerializer.Serialize(ok.Value);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.True(root.TryGetProperty("results", out var resultsElem));
            Assert.Equal(2, resultsElem.GetArrayLength());

            var first = resultsElem[0];
            Assert.Equal(a1.Id, first.GetProperty("id").GetInt32());
            Assert.True(first.GetProperty("success").GetBoolean());
            Assert.True(first.GetProperty("errors").GetArrayLength() == 0);

            var second = resultsElem[1];
            Assert.Equal(999999, second.GetProperty("id").GetInt32());
            Assert.False(second.GetProperty("success").GetBoolean());
            Assert.True(second.GetProperty("errors").GetArrayLength() >= 1);

            var storedA1 = await GetFreshAudiobookAsync(a1.Id);
            Assert.NotNull(storedA1);
            Assert.True(storedA1.Monitored);
            Assert.Equal(42, storedA1.QualityProfileId);
            Assert.False(string.IsNullOrWhiteSpace(storedA1.BasePath));
            Assert.StartsWith(FileUtils.NormalizeStoredPath(tempRoot), storedA1.BasePath);
            Assert.Contains("Author A", storedA1.BasePath);
            Assert.Contains("Book A", storedA1.BasePath);
            Assert.Equal(Path.Join(storedA1.BasePath, "book.m4b"), storedA1.FilePath);
            Assert.Equal(Path.Join(storedA1.BasePath, "cover.jpg"), storedA1.ImageUrl);
            var storedFile = Assert.Single(storedA1.Files!);
            Assert.Equal(Path.Join(storedA1.BasePath, "book.m4b"), storedFile.Path);

            var histories = await _historyRepository.GetByAudiobookIdAsync(a1.Id);
            Assert.True(histories.Count >= 1);
        }

        private async Task AddTrackedFileAsync(Audiobook audiobook, string filePath)
        {
            var resolution = await _provider
                .GetRequiredService<IFileSystemSemanticsResolver>()
                .ResolveAsync(filePath, FileSystemCaseSensitivityMode.Auto);
            Assert.Equal(PathIdentityState.Valid, resolution.State);
            var identity = AudiobookFilePathIdentity.CreateValid(
                filePath,
                resolution.Semantics,
                FileSystemCaseSensitivityMode.Auto,
                resolution.BoundaryPath);
            var tracked = new AudiobookFile
            {
                AudiobookId = audiobook.Id,
                Audiobook = audiobook,
                Path = filePath
            };
            tracked.ApplyPathIdentity(filePath, identity);
            using (var parent = PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                Path.GetDirectoryName(filePath)!,
                createMissing: false))
            using (var file = parent.OpenExistingFileForStableRead(Path.GetFileName(filePath)))
            {
                tracked.ApplyPhysicalObjectIdentity(
                    file.GetObjectIdentity(),
                    DateTime.UtcNow);
            }
            await _audiobookFileRepository.AddAsync(tracked);
        }

        private async Task<Audiobook?> GetFreshAudiobookAsync(int id)
        {
            using var scope = _provider.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
            return await repository.GetByIdAsync(id);
        }
    }
}
