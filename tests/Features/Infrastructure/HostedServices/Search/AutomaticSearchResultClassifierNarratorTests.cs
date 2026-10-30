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
 */
using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.HostedServices.Search
{
    [Trait("Area", "Search")]
    [Trait("Name", nameof(AutomaticSearchResultClassifierNarratorTests))]
    [Trait("Category", "Infrastructure")]
    public class AutomaticSearchResultClassifierNarratorTests : BaseTests
    {
        private static AutomaticSearchResultClassifier CreateClassifier() =>
            new(NullLogger.Instance);

        [Fact]
        public void BuildSearchQuery_RequiredNarratorSet_IsAppendedToQuery()
        {
            var classifier = CreateClassifier();
            var audiobook = new Audiobook
            {
                Title = "The Martian",
                Authors = new List<string> { "Andy Weir" },
                RequiredNarrator = "R.C. Bray"
            };

            var query = classifier.BuildSearchQuery(audiobook);

            Assert.Equal("The Martian Andy Weir R.C. Bray", query);
        }

        [Fact]
        public void BuildSearchQuery_NoRequiredNarrator_OmitsIt()
        {
            var classifier = CreateClassifier();
            var audiobook = new Audiobook { Title = "The Martian", Authors = new List<string> { "Andy Weir" } };

            var query = classifier.BuildSearchQuery(audiobook);

            Assert.Equal("The Martian Andy Weir", query);
        }

        [Theory]
        [InlineData("R.C. Bray", "Brisingr by Christopher Paolini", "R.C. Bray", true)]
        [InlineData(null, "The Martian [ENG / M4B]", "Wil Wheaton", true)]
        [InlineData("R.C. Bray", "The Martian by Andy Weir [ENG / M4B]", "Wil Wheaton", false)]
        [InlineData("R.C. Bray", "The Martian, Read by R.C. Bray", null, true)]
        [InlineData("R C Bray", "The Martian by Andy Weir [ENG / M4B]", "R.C. Bray", true)]
        [InlineData("R.C. Bray", "The Martian - Andy Weir - RC Bray [M4B]", null, true)]
        [InlineData("r. c. bray", "The Martian", "R.C. Bray", true)]
        [InlineData("Jerome Pride", "Some Book", "Jérôme Pride", true)]
        [InlineData("R C Bray", "The Martian by Andy Weir [ENG / M4B]", "Wil Wheaton", false)]
        public void MatchesRequiredNarrator_ChecksNarratorFieldAndTitle(
            string? requiredNarrator, string title, string? resultNarrator, bool expected)
        {
            var result = new SearchResult { Title = title, Narrator = resultNarrator };

            var matches = AutomaticSearchResultClassifier.MatchesRequiredNarrator(result, requiredNarrator);

            Assert.Equal(expected, matches);
        }
    }
}
