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

using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Search.Matching;

[Trait("Name", "BookIdentityPolicyTests")]
[Trait("Category", "Application")]
public sealed class BookIdentityPolicyTests : BaseTests
{
    [Theory]
    [InlineData("Good Omens:A Long Subtitle")]
    [InlineData("Good Omens: A Long Subtitle")]
    [InlineData("Good Omens : A Long Subtitle")]
    [InlineData("Good Omens：A Long Subtitle")]
    [InlineData("Good Omens:\tA Long Subtitle")]
    public void SeparateSubtitleSupportsColonSpacingWithoutWeakeningAuthorEvidence(string title)
    {
        var book = new Audiobook { Title = title, Subtitle = "A Long Subtitle", Authors = ["Neil Gaiman"] };
        var matcher = new BookIdentityMatcher();
        var evidence = new CandidateIdentityEvidence
        {
            ReleaseName = "Good Omens",
            Authors = [new("Neil Gaiman", IdentityEvidenceOrigin.ProviderSupplied)]
        };
        Assert.True(matcher.Evaluate(BookMatchContext.From(book), evidence).IsConfirmed);
        Assert.False(matcher.Evaluate(BookMatchContext.From(book), evidence with
        { Authors = [new("Other Writer", IdentityEvidenceOrigin.ProviderSupplied)] }).IsConfirmed);
        book.Subtitle = null;
        Assert.False(matcher.Evaluate(BookMatchContext.From(book), evidence).IsConfirmed);
    }

    [Theory]
    [InlineData("Good Omens:A Long Subtitle")]
    [InlineData("Good Omens: A Long Subtitle")]
    public void SeparateTitleAndSubtitleSupportBothCombinedReleaseSpellings(string release)
    {
        var target = BookMatchContext.From(new Audiobook
        { Title = "Good Omens", Subtitle = "A Long Subtitle", Authors = ["Neil Gaiman"] });
        Assert.True(new BookIdentityMatcher().Evaluate(target, new()
        {
            ReleaseName = release,
            Authors = [new("Neil Gaiman", IdentityEvidenceOrigin.ProviderSupplied)]
        }).IsConfirmed);
    }

    [Fact]
    public void SeparateSubtitleOnlyRemovesTheExactDelimitedField()
    {
        var book = new Audiobook
        { Title = "Book: Other Subtitle", Subtitle = "Subtitle", Authors = ["Some Writer"] };
        var context = BookMatchContext.From(book);
        Assert.Contains("Book: Other Subtitle", context.Titles);
        Assert.DoesNotContain("Book", context.Titles);
        Assert.DoesNotContain("Book: Other", context.Titles);
    }

    [Theory]
    [InlineData("Artemis", "Andy Weir", "Andy Weir - Artemis - 2017 - M4B", null, true)]
    [InlineData("Artemis", "Andy Weir", "Andy Weir - The Martian - 2014", null, false)]
    [InlineData("Artemis", "Andy Weir", "Eoin Colfer - Artemis Fowl - 2009", null, false)]
    [InlineData("Revival", "Stephen King", "Chris Weitz - Revival", null, false)]
    [InlineData("Artemis", "Andy Weir", "Artemis", null, false)]
    [InlineData("Artemis", "Andy Weir", "Artemis", "Andy Weir", true)]
    public void RetainedFallbackCasesUseTheCommonIdentityPolicy(string title, string author,
        string release, string? suppliedAuthor, bool expected)
    {
        var target = BookMatchContext.From(new Audiobook { Title = title, Authors = [author] });
        Assert.Equal(expected, new BookIdentityMatcher().Evaluate(target, new()
        {
            ReleaseName = release,
            Authors = suppliedAuthor is null ? [] : [new(suppliedAuthor, IdentityEvidenceOrigin.ProviderSupplied)]
        }).IsConfirmed);
    }

    [Theory]
    [InlineData("novelnovel")]
    [InlineData("editionedition")]
    [InlineData("unabridgedaudiobook")]
    public void UnknownConcatenatedAnnotationsRemainUnresolved(string annotation)
    {
        var target = BookMatchContext.From(new Audiobook { Title = "Dune", Authors = ["Frank Herbert"] });
        var result = new BookIdentityMatcher().Evaluate(target, new()
        {
            ReleaseName = $"Dune [{annotation}]",
            Authors = [new("Frank Herbert", IdentityEvidenceOrigin.ProviderSupplied)]
        });
        Assert.Equal(BookIdentityStatus.UnresolvedTitle, result.Status);
    }

