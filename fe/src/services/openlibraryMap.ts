// Utility to map OpenLibraryBook to canonical SearchResult
import type { OpenLibraryBook } from './openlibrary'
import type { SearchResult } from '@/types'

/**
 * Map an OpenLibraryBook to the canonical SearchResult structure.
 * This ensures OpenLibrary results are compatible with Audimeta/Audnexus results.
 */
export function mapOpenLibraryBookToSearchResult(book: OpenLibraryBook): SearchResult {
  return {
    id: book.key || '',
    title: book.title || '',
    artist: Array.isArray(book.author_name)
      ? book.author_name.join(', ')
      : (book.author_name || ''),
    album: '',
    category: '',
    source: 'OpenLibrary',
    sourceLink: book.key ? `https://openlibrary.org${book.key}` : undefined,
    publishedDate: book.first_publish_year ? String(book.first_publish_year) : '',
    format: 'Book',
    score: undefined,
    // Indexer-specific (not used)
    thumbnailRetentionDays: undefined,
    size: 0,
    seeders: undefined,
    leechers: undefined,
    grabs: undefined,
    files: undefined,
    magnetLink: '',
    torrentUrl: '',
    nzbUrl: '',
    downloadType: '',
    quality: undefined,
    resultUrl: book.key ? `https://openlibrary.org${book.key}` : undefined,
    // Metadata-specific
    description: undefined,
    subtitle: undefined,
    publisher: book.publisher ? (Array.isArray(book.publisher) ? book.publisher.join(', ') : book.publisher) : undefined,
    language: book.language ? (Array.isArray(book.language) ? book.language[0] : book.language) : undefined,
    runtime: undefined,
    narrator: undefined,
    imageUrl: book.cover_i ? `https://covers.openlibrary.org/b/id/${book.cover_i}-L.jpg` : undefined,
    asin: '',
    isbn: book.isbn && book.isbn.length > 0 ? book.isbn[0] : '',
    series: book.seriesList && book.seriesList.length > 0 ? book.seriesList[0] : undefined,
    seriesNumber: undefined,
    seriesList: book.seriesList,
    genres: book.subject ? (Array.isArray(book.subject) ? book.subject : [book.subject]) : undefined,
    productUrl: book.key ? `https://openlibrary.org${book.key}` : undefined,
    isEnriched: false,
    metadataSource: 'openlibrary',
    authors: book.author_name
      ? (Array.isArray(book.author_name)
          ? book.author_name.map((name) => ({ name }))
          : [{ name: book.author_name }])
      : undefined,
    narrators: [],
    lengthMinutes: undefined,
    link: book.key ? `https://openlibrary.org${book.key}` : undefined,
    releaseDate: book.first_publish_year ? String(book.first_publish_year) : undefined,
    publishDate: book.first_publish_year ? String(book.first_publish_year) : undefined,
  }
}
