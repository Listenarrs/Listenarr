
namespace Listenarr.Application.Search.Contracts
{
    /// <summary>
    /// Searches Open Library for book metadata
    /// </summary>
    public interface IOpenLibraryService
    {
        /// <summary>
        /// Gets ISBNs for a book by title and author
        /// </summary>
        /// <param name="title">Book title</param>
        /// <param name="author">Optional author name</param>
        /// <returns>List of ISBNs</returns>
        Task<List<string>> GetIsbnsForTitleAsync(string title, string? author = null);

        /// <summary>
        /// Searches for books in Open Library
        /// </summary>
        /// <param name="title">Book title</param>
        /// <param name="author">Optional author name</param>
        /// <param name="limit">Maximum number of results</param>
        /// <returns>Search response with book results</returns>
        Task<OpenLibrarySearchResponse> SearchBooksAsync(string title, string? author = null, int limit = 10);
    }
}
