/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */


namespace Listenarr.Infrastructure.Search.Providers.MyAnonamouse;

internal static class MyAnonamouseRequestFactory
{
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36";

    public static Uri BuildSearchUri(Indexer indexer, string query, SearchRequest? request = null, int perPage = 100)
    {
        var (parsedTitle, parsedAuthor) = ParseTitleAuthorFromQuery(query);
        var searchType = "all";
        if (!string.IsNullOrWhiteSpace(parsedTitle) && string.IsNullOrWhiteSpace(parsedAuthor))
        {
            searchType = "title";
        }
        else if (string.IsNullOrWhiteSpace(parsedTitle) && !string.IsNullOrWhiteSpace(parsedAuthor))
        {
            searchType = "author";
        }

        var searchFields = new Dictionary<string, bool>
        {
            ["title"] = true,
            ["author"] = true,
            ["narrator"] = true,
            ["series"] = true,
            ["description"] = false,
            ["filenames"] = true,
            ["filetype"] = true
        };

        if (request?.MyAnonamouse != null)
        {
            var options = request.MyAnonamouse;
            if (options.SearchInDescription.HasValue)
            {
                searchFields["description"] = options.SearchInDescription.Value;
            }

            if (options.SearchInSeries.HasValue)
            {
                searchFields["series"] = options.SearchInSeries.Value;
            }

            if (options.SearchInFilenames.HasValue)
            {
                searchFields["filenames"] = options.SearchInFilenames.Value;
            }
        }

        var queryParameters = new List<KeyValuePair<string, string>>
        {
            new("tor[text]", query),
            new("tor[searchIn]", "torrents")
        };

        foreach (var category in new[] { "39", "49", "50", "83", "51", "97", "40", "41", "106", "42", "52", "98", "54", "55", "43", "99", "84", "44", "56", "45", "57", "85", "87", "119", "88", "58", "59", "46", "47", "53", "89", "100", "108", "48", "111", "0" })
        {
            queryParameters.Add(new("tor[cat][]", category));
        }

        queryParameters.Add(new("tor[main_cat][]", "13"));
        queryParameters.Add(new("tor[browse_lang][]", "1"));
        queryParameters.Add(new("tor[browseFlagsHideVsShow]", "0"));
        queryParameters.Add(new("tor[sortType]", "default"));
        queryParameters.Add(new("tor[startNumber]", "0"));
        queryParameters.Add(new("tor[perpage]", perPage.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        foreach (var field in searchFields)
        {
            queryParameters.Add(new($"tor[srchIn][{field.Key}]", field.Value ? "true" : "false"));
        }

        queryParameters.Add(new("tor[searchType]", searchType));

        switch (request?.MyAnonamouse?.Filter)
        {
            case MamTorrentFilter.Active:
                queryParameters.Add(new("tor[onlyActive]", "1"));
                break;
            case MamTorrentFilter.Freeleech:
                queryParameters.Add(new("tor[onlyFreeleech]", "1"));
                break;
            case MamTorrentFilter.FreeleechOrVip:
                queryParameters.Add(new("tor[freeleechOrVip]", "1"));
                break;
            case MamTorrentFilter.Vip:
                queryParameters.Add(new("tor[onlyVip]", "1"));
                break;
            case MamTorrentFilter.NotVip:
                queryParameters.Add(new("tor[notVip]", "1"));
                break;
        }

        if (request?.MyAnonamouse?.FreeleechWedge is { } freeleechWedge)
        {
            queryParameters.Add(new("tor[freeleechWedge]", freeleechWedge.ToString().ToLowerInvariant()));
        }

        var queryString = string.Join(
            "&",
            queryParameters.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        var baseUrl = indexer.Url.TrimEnd('/');
        return new Uri($"{baseUrl}/tor/js/loadSearchJSONbasic.php?{queryString}", UriKind.Absolute);
    }

    public static HttpRequestMessage CreateSearchRequest(Uri uri, string mamId, bool addCookieHeader)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.Accept.ParseAdd("application/json, text/javascript, */*; q=0.01");
        request.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        request.Headers.Referrer = new Uri($"{uri.Scheme}://{uri.Authority}/");

        if (addCookieHeader)
        {
            request.Headers.Add("Cookie", $"mam_id={mamId}");
        }

        return request;
    }

    private static (string? Title, string? Author) ParseTitleAuthorFromQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return (null, null);
        }

        var value = query.Trim();
        var byIndex = value.LastIndexOf(" by ", StringComparison.OrdinalIgnoreCase);
        if (byIndex > 0)
        {
            return (value[..byIndex].Trim(), value[(byIndex + 4)..].Trim());
        }

        var dashParts = value.Split(new[] { " - " }, 2, StringSplitOptions.None);
        if (dashParts.Length == 2)
        {
            return (dashParts[0].Trim(), dashParts[1].Trim());
        }

        var commaParts = value.Split(new[] { ',' }, 2);
        return commaParts.Length == 2
            ? (commaParts[1].Trim(), commaParts[0].Trim())
            : (null, null);
    }
}
