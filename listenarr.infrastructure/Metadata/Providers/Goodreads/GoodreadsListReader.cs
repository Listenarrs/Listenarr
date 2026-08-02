/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System.Net;
using System.Text;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Metadata.Providers.Goodreads;

public sealed class GoodreadsListReader : IGoodreadsListReader
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<GoodreadsListReader> _logger;

    public GoodreadsListReader(HttpClient httpClient, ILogger<GoodreadsListReader> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<IReadOnlyList<GoodreadsImportBook>> ReadAsync(
        GoodreadsImportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!string.IsNullOrWhiteSpace(request.CsvContent))
        {
            return ParseCsv(request.CsvContent);
        }

        if (!string.IsNullOrWhiteSpace(request.Url))
        {
            return await ReadUrlAsync(request.Url, cancellationToken);
        }

        throw new ArgumentException("Provide either csvContent from a Goodreads export or a public Goodreads url.");
    }

    internal static IReadOnlyList<GoodreadsImportBook> ParseCsv(string csvContent)
    {
        var rows = ParseCsvRows(csvContent).ToList();
        if (rows.Count == 0)
        {
            return [];
        }

        var headers = rows[0].Select(NormalizeHeader).ToList();
        var books = new List<GoodreadsImportBook>();

        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            string? Get(string header)
            {
                var index = headers.IndexOf(NormalizeHeader(header));
                return index >= 0 && index < row.Count ? CleanCell(row[index]) : null;
            }

            var title = FirstNonEmpty(Get("Title"), Get("Book Title"));
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var isbn = NormalizeIsbnValues(Get("ISBN"), Get("ISBN13"));
            books.Add(new GoodreadsImportBook
            {
                SourceIndex = i,
                GoodreadsId = Get("Book Id"),
                Title = title,
                Author = FirstNonEmpty(Get("Author"), Get("Author l-f"), Get("Additional Authors")),
                Isbn = isbn,
                PublishYear = FirstNonEmpty(Get("Original Publication Year"), Get("Year Published")),
                Bookshelf = FirstNonEmpty(Get("Bookshelves"), Get("Exclusive Shelf")),
                SourceUrl = BuildGoodreadsBookUrl(Get("Book Id"))
            });
        }

        return books;
    }

    private async Task<IReadOnlyList<GoodreadsImportBook>> ReadUrlAsync(
        string url,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("Goodreads URL is invalid.");
        }

        if (!IsGoodreadsHost(uri.Host))
        {
            throw new ArgumentException("Only goodreads.com URLs can be imported.");
        }

        if (!OutboundRequestSecurity.TryValidateExternalHttpUri(uri, out var validationReason))
        {
            throw new ArgumentException($"Goodreads URL is not allowed: {validationReason}");
        }

        if (!await OutboundRequestSecurity.TryValidateResolvedExternalHttpUriAsync(uri, _logger))
        {
            throw new ArgumentException("Goodreads URL is not allowed because it resolved to a private or loopback address.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("Listenarr/1.0 GoodreadsImport");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseHtml(html, uri);
    }

    internal static IReadOnlyList<GoodreadsImportBook> ParseHtml(string html, Uri sourceUri)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);

        var rows = (IEnumerable<HtmlNode>?)document.DocumentNode.SelectNodes("//tr[contains(@class,'bookalike') or .//a[contains(@class,'bookTitle')]]")
            ?? document.DocumentNode.SelectNodes("//a[contains(@class,'bookTitle')]")
            ?? Enumerable.Empty<HtmlNode>();
        var books = new List<GoodreadsImportBook>();
        var sourceIndex = 0;

        foreach (var row in rows)
        {
            var titleNode = row.SelectSingleNode(".//a[contains(@class,'bookTitle')]")
                ?? (row.Name.Equals("a", StringComparison.OrdinalIgnoreCase) ? row : null);
            var title = WebUtility.HtmlDecode(titleNode?.InnerText ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var authorNode = row.SelectSingleNode(".//a[contains(@class,'authorName')]")
                ?? row.SelectSingleNode(".//*[contains(@class,'authorName')]");
            var href = titleNode?.GetAttributeValue("href", string.Empty);

            sourceIndex++;
            books.Add(new GoodreadsImportBook
            {
                SourceIndex = sourceIndex,
                GoodreadsId = ExtractGoodreadsId(href),
                Title = CollapseWhitespace(title) ?? title,
                Author = CollapseWhitespace(WebUtility.HtmlDecode(authorNode?.InnerText ?? string.Empty)),
                SourceUrl = BuildAbsoluteUrl(sourceUri, href)
            });
        }

        return books
            .GroupBy(book => !string.IsNullOrWhiteSpace(book.GoodreadsId)
                ? $"id:{book.GoodreadsId}"
                : $"title:{book.Title}|author:{book.Author}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static IEnumerable<List<string>> ParseCsvRows(string csv)
    {
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < csv.Length; i++)
        {
            var ch = csv[i];
            if (inQuotes)
            {
                if (ch == '"' && i + 1 < csv.Length && csv[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (ch == '"')
                {
                    inQuotes = false;
                }
                else
                {
                    field.Append(ch);
                }

                continue;
            }

            if (ch == '"')
            {
                inQuotes = true;
            }
            else if (ch == ',')
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (ch == '\r' || ch == '\n')
            {
                if (ch == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n')
                {
                    i++;
                }

                row.Add(field.ToString());
                field.Clear();
                yield return row;
                row = [];
            }
            else
            {
                field.Append(ch);
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            yield return row;
        }
    }

    private static bool IsGoodreadsHost(string host)
    {
        var normalized = host.Trim().ToLowerInvariant();
        return normalized == "goodreads.com" || normalized.EndsWith(".goodreads.com", StringComparison.Ordinal);
    }

    private static string NormalizeHeader(string value) =>
        value.Trim().Replace(" ", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

    private static string? CleanCell(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim().Trim('=').Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
        {
            trimmed = trimmed[1..^1];
        }

        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static List<string> NormalizeIsbnValues(params string?[] values) =>
        values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => new string(value!.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant())
            .Where(value => value.Length is 10 or 13)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string? BuildGoodreadsBookUrl(string? goodreadsId) =>
        string.IsNullOrWhiteSpace(goodreadsId) ? null : $"https://www.goodreads.com/book/show/{goodreadsId.Trim()}";

    private static string? BuildAbsoluteUrl(Uri sourceUri, string? href)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            return null;
        }

        return Uri.TryCreate(sourceUri, href, out var absoluteUri) ? absoluteUri.ToString() : null;
    }

    private static string? ExtractGoodreadsId(string? href)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            return null;
        }

        var path = href.Split('?', '#')[0];
        var match = System.Text.RegularExpressions.Regex.Match(path, @"/book/show/(\d+)");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? CollapseWhitespace(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : System.Text.RegularExpressions.Regex.Replace(value.Trim(), @"\s+", " ");
}
