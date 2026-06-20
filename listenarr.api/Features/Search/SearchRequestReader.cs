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
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Listenarr.Api.Features.Search
{
    internal sealed class SearchRequestReader
    {
        private readonly ILogger _logger;

        public SearchRequestReader(ILogger logger)
        {
            _logger = logger;
        }

        public SearchRequest? Read(JsonElement requestJson)
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
            return JsonSerializer.Deserialize<SearchRequest>(requestJson.GetRawText(), options);
        }

        public string? NormalizeAdvancedRequest(SearchRequest request)
        {
            request.Author = SearchRequestNormalizer.NormalizeStructuredAdvancedField(request.Author, "AUTHOR:");
            request.Title = SearchRequestNormalizer.NormalizeStructuredAdvancedField(request.Title, "TITLE:");
            request.Isbn = SearchRequestNormalizer.NormalizeStructuredAdvancedField(request.Isbn, "ISBN:");
            request.Asin = SearchRequestNormalizer.NormalizeStructuredAdvancedField(request.Asin, "ASIN:");

            try
            {
                if (string.IsNullOrWhiteSpace(request.Isbn))
                {
                    return null;
                }

                var rawIsbn = Regex.Replace(request.Isbn, "[^0-9Xx]", string.Empty);
                if (rawIsbn.Length == 10)
                {
                    var converted = SearchRequestNormalizer.ConvertIsbn10ToIsbn13(rawIsbn);
                    if (converted == null)
                    {
                        return "Invalid ISBN-10 provided";
                    }

                    request.Isbn = converted;
                    _logger.LogInformation("Converted ISBN-10 to ISBN-13: {Original} -> {Converted}", rawIsbn, converted);
                }
                else if (rawIsbn.Length == 13)
                {
                    if (!Regex.IsMatch(rawIsbn, "^[0-9]{13}$"))
                    {
                        return "ISBN must be 13 digits";
                    }

                    request.Isbn = rawIsbn;
                }
                else
                {
                    return "ISBN must be either 10 or 13 characters";
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to normalize ISBN in advanced search");
                return "Invalid ISBN format";
            }

            return null;
        }
    }
}
