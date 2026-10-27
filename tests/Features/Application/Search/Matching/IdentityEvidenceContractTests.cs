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
using Listenarr.Application.Search.Matching;
using Microsoft.Extensions.Logging.Abstractions;

using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Search.Matching;

[Trait("Name", "IdentityEvidenceContractTests")]
[Trait("Category", "Application")]
public sealed class IdentityEvidenceContractTests : BaseTests
{
    [Theory]
    [InlineData("Unknown Author")]
    [InlineData("unknown")]
    public void DisplayPlaceholdersCannotCorroborateTheSameTitle(string author)
    {
        var matcher = new BookIdentityMatcher();
        var target = BookMatchContext.From(new() { Title = "Dune", Authors = [author] });
        var evidence = new CandidateIdentityEvidence
        {
            ReleaseName = "Dune [M4B]",
            Authors = [new(author, IdentityEvidenceOrigin.ProviderSupplied)]
        };
        Assert.Equal(BookIdentityStatus.ExactTitleOnly, matcher.Evaluate(target, evidence).Status);
        Assert.False(matcher.Evaluate(target, evidence with { ReleaseName = author + " - Dune [M4B]" }).IsConfirmed);
    }

    [Fact]
    public void MalformedEvidenceDefersInsteadOfThrowing()
    {
        var evidence = new CandidateIdentityEvidence { ReleaseName = "Dune", Authors = null! };
        var result = new SearchResult { IdentityEvidence = evidence };
        Assert.Equal(BookIdentityStatus.AssessmentUnavailable, new BookIdentityMatcher().Evaluate(
            BookMatchContext.From(new() { Title = "Dune" }), BookIdentityMatcher.Snapshot(result)).Status);
    }

    [Fact]
    public void MissingReferenceAndOversizedEvidenceCannotConfirm()
    {
        var matcher = new BookIdentityMatcher();
        Assert.Equal(BookIdentityStatus.MissingReference, matcher.Evaluate(
            BookMatchContext.From(new()), new() { ReleaseName = "Some Writer - Dune" }).Status);
        Assert.Equal(BookIdentityStatus.AssessmentUnavailable, matcher.Evaluate(
            BookMatchContext.From(new() { Title = "Dune" }), new() { ReleaseName = new string('x', 16385) }).Status);
    }

    [Fact]
    public void VerifiedIsbnUsesTheCandidateIdentifier()
    {
        var matcher = new BookIdentityMatcher();
        var target = BookMatchContext.From(new() { Title = "Dune", Isbn = ["978-0-441-17271-9"] });
        var evidence = new CandidateIdentityEvidence
        {
            ReleaseName = "Dune",
            Identifiers = [new("ISBN", "9780441172719", IdentityEvidenceOrigin.Verified)]
        };
        Assert.True(matcher.Evaluate(target, evidence).IsConfirmed);
        Assert.False(matcher.Evaluate(target, evidence with
        { Identifiers = [new("ISBN", "9780441172719", IdentityEvidenceOrigin.ProviderSupplied)] }).IsConfirmed);
    }

    [Fact]
    public void ProviderContributorsKeepTheirRoles_AndSurviveConversionAndSerialization()
    {
        var payload = JsonSerializer.Serialize(new
        {
            data = new[] { new
        {
            id = "1", title = "Stories", author_info = "{\"1\":\"Neil Gaiman\",\"2\":\"Al Sarrantonio\"}",
            narrator_info = "{\"1\":\"Some Narrator\"}", filetype = "M4B", size = 1000000
        } }
        });
        var indexer = new Indexer { Id = 42, Name = "fixture", Url = "http://fixture.invalid", Implementation = "MyAnonamouse", Type = "Torrent" };
        var parsed = Assert.Single(MyAnonamouseResponseParser.Parse(payload, indexer, NullLogger.Instance));
        Assert.Equal(2, parsed.IdentityEvidence!.Authors.Count);
        Assert.All(parsed.IdentityEvidence.Authors, a => Assert.Equal(IdentityEvidenceOrigin.ProviderSupplied, a.Origin));
        Assert.DoesNotContain(parsed.IdentityEvidence.Authors, a => a.Name == "Some Narrator");
        var converted = SearchResultConverters.ToSearchResult(parsed);
        var replay = JsonSerializer.Deserialize<SearchResult>(JsonSerializer.Serialize(converted))!;
        var roundTrip = SearchResultConverters.ToSearchResult(SearchResultConverters.ToIndexerSearchResult(replay));
        Assert.Equal(parsed.IdentityEvidence.ReleaseName, roundTrip.IdentityEvidence!.ReleaseName);
        Assert.Equal(parsed.IdentityEvidence.Authors, roundTrip.IdentityEvidence.Authors);
    }

