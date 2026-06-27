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

using Listenarr.Tests.Builders;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Application.Search.DomainRotation
{
    [Trait("Area", "Search")]
    [Trait("Name", "IndexerDomainRotationServiceTests")]
    [Trait("Category", "DomainRotation")]
    public class IndexerDomainRotationServiceTests
    {
        private readonly Mock<IApplicationSettingsRepository> _settingsRepo;
        private readonly Mock<IIndexerRepository> _indexerRepo;
        private readonly Mock<IDomainReachabilityChecker> _checker;
        private readonly IndexerDomainRotationService _sut;

        private readonly ApplicationSettings _defaultSettings = new()
        {
            EnableIndexerDomainRotation = true,
        };

        public IndexerDomainRotationServiceTests()
        {
            _settingsRepo = new Mock<IApplicationSettingsRepository>();
            _indexerRepo = new Mock<IIndexerRepository>();
            _checker = new Mock<IDomainReachabilityChecker>();

            _settingsRepo
                .Setup(r => r.GetAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(_defaultSettings);

            _sut = new IndexerDomainRotationService(
                _settingsRepo.Object,
                _indexerRepo.Object,
                _checker.Object,
                NullLogger<IndexerDomainRotationService>.Instance);
        }

        [Fact]
        [Trait("Scenario", "CurrentHostReachable_NoChange")]
        public async Task EnsureReachable_CurrentHostReachable_ReturnsNoChange()
        {
            // Given
            var indexer = new IndexerBuilder()
                .WithImplementation("AudioBookBay")
                .WithUrl("https://audiobookbay.se/")
                .WithEnabled()
                .Build();

            _checker
                .Setup(c => c.IsReachableAsync("https", "audiobookbay.se", It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            // When
            var result = await _sut.EnsureReachableAsync(indexer, CancellationToken.None);

            // Then
            Assert.False(result.Changed);
            Assert.Null(result.NewHost);
            _indexerRepo.Verify(r => r.UpdateAsync(It.IsAny<Indexer>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        [Trait("Scenario", "CurrentUnreachable_SecondCandidateReachable_Rotates")]
        public async Task EnsureReachable_CurrentUnreachable_RotatesToFirstWorkingMirror()
        {
            // Given
            var indexer = new IndexerBuilder()
                .WithImplementation("AudioBookBay")
                .WithUrl("https://audiobookbay.se/")
                .WithEnabled()
                .Build();

            // current host down
            _checker
                .Setup(c => c.IsReachableAsync("https", "audiobookbay.se", It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            // first seed (audiobookbay.lu) up
            _checker
                .Setup(c => c.IsReachableAsync("https", "audiobookbay.lu", It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            // When
            var result = await _sut.EnsureReachableAsync(indexer, CancellationToken.None);

            // Then
            Assert.True(result.Changed);
            Assert.Equal("audiobookbay.lu", result.NewHost);
            Assert.Contains("audiobookbay.lu", indexer.Url);
            _indexerRepo.Verify(r => r.UpdateAsync(indexer, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        [Trait("Scenario", "AllCandidatesUnreachable_NoChange")]
        public async Task EnsureReachable_AllCandidatesUnreachable_ReturnsNoChange()
        {
            // Given
            var indexer = new IndexerBuilder()
                .WithImplementation("AnnasArchive")
                .WithUrl("https://annas-archive.org/")
                .WithEnabled()
                .Build();

            _checker
                .Setup(c => c.IsReachableAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);

            // When
            var result = await _sut.EnsureReachableAsync(indexer, CancellationToken.None);

            // Then
            Assert.False(result.Changed);
            Assert.Null(result.NewHost);
            Assert.Contains("no working mirror", result.Reason);
            _indexerRepo.Verify(r => r.UpdateAsync(It.IsAny<Indexer>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        [Trait("Scenario", "RotationDisabledInSettings_NoOp")]
        public async Task EnsureReachable_RotationDisabled_ReturnsNoOp()
        {
            // Given
            _settingsRepo
                .Setup(r => r.GetAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ApplicationSettings { EnableIndexerDomainRotation = false });

            var indexer = new IndexerBuilder()
                .WithImplementation("AudioBookBay")
                .WithUrl("https://audiobookbay.se/")
                .WithEnabled()
                .Build();

            // When
            var result = await _sut.EnsureReachableAsync(indexer, CancellationToken.None);

            // Then
            Assert.False(result.Changed);
            _checker.Verify(
                c => c.IsReachableAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        [Trait("Scenario", "NonCatalogImplementation_NoOp")]
        public async Task EnsureReachable_NonCatalogImplementation_ReturnsNoOp()
        {
            // Given
            var indexer = new IndexerBuilder()
                .WithImplementation("Custom")
                .WithUrl("https://example.com/")
                .WithEnabled()
                .Build();

            // When
            var result = await _sut.EnsureReachableAsync(indexer, CancellationToken.None);

            // Then
            Assert.False(result.Changed);
            _checker.Verify(
                c => c.IsReachableAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        [Trait("Scenario", "IndexerDisabled_NoOp")]
        public async Task EnsureReachable_IndexerDisabled_ReturnsNoOp()
        {
            // Given
            var indexer = new IndexerBuilder()
                .WithImplementation("AudioBookBay")
                .WithUrl("https://audiobookbay.se/")
                .WithDisabled()
                .Build();

            // When
            var result = await _sut.EnsureReachableAsync(indexer, CancellationToken.None);

            // Then
            Assert.False(result.Changed);
            _checker.Verify(
                c => c.IsReachableAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        [Trait("Scenario", "UserOverridesIncludedInCandidates")]
        public async Task EnsureReachable_UserOverridesIncluded_TriesOverrideHost()
        {
            // Given — current down, all seeds down, override host up
            _settingsRepo
                .Setup(r => r.GetAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ApplicationSettings
                {
                    EnableIndexerDomainRotation = true,
                    IndexerMirrorOverridesJson = """{"AnnasArchive": ["annas-archive.xyz"]}""",
                });

            var indexer = new IndexerBuilder()
                .WithImplementation("AnnasArchive")
                .WithUrl("https://annas-archive.org/")
                .WithEnabled()
                .Build();

            _checker
                .Setup(c => c.IsReachableAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            _checker
                .Setup(c => c.IsReachableAsync("https", "annas-archive.xyz", It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            // When
            var result = await _sut.EnsureReachableAsync(indexer, CancellationToken.None);

            // Then
            Assert.True(result.Changed);
            Assert.Equal("annas-archive.xyz", result.NewHost);
        }

        [Fact]
        [Trait("Scenario", "CandidateResolution_DeduplicatesCurrentHost")]
        public async Task EnsureReachable_CurrentHostInSeeds_NotTestedTwice()
        {
            // Given — audiobookbay.lu is the current host AND appears in seeds
            var indexer = new IndexerBuilder()
                .WithImplementation("AudioBookBay")
                .WithUrl("https://audiobookbay.lu/")
                .WithEnabled()
                .Build();

            _checker
                .Setup(c => c.IsReachableAsync("https", "audiobookbay.lu", It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            // second seed up
            _checker
                .Setup(c => c.IsReachableAsync("https", "audiobookbay.se", It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            // When
            var result = await _sut.EnsureReachableAsync(indexer, CancellationToken.None);

            // Then
            Assert.True(result.Changed);
            // audiobookbay.lu tested exactly once (as current), not again as a candidate
            _checker.Verify(
                c => c.IsReachableAsync("https", "audiobookbay.lu", It.IsAny<CancellationToken>()),
                Times.Once);
        }
    }
}
