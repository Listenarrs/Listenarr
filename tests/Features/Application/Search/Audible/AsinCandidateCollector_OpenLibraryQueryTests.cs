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
using Microsoft.Extensions.Logging.Abstractions;

using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Search.Audible
{
    /// <summary>
    /// The composed search query carries "AUTHOR:"/"TITLE:" prefixes. OpenLibrary's normalizer
    /// strips punctuation, so those prefixes become literal search tokens and match nothing -
    /// which silently disables the OpenLibrary fallback for books with no Audible edition.
    /// </summary>
    [Trait("Area", "Search")]
    [Trait("Name", "AsinCandidateCollector_OpenLibraryQueryTests")]
    [Trait("Category", "AsinCandidateCollector")]
    public class AsinCandidateCollector_OpenLibraryQueryTests : BaseTests
    {
        private static (AsinCandidateCollector Collector, Mock<IOpenLibraryService> Service) Create()
        {
            var openLibrary = new Mock<IOpenLibraryService>();
            openLibrary
                .Setup(s => s.SearchBooksAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int>()))
                .ReturnsAsync(new OpenLibrarySearchResponse());

            var collector = new AsinCandidateCollector(
                NullLogger<AsinCandidateCollector>.Instance,
                openLibrary.Object,
                new MetadataConverters(
                    Mock.Of<IImageCacheService>(),
                    NullLogger<MetadataConverters>.Instance),
                new SearchProgressReporter(null, NullLogger<SearchProgressReporter>.Instance));

            return (collector, openLibrary);
        }

        [Fact]
        public async Task ParsedFieldsArePreferredOverTheComposedQuery()
        {
            var (collector, service) = Create();

            await collector.CollectCandidatesAsync(
                "AUTHOR:Abelson TITLE:Structure and Interpretation of Computer Programs",
                skipOpenLibrary: false,
                ct: default,
                title: "Structure and Interpretation of Computer Programs",
                author: "Abelson");

            service.Verify(
                s => s.SearchBooksAsync(
                    "Structure and Interpretation of Computer Programs",
                    "Abelson",
                    It.IsAny<int>()),
                Times.Once);
        }

        [Fact]
        public async Task WithoutParsedFields_PrefixesAreStrippedFromTheQuery()
        {
            var (collector, service) = Create();

            await collector.CollectCandidatesAsync(
                "AUTHOR:Abelson TITLE:Structure and Interpretation of Computer Programs",
                skipOpenLibrary: false);

            service.Verify(
                s => s.SearchBooksAsync(
                    It.Is<string>(q =>
                        !q.Contains("AUTHOR:", StringComparison.OrdinalIgnoreCase)
                        && !q.Contains("TITLE:", StringComparison.OrdinalIgnoreCase)
                        && q.Contains("Structure and Interpretation of Computer Programs")),
                    It.IsAny<string?>(),
                    It.IsAny<int>()),
                Times.Once);
        }

        [Fact]
        public async Task PlainQueryIsPassedThroughUnchanged()
        {
            var (collector, service) = Create();

            await collector.CollectCandidatesAsync("Dilation Sleep", skipOpenLibrary: false);

            service.Verify(
                s => s.SearchBooksAsync("Dilation Sleep", It.IsAny<string?>(), It.IsAny<int>()),
                Times.Once);
        }

        [Fact]
        public async Task OpenLibraryIsNotQueriedWhenDisabled()
        {
            var (collector, service) = Create();

            await collector.CollectCandidatesAsync("Dilation Sleep", skipOpenLibrary: true);

            service.Verify(
                s => s.SearchBooksAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int>()),
                Times.Never);
        }
    }
}