    [Fact]
    public void LegacyDisplayArtistCannotUpgradeToSuppliedIdentity()
    {
        var legacy = new SearchResult { Title = "Dune", Artist = "Frank Herbert" };
        var evidence = BookIdentityMatcher.Snapshot(legacy);
        Assert.Empty(evidence.Authors);
        var result = new BookIdentityMatcher().Evaluate(BookMatchContext.From(new()
        { Title = "Dune", Authors = ["Frank Herbert"] }), evidence);
        Assert.Equal(BookIdentityStatus.ExactTitleOnly, result.Status);
    }

    [Theory]
    [InlineData(IdentityEvidenceOrigin.Unattributed, false)]
    [InlineData(IdentityEvidenceOrigin.ReleaseNameDerived, false)]
    [InlineData(IdentityEvidenceOrigin.Verified, true)]
    public void IdentifierAssuranceIsExplicit(IdentityEvidenceOrigin origin, bool expected)
    {
        var target = BookMatchContext.From(new() { Title = "Dune", Asin = "B000000001" });
        var evidence = new CandidateIdentityEvidence
        {
            ReleaseName = "Dune",
            Identifiers = [new("asin", "B000000001", origin)]
        };
        Assert.Equal(expected, new BookIdentityMatcher().Evaluate(target, evidence).IsConfirmed);
    }

    [Fact]
    public void IdentifierCannotOverrideReliableContradictoryTitleOrAuthor()
    {
        var context = BookMatchContext.From(new() { Title = "Dune", Authors = ["Frank Herbert"], Asin = "B000000001" });
        var evidence = new CandidateIdentityEvidence
        {
            ReleaseName = "The Shining",
            Identifiers = [new("asin", "B000000001", IdentityEvidenceOrigin.Verified)]
        };
        var matcher = new BookIdentityMatcher();
        Assert.Equal(BookIdentityStatus.ConflictingEvidence, matcher.Evaluate(context, evidence).Status);
        Assert.Equal(BookIdentityStatus.ConflictingEvidence, matcher.Evaluate(context, evidence with
        {
            ReleaseName = "Dune",
            Authors = [new("Stephen King", IdentityEvidenceOrigin.ProviderSupplied)]
        }).Status);
    }

    [Fact]
    public void AChangedEditionIdentifierDoesNotVetoAnOtherwiseCorroboratedWork()
    {
        var target = BookMatchContext.From(new() { Title = "Dune", Authors = ["Frank Herbert"], Asin = "B000000001" });
        Assert.True(new BookIdentityMatcher().Evaluate(target, new()
        {
            ReleaseName = "Dune",
            Authors = [new("Frank Herbert", IdentityEvidenceOrigin.ProviderSupplied)],
            Identifiers = [new("asin", "B000000002", IdentityEvidenceOrigin.Verified)]
        }).IsConfirmed);
    }

    [Fact]
    public void GeneratedLayoutsAndMetadataCannotCreateMissingTitleEvidence()
    {
        var matcher = new BookIdentityMatcher();
        foreach (var title in new[] { "Writing a Novel", "A Novel Obsession", "It", "S.", "Война и мир", "𐐀𐐁" })
            foreach (var separator in new[] { " - ", " – ", " — ", "−" })
                foreach (var annotation in new[] { "", " [M4B]", " (Unabridged Edition).", " - 2024 - M4B" })
                {
                    var context = BookMatchContext.From(new() { Title = title, Authors = ["Some Writer"] });
                    var correct = matcher.Evaluate(context, new() { ReleaseName = "Some Writer" + separator + title + annotation });
                    Assert.True(correct.IsConfirmed, $"{title}/{separator}/{annotation}: {correct}");
                    var wrong = matcher.Evaluate(context, new() { ReleaseName = "Some Writer" + separator + "Unrelated Work" + annotation });
                    Assert.False(wrong.IsConfirmed);
                }
    }
}