    public static IEnumerable<object[]> Corpus()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "AutomaticSearch", "identity-policy-v1.json")));
        foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
            yield return [item.GetProperty("id").GetString()!, item.GetRawText()];
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void AdjudicatedCorpus(string id, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var c = doc.RootElement;
        var target = new Audiobook
        {
            Title = c.GetProperty("title").GetString(),
            Authors = c.GetProperty("authors").EnumerateArray().Select(a => a.GetString()!).ToList()
        };
        foreach (var r in c.GetProperty("results").EnumerateArray())
        {
            var evidence = new CandidateIdentityEvidence
            {
                ReleaseName = r.GetProperty("title").GetString()!,
                Authors = r.GetProperty("contributors").EnumerateArray().Select(a => new ContributorEvidence(
                    a.GetString()!, Enum.Parse<IdentityEvidenceOrigin>(r.GetProperty("origin").GetString()!))).ToArray()
            };
            var assessment = new BookIdentityMatcher().Evaluate(BookMatchContext.From(target), evidence);
            Assert.True(assessment.IsConfirmed == r.GetProperty("expectedConfirmed").GetBoolean(),
                $"{id}: {evidence.ReleaseName}: {assessment.Status} ({assessment.Reason})");
        }
    }

    [Theory]
    [InlineData("Writing a Novel")]
    [InlineData("A Novel Obsession")]
    [InlineData("The Novel")]
    [InlineData("The Last Edition")]
    [InlineData("An Abridged History")]
    [InlineData("It")]
    [InlineData("S.")]
    [InlineData("𐐀𐐁")]
    public void LiteralTitleIsPreserved_AndAuthorChangesOnlyCorroboration(string title)
    {
        var target = BookMatchContext.From(new Audiobook { Title = title, Authors = ["Some Writer"] });
        var evidence = new CandidateIdentityEvidence { ReleaseName = title + " [M4B]" };
        var matcher = new BookIdentityMatcher();
        var alone = matcher.Evaluate(target, evidence);
        var corroborated = matcher.Evaluate(target, evidence with
        {
            Authors = [new("Some Writer", IdentityEvidenceOrigin.ProviderSupplied)]
        });
        Assert.Equal(BookIdentityStatus.ExactTitleOnly, alone.Status);
        Assert.Equal(title.Normalize(System.Text.NormalizationForm.FormC), alone.MatchedTitle);
        Assert.Equal(BookIdentityStatus.CorroboratedTitle, corroborated.Status);
        Assert.Equal(alone.MatchedTitle, corroborated.MatchedTitle);
    }

    [Fact]
    public void SeparateSubtitleSupportsMainTitle_ButUnverifiedLegacyPrefixDoesNot()
    {
        var matcher = new BookIdentityMatcher();
        var evidence = new CandidateIdentityEvidence
        {
            ReleaseName = "Good Omens [M4B]",
            Authors = [new("Neil Gaiman", IdentityEvidenceOrigin.ProviderSupplied)]
        };
        var target = new Audiobook { Title = "Good Omens: A Long Subtitle", Authors = ["Neil Gaiman"] };
        Assert.False(matcher.Evaluate(BookMatchContext.From(target), evidence).IsConfirmed);
        target.Subtitle = "A Long Subtitle";
        Assert.True(matcher.Evaluate(BookMatchContext.From(target), evidence).IsConfirmed);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("!")]
    [InlineData("。")]
    public void EditionMetadataNeverCreatesWorkIdentity(string punctuation)
    {
        var target = BookMatchContext.From(new Audiobook
        {
            Title = "Dune Unabridged Edition" + punctuation,
            Authors = ["Frank Herbert"]
        });
        var result = new BookIdentityMatcher().Evaluate(target, new()
        {
            ReleaseName = "The Shining Unabridged Edition" + punctuation,
            Authors = [new("Stephen King", IdentityEvidenceOrigin.ProviderSupplied)]
        });
        Assert.False(result.IsConfirmed);
    }

    [Fact]
    public void InferredArtistIsNotAnIndependentCredit()
    {
        var target = BookMatchContext.From(new Audiobook { Title = "Dune", Authors = ["Frank Herbert"] });
        var result = new BookIdentityMatcher().Evaluate(target, new()
        {
            ReleaseName = "Dune",
            Authors = [new("Frank Herbert", IdentityEvidenceOrigin.ReleaseNameDerived)]
        });
        Assert.Equal(BookIdentityStatus.ExactTitleOnly, result.Status);
    }

    [Theory]
    [InlineData("Dune: Novel [M4B]", false)]
    [InlineData("Dune: Novel - [M4B]", false)]
    [InlineData("Dune - Novel [M4B]", true)]
    public void EditionWordsDoNotCreateAParsedCredit(string release, bool expected)
    {
        var target = BookMatchContext.From(new Audiobook { Title = "Dune", Authors = ["Novel"] });
        Assert.Equal(expected, new BookIdentityMatcher().Evaluate(target, new() { ReleaseName = release }).IsConfirmed);
    }
}
