namespace Listenarr.Application.Audiobooks.Contracts
{
    /// <summary>
    /// Resolves ASINs from ISBNs for metadata lookup
    /// </summary>
    public interface IAsinLookupService
    {
        /// <summary>
        /// Gets an ASIN for a given ISBN
        /// </summary>
        /// <param name="isbn">The ISBN to convert</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Tuple containing success flag, ASIN, and optional error message</returns>
        Task<(bool Success, string? Asin, string? Error)> GetAsinFromIsbnAsync(string isbn, CancellationToken ct = default);

        /// <summary>
        /// Verify whether a given ASIN's product page contains the provided ISBN.
        /// Returns a tuple indicating whether the verification request succeeded and whether the ISBN was found on the product page.
        /// </summary>
        /// <param name="asin">ASIN to verify</param>
        /// <param name="isbn">ISBN digits to look for</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Tuple (Success, MatchesIsbn, Error)</returns>
        Task<(bool Success, bool MatchesIsbn, string? Error)> VerifyAsinContainsIsbnAsync(string asin, string isbn, CancellationToken ct = default);
    }
}
