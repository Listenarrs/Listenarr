/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System.Text.RegularExpressions;

namespace Listenarr.Infrastructure.Search.Providers.InternetArchive;

internal static partial class InternetArchiveSearchQueryPlanner
{
    public const string DefaultCollection = "librivoxaudio";

    public static InternetArchiveSearchQueryPlan Create(string? collection, string query, SearchRequest? request)
    {
        var normalizedCollection = NormalizeCollection(collection);
        var queries = new List<string>();
        var normalizedQuery = NormalizeSearchText(query);
        var normalizedTitle = NormalizeSearchText(request?.Title);
        var normalizedAuthor = NormalizeSearchText(request?.Author);

        if (!string.IsNullOrWhiteSpace(normalizedTitle) && !string.IsNullOrWhiteSpace(normalizedAuthor))
        {
            queries.Add(BuildQuery(normalizedCollection, $"title:({normalizedTitle}) AND creator:({normalizedAuthor})"));
        }

        if (!string.IsNullOrWhiteSpace(normalizedTitle))
        {
            queries.Add(BuildQuery(normalizedCollection, $"title:({normalizedTitle})"));
        }

        var broadTerms = CreateBroadTerms(normalizedQuery, normalizedTitle, normalizedAuthor);
        if (!string.IsNullOrWhiteSpace(broadTerms))
        {
            // Internet Archive's fielded title/creator search does not reliably match mixed
            // title + author free text. A collection-scoped metadata query allows terms to
            // match across IA metadata fields, which is required for common audiobook queries.
            queries.Add(BuildQuery(normalizedCollection, $"({broadTerms})"));
        }

        return new InternetArchiveSearchQueryPlan(
            normalizedCollection,
            !string.Equals(normalizedCollection, collection?.Trim(), StringComparison.Ordinal),
            queries.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static string BuildQuery(string collection, string criteria) =>
        $"collection:{collection} AND {criteria}";

    private static string CreateBroadTerms(string? query, string? title, string? author)
    {
        if (!string.IsNullOrWhiteSpace(query))
        {
            return query;
        }

        return NormalizeSearchText(string.Join(" ", new[] { title, author }.Where(value => !string.IsNullOrWhiteSpace(value))));
    }

    private static string NormalizeCollection(string? collection)
    {
        if (string.IsNullOrWhiteSpace(collection))
        {
            return DefaultCollection;
        }

        var trimmed = collection.Trim();
        return CollectionRegex().IsMatch(trimmed)
            ? trimmed
            : DefaultCollection;
    }

    private static string NormalizeSearchText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        const string syntaxChars = "*/\\<>:?|^~`$#%&+={}[]\"!()";
        var characters = value
            .Select(ch => char.IsControl(ch) || syntaxChars.IndexOf(ch) >= 0 ? ' ' : ch)
            .ToArray();
        return WhitespaceRegex().Replace(new string(characters), " ").Trim();
    }

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex CollectionRegex();

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceRegex();
}

internal sealed record InternetArchiveSearchQueryPlan(
    string Collection,
    bool UsedDefaultCollection,
    IReadOnlyList<string> Queries);
