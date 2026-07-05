/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using System.Text.Json;

namespace Listenarr.Tests.Features.Api.Features.Search
{
    // Regression tests for #525: the advanced-search inlined ASIN lookup
    // (StructuredSearchWorkflow.TryExecuteAsinSearchAsync) used to return whatever
    // ConvertMetadataToSearchResultAsync produced, including its literal "Unknown Title" fallback
    // when an ASIN was searched with no title (e.g. Library Import reading the ASIN from file tags).
    // The title guard now makes such a lookup return null and fall through to the validated unified
    // search path (IntelligentSearchAsync -> AsinSearchHandler) instead of surfacing a titleless result.
    public class SearchControllerAsinTitleGuardTests
    {
        private static (StructuredSearchWorkflow workflow, Mock<ISearchService> search, Mock<AudibleService> audible) Build()
        {
            var search = new Mock<ISearchService>();
            search
                .Setup(s => s.IntelligentSearchAsync(
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<string>(),
                    It.IsAny<bool>(),
                    It.IsAny<double>(),
                    It.IsAny<string>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<MetadataSearchResult>());

            using var httpClient = new System.Net.Http.HttpClient();
            var audible = new Mock<AudibleService>(httpClient, Mock.Of<ILogger<AudibleService>>());
            var metadata = new Mock<IAudiobookMetadataService>();
            var workflow = new StructuredSearchWorkflow(
                search.Object,
                Mock.Of<ILogger<StructuredSearchWorkflow>>(),
                audible.Object,
                metadata.Object,
                imageCacheService: Mock.Of<IImageCacheService>());
            return (workflow, search, audible);
        }

        private static JsonElement AdvancedAsinRequest(string asin)
            => JsonSerializer.SerializeToElement(new SearchRequest { Mode = SearchMode.Advanced, Asin = asin, Title = null });

        private void VerifyUnifiedSearchCalled(Mock<ISearchService> search, Times times)
            => search.Verify(s => s.IntelligentSearchAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<double>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()), times);

        [Fact]
        public async Task AdvancedAsinSearch_TitlelessMetadata_FallsThroughToUnifiedSearch()
        {
            var (workflow, search, audible) = Build();
            // Audible returns a product with a cover but NO title (the #525 trigger).
            audible
                .Setup(a => a.GetBookMetadataAsync("B00NOTITLE", It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>()))
                .ReturnsAsync(new AudibleBookResponse { Asin = "B00NOTITLE", Title = null, ImageUrl = "https://example.test/cover.jpg" });

            await workflow.ExecuteAsync(AdvancedAsinRequest("B00NOTITLE"), simplified: true, new Microsoft.AspNetCore.Http.DefaultHttpContext());

            // The titleless result is rejected -> flow falls through to the validated unified path.
            VerifyUnifiedSearchCalled(search, Times.AtLeastOnce());
        }

        [Fact]
        public async Task AdvancedAsinSearch_RealTitle_UsesInlinedResult_DoesNotFallThrough()
        {
            var (workflow, search, audible) = Build();
            audible
                .Setup(a => a.GetBookMetadataAsync("B00GOODASIN", It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>()))
                .ReturnsAsync(new AudibleBookResponse { Asin = "B00GOODASIN", Title = "Project Hail Mary" });

            await workflow.ExecuteAsync(AdvancedAsinRequest("B00GOODASIN"), simplified: true, new Microsoft.AspNetCore.Http.DefaultHttpContext());

            // A valid ASIN with a real title is handled by the inlined path and must NOT fall through.
            VerifyUnifiedSearchCalled(search, Times.Never());
        }
    }
}
