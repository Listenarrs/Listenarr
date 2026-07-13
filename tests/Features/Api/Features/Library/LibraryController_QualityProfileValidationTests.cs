/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Microsoft.AspNetCore.Mvc;
using Listenarr.Tests.Common;
using Listenarr.Tests.Builders;

namespace Listenarr.Tests.Features.Api.Features.Library
{
    public class LibraryController_QualityProfileValidationTests : BaseTests
    {
        private readonly Mock<IImageCacheService> _imageCacheServiceMock = new();
        private string _tempRoot = null!;
        private int _defaultProfileId;

        public override async Task InitializeAsync()
        {
            _imageCacheServiceMock
                .Setup(m => m.MoveToLibraryStorageAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync((string?)null);

            Init(services => services.WithSingleton(_imageCacheServiceMock.Object));

            _tempRoot = FileService.GetTempDirectory("listenarr-test");

            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithFolderNamingPattern("{Author}")
                .WithFileNamingPattern("{Title}")
                .Build());

            await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithIsDefault()
                .WithPath(_tempRoot)
                .Build());

            var defaultProfile = await _qualityProfileRepository.AddAsync(QualityProfile.CreateDefault());
            _defaultProfileId = defaultProfile.Id;
        }

        [Fact]
        public async Task AddToLibrary_WithBogusQualityProfileId_ReturnsBadRequestAndPersistsNothing()
        {
            var controller = _provider.GetRequiredService<LibraryController>();

            var request = new LibraryController.AddToLibraryRequest
            {
                Metadata = new AudibleBookMetadata
                {
                    Title = "Bogus Profile Title",
                    Author = "Some Author"
                },
                Monitored = true,
                QualityProfileId = 999999
            };

            // Act
            var actionResult = await controller.AddToLibrary(request);

            // Assert
            Assert.IsType<BadRequestObjectResult>(actionResult);
            Assert.Empty(await _audiobookRepository.GetAllAsync());
        }

        [Fact]
        public async Task AddToLibrary_WithNoQualityProfileId_AssignsDefaultAndPersists()
        {
            var controller = _provider.GetRequiredService<LibraryController>();

            var request = new LibraryController.AddToLibraryRequest
            {
                Metadata = new AudibleBookMetadata
                {
                    Title = "Default Profile Title",
                    Author = "Some Author"
                },
                Monitored = true
            };

            // Act
            var actionResult = await controller.AddToLibrary(request);

            // Assert
            Assert.IsType<OkObjectResult>(actionResult);
            var stored = Assert.Single(await _audiobookRepository.GetAllAsync());
            Assert.Equal(_defaultProfileId, stored.QualityProfileId);
        }
    }
}
