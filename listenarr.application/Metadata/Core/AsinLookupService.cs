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
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Metadata.Core
{
    /// <summary>
    /// ISBN-to-ASIN resolver backed by metadata providers.
    /// </summary>
    public class AsinLookupService : IAsinLookupService
    {
        private readonly AudibleService _audibleService;
        private readonly ILogger<AsinLookupService> _logger;

        public AsinLookupService(AudibleService audibleService, ILogger<AsinLookupService> logger)
        {
            _audibleService = audibleService;
            _logger = logger;
        }

        public async Task<(bool Success, string? Asin, string? Error)> GetAsinFromIsbnAsync(string isbn, CancellationToken ct = default)
        {
            var normalizedIsbn = NormalizeIsbn(isbn);
            if (string.IsNullOrWhiteSpace(normalizedIsbn))
            {
                return (false, null, "ISBN required");
            }

            if (normalizedIsbn.Length != 10 && normalizedIsbn.Length != 13)
            {
                return (false, null, "Invalid ISBN length");
            }

            try
            {
                ct.ThrowIfCancellationRequested();

                var search = await _audibleService.SearchByIsbnAsync(
                    normalizedIsbn,
                    page: 1,
                    limit: 25,
                    region: "us");

                var candidates = search?.Results ?? new List<AudibleSearchResult>();
                if (!candidates.Any())
                {
                    return (false, null, "ASIN not found for ISBN");
                }

                var exactMatch = candidates.FirstOrDefault(c =>
                    LooksLikeAsin(c.Asin) && IsSameIsbn(c.Isbn, normalizedIsbn));

                if (exactMatch != null)
                {
                    return (true, NormalizeAsin(exactMatch.Asin!), null);
                }

                var firstAsin = candidates.FirstOrDefault(c => LooksLikeAsin(c.Asin));
                if (firstAsin == null)
                {
                    return (false, null, "ASIN not found for ISBN");
                }

                _logger.LogDebug(
                    "ISBN lookup for {Isbn} returned non-exact fallback ASIN {Asin} (candidate ISBN: {CandidateIsbn})",
                    normalizedIsbn,
                    firstAsin.Asin,
                    firstAsin.Isbn);

                return (true, NormalizeAsin(firstAsin.Asin!), null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Failed to resolve ASIN from ISBN {Isbn}", normalizedIsbn);
                return (false, null, "Lookup failed");
            }
        }

        public async Task<(bool Success, bool MatchesIsbn, string? Error)> VerifyAsinContainsIsbnAsync(
            string asin,
            string isbn,
            CancellationToken ct = default)
        {
            var normalizedAsin = NormalizeAsin(asin);
            var normalizedIsbn = NormalizeIsbn(isbn);

            if (!LooksLikeAsin(normalizedAsin) || string.IsNullOrWhiteSpace(normalizedIsbn))
            {
                return (false, false, "ASIN and ISBN are required");
            }

            try
            {
                ct.ThrowIfCancellationRequested();

                var metadata = await _audibleService.GetBookMetadataAsync(normalizedAsin, region: "us", useCache: true);
                if (metadata == null)
                {
                    return (false, false, "Metadata not found");
                }

                if (string.IsNullOrWhiteSpace(metadata.Isbn))
                {
                    return (true, false, null);
                }

                return (true, IsSameIsbn(metadata.Isbn, normalizedIsbn), null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "VerifyAsinContainsIsbnAsync failed for {Asin}", normalizedAsin);
                return (false, false, ex.Message);
            }
        }

        private static bool LooksLikeAsin(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length != 10)
            {
                return false;
            }

            return value.All(char.IsLetterOrDigit);
        }

        private static string NormalizeAsin(string value) => value.Trim().ToUpperInvariant();

        private static string? NormalizeIsbn(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var cleaned = new List<char>(value.Length);
            foreach (var ch in value.Trim())
            {
                if (char.IsDigit(ch))
                {
                    cleaned.Add(ch);
                    continue;
                }

                if ((ch == 'x' || ch == 'X') && cleaned.Count == 9)
                {
                    cleaned.Add('X');
                }
            }

            return cleaned.Count == 0 ? null : new string(cleaned.ToArray());
        }

        private static bool IsSameIsbn(string? candidateIsbn, string normalizedRequestIsbn)
        {
            var normalizedCandidate = NormalizeIsbn(candidateIsbn);
            if (string.IsNullOrWhiteSpace(normalizedCandidate))
            {
                return false;
            }

            if (string.Equals(normalizedCandidate, normalizedRequestIsbn, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (normalizedRequestIsbn.Length == 13 && normalizedRequestIsbn.StartsWith("978", StringComparison.Ordinal))
            {
                var requestAs10 = ConvertIsbn13ToIsbn10(normalizedRequestIsbn);
                if (!string.IsNullOrWhiteSpace(requestAs10) &&
                    string.Equals(normalizedCandidate, requestAs10, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            if (normalizedCandidate.Length == 13 && normalizedCandidate.StartsWith("978", StringComparison.Ordinal))
            {
                var candidateAs10 = ConvertIsbn13ToIsbn10(normalizedCandidate);
                if (!string.IsNullOrWhiteSpace(candidateAs10) &&
                    string.Equals(candidateAs10, normalizedRequestIsbn, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string? ConvertIsbn13ToIsbn10(string isbn13)
        {
            if (string.IsNullOrWhiteSpace(isbn13) || isbn13.Length != 13 || !isbn13.StartsWith("978", StringComparison.Ordinal))
            {
                return null;
            }

            var core = isbn13.Substring(3, 9);
            var sum = 0;
            for (var i = 0; i < core.Length; i++)
            {
                if (!char.IsDigit(core[i]))
                {
                    return null;
                }

                sum += (10 - i) * (core[i] - '0');
            }

            var remainder = 11 - (sum % 11);
            string checkDigit;
            if (remainder == 11)
            {
                checkDigit = "0";
            }
            else if (remainder == 10)
            {
                checkDigit = "X";
            }
            else
            {
                checkDigit = remainder.ToString();
            }

            return core + checkDigit;
        }
    }
}

