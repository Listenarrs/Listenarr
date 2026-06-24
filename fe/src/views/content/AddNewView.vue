<!--
  Listenarr - Audiobook Management System
  Copyright (C) 2024-2026 Listenarr Contributors

  This program is free software: you can redistribute it and/or modify
  it under the terms of the GNU Affero General Public License as published
  by the Free Software Foundation, either version 3 of the License, or
  (at your option) any later version.

  This program is distributed in the hope that it will be useful,
  but WITHOUT ANY WARRANTY; without even the implied warranty of
  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
  GNU Affero General Public License for more details.

  You should have received a copy of the GNU Affero General Public License
  along with this program. If not, see <https://www.gnu.org/licenses/>.
-->
<template>
  <div class="add-new-view">
    <div class="page-header">
      <h1><PhPlusCircle /> Add New Audiobook</h1>
    </div>

    <!-- Unified Search -->
    <div class="search-section">
      <div class="search-container">
        <div class="search-bar-section">
          <div v-if="!showAdvancedSearch" class="unified-search-bar">
            <button
              v-if="!showAdvancedSearch"
              type="button"
              @click="toggleAdvancedSearch"
              class="search-btn advanced-btn"
              :title="showAdvancedSearch ? 'Hide Advanced Search' : 'Show Advanced Search'"
              aria-pressed="false"
              aria-controls="advanced-search"
              aria-expanded="false"
            >
              <PhFunnelSimple /> {{ showAdvancedSearch ? 'Hide' : 'Advanced' }}
            </button>
            <div v-if="!showAdvancedSearch" class="search-method">
              <label for="unified-search-input" class="search-method-label"
                >Search for Audiobooks</label
              >
              <p class="search-help">
                Enter an ASIN (e.g., B08G9PRS1K) or search by title and author.<br />
              </p>
            </div>
            <form
              class="unified-search-form"
              role="search"
              aria-label="Search audiobooks"
              @submit.prevent="onUnifiedSearchSubmit"
            >
              <input
                ref="searchInput"
                v-model="searchQuery"
                id="unified-search-input"
                aria-label="Search query"
                aria-describedby="unified-search-hint"
                type="text"
                :placeholder="searchPlaceholder"
                class="form-input search-input"
                :class="{ error: searchError }"
                @input="handleSearchInput"
                @keydown.enter.prevent="onUnifiedSearchSubmit"
              />

              <div class="region-select-wrapper" data-testid="search-region-indicator">
                <label for="region-select" class="region-label">Region:</label>
                <select
                  v-model="selectedSearchRegion"
                  id="region-select"
                  class="region-select form-select"
                  aria-label="Select Audible search region"
                >
                  <option
                    v-for="option in searchRegionOptions"
                    :key="option.value"
                    :value="option.value"
                  >
                    {{ option.label }}
                  </option>
                </select>
              </div>

              <div class="language-select-wrapper">
                <label for="language-select" class="language-label">Language:</label>
                <select
                  v-model="preferredSearchLanguage"
                  id="language-select"
                  class="language-select form-select"
                  aria-label="Filter results by language"
                >
                  <option
                    v-for="option in preferredSearchLanguageOptions"
                    :key="option.value"
                    :value="option.value"
                  >
                    {{ option.label }}
                  </option>
                </select>
              </div>

              <button
                type="submit"
                :disabled="!isSearching && !searchQuery.trim()"
                class="search-btn"
                aria-label="Execute search"
              >
                <span v-if="isSearching">
                  <PhSpinner class="ph-spin" />
                  Cancel
                </span>
                <span v-else>
                  <PhMagnifyingGlass />
                  Search
                </span>
              </button>
            </form>
          </div>

          <!-- Inline Advanced Search Section -->
          <div
            v-if="showAdvancedSearch"
            id="advanced-search"
            role="region"
            class="advanced-search-section"
            aria-labelledby="advanced-search-label"
          >
            <div class="advanced-search-header">
              <h3 id="advanced-search-label"><PhFunnelSimple /> Advanced Search</h3>
              <p class="help-text">
                Enter multiple search criteria for more precise results. When both Title and Author
                are provided, uses the Audible catalog for maximum accuracy.
              </p>
            </div>
            <button
              type="button"
              @click="toggleAdvancedSearch"
              class="simple-search-button"
              aria-label="Return to simple search"
              aria-controls="advanced-search"
              :aria-expanded="showAdvancedSearch"
            >
              <PhArrowLeft /> Simple Search
            </button>
            <form
              class="advanced-search-form"
              role="search"
              aria-label="Advanced search audiobooks"
              @submit.prevent="performAdvancedSearch"
            >
              <div class="form-row">
                <div class="form-group">
                  <label for="adv-title">Title</label>
                  <input
                    id="adv-title"
                    aria-label="Title"
                    v-model="advancedSearchParams.title"
                    type="text"
                    placeholder="e.g., Dune"
                    class="form-input"
                    aria-describedby="hint-adv-title"
                    @keydown.enter.prevent="performAdvancedSearch"
                  />
                  <div class="form-hint" id="hint-adv-title">
                    Use full or partial titles for best matches.
                  </div>
                </div>

                <div class="form-group">
                  <label for="adv-author">Author</label>
                  <input
                    id="adv-author"
                    aria-label="Author"
                    v-model="advancedSearchParams.author"
                    type="text"
                    placeholder="e.g., Frank Herbert"
                    class="form-input"
                    @keydown.enter.prevent="performAdvancedSearch"
                  />
                </div>
                <div class="form-group">
                  <label for="adv-series">Series</label>
                  <input
                    id="adv-series"
                    aria-label="Series"
                    v-model="advancedSearchParams.series"
                    type="text"
                    placeholder="e.g., The Empyrean"
                    class="form-input"
                    @keydown.enter.prevent="performAdvancedSearch"
                  />
                </div>
              </div>

              <div class="form-row">
                <div class="form-group">
                  <label for="adv-isbn">ISBN</label>
                  <input
                    id="adv-isbn"
                    aria-label="ISBN"
                    v-model="advancedSearchParams.isbn"
                    type="text"
                    placeholder="e.g., 9780441172719"
                    class="form-input"
                    @keydown.enter.prevent="performAdvancedSearch"
                  />
                  <div class="form-hint">
                    <div>Include hyphens or omit them — both work.</div>
                    <div v-if="convertedIsbn" class="small-note">
                      Converted ISBN-13: {{ convertedIsbn }}
                    </div>
                  </div>
                </div>

                <div class="form-group">
                  <label for="adv-asin">ASIN</label>
                  <input
                    id="adv-asin"
                    aria-label="ASIN"
                    v-model="advancedSearchParams.asin"
                    type="text"
                    placeholder="e.g., B08G9PRS1K"
                    class="form-input"
                    @keydown.enter.prevent="performAdvancedSearch"
                  />
                  <div class="form-hint">ASINs are case-insensitive; remove spaces.</div>
                </div>

                <div class="form-group">
                  <label for="adv-language">Language</label>
                  <select
                    id="adv-language"
                    aria-label="Filter results by language"
                    v-model="preferredSearchLanguage"
                    class="form-input"
                  >
                    <option
                      v-for="option in preferredSearchLanguageOptions"
                      :key="option.value"
                      :value="option.value"
                    >
                      {{ option.label }}
                    </option>
                  </select>
                </div>

                <div class="form-group region-display-group">
                  <label for="adv-region">Region</label>
                  <select
                    id="adv-region"
                    aria-label="Select Audible search region"
                    v-model="selectedSearchRegion"
                    class="form-input"
                    data-testid="advanced-search-region"
                  >
                    <option
                      v-for="option in searchRegionOptions"
                      :key="option.value"
                      :value="option.value"
                    >
                      {{ option.label }}
                    </option>
                  </select>
                </div>
              </div>

              <div v-if="advancedSearchError" class="error-message">
                <PhWarningCircle />
                {{ advancedSearchError }}
              </div>

              <div class="advanced-search-actions">
                <div class="advanced-search-buttons">
                  <button
                    type="button"
                    @click="clearAdvancedSearch"
                    class="btn-secondary"
                    aria-label="Clear advanced search"
                  >
                    <PhArrowClockwise /> Clear
                  </button>
                  <button
                    type="submit"
                    :disabled="!isValidAdvancedSearch || isSearching"
                    class="btn-primary"
                    aria-label="Execute advanced search"
                  >
                    <PhSpinner v-if="isSearching" class="ph-spin" />
                    <PhMagnifyingGlass v-else />
                    {{ isSearching ? 'Searching...' : 'Search' }}
                  </button>
                </div>
              </div>
            </form>
            <div class="divider" aria-hidden="true"></div>
          </div>
          <!-- advanced-search-section -->

          <div
            v-if="!showAdvancedSearch"
            id="unified-search-hint"
            class="search-hint"
            role="status"
            aria-live="polite"
          >
            <PhInfo />
            <span v-if="!searchQuery.trim()" class="search-prefix-hint">
              Use prefixes for precise searches: <strong>ASIN:</strong>, <strong>AUTHOR:</strong>,
              <strong>TITLE:</strong>, <strong>ISBN:</strong>, or <strong>SERIES:</strong>
            </span>
            <span v-else-if="searchType === 'asin'">Searching by ASIN</span>
            <span v-else-if="searchType === 'title'">
              <span v-if="searchQuery.toUpperCase().startsWith('TITLE:')">Searching by title</span>
              <span v-else-if="searchQuery.toUpperCase().startsWith('AUTHOR:')"
                >Searching by author</span
              >
              <span v-else-if="searchQuery.toUpperCase().startsWith('SERIES:')"
                >Searching by series</span
              >
              <span v-else>Searching by title</span>
            </span>
            <span v-else-if="searchType === 'isbn'">Searching by ISBN</span>
          </div>

          <div
            v-if="!showAdvancedSearch && searchError"
            class="error-message"
            role="alert"
            aria-live="assertive"
          >
            <PhWarningCircle />
            {{ searchError }}
          </div>
        </div>
      </div>
    </div>
    <!-- Loading State -->
    <div v-if="isSearching && !hasResults" class="loading-results">
      <div class="loading-spinner">
        <PhSpinner class="ph-spin" />
        <p>Searching for audiobooks...</p>
        <p v-if="searchStatus" class="search-status">{{ searchStatus }}</p>
      </div>
    </div>

    <!-- Results Section -->
    <div v-if="hasResults" class="search-results">
      <!-- ASIN Results -->
      <div v-if="searchType === 'asin' && audibleResult">
        <h2>Audiobook Found</h2>
        <div class="title-results">
          <div class="title-result-card">
            <div class="result-poster">
              <img
                v-if="audibleResult.imageUrl"
                :src="getAudibleResultImageSrc()"
                :alt="audibleResult.title"
                loading="lazy"
                decoding="async"
                @error="handleLazyImageError"
              />
              <span v-else>
                <img
                  :src="getPlaceholderUrl()"
                  alt="Cover unavailable"
                  loading="lazy"
                  class="placeholder-cover-image"
                  decoding="async"
                  @error="handleLazyImageError"
                />
              </span>
            </div>
            <div class="result-info">
              <h3>{{ safeText(audibleResult.title) }}</h3>
              <p class="result-author">
                by
                {{
                  (audibleResult.authors || []).map((author) => safeText(author)).join(', ') ||
                  'Unknown Author'
                }}
              </p>
              <p v-if="audibleResult.narrators?.length" class="result-narrator">
                Narrated by
                {{ audibleResult.narrators.map((narrator) => safeText(narrator)).join(', ') }}
              </p>

              <div class="result-stats">
                <span
                  v-if="
                    audibleResult.runtime ||
                    audibleResult.searchResult?.runtime ||
                    audibleResult.searchResult?.lengthMinutes
                  "
                  class="stat-item"
                >
                  <PhClock />
                  {{
                    formatRuntime(
                      (audibleResult.runtime ??
                        audibleResult.searchResult?.lengthMinutes ??
                        audibleResult.searchResult?.runtime ??
                        0)!,
                    )
                  }}
                </span>
                <span
                  v-if="audibleResult.language || audibleResult.searchResult?.language"
                  class="stat-item"
                >
                  <PhGlobe />
                  {{
                    capitalizeLanguage(
                      (audibleResult.language || audibleResult.searchResult?.language)!,
                    )
                  }}
                </span>
              </div>

              <!-- Series badges on separate line -->
              <div
                v-if="
                  audibleResult.seriesList?.length ||
                  audibleResult.series ||
                  audibleResult.searchResult?.series ||
                  audibleResult.searchResult?.seriesNumber
                "
                class="result-series"
              >
                <span
                  class="series-badge"
                  :title="
                    audibleResult.seriesList?.length
                      ? audibleResult.seriesList.join(', ')
                      : audibleResult.searchResult?.seriesList?.length
                        ? audibleResult.searchResult.seriesList.join(', ')
                        : (audibleResult.series || audibleResult.searchResult?.series) +
                          (audibleResult.searchResult?.seriesNumber
                            ? ` #${audibleResult.searchResult.seriesNumber}`
                            : '')
                  "
                >
                  <PhBook />
                  {{
                    safeText(
                      audibleResult.series ??
                        audibleResult.searchResult?.series ??
                        (audibleResult.seriesList && audibleResult.seriesList[0]),
                    )
                  }}<span v-if="audibleResult.searchResult?.seriesNumber">
                    #{{ audibleResult.searchResult.seriesNumber }}</span
                  >
                </span>
              </div>

              <!-- Metadata badges -->
              <div class="metadata-badges">
                <span v-if="audibleResult.publisher" class="metadata-badge">
                  <PhBuilding />
                  {{ safeText(audibleResult.publisher) }}
                </span>
                <span v-if="audibleResult.publishedDate" class="metadata-badge">
                  <PhCalendar />
                  {{ formatDate(audibleResult.publishedDate) }}
                </span>
                <span v-else-if="audibleResult.publishYear" class="metadata-badge">
                  <PhCalendar />
                  {{ audibleResult.publishYear }}
                </span>
              </div>

              <div class="result-meta">
                <a
                  v-if="audibleResultSharedSourceUrl"
                  :href="audibleResultSharedSourceUrl"
                  target="_blank"
                  rel="noopener noreferrer"
                  class="metadata-source-link source-link combined-source-link"
                  :data-source="audibleResultMetadataSource"
                >
                  <PhGlobe />
                  <PhCloud />
                  Audible
                </a>
                <template v-else>
                  <a
                    v-if="
                      audibleResult.asin &&
                      ((audibleResult.metadataSource &&
                        audibleResult.metadataSource.toLowerCase().includes('audible')) ||
                        (audibleResult.searchResult &&
                          audibleResult.searchResult.metadataSource &&
                          audibleResult.searchResult.metadataSource
                            .toLowerCase()
                            .includes('audible')))
                    "
                    :href="audibleResultProductUrl"
                    target="_blank"
                    rel="noopener noreferrer"
                    class="metadata-source-link"
                    :data-source="
                      audibleResult.metadataSource ||
                      (audibleResult.searchResult && audibleResult.searchResult.metadataSource)
                    "
                  >
                    <PhGlobe />
                    Audible
                  </a>
                  <span
                    v-else-if="audibleResult.metadataSource"
                    class="metadata-source-badge"
                    :data-source="audibleResult.metadataSource"
                  >
                    <PhGlobe />
                    Metadata: {{ audibleResult.metadataSource }}
                  </span>

                  <a
                    v-if="audibleResultSourceUrl"
                    :href="audibleResultSourceUrl"
                    target="_blank"
                    rel="noopener noreferrer"
                    class="source-link"
                  >
                    <PhCloud />
                    {{
                      isAudibleHost(audibleResultSourceUrl)
                        ? 'Audible'
                        : `Source: ${audibleResult.source}`
                    }}
                  </a>
                  <span v-else-if="audibleResult.source" class="source-badge">
                    <PhCloud />
                    Source: {{ audibleResult.source }}
                  </span>
                </template>
                <span v-if="audibleResult.explicit" class="metadata-badge">
                  <PhWarning />
                  Explicit
                </span>
                <span v-if="audibleResult.abridged" class="metadata-badge">
                  <PhScissors />
                  Abridged
                </span>
              </div>
            </div>
            <div class="result-actions">
              <button
                :class="[
                  'btn',
                  audibleResult &&
                  ((audibleResult.asin && addedAsins.has(audibleResult.asin)) ||
                    (audibleResult.openLibraryId &&
                      addedOpenLibraryIds.has(audibleResult.openLibraryId)))
                    ? 'btn-success'
                    : 'btn-primary',
                ]"
                @click="addToLibrary(audibleResult)"
                :disabled="
                  !!(
                    (audibleResult &&
                      ((audibleResult.asin && addedAsins.has(audibleResult.asin)) ||
                        (audibleResult.openLibraryId &&
                          addedOpenLibraryIds.has(audibleResult.openLibraryId)))) ||
                    (audibleResult &&
                      audibleResult.metadataSource &&
                      audibleResult.metadataSource.toLowerCase().includes('openlibrary') &&
                      !canAddOpenLibraryResult)
                  )
                "
              >
                <component
                  :is="
                    audibleResult &&
                    ((audibleResult.asin && addedAsins.has(audibleResult.asin)) ||
                      (audibleResult.openLibraryId &&
                        addedOpenLibraryIds.has(audibleResult.openLibraryId)))
                      ? PhCheck
                      : PhPlus
                  "
                />
                {{
                  !!(
                    audibleResult &&
                    ((audibleResult.asin && addedAsins.has(audibleResult.asin)) ||
                      (audibleResult.openLibraryId &&
                        addedOpenLibraryIds.has(audibleResult.openLibraryId)))
                  )
                    ? 'Added'
                    : 'Add to Library'
                }}
              </button>
            </div>
          </div>
        </div>
      </div>

      <!-- ISBN Auto-processing Status -->
      <div v-if="searchType === 'isbn' && isSearching" class="inline-status">
        <PhSpinner class="ph-spin" />
        <span>Searching Amazon/Audible for audiobook...</span>
      </div>
      <div
        v-else-if="searchType === 'isbn' && !isSearching && isbnLookupMessage"
        class="inline-status"
        :class="{ warning: isbnLookupWarning }"
      >
        <component :is="isbnLookupWarning ? PhWarningCircle : PhInfo" />
        <span>{{ isbnLookupMessage }}</span>
      </div>

      <!-- Title Search Results -->
      <div v-if="searchType === 'title' && titleResults.length > 0">
        <h2>
          Found {{ totalTitleResultsCount }} Book{{ totalTitleResultsCount === 1 ? '' : 's' }}
        </h2>
        <div v-if="hasTitleResultControls" class="results-controls">
          <div
            v-if="isAudiblePaged && Math.ceil(audibleTotal / audibleLimit) > 1"
            class="audible-pagination"
          >
            <button
              class="btn btn-secondary"
              :disabled="audiblePage <= 1"
              @click.prevent="changeAudiblePage(audiblePage - 1)"
            >
              Prev
            </button>
            <span class="page-indicator"
              >Page {{ audiblePage }} of
              {{ Math.max(1, Math.ceil(audibleTotal / audibleLimit)) }}</span
            >
            <button
              class="btn btn-secondary"
              :disabled="audiblePage * audibleLimit >= audibleTotal"
              @click.prevent="changeAudiblePage(audiblePage + 1)"
            >
              Next
            </button>
          </div>

          <div v-if="!isAudiblePaged && totalPages > 1" class="client-pagination-controls">
            <div class="pagination-settings">
              <label class="small-label">Results per page</label>
              <select
                v-model.number="resultsPerPage"
                @change="
                  () => {
                    currentAdvancedPage = 1
                  }
                "
                class="form-input small-select"
              >
                <option :value="10">10</option>
                <option :value="25">25</option>
                <option :value="50">50</option>
                <option :value="100">100</option>
              </select>
            </div>

            <div class="pagination-nav">
              <button
                class="btn btn-secondary"
                :disabled="currentAdvancedPage <= 1"
                @click.prevent="currentAdvancedPage--"
              >
                <PhCaretLeft />
                Prev
              </button>
              <span class="page-indicator">Page {{ currentAdvancedPage }} of {{ totalPages }}</span>
              <button
                class="btn btn-secondary"
                :disabled="currentAdvancedPage >= totalPages"
                @click.prevent="currentAdvancedPage++"
              >
                Next
                <PhCaretRight />
              </button>
            </div>
          </div>
        </div>
        <div class="title-results">
          <div v-for="book in displayedTitleResults" :key="book.key" class="title-result-card">
            <div class="result-poster">
              <img
                :src="getCoverUrl(book)"
                :alt="book.title"
                loading="lazy"
                decoding="async"
                @error="handleLazyImageError"
              />
            </div>
            <div class="result-info">
              <h3>
                {{ safeText(book.title) }}
              </h3>
              <p v-if="book.searchResult?.subtitle" class="result-subtitle">
                {{ safeText(book.searchResult.subtitle) }}
              </p>
              <p class="result-author">by {{ formatAuthors(book) }}</p>

              <!-- Audiobook metadata from enriched results -->
              <p v-if="book.searchResult?.narrator" class="result-narrator">
                Narrated by {{ book.searchResult.narrator }}
              </p>

              <div class="result-stats">
                <span
                  v-if="book.searchResult?.runtime || book.searchResult?.lengthMinutes"
                  class="stat-item"
                >
                  <PhClock />
                  {{
                    formatRuntime(
                      book.searchResult?.lengthMinutes ?? book.searchResult?.runtime ?? 0,
                    )
                  }}
                </span>
                <span v-if="book.searchResult?.language" class="stat-item">
                  <PhGlobe />
                  {{ capitalizeLanguage(book.searchResult.language) }}
                </span>
              </div>

              <!-- Series badges on separate line -->
              <div
                v-if="
                  (Array.isArray(book.searchResult?.seriesList) &&
                    book.searchResult.seriesList.some(
                      (s) => typeof s === 'string' && s.trim().length > 0,
                    )) ||
                  (typeof book.searchResult?.series === 'string' &&
                    book.searchResult.series.trim().length > 0)
                "
                class="result-series"
              >
                <span
                  class="series-badge"
                  :title="
                    Array.isArray(book.searchResult?.seriesList) &&
                    book.searchResult.seriesList.some(
                      (s) => typeof s === 'string' && s.trim().length > 0,
                    )
                      ? book.searchResult.seriesList
                          .filter((s) => typeof s === 'string' && s.trim().length > 0)
                          .join(', ')
                      : `${book.searchResult?.series}${book.searchResult?.seriesNumber ? ` #${book.searchResult.seriesNumber}` : ''}`
                  "
                >
                  <PhBook />
                  {{
                    safeText(
                      Array.isArray(book.searchResult?.seriesList) &&
                        book.searchResult.seriesList.some(
                          (s) => typeof s === 'string' && s.trim().length > 0,
                        )
                        ? book.searchResult.seriesList.find(
                            (s) => typeof s === 'string' && s.trim().length > 0,
                          )
                        : book.searchResult?.series,
                    )
                  }}<span v-if="book.searchResult?.seriesNumber">
                    #{{ book.searchResult.seriesNumber }}</span
                  >
                </span>
              </div>

              <!-- Metadata badges -->
              <div class="metadata-badges">
                <span v-if="book.publisher?.length" class="metadata-badge">
                  <PhBuilding />
                  {{ safeText(book.publisher[0]) }}
                </span>
                <span v-if="book.searchResult?.publishedDate" class="metadata-badge">
                  <PhCalendar />
                  {{ formatDate(book.searchResult.publishedDate) }}
                </span>
                <span v-else-if="book.first_publish_year" class="metadata-badge">
                  <PhCalendar />
                  {{ book.first_publish_year }}
                </span>
                <span
                  v-if="
                    book.searchResult?.id &&
                    ((book.metadataSource &&
                      book.metadataSource.toLowerCase().includes('openlibrary')) ||
                      (book.searchResult?.metadataSource &&
                        book.searchResult.metadataSource.toLowerCase().includes('openlibrary')))
                  "
                  class="metadata-badge"
                >
                  <PhBarcode />
                  {{ book.searchResult.id }}
                </span>
              </div>

              <div class="result-meta">
                <a
                  v-if="getSharedMetadataSourceUrl(book)"
                  :href="getSharedMetadataSourceUrl(book)"
                  target="_blank"
                  rel="noopener noreferrer"
                  class="metadata-source-link source-link combined-source-link"
                  :data-source="getBookMetadataSource(book)"
                >
                  <PhGlobe />
                  <PhCloud />
                  {{ getMetadataSourceLabel(getBookMetadataSource(book)) }}
                </a>
                <template v-else>
                  <a
                    v-if="getBookMetadataSource(book) && getMetadataSourceUrl(book)"
                    :href="getMetadataSourceUrl(book)"
                    target="_blank"
                    rel="noopener noreferrer"
                    class="metadata-source-link"
                    :data-source="getBookMetadataSource(book)"
                  >
                    <PhGlobe />
                    {{ getMetadataSourceLabel(getBookMetadataSource(book)) }}
                  </a>
                  <span
                    v-else-if="getBookMetadataSource(book)"
                    class="metadata-source-badge"
                    :data-source="getBookMetadataSource(book)"
                  >
                    <PhGlobe />
                    {{ getMetadataSourceLabel(getBookMetadataSource(book)) }}
                  </span>

                  <!-- Prefer to show Audible as product source when metadata comes from Audible -->
                  <a
                    v-if="getSourceUrl(book)"
                    :href="getSourceUrl(book)"
                    target="_blank"
                    rel="noopener noreferrer"
                    class="source-link"
                  >
                    <PhCloud />
                    {{
                      isAudibleHost(getSourceUrl(book))
                        ? 'Audible'
                        : book.searchResult?.source || getBookMetadataSource(book) || 'OpenLibrary'
                    }}
                  </a>
                  <span v-else-if="book.searchResult?.source" class="source-badge">
                    <PhCloud />
                    Source: {{ book.searchResult.source }}
                  </span>
                </template>
              </div>
            </div>

            <div class="result-actions">
              <button
                :class="[
                  'btn',
                  !!(getAsin(book) && addedAsins.has(getAsin(book)!)) ||
                  (!!book.searchResult?.id && addedOpenLibraryIds.has(book.searchResult.id))
                    ? 'btn-success'
                    : 'btn-primary',
                ]"
                @click="selectTitleResult(book)"
                :disabled="
                  !!(getAsin(book) && addedAsins.has(getAsin(book)!)) ||
                  (!!book.searchResult?.id && addedOpenLibraryIds.has(book.searchResult.id))
                "
              >
                <component
                  :is="
                    !!(getAsin(book) && addedAsins.has(getAsin(book)!)) ||
                    (!!book.searchResult?.id && addedOpenLibraryIds.has(book.searchResult.id))
                      ? PhCheck
                      : PhPlus
                  "
                />
                {{
                  !!(getAsin(book) && addedAsins.has(getAsin(book)!)) ||
                  (!!book.searchResult?.id && addedOpenLibraryIds.has(book.searchResult.id))
                    ? 'Added'
                    : 'Add to Library'
                }}
              </button>
            </div>
          </div>
        </div>
      </div>
    </div>

    <!-- No Results -->
    <EmptyState
      v-if="searchType === 'asin' && !audibleResult && !isSearching && !isCancelled && searchQuery"
      title="No Audiobook Found"
      :message="emptyStateAsinMessage"
    >
      <template #icon>
        <PhMagnifyingGlass :size="48" />
      </template>
      <template #action>
        <button class="btn btn-primary" @click="tryTitleSearch">
          <PhMagnifyingGlass />
          Try Title Search
        </button>
      </template>
    </EmptyState>

    <EmptyState
      v-if="
        searchType === 'title' &&
        titleResults.length === 0 &&
        !isSearching &&
        !isCancelled &&
        searchQuery
      "
      :title="!asinFilteringApplied ? 'No Books Found' : 'No Audiobook Matches'"
      :message="emptyStateTitleMessage"
    >
      <template #icon>
        <PhBook :size="48" />
      </template>
    </EmptyState>

    <!-- Error States -->
    <div v-if="hasError" class="error-state">
      <div class="error-icon">
        <PhWarningCircle />
      </div>
      <h2>Search Error</h2>
      <p>{{ errorMessage }}</p>
      <div class="quick-actions">
        <button class="btn btn-primary" @click="retrySearch">
          <PhArrowClockwise />
          Try Again
        </button>
      </div>
    </div>
  </div>

  <!-- Add to Library Modal -->
  <AddLibraryModal
    :visible="showAddLibraryModal"
    :book="selectedBookForLibrary"
    :resolved-image-url="getSelectedBookImageSrc()"
    @close="closeAddLibraryModal"
    @added="handleLibraryAdded"
  />

  <!-- Confirm dialog removed: using centralized showConfirm service mounted in App.vue -->
</template>

<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted, watch, nextTick } from 'vue'
import {
  PhPlusCircle,
  PhSpinner,
  PhMagnifyingGlass,
  PhInfo,
  PhWarningCircle,
  PhClock,
  PhGlobe,
  PhCheck,
  PhPlus,
  PhBook,
  PhArrowLeft,
  PhArrowClockwise,
  PhCloud,
  PhFunnelSimple,
  PhCalendar,
  PhBuilding,
  PhBarcode,
  PhWarning,
  PhScissors,
  PhCaretLeft,
  PhCaretRight,
} from '@phosphor-icons/vue'
import { useRoute, useRouter } from 'vue-router'
import type {
  AudibleBookMetadata,
  SearchResult,
  Audiobook,
  AudibleAuthor,
  AudibleNarrator,
  AudibleGenre,
  AudibleSearchResult,
} from '@/types'
import { apiService } from '@/services/api'
import { getPlaceholderUrl } from '@/utils/placeholder'
import { handleImageError } from '@/utils/imageFallback'
import type { OpenLibraryBook } from '@/services/openlibrary'
import { openLibraryService } from '@/services/openlibrary'

// Extend Window interface for debug helper used during development
declare global {
  interface Window {
    addnew_rawDebugResults?: unknown
  }
}
import { isbnService, type ISBNBook } from '@/services/isbn'
import { signalRService } from '@/services/signalr'
import { useConfigurationStore } from '@/stores/configuration'
import { useRootFoldersStore } from '@/stores/rootFolders'
import { useLibraryStore } from '@/stores/library'
import AddLibraryModal from '@/components/domain/audiobook/AddLibraryModal.vue'
import { useToast } from '@/services/toastService'
import { safeText } from '@/utils/textUtils'
import { logger } from '@/utils/logger'
import { buildAmazonProductUrl, buildAudibleProductUrl } from '@/utils/marketDomains'
import { useSearch } from '@/composables/useSearch'
import { useLibraryCheck } from '@/composables/useLibraryCheck'
import { useAdvancedSearch } from '@/composables/useAdvancedSearch'
import { EmptyState } from '@/components/base'
import { formatDate, formatRuntime, capitalizeLanguage } from '@/utils/searchResultFormatting'
import {
  extractAuthors,
  normalizeSource,
  getOptionalString,
  canAddOpenLibraryResult as canAddOpenLibraryResultHelper,
} from '@/utils/searchResultHelpers'
import { useProtectedImages, isLikelyBackendImageUrl } from '@/composables/useProtectedImages'
import {
  getPrimaryPreferredSearchLanguageForRegion,
  getPreferredSearchLanguageFilter,
  normalizePreferredSearchLanguage,
  normalizeSearchResultLanguage,
  normalizeSearchRegion,
  preferredSearchLanguageOptions,
  searchRegionOptions,
} from '@/utils/languageMapping'

// Helper to normalize ISBN (strip hyphens/spaces, prefer ISBN-13 if present)
function normalizeIsbn(input: unknown): string | undefined {
  if (!input) return undefined
  if (Array.isArray(input)) {
    const norm = input.map(normalizeIsbn).find(Boolean)
    return norm
  }
  const isbn = String(input).replace(/[-\s]/g, '')
  if (/^\d{13}$/.test(isbn)) return isbn
  if (/^\d{9}[\dX]$/i.test(isbn)) {
    // Convert ISBN-10 to ISBN-13
    const base = '978' + isbn.slice(0, 9)
    let sum = 0
    for (let i = 0; i < 12; i++) {
      const digit = parseInt(base.charAt(i), 10)
      if (isNaN(digit)) return undefined
      sum += i % 2 === 0 ? digit : digit * 3
    }
    const check = (10 - (sum % 10)) % 10
    return base + String(check)
  }
  return undefined
}

// Extended type for title search results that includes search metadata
type TitleSearchResult = Omit<OpenLibraryBook, 'isbn'> & {
  searchResult?: Partial<SearchResult> // Store the enriched SearchResult from intelligent search
  imageUrl?: string // For results that have direct image URLs
  metadataSource?: string // Store which metadata source was used
  isbn?: string | string[] // Normalized ISBN (string or array for OpenLibrary compatibility)
  region?: string // Audible/Amazon market region from the search result
}

// Loose result type used for normalization of diverse backend shapes
type LooseResult = Partial<SearchResult> & Record<string, unknown>

const ASIN_KEYS = ['asin', 'Asin'] as const
const METADATA_SOURCE_KEYS = ['metadataSource'] as const
const PRODUCT_LINK_KEYS = ['productUrl', 'link', 'Link', 'sourceLink'] as const
const PUBLISH_DATE_KEYS = [
  'publishedDate',
  'PublishedDate',
  'releaseDate',
  'ReleaseDate',
  'release_date',
  'Release_date',
  'publishDate',
  'PublishDate',
] as const
const SOURCE_KEYS = ['source', 'Source'] as const

interface AudibleSeriesEntry {
  name?: string
  position?: string | number
}

interface AudibleMetadataPayload {
  asin?: string
  region?: string
  title?: string
  subtitle?: string
  authors?: AudibleAuthor[]
  narrators?: AudibleNarrator[]
  publisher?: string
  releaseDate?: string
  publishDate?: string
  publishedDate?: string
  description?: string
  imageUrl?: string
  image?: string
  runtime?: number
  lengthMinutes?: number
  language?: string
  genres?: AudibleGenre[]
  series?: AudibleSeriesEntry[]
  bookFormat?: string
  isbn?: string
}

interface AudibleMetadataResponse {
  metadata?: AudibleMetadataPayload
  source?: string
}

const route = useRoute()
const router = useRouter()
const configStore = useConfigurationStore()
const rootFoldersStore = useRootFoldersStore()
const libraryStore = useLibraryStore()
const toast = useToast()
const { getProtectedImageSrc } = useProtectedImages()

// Initialize composables
const {
  searchQuery,
  searchLanguage,
  preferredSearchLanguage,
  searchType,
  isSearching,
  searchError,
  searchStatus,
  searchPlaceholder,
  handleSearchInput,
  performSearch: runSearch,
  cancelSearch,
} = useSearch()

const { addedAsins, addedOpenLibraryIds, checkExistingInLibrary } = useLibraryCheck()

// Metadata sources are handled by the search composable; no local variable needed

// Use region-aware helpers from utils/marketDomains

// Initialize advanced search composable for form state and persistence
const {
  showAdvancedSearch,
  advancedSearchParams,
  isValidAdvancedSearch,
  toggleAdvancedSearch,
  resetAdvancedSearch,
} = useAdvancedSearch()

const advancedSearchError = ref('')

function tryTitleSearch() {
  searchQuery.value = ''
  searchType.value = 'title'
}

// Audible pagination state for advanced searches
const audiblePage = ref(1)
const audibleLimit = ref(50)
const audibleTotal = ref(0)
const isAudiblePaged = ref(false)
const allAudibleResults = ref<AudibleSearchResult[]>([])

logger.debug('AddNewView component loaded')
logger.debug('libraryStore:', libraryStore)

onMounted(() => {
  // If URL has a page query parameter (page=[num]), initialize audiblePage
  const p = route.query.page
  if (p) {
    const np = Number(p)
    if (!isNaN(np) && np > 0) audiblePage.value = np
  }
})

// Library checking functions - now handled by useLibraryCheck composable

// Unified Search - now handled by useSearch composable
const isCancelled = ref(false)
let unsubscribeSearchProgress: (() => void) | null = null
let stopWatchingLibrary: (() => void) | null = null

// Results
const audibleResult = ref<AudibleBookMetadata | null>(null)
const titleResults = ref<TitleSearchResult[]>([])
const resolvedAsins = ref<Record<string, string>>({})
const asinFilteringApplied = ref(false)
const isbnResult = ref<ISBNBook | null>(null) // retained for potential enrichment but not directly rendered
const isbnLookupMessage = ref('')
const isbnLookupWarning = ref(false)
const totalTitleResultsCount = ref<number>(0)

const selectedSearchRegion = computed({
  get: () => normalizeSearchRegion(searchLanguage.value),
  set: (value: string) => {
    const normalizedRegion = normalizeSearchRegion(value)
    searchLanguage.value = normalizedRegion
    preferredSearchLanguage.value = getPrimaryPreferredSearchLanguageForRegion(normalizedRegion)
  },
})

const audibleResultRegion = computed(() => audibleResult.value?.region || searchLanguage.value)

const audibleResultMetadataSource = computed(() => {
  return audibleResult.value?.metadataSource ?? audibleResult.value?.searchResult?.metadataSource
})

const audibleResultProductUrl = computed(() =>
  audibleResult.value?.asin
    ? buildAudibleProductUrl(audibleResult.value.asin, audibleResultRegion.value)
    : undefined,
)

const audibleResultSourceUrl = computed(() => {
  const result = audibleResult.value
  if (!result?.sourceLink) return undefined
  if (result.asin && isAudibleHost(result.sourceLink)) {
    return buildAudibleProductUrl(result.asin, audibleResultRegion.value)
  }

  return result.sourceLink
})

const audibleResultSharedSourceUrl = computed(() => {
  const metadataSource = audibleResultMetadataSource.value
  if (!metadataSource?.toLowerCase().includes('audible')) return undefined
  if (!audibleResult.value?.asin) return undefined

  return areSameUrl(audibleResultProductUrl.value, audibleResultSourceUrl.value)
    ? audibleResultProductUrl.value
    : undefined
})

// Parsed search query components (for error messages)
const asinQuery = ref('')
const titleQuery = ref('')
const authorQuery = ref('')

// Empty state messages (computed to avoid template string escaping issues)
const emptyStateAsinMessage = computed(() => {
  return `No audiobook was found with ASIN "${asinQuery.value}". Please check the ASIN and try again.`
})

const emptyStateTitleMessage = computed(() => {
  if (!asinFilteringApplied.value) {
    const authorPart = authorQuery.value ? ` by ${authorQuery.value}` : ''
    return `No books were found matching "${titleQuery.value}"${authorPart}. Try different search terms.`
  }
  return 'No audiobooks found. Try refining your search terms.'
})

// Pagination / candidate limits for advanced results
const resultsPerPage = ref<number>(50)
const currentAdvancedPage = ref<number>(1)

const totalPages = computed(() =>
  Math.max(
    1,
    Math.ceil((totalTitleResultsCount.value || titleResults.value.length) / resultsPerPage.value),
  ),
)

const hasTitleResultControls = computed(() => {
  if (isAudiblePaged.value) {
    return Math.ceil(audibleTotal.value / audibleLimit.value) > 1
  }

  return totalPages.value > 1
})

const pagedTitleResults = computed(() => {
  const start = (currentAdvancedPage.value - 1) * resultsPerPage.value
  return titleResults.value.slice(start, start + resultsPerPage.value)
})

const displayedTitleResults = computed(() => {
  // If the results come from Audible paged API, show full list (server-side paging)
  if (isAudiblePaged.value) return titleResults.value
  return pagedTitleResults.value
})

const selectedLanguageFilter = computed(() =>
  getPreferredSearchLanguageFilter(preferredSearchLanguage.value),
)

// Compute converted ISBN-13 for display when user enters an ISBN-10
const convertedIsbn = computed(() => {
  try {
    const raw = (advancedSearchParams.value && advancedSearchParams.value.isbn) || ''
    const cleaned = String(raw).replace(/[-\s]/g, '').toUpperCase()
    if (!cleaned) return ''

    // If already a valid ISBN-13, show it
    if (/^\d{13}$/.test(cleaned)) return cleaned

    // If ISBN-10 (may end with X), convert to ISBN-13 (978 prefix)
    if (/^\d{9}[\dX]$/i.test(cleaned)) {
      const isbn10 = cleaned
      const base = '978' + isbn10.slice(0, 9)
      let sum = 0
      for (let i = 0; i < 12; i++) {
        const digit = parseInt(base.charAt(i), 10)
        if (isNaN(digit)) return ''
        sum += i % 2 === 0 ? digit : digit * 3
      }
      const check = (10 - (sum % 10)) % 10
      return base + String(check)
    }

    return ''
  } catch {
    return ''
  }
})

// Cache for best cover selection per book key
const coverSelection = ref<Record<string, string>>({})
const coverSelectionInFlight = ref<Set<string>>(new Set())
const coverSelectionAttempted = ref<Set<string>>(new Set())

// General state
const errorMessage = ref('')

// Modal state
const showAddLibraryModal = ref(false)
const selectedBookForLibrary = ref<AudibleBookMetadata>({} as AudibleBookMetadata)

// Local storage keys for persisting search results
const RESULTS_KEY = 'listenarr.addNewResults'
const SEARCH_TYPE_KEY = 'listenarr.addNewSearchType'
const TITLE_RESULTS_COUNT_KEY = 'listenarr.addNewTitleResultsCount'
const ASIN_FILTERING_KEY = 'listenarr.addNewAsinFiltering'
const RESOLVED_ASINS_KEY = 'listenarr.addNewResolvedAsins'
const ADDED_ASINS_KEY = 'listenarr.addNewAddedAsins'
const ADDED_OLIDS_KEY = 'listenarr.addNewAddedOLIDs'

// Initialize search results from localStorage
try {
  const storedResults = localStorage.getItem(RESULTS_KEY)
  if (storedResults) {
    const parsed = JSON.parse(storedResults)
    if (parsed.audibleResult) audibleResult.value = parsed.audibleResult
    if (parsed.titleResults) titleResults.value = parsed.titleResults
    if (parsed.isbnResult) isbnResult.value = parsed.isbnResult
  }

  const storedSearchType = localStorage.getItem(SEARCH_TYPE_KEY)
  if (storedSearchType) {
    searchType.value = storedSearchType as 'asin' | 'title' | 'isbn' | null
  }

  const storedCount = localStorage.getItem(TITLE_RESULTS_COUNT_KEY)
  if (storedCount) {
    totalTitleResultsCount.value = parseInt(storedCount, 10)
  }

  const storedFiltering = localStorage.getItem(ASIN_FILTERING_KEY)
  if (storedFiltering) {
    asinFilteringApplied.value = storedFiltering === 'true'
  }

  const storedResolved = localStorage.getItem(RESOLVED_ASINS_KEY)
  if (storedResolved) {
    resolvedAsins.value = JSON.parse(storedResolved)
  }

  const storedAdded = localStorage.getItem(ADDED_ASINS_KEY)
  if (storedAdded) {
    addedAsins.value = new Set(JSON.parse(storedAdded))
  }
  const storedAddedOl = localStorage.getItem(ADDED_OLIDS_KEY)
  if (storedAddedOl) {
    try {
      addedOpenLibraryIds.value = new Set(JSON.parse(storedAddedOl))
    } catch {}
  }
} catch (error) {
  logger.warn('Failed to restore persisted state:', error)
}

// Watch search results changes and persist to localStorage
watch([audibleResult, titleResults, isbnResult], () => {
  try {
    const results = {
      audibleResult: audibleResult.value,
      titleResults: titleResults.value,
      isbnResult: isbnResult.value,
    }
    localStorage.setItem(RESULTS_KEY, JSON.stringify(results))
  } catch {}
})

watch(searchType, (v) => {
  try {
    localStorage.setItem(SEARCH_TYPE_KEY, v || '')
  } catch {}
})

watch(totalTitleResultsCount, (v) => {
  try {
    localStorage.setItem(TITLE_RESULTS_COUNT_KEY, v.toString())
  } catch {}
})

watch(asinFilteringApplied, (v) => {
  try {
    localStorage.setItem(ASIN_FILTERING_KEY, v.toString())
  } catch {}
})

watch(
  resolvedAsins,
  (v) => {
    try {
      localStorage.setItem(RESOLVED_ASINS_KEY, JSON.stringify(v))
    } catch {}
  },
  { deep: true },
)

watch(
  addedAsins,
  (v) => {
    try {
      localStorage.setItem(ADDED_ASINS_KEY, JSON.stringify(Array.from(v)))
    } catch {}
  },
  { deep: true },
)

watch(
  addedOpenLibraryIds,
  (v) => {
    try {
      localStorage.setItem(ADDED_OLIDS_KEY, JSON.stringify(Array.from(v)))
    } catch {}
  },
  { deep: true },
)

// Scroll to top of results when page changes
watch(currentAdvancedPage, () => {
  nextTick(() => {
    const titleResultsElement = document.querySelector('.title-results')
    if (titleResultsElement) {
      const elementTop = titleResultsElement.getBoundingClientRect().top + window.scrollY
      window.scrollTo({ top: elementTop - 125, behavior: 'smooth' })
    }
  })
})

// Computed properties
const hasResults = computed(() => {
  return (
    (searchType.value === 'asin' && audibleResult.value) ||
    (searchType.value === 'title' && titleResults.value.length > 0) ||
    (searchType.value === 'isbn' && audibleResult.value)
  )
})

// Whether the currently selected audible/title result (OpenLibrary-shaped)
// is acceptable to add via the OpenLibrary flow (title + ISBN present).
const canAddOpenLibraryResult = computed(() => {
  try {
    // prefer the raw searchResult payload if present, otherwise the flattened audibleResult
    const candidate =
      (audibleResult.value && (audibleResult.value.searchResult ?? audibleResult.value)) || null
    return canAddOpenLibraryResultHelper(candidate)
  } catch {
    return false
  }
})

const hasError = computed(() => {
  return Boolean(errorMessage.value)
})

// Unified Search Methods - now handled by useSearch composable

const getResultLanguageKey = (result: Partial<SearchResult> | LooseResult): string | undefined => {
  const candidate = result as LooseResult
  const nested = candidate.searchResult as LooseResult | undefined
  const rawLanguage =
    candidate.language ?? candidate.Language ?? nested?.language ?? nested?.Language

  if (Array.isArray(rawLanguage)) {
    for (const entry of rawLanguage) {
      if (typeof entry === 'string') {
        const normalized = normalizeSearchResultLanguage(entry)
        if (normalized) return normalized
      }
    }
    return undefined
  }

  if (typeof rawLanguage === 'string') {
    return normalizeSearchResultLanguage(rawLanguage)
  }

  return undefined
}

const filterResultsBySelectedLanguage = <T extends Partial<SearchResult> | LooseResult>(
  results: T[],
): T[] => {
  const filter = selectedLanguageFilter.value
  if (!filter) return results
  return results.filter((result) => getResultLanguageKey(result) === filter)
}

const getDateRecordValue = (
  record: Record<string, unknown> | undefined,
  keys: readonly string[],
): string | undefined => {
  if (!record) return undefined

  for (const key of keys) {
    const value = record[key]
    if (typeof value === 'string' && value.trim()) return value.trim()
    if (value instanceof Date) return value.toISOString().split('T')[0]
  }

  return undefined
}

const getResultPublishYear = (result: LooseResult): number | undefined => {
  const nested = getNestedResultRecord(result)
  const publishDateStr =
    getDateRecordValue(result, PUBLISH_DATE_KEYS) ?? getDateRecordValue(nested, PUBLISH_DATE_KEYS)
  if (!publishDateStr) return undefined

  const year = parseInt(publishDateStr.substring(0, 4), 10)
  return Number.isNaN(year) ? undefined : year
}

const handleAdvancedSearchResults = async (results: Array<Partial<SearchResult> | LooseResult>) => {
  const filteredResults = filterResultsBySelectedLanguage(results)

  // Convert search results to title results format
  titleResults.value = []
  audibleResult.value = null
  searchType.value = 'title'
  allAudibleResults.value = filteredResults.filter((result) => {
    const rr = result as Record<string, unknown>
    return Boolean(rr['asin'] ?? rr['Asin'] ?? rr['isEnriched'])
  }) as unknown as AudibleSearchResult[]

  if (!filteredResults.length) {
    totalTitleResultsCount.value = 0
    searchStatus.value = ''
    await checkExistingInLibrary()
    toast.info('No results found for the selected language', 'Search')
    return
  }

  for (const result of filteredResults) {
    const r = result as LooseResult
    const rr = r as Record<string, unknown>
    const nestedSearchResult = getNestedResultRecord(rr)

    // Normalize all sources to standard TitleSearchResult
    // 1. Normalize ISBN
    const normalizedIsbn = normalizeIsbn(rr['isbn'] ?? rr['ISBN'] ?? rr['isbns'] ?? rr['ISBNs'])

    // 2. Normalize authors
    const authorsFromResult = extractAuthors(r)

    // 3. Normalize publish year
    const publishYear = getResultPublishYear(r)

    // 4. Normalize source
    const source = normalizeSource(getResultSourceValue(rr, nestedSearchResult)) || ''

    // 5. Normalize publisher
    let publisher: string[] | undefined = undefined
    if (Array.isArray(result.publisher)) {
      publisher = result.publisher as string[]
    } else if (result.publisher) {
      publisher = [String(result.publisher)]
    }

    // 6. Normalize imageUrl
    let imageUrl = result.imageUrl
    if (!imageUrl && rr['cover_i']) {
      imageUrl = `https://covers.openlibrary.org/b/id/${rr['cover_i']}-L.jpg`
    }

    // 7. Normalize key
    const key = String(rr['asin'] ?? rr['id'] ?? rr['key'] ?? normalizedIsbn ?? '')

    const isAudibleBacked = isAudibleBackedResult(rr, source, nestedSearchResult)

    // 8. Normalize metadataSource
    const metadataSource = getAlignedMetadataSource(rr, source, nestedSearchResult)

    // 9. Compose TitleSearchResult
    const titleResult: TitleSearchResult = {
      title: result.title || '',
      author_name: authorsFromResult.length
        ? authorsFromResult
        : [
            ((result as Record<string, unknown>)['author'] ??
              (result as Record<string, unknown>)['Artist'] ??
              (result as Record<string, unknown>)['artist'] ??
              '') as string,
          ],
      first_publish_year: publishYear,
      cover_i: rr['cover_i'] as number | undefined,
      key,
      searchResult: result as Partial<SearchResult>,
      imageUrl,
      metadataSource,
      publisher,
      isbn: normalizedIsbn,
    }

    const looksLikeAudibleMetadata =
      (metadataSource && String(metadataSource).toLowerCase().includes('audible')) ||
      isAudibleBacked

    if (looksLikeAudibleMetadata) {
      // Populate commonly used Audible-like fields (flattened to top-level)
      const tr = titleResult as unknown as Record<string, unknown>
      const rrRes = result as unknown as Record<string, unknown>
      const rr = result as unknown as Record<string, unknown>
      tr['subtitle'] =
        rrRes['subtitles'] ??
        rrRes['Subtitles'] ??
        rrRes['subtitle'] ??
        rrRes['Subtitle'] ??
        undefined
      // Always normalize ISBN for Audible/Audnexus/OpenLibrary results too
      tr['isbn'] = normalizeIsbn(rrRes['isbn'] ?? rrRes['ISBN'] ?? rrRes['isbns'] ?? rrRes['ISBNs'])
      tr['narrator'] = (() => {
        const narr =
          rrRes['narrators'] ?? rrRes['Narrators'] ?? rrRes['narrator'] ?? rrRes['Narrator']
        if (Array.isArray(narr)) {
          return (narr as unknown[])
            .map((n: unknown) => {
              if (typeof n === 'object' && n)
                return (
                  (n as Record<string, unknown>)['name'] ?? (n as Record<string, unknown>)['Name']
                )
              if (typeof n === 'string') return n
              return undefined
            })
            .filter(Boolean)
            .join(', ')
        }
        if (typeof narr === 'string') return narr as string
        return undefined
      })()
      if (tr['narrator']) {
        ;(tr['searchResult'] as Record<string, unknown>)['narrator'] = tr['narrator']
      }
      tr['runtime'] = (() => {
        const rawMinutes =
          rrRes['runtimeLengthMin'] ??
          rrRes['lengthMinutes'] ??
          rrRes['runtimeMinutes'] ??
          rrRes['RuntimeLengthMin']
        if (rawMinutes !== undefined && rawMinutes !== null) {
          const m = Number(rawMinutes)
          if (!isNaN(m)) return Math.max(0, Math.round(m))
        }

        const raw =
          rrRes['runtime'] ?? rrRes['Runtime'] ?? rrRes['RuntimeMinutes'] ?? rrRes['RuntimeSeconds']
        if (raw === undefined || raw === null) return undefined
        const num = Number(raw)
        if (isNaN(num)) return undefined
        // Values > 20000 are likely stored in seconds (legacy); convert to minutes
        return num > 20000 ? Math.round(num / 60) : Math.round(num)
      })()
      // Also set runtime on searchResult for template display
      if (tr['runtime']) {
        ;(tr['searchResult'] as Record<string, unknown>)['runtime'] = tr['runtime']
      }
      tr['publishedDate'] =
        rrRes['releaseDate'] ??
        rrRes['ReleaseDate'] ??
        rrRes['publishedDate'] ??
        rrRes['PublishedDate'] ??
        undefined
      tr['description'] = rrRes['description'] ?? rrRes['Description'] ?? undefined
      tr['asin'] = rrRes['asin'] ?? rrRes['Asin'] ?? undefined
      tr['id'] = rrRes['asin'] ?? rrRes['sku'] ?? rrRes['id'] ?? rrRes['title']
      normalizeResultRegionAndProductUrl(tr, rrRes, titleResult)
      if (tr['subtitle']) {
        ;(tr['searchResult'] as Record<string, unknown>)['subtitle'] = tr['subtitle']
      }
      if (tr['productUrl']) {
        ;(tr['searchResult'] as Record<string, unknown>)['productUrl'] = tr['productUrl']
      }
      // preserve seriesList for tooltip display when provided as an array
      try {
        const rawSeries = rr['series'] ?? rr['Series']
        if (Array.isArray(rawSeries) && rawSeries.length) {
          let firstSeriesPosition: string | undefined
          const list = (rawSeries as unknown[])
            .map((s: unknown) => {
              if (typeof s === 'object' && s) {
                const srec = s as Record<string, unknown>
                const name = (srec['name'] ?? srec['Name'] ?? String(s)) as string
                const position = srec['position'] ?? srec['Position']
                if (!firstSeriesPosition && position !== undefined && position !== null) {
                  firstSeriesPosition = String(position)
                }
                return name
              }
              return String(s)
            })
            .filter(Boolean) as string[]
          tr['seriesList'] = list
          tr['searchResult'] = tr['searchResult'] ?? rr
          ;(tr['searchResult'] as Record<string, unknown>)['seriesList'] = list
          // and choose first element as visible series string
          tr['series'] = list[0]
          ;(tr['searchResult'] as Record<string, unknown>)['series'] = list[0]
          if (!tr['seriesNumber'] && firstSeriesPosition) {
            tr['seriesNumber'] = firstSeriesPosition
            ;(tr['searchResult'] as Record<string, unknown>)['seriesNumber'] = firstSeriesPosition
          }
        } else if (rawSeries && typeof rawSeries === 'object' && !Array.isArray(rawSeries)) {
          // Handle single series object: convert to string by getting name property
          const seriesObj = rawSeries as Record<string, unknown>
          const seriesName = (seriesObj['name'] ?? seriesObj['Name'] ?? String(rawSeries)) as string
          const seriesPosition = seriesObj['position'] ?? seriesObj['Position']
          const displaySeries = seriesPosition ? `${seriesName} #${seriesPosition}` : seriesName
          tr['series'] = displaySeries
          ;(tr['searchResult'] as Record<string, unknown>)['series'] = displaySeries
        } else {
          // Handle string or other primitive types
          const displaySeries = String(rawSeries ?? '')
          tr['series'] = displaySeries
          ;(tr['searchResult'] as Record<string, unknown>)['series'] = displaySeries
        }
      } catch {
        // Fallback if processing fails
        const fallbackSeries = String(rr['series'] ?? '')
        tr['series'] = fallbackSeries
        ;(tr['searchResult'] as Record<string, unknown>)['series'] = fallbackSeries
      }
      tr['seriesNumber'] = rrRes['seriesNumber'] ?? rrRes['seriesPosition'] ?? undefined
      if (!tr['imageUrl'] && rrRes['imageUrl']) tr['imageUrl'] = rrRes['imageUrl']
    }
    titleResults.value.push(titleResult)
  }

  totalTitleResultsCount.value = filteredResults.length
  searchStatus.value = ''

  // Check library status
  await checkExistingInLibrary()

  // Notify user and scroll results into view (match Advanced Search behavior)
  toast.info(`Found ${filteredResults.length} results`, 'Search')
  try {
    await nextTick()
    const el = document.querySelector('.search-results') as HTMLElement | null
    if (el) {
      const topNav = document.querySelector('.top-nav') as HTMLElement | null
      const navOffset = topNav ? topNav.offsetHeight + 12 : 72

      function findScrollableAncestor(node: HTMLElement | null): HTMLElement | Window {
        let current = node?.parentElement || null
        while (current && current !== document.documentElement) {
          const style = getComputedStyle(current)
          const overflowY = style.overflowY
          if (
            (overflowY === 'auto' || overflowY === 'scroll') &&
            current.scrollHeight > current.clientHeight
          )
            return current
          current = current.parentElement
        }
        return window
      }

      const ancestor = findScrollableAncestor(el)
      if (ancestor === window) {
        const top = window.scrollY + el.getBoundingClientRect().top - navOffset
        window.scrollTo({ top: Math.max(0, top), behavior: 'smooth' })
      } else {
        const anc = ancestor as HTMLElement
        const top =
          anc.scrollTop +
          el.getBoundingClientRect().top -
          anc.getBoundingClientRect().top -
          navOffset
        anc.scrollTo({ top: Math.max(0, top), behavior: 'smooth' })
      }
    }
  } catch {
    // ignore scroll failures
  }
}

const performAdvancedSearch = async () => {
  // If advanced UI is visible, use the advanced form fields directly
  if (showAdvancedSearch.value) {
    const p = advancedSearchParams.value as {
      title?: string
      author?: string
      series?: string
      isbn?: string
      asin?: string
    }
    const hasAny = Boolean(
      (p.title && p.title.trim()) ||
      (p.author && p.author.trim()) ||
      (p.series && p.series.trim()) ||
      (p.isbn && p.isbn.trim()) ||
      (p.asin && p.asin.trim()),
    )
    if (!hasAny) {
      advancedSearchError.value = 'Please enter a search term'
      return
    }

    advancedSearchError.value = ''
    isSearching.value = true
    searchError.value = ''

    try {
      const languageFilter = getPreferredSearchLanguageFilter(preferredSearchLanguage.value)
      // Use the unified advanced search endpoint for all advanced queries.
      // The backend returns enriched SearchResult objects which are mapped
      // into the UI by handleAdvancedSearchResults.
      isAudiblePaged.value = false
      const params: Record<string, unknown> = {}
      if (p.title && p.title.trim()) params.title = p.title.trim()
      if (p.author && p.author.trim()) params.author = p.author.trim()
      if (p.isbn && p.isbn.trim()) params.isbn = p.isbn.trim()
      if (p.asin && p.asin.trim()) params.asin = p.asin.trim()
      params.region = searchLanguage.value
      if (languageFilter) params.language = languageFilter

      const seriesName = p.series ? p.series.trim() : ''

      // If the user only provided a series, use the unified advanced search POST
      // so the backend performs the Audible series lookup and conversion.
      if (seriesName && !params.title && !params.author && !params.isbn && !params.asin) {
        try {
          const seriesSearchParams: Record<string, unknown> = {
            series: seriesName,
            region: searchLanguage.value,
            pagination: { page: 1, limit: resultsPerPage.value },
          }
          if (languageFilter) seriesSearchParams.language = languageFilter
          const resp = await apiService.advancedSearch(seriesSearchParams)
          // backend returns enriched SearchResult objects
          await handleAdvancedSearchResults(resp as Partial<SearchResult>[])
        } catch (e) {
          throw e
        }

        return
      }

      // Include pagination and candidate limits so the backend can adjust candidate/return caps
      params.pagination = { page: 1, limit: resultsPerPage.value }

      currentAdvancedPage.value = 1

      // If both author and series provided, author takes priority; perform author search then filter by series (no extra Audible series lookup)
      if (params.author && seriesName) {
        const results = (await apiService.advancedSearch(params)) as Partial<SearchResult>[]

        const filtered = results.filter((r) => {
          try {
            const sr =
              ((r as unknown as Record<string, unknown>)['searchResult'] as
                | Partial<SearchResult>
                | undefined) ?? (r as Partial<SearchResult>)

            // If searchResult.series is an array of objects with asin fields, check asin or name
            if (Array.isArray(sr.series) && sr.series.length) {
              for (const s of sr.series) {
                const a =
                  (s &&
                    ((s as Record<string, unknown>)['asin'] ??
                      (s as Record<string, unknown>)['Asin'] ??
                      (s as Record<string, unknown>)['ASIN'])) ??
                  (typeof s === 'string' ? s : undefined)
                if (
                  a &&
                  (String(a).toLowerCase().includes(seriesName.toLowerCase()) ||
                    String(a) === seriesName)
                )
                  return true
                const n =
                  (s &&
                    ((s as Record<string, unknown>)['name'] ??
                      (s as Record<string, unknown>)['Name'])) ??
                  undefined
                if (n && String(n).toLowerCase().includes(seriesName.toLowerCase())) return true
              }
            }

            // If series is a string
            if (
              sr.series &&
              typeof sr.series === 'string' &&
              sr.series.toLowerCase().includes(seriesName.toLowerCase())
            )
              return true

            // Fallback: check top-level result.series
            if (
              r.series &&
              typeof r.series === 'string' &&
              r.series.toLowerCase().includes(seriesName.toLowerCase())
            )
              return true
          } catch {}
          return false
        })

        // If filtering produced no matches, fall back to returning the unfiltered author results
        await handleAdvancedSearchResults(filtered.length ? filtered : results)

        // done: scroll into view
        nextTick(() => {
          const titleResultsElement = document.querySelector('.title-results')
          if (titleResultsElement) {
            const elementTop = titleResultsElement.getBoundingClientRect().top + window.scrollY
            window.scrollTo({ top: elementTop - 125, behavior: 'smooth' })
          }
        })
        return
      }

      const results = await apiService.advancedSearch(params)
      await handleAdvancedSearchResults(results)

      // Scroll to top of results after search
      nextTick(() => {
        const titleResultsElement = document.querySelector('.title-results')
        if (titleResultsElement) {
          const elementTop = titleResultsElement.getBoundingClientRect().top + window.scrollY
          window.scrollTo({ top: elementTop - 125, behavior: 'smooth' })
        }
      })
    } catch (err) {
      advancedSearchError.value = err instanceof Error ? err.message : 'Search failed'
      errorMessage.value = advancedSearchError.value
    } finally {
      isSearching.value = false
    }

    return
  }

  // Fallback: if advanced UI is hidden, parse the unified search query for advanced tokens
  const query = searchQuery.value.trim()
  if (!query) {
    advancedSearchError.value = 'Please enter a search term'
    return
  }

  advancedSearchError.value = ''
  isSearching.value = true
  searchError.value = ''

  try {
    // Parse the query to extract advanced params
    const params: Record<string, unknown> = {}
    const parts = query.split(/\s+/)

    for (const part of parts) {
      if (part.toUpperCase().startsWith('TITLE:')) {
        params.title = part.substring(6).trim()
      } else if (part.toUpperCase().startsWith('AUTHOR:')) {
        params.author = part.substring(7).trim()
      } else if (part.toUpperCase().startsWith('ISBN:')) {
        params.isbn = part.substring(5).trim()
      } else if (part.toUpperCase().startsWith('ASIN:')) {
        params.asin = part.substring(5).trim()
      }
    }

    // If no explicit prefixes were provided, treat the entire query as a title search
    const hasExplicit = params.title || params.author || params.isbn || params.asin
    if (!hasExplicit) {
      params.title = query
    }

    // Always include the configured default region for Audible/Audible lookups
    params.region = searchLanguage.value
    const languageFilter = getPreferredSearchLanguageFilter(preferredSearchLanguage.value)
    if (languageFilter) params.language = languageFilter

    // Include pagination/cap when calling advanced search from unified query
    params.pagination = { page: 1, limit: resultsPerPage.value }
    currentAdvancedPage.value = 1
    const results = await apiService.advancedSearch(params)
    await handleAdvancedSearchResults(results)
  } catch (err) {
    advancedSearchError.value = err instanceof Error ? err.message : 'Search failed'
    errorMessage.value = advancedSearchError.value
  } finally {
    isSearching.value = false
  }
}

const clearAdvancedSearch = () => {
  // Reset form state via composable
  resetAdvancedSearch()
  // Mirror onMounted: respect the user's saved defaultSearchLanguage; only fall back
  // to the region's primary language when no default language is configured.
  const savedLanguage = configStore.applicationSettings?.defaultSearchLanguage
  preferredSearchLanguage.value =
    typeof savedLanguage === 'string' && savedLanguage.trim()
      ? normalizePreferredSearchLanguage(savedLanguage)
      : getPrimaryPreferredSearchLanguageForRegion(searchLanguage.value)
  advancedSearchError.value = ''
  // Reset audible paging state
  audiblePage.value = 1
  audibleTotal.value = 0
  isAudiblePaged.value = false
  allAudibleResults.value = []
}

const changeAudiblePage = async (newPage: number) => {
  if (newPage < 1) return
  audiblePage.value = newPage
  // Update URL query param
  try {
    const q = { ...router.currentRoute.value.query } as Record<string, string>
    q.page = String(audiblePage.value)
    router.replace({ query: q })
  } catch {}

  // If we have all results stored, just update the display without calling API
  if (allAudibleResults.value.length > 0) {
    const startIndex = (audiblePage.value - 1) * audibleLimit.value
    const endIndex = startIndex + audibleLimit.value
    const pageResults = allAudibleResults.value.slice(startIndex, endIndex)
    const converted = (pageResults as unknown[]).map((r: unknown) => {
      const rr = r as Record<string, unknown>
      return {
        asin: (rr['asin'] ?? rr['Asin'] ?? '') as string,
        title: (rr['title'] ?? rr['Title'] ?? '') as string,
        artist: (((rr['authors'] ?? rr['Authors']) as unknown[] | undefined) ?? [])
          .map((a: unknown) =>
            typeof a === 'object' && a
              ? ((a as Record<string, unknown>)['name'] ?? (a as Record<string, unknown>)['Name'])
              : typeof a === 'string'
                ? a
                : '',
          )
          .filter(Boolean)
          .join(', '),
        imageUrl: (rr['imageUrl'] ?? rr['ImageUrl'] ?? '') as string,
        runtime: (() => {
          const raw =
            rr['runtimeLengthMin'] ??
            rr['lengthMinutes'] ??
            rr['runtimeMinutes'] ??
            rr['RuntimeLengthMin'] ??
            rr['runtime'] ??
            rr['Runtime'] ??
            rr['RuntimeMinutes'] ??
            rr['RuntimeSeconds']
          if (raw === undefined || raw === null) return undefined
          const n = Number(raw)
          if (isNaN(n)) return undefined
          // Values > 20000 are likely stored in seconds (legacy); convert to minutes
          return n > 20000 ? Math.round(n / 60) : Math.round(n)
        })(),
        language: rr['language'] ?? rr['Language'],
        metadataSource: 'Audible',
        id: (rr['asin'] ?? rr['sku'] ?? rr['sku'] ?? rr['title']) as string,
      } as Partial<SearchResult>
    }) as Partial<SearchResult>[]
    await handleAdvancedSearchResults(converted)

    // Scroll to top of results after page change
    nextTick(() => {
      const titleResultsElement = document.querySelector('.title-results')
      if (titleResultsElement) {
        const elementTop = titleResultsElement.getBoundingClientRect().top + window.scrollY
        window.scrollTo({ top: elementTop - 125, behavior: 'smooth' })
      }
    })
  } else {
    // Fallback: call API if no cached results
    await performAdvancedSearch()
  }
}

const scrollUnifiedSearchInputIntoView = async () => {
  try {
    await nextTick()
    const inputEl = document.querySelector('#unified-search-input') as HTMLElement | null
    if (inputEl) {
      const topNav = document.querySelector('.top-nav') as HTMLElement | null
      const navOffset = topNav ? topNav.offsetHeight + 12 : 72
      const top = window.scrollY + inputEl.getBoundingClientRect().top - navOffset
      window.scrollTo({ top: Math.max(0, top), behavior: 'smooth' })
    }
  } catch {}
}

const handleUnifiedSearchResults = async (results: SearchResult[] | null | undefined) => {
  try {
    if (!Array.isArray(results)) return
    if (results.length) {
      await handleSimpleSearchResults(results)
      return
    }

    searchStatus.value = ''
    searchError.value = ''
    toast.info('No results found', 'Search')
    await scrollUnifiedSearchInputIntoView()
  } catch (e) {
    logger.debug('Error handling unified search results', e)
  }
}

const performSearch = async () => {
  const results = await runSearch()
  await handleUnifiedSearchResults(results)
  return results
}

const onUnifiedSearchSubmit = async () => {
  if (isSearching.value) {
    cancelSearch()
    return
  }

  await performSearch()
}

// Helper methods for Open Library results
const getCoverUrl = (book: TitleSearchResult): string => {
  const key = book.key || JSON.stringify(book.title || '')
  // If we've already selected a best cover, return it (proxied)
  if (coverSelection.value[key]) {
    return getProtectedImageSrc(coverSelection.value[key], getPlaceholderUrl())
  }

  // Start background evaluation once per key (non-blocking).
  // We mark "attempted" up front so failed evaluations do not retry on every render.
  if (!coverSelectionAttempted.value.has(key) && !coverSelectionInFlight.value.has(key)) {
    coverSelectionAttempted.value.add(key)
    coverSelectionInFlight.value.add(key)
    pickBestCoverForBook(book)
      .catch(() => logger.debug('pickBestCoverForBook error'))
      .finally(() => {
        coverSelectionInFlight.value.delete(key)
      })
  }

  // Immediate fallback: prefer explicit imageUrl, then searchResult image
  if (book.imageUrl) {
    return getProtectedImageSrc(book.imageUrl, getPlaceholderUrl())
  }
  const imageUrl = book.searchResult?.imageUrl || ''
  return getProtectedImageSrc(imageUrl, getPlaceholderUrl())
}

const getAudibleResultImageSrc = (): string => {
  const result = audibleResult.value
  if (!result?.imageUrl) return getPlaceholderUrl()
  return getProtectedImageSrc(result.imageUrl, getPlaceholderUrl())
}

const getSelectedBookImageSrc = (): string => {
  const book = selectedBookForLibrary.value
  if (!book?.imageUrl) return getPlaceholderUrl()
  return getProtectedImageSrc(book.imageUrl, getPlaceholderUrl())
}

// Try to pick the image whose aspect ratio is closest to 1:1 from available candidates
const pickBestCoverForBook = async (book: TitleSearchResult): Promise<void> => {
  try {
    const key = book.key || JSON.stringify(book.title || '')
    // Do not repeat work if we already have a selection
    if (coverSelection.value[key]) return

    const candidates: string[] = []
    if (book.imageUrl) candidates.push(book.imageUrl)
    if (book.searchResult?.imageUrl) candidates.push(book.searchResult.imageUrl)

    // If OpenLibrary book has a cover id, include sizes (L, M, S)
    try {
      const olBook = book as OpenLibraryBook
      const coverId = (olBook as unknown as { cover_i?: number }).cover_i
      if (coverId && coverId > 0) {
        const uL = openLibraryService.getCoverUrl(coverId, 'L')
        const uM = openLibraryService.getCoverUrl(coverId, 'M')
        const uS = openLibraryService.getCoverUrl(coverId, 'S')
        if (uL) candidates.push(uL)
        if (uM) candidates.push(uM)
        if (uS) candidates.push(uS)
      }
    } catch {
      logger.debug('cover id extraction failed')
    }

    // Normalize and dedupe
    const uniq = Array.from(new Set(candidates.filter((u) => !!u))) as string[]
    if (!uniq.length) return
    if (uniq.length < 2) return

    // If all candidates are backend-proxied image endpoints, keep the default
    // selected image and skip additional measurement fetches to avoid duplicate
    // API traffic for each rendered result.
    const allBackendCandidates = uniq.every((url) => {
      const resolved = apiService.getImageUrl(url)
      return !!resolved && isLikelyBackendImageUrl(resolved)
    })
    if (allBackendCandidates) return

    // Load images and measure aspect ratios with timeout
    const results: Array<{ url: string; score: number }> = []
    for (const url of uniq) {
      try {
        const resolved = apiService.getImageUrl(url)
        if (resolved && isLikelyBackendImageUrl(resolved)) {
          // Avoid extra authenticated fetches for backend image URLs here.
          continue
        }
        const imageUrl = apiService.getImageUrl(url)
        if (!imageUrl) continue
        const ratio = await measureImageAspectRatio(imageUrl, 3000)
        if (ratio && ratio > 0) {
          const score = Math.abs(ratio - 1)
          results.push({ url, score })
        }
      } catch (e) {
        logger.debug('Failed to load image for ratio check', url, e)
      }
    }

    if (results.length === 0) return
    // Choose minimum score (closest to 1:1)
    results.sort((a, b) => a.score - b.score)
    if (results[0] && results[0].url) coverSelection.value[key] = results[0].url
  } catch (e) {
    logger.debug('pickBestCoverForBook overall failure', e)
  }
}

// Use shared image error handler to keep behavior consistent across views
const handleLazyImageError = (ev: Event) => {
  try {
    return handleImageError(ev)
  } catch {
    /* swallow */
  }
}

const measureImageAspectRatio = (url: string, timeoutMs = 3000): Promise<number | null> => {
  return new Promise((resolve) => {
    const img = new Image()
    let settled = false
    const t = setTimeout(() => {
      if (!settled) {
        settled = true
        img.src = ''
        resolve(null)
      }
    }, timeoutMs)

    img.onload = () => {
      if (settled) return
      settled = true
      clearTimeout(t)
      try {
        const w = img.naturalWidth || img.width
        const h = img.naturalHeight || img.height
        if (!w || !h) return resolve(null)
        resolve(w / h)
      } catch {
        resolve(null)
      }
    }

    img.onerror = () => {
      if (settled) return
      settled = true
      clearTimeout(t)
      resolve(null)
    }

    img.src = url
  })
}

const formatAuthors = (book: TitleSearchResult): string => {
  if (Array.isArray(book.author_name)) return book.author_name.join(', ')
  if (typeof book.author_name === 'string' && book.author_name.trim())
    return book.author_name.trim()
  return book.searchResult?.artist || 'Unknown Author'
}

// Safely check whether a URL points to Audible or a subdomain of Audible. Avoids substring checks that can be
// bypassed by crafted URLs containing 'audible.com' elsewhere (e.g., query parameters or malicious hostnames).
const isAudibleHost = (url?: string): boolean => {
  if (!url) return false
  try {
    // Use a base origin so relative URLs can be parsed too
    const parsed = new URL(url, window.location.origin)
    const host = parsed.hostname.toLowerCase()
    return host.startsWith('audible.') || host.startsWith('www.audible.')
  } catch {
    // If parsing fails, treat as not audible
    return false
  }
}

const areSameUrl = (left?: string, right?: string): boolean => {
  if (!left || !right) return false

  try {
    return (
      new URL(left, window.location.origin).href === new URL(right, window.location.origin).href
    )
  } catch {
    return left === right
  }
}

const getStringRecordValue = (
  record: Record<string, unknown> | undefined,
  keys: readonly string[],
): string | undefined => {
  if (!record) return undefined

  for (const key of keys) {
    const value = record[key]
    if (typeof value === 'string' && value.trim()) return value.trim()
  }

  return undefined
}

const getNestedResultRecord = (
  record: Record<string, unknown> | undefined,
): Record<string, unknown> | undefined => {
  const nested = record?.['searchResult']
  return nested && typeof nested === 'object' ? (nested as Record<string, unknown>) : undefined
}

const getExplicitAsinValue = (
  record: Record<string, unknown> | undefined,
  nested = getNestedResultRecord(record),
): string | undefined => {
  return getStringRecordValue(record, ASIN_KEYS) ?? getStringRecordValue(nested, ASIN_KEYS)
}

const getResultProductLink = (
  record: Record<string, unknown> | undefined,
  nested = getNestedResultRecord(record),
): string | undefined => {
  return (
    getStringRecordValue(record, PRODUCT_LINK_KEYS) ??
    getStringRecordValue(nested, PRODUCT_LINK_KEYS)
  )
}

const getResultSourceValue = (
  record: Record<string, unknown> | undefined,
  nested = getNestedResultRecord(record),
): string | undefined => {
  return (
    getStringRecordValue(record, METADATA_SOURCE_KEYS) ??
    getStringRecordValue(nested, METADATA_SOURCE_KEYS) ??
    getStringRecordValue(record, SOURCE_KEYS) ??
    getStringRecordValue(nested, SOURCE_KEYS)
  )
}

const isAudibleBackedResult = (
  record: Record<string, unknown> | undefined,
  normalizedSource = '',
  nested = getNestedResultRecord(record),
): boolean => {
  const sourceValues = [
    normalizedSource,
    getStringRecordValue(record, METADATA_SOURCE_KEYS),
    getStringRecordValue(nested, METADATA_SOURCE_KEYS),
    getStringRecordValue(record, SOURCE_KEYS),
    getStringRecordValue(nested, SOURCE_KEYS),
  ]

  return (
    sourceValues.some((source) => source?.toLowerCase().includes('audible')) ||
    Boolean(record?.['isEnriched'] ?? nested?.['isEnriched']) ||
    Boolean(getExplicitAsinValue(record, nested)) ||
    isAudibleHost(getResultProductLink(record, nested))
  )
}

const getAlignedMetadataSource = (
  record: Record<string, unknown> | undefined,
  normalizedSource = '',
  nested = getNestedResultRecord(record),
): string | undefined => {
  if (normalizedSource) return normalizedSource

  const explicitSource =
    getStringRecordValue(record, METADATA_SOURCE_KEYS) ??
    getStringRecordValue(nested, METADATA_SOURCE_KEYS)
  if (explicitSource) return explicitSource

  return isAudibleBackedResult(record, normalizedSource, nested) ? 'audible' : undefined
}

const getMetadataSourceLabel = (source?: string): string => {
  if (!source) return ''
  return source.toLowerCase().includes('audible') ? 'Audible' : `Metadata: ${source}`
}

/**
 * Normalises the region and productUrl fields on a raw title result record.
 * Propagates the resolved region into the nested searchResult, then determines
 * whether the product link should be an Audible canonical URL or the raw value.
 * Extracted to avoid duplication between handleAdvancedSearchResults and
 * handleSimpleSearchResults.
 */
const normalizeResultRegionAndProductUrl = (
  tr: Record<string, unknown>,
  rrRes: Record<string, unknown>,
  titleResult: TitleSearchResult,
): void => {
  tr['region'] = rrRes['region'] ?? rrRes['Region'] ?? undefined
  if (tr['region']) {
    ;(tr['searchResult'] as Record<string, unknown>)['region'] = tr['region']
  }
  const rawProductUrl = getResultProductLink(rrRes)
  const asin = getExplicitAsinValue(tr, rrRes)
  const resultRegion = typeof tr['region'] === 'string' ? tr['region'] : searchLanguage.value
  const sourceIsAudible =
    String(titleResult.metadataSource ?? '')
      .toLowerCase()
      .includes('audible') ||
    String(rrRes['source'] ?? rrRes['Source'] ?? '')
      .toLowerCase()
      .includes('audible')
  tr['productUrl'] =
    asin && (sourceIsAudible || (typeof rawProductUrl === 'string' && isAudibleHost(rawProductUrl)))
      ? buildAudibleProductUrl(asin, resultRegion)
      : rawProductUrl
}

const getAsin = (book: TitleSearchResult): string | null => {
  return (
    ((book as unknown as Record<string, unknown>)['asin'] as string | undefined) ||
    book.searchResult?.asin ||
    getNestedSearchResultValue<string>(book, 'asin') ||
    resolvedAsins.value[book.key] ||
    null
  )
}

const getResultRegion = (book: TitleSearchResult): string => {
  const rawRegion =
    book.region ??
    (book.searchResult as Record<string, unknown> | undefined)?.['region'] ??
    getNestedSearchResultValue<string>(book, 'region')
  return typeof rawRegion === 'string' && rawRegion.trim() ? rawRegion : searchLanguage.value
}

const getNestedSearchResultValue = <T,>(book: TitleSearchResult, field: string): T | undefined => {
  const nested = getNestedResultRecord(book.searchResult as Record<string, unknown> | undefined)
  return nested?.[field] as T | undefined
}

const getBookMetadataSource = (book: TitleSearchResult): string | undefined => {
  const source =
    book.metadataSource ??
    ((book.searchResult as Record<string, unknown> | undefined)?.['metadataSource'] as
      | string
      | undefined) ??
    getNestedSearchResultValue<string>(book, 'metadataSource')

  return typeof source === 'string' && source.trim() ? source : undefined
}

const getBookProductUrl = (book: TitleSearchResult): string | undefined => {
  const bookRecord = book as unknown as Record<string, unknown>
  const searchResultRecord = book.searchResult as Record<string, unknown> | undefined

  return (
    getResultProductLink(bookRecord, searchResultRecord) ?? getResultProductLink(searchResultRecord)
  )
}

const getSharedMetadataSourceUrl = (book: TitleSearchResult): string | undefined => {
  if (!getBookMetadataSource(book)) return undefined

  const metadataUrl = getMetadataSourceUrl(book)
  const sourceUrl = getSourceUrl(book)
  return areSameUrl(metadataUrl, sourceUrl) ? metadataUrl : undefined
}

const getMetadataSourceUrl = (book: TitleSearchResult): string | undefined => {
  const source = getBookMetadataSource(book)
  if (!source) return undefined

  // OpenLibrary metadata does not require an ASIN; prefer resultUrl (JSON) then productUrl or OL work URL
  if (source.toLowerCase().includes('openlibrary')) {
    // Prefer the canonical metadata/result URL (e.g., OpenLibrary .json) if provided
    if (book.searchResult?.resultUrl) return book.searchResult.resultUrl
    const nestedResultUrl = getNestedSearchResultValue<string>(book, 'resultUrl')
    if (nestedResultUrl) return nestedResultUrl
    // Fall back to productUrl (human-facing page) if resultUrl is not available
    const productUrl = getBookProductUrl(book)
    if (productUrl) return productUrl
    const olBook = book as OpenLibraryBook
    // Avoid using our local generated keys (they start with 'search-') — prefer real OL identifiers
    const candidateKey = (olBook.key || '').toString()
    const looksLikeLocalKey =
      candidateKey.startsWith('search-') || candidateKey.startsWith('search-unknown-')
    if (!looksLikeLocalKey) {
      // If the key is a work (e.g., '/works/OL82548W'), prefer work JSON/page URLs
      if (candidateKey.startsWith('/works')) {
        const workJson = openLibraryService.getWorkJsonUrlFromBook(olBook)
        if (workJson) return workJson
        const workPage = openLibraryService.getWorkPageUrlFromBook(olBook)
        if (workPage) return workPage
      }

      // Prefer a book (edition) JSON metadata link (OLID) for metadata badge
      const jsonUrl = openLibraryService.getBookJsonUrlFromBook(olBook)
      if (jsonUrl) return jsonUrl

      // Fall back to a book page URL if JSON isn't available
      const pageUrl = openLibraryService.getBookPageUrlFromBook(olBook)
      if (pageUrl) return pageUrl

      // If key is a work but we couldn't derive an edition, fallback to work search by title
      if (candidateKey.startsWith('/works') && book.title) {
        const firstAuthor = Array.isArray(book.author_name)
          ? book.author_name[0]
          : typeof book.author_name === 'string'
            ? book.author_name
            : ''
        const q = `${book.title}${firstAuthor ? ' ' + firstAuthor : ''}`
        return openLibraryService.getSearchUrl(q)
      }

      // If it's a plain OLID like 'OL123M' or a canonical /books path, return the generic book URL
      if (candidateKey.startsWith('/books') || /^OL\w+/i.test(candidateKey)) {
        return openLibraryService.getBookUrl(candidateKey)
      }
    }
    // No key: fall back to search by title
    if (book.title) return openLibraryService.getSearchUrl(book.title)
    return undefined
  }

  const asin = getAsin(book)
  const productUrl = getBookProductUrl(book)

  // Map metadata source to URL for ASIN-based providers
  if (source.toLowerCase().includes('audible')) {
    if (productUrl) return productUrl
    if (!asin) return undefined
    return buildAudibleProductUrl(asin, getResultRegion(book))
  } else if (source.toLowerCase().includes('audnex')) {
    if (!asin) return undefined
    // Audnexus API format
    return `https://api.audnex.us/books/${asin}`
  } else if (source === 'Amazon') {
    if (productUrl) return productUrl
    if (!asin) return undefined
    return buildAmazonProductUrl(asin, getResultRegion(book))
  } else if (source === 'Audible') {
    if (productUrl) return productUrl
    if (!asin) return undefined
    return buildAudibleProductUrl(asin, getResultRegion(book))
  }

  return undefined
}

// Get a sensible 'source' URL for the book (indexer/product or OpenLibrary work page)
const getSourceUrl = (book: TitleSearchResult): string | undefined => {
  const asin = getAsin(book)
  const metaSource = (getBookMetadataSource(book) ?? '').toString().toLowerCase()

  // Prefer explicit productUrl from the enriched SearchResult
  const productUrl = getBookProductUrl(book)
  if (productUrl) {
    if ((metaSource.includes('audible') || isAudibleHost(productUrl)) && asin) {
      return buildAudibleProductUrl(asin, getResultRegion(book))
    }

    return productUrl
  }

  // If metadata indicates Audible-backed metadata, link to the Audible product page when possible.
  if (metaSource.includes('audible') && asin) {
    return buildAudibleProductUrl(asin, getResultRegion(book))
  }

  // If provider/source is OpenLibrary or we have an OL key, link to the OL work page
  if (
    book.searchResult?.source?.toLowerCase().includes('openlibrary') ||
    metaSource.includes('openlibrary')
  ) {
    const olBook = book as OpenLibraryBook
    const candidateKey = (olBook.key || '').toString()
    const looksLikeLocalKey =
      candidateKey.startsWith('search-') || candidateKey.startsWith('search-unknown-')
    if (!looksLikeLocalKey) {
      // Prefer the human-facing book page URL (edition if available)
      const pageUrl = openLibraryService.getBookPageUrlFromBook(olBook)
      if (pageUrl) return pageUrl
      if (candidateKey.startsWith('/books') || /^OL\w+/i.test(candidateKey))
        return openLibraryService.getBookUrl(candidateKey)
    }
    // Fallback to searching by title when we don't have a usable OL identifier
    if (book.title) return openLibraryService.getSearchUrl(book.title)
  }

  return undefined
}

// Extract ISBN candidates from an OpenLibrary-derived TitleSearchResult
const extractIsbnCandidates = (book: TitleSearchResult): string[] => {
  try {
    // Prefer OpenLibrary service helpers when available
    const isbns: string[] = openLibraryService.getISBNs(book as OpenLibraryBook)
    if (!isbns || isbns.length === 0) return []
    // Normalize and dedupe
    const cleaned = Array.from(new Set(isbns.map((i) => i.replace(/[-\s]/g, ''))))
    return cleaned
  } catch (e) {
    logger.debug('extractIsbnCandidates error', e)
    return []
  }
}

// No custom lazy loading logic needed; browser handles loading="lazy"

// Resolve a single book's ASIN by trying its ISBN candidates via backend lookup
const resolveAsinForBook = async (book: TitleSearchResult): Promise<string | null> => {
  if (!book) return null
  // If already present on the enriched search result, return it
  if (book.searchResult && book.searchResult.asin) return book.searchResult.asin

  const candidates = extractIsbnCandidates(book)
  if (!candidates || candidates.length === 0) return null

  for (const isbn of candidates.slice(0, 3)) {
    // try up to 3 candidates
    if (!isbnService.validateISBN(isbn)) continue
    try {
      const resp = await apiService.getAsinFromIsbn(isbn)
      if (resp && resp.success && resp.asin) {
        // update resolved map and the underlying searchResult if present
        resolvedAsins.value[book.key] = resp.asin
        if (book.searchResult) {
          book.searchResult.asin = resp.asin
        }
        logger.debug('Resolved ASIN from ISBN', { isbn, asin: resp.asin, bookKey: book.key })
        return resp.asin
      }
    } catch (e) {
      logger.debug('resolveAsinForBook API error for ISBN', isbn, e)
    }
    // small delay to avoid hammering backend
    await new Promise((r) => setTimeout(r, 150))
  }
  return null
}

// Iterate over titleResults and attempt to resolve missing ASINs in background
const attemptResolveAsinsForTitleResults = async (): Promise<void> => {
  if (!titleResults.value || titleResults.value.length === 0) return

  for (const book of titleResults.value) {
    try {
      // Skip if already have an ASIN
      if ((book.searchResult && book.searchResult.asin) || resolvedAsins.value[book.key]) continue
      const asin = await resolveAsinForBook(book)
      if (asin) {
        // Trigger library re-check so UI updates added status and buttons
        await checkExistingInLibrary()
      }
    } catch (e) {
      logger.debug('attemptResolveAsinsForTitleResults error for book', book.key, e)
    }
  }
}

// kick off background resolution when results are present
;(() => {
  try {
    if (titleResults.value && titleResults.value.length) {
      attemptResolveAsinsForTitleResults().catch(() =>
        logger.debug('ASIN resolution background job failed'),
      )
    }
  } catch {}
})()

// Removed manual ASIN helper methods (createAsinSearchHint, openAmazonSearch, useBookForAsinSearch)

// Common methods for both search types
const selectTitleResult = async (book: TitleSearchResult) => {
  logger.debug('selectTitleResult called with book:', book)
  const asin = resolvedAsins.value[book.key] || book.searchResult?.asin

  try {
    // If we have enriched search result, use it directly even if no ASIN is present
    if (book.searchResult && book.searchResult.isEnriched) {
      const result = book.searchResult
      logger.debug('Using enriched metadata from intelligent search:', result)

      const metadata: AudibleBookMetadata = {
        asin: result.asin || '',
        region: getResultRegion(book),
        title: result.title || 'Unknown Title',
        subtitle: undefined,
        authors: result.artist ? [result.artist] : [],
        narrators: result.narrator ? [result.narrator] : [],
        publisher: result.publisher,
        publishedDate: result.publishedDate,
        description: result.description,
        imageUrl: result.imageUrl,
        runtime: result.runtime,
        language: result.language,
        genres: [],
        series: result.series,
        seriesNumber: result.seriesNumber,
        abridged: false,
        isbn: undefined,
        source: book.metadataSource || result.source,
        openLibraryId: result.id || undefined,
      }

      // Add to library directly using the enriched metadata
      await addToLibrary(metadata)
      return
    }

    // Fallback: if we have an ASIN, fetch metadata from configured sources
    if (asin) {
      logger.debug('Fetching metadata for ASIN:', asin)
      toast.info('Fetching metadata', `Getting book details from configured sources...`)
      const response = await apiService.getAudibleMetadata<
        AudibleMetadataResponse | AudibleMetadataPayload
      >(asin, searchLanguage.value)
      const responseObj =
        response && typeof response === 'object'
          ? (response as AudibleMetadataResponse | AudibleMetadataPayload)
          : {}
      const audibleData =
        'metadata' in responseObj &&
        responseObj.metadata &&
        typeof responseObj.metadata === 'object'
          ? responseObj.metadata
          : (responseObj as AudibleMetadataPayload)
      const metadataSource =
        ('source' in responseObj && typeof responseObj.source === 'string'
          ? responseObj.source
          : undefined) ||
        book.metadataSource ||
        'configured metadata source'
      logger.debug(`Metadata fetched from ${metadataSource}:`, audibleData)

      // Store the metadata source in the book object so it shows in the UI
      book.metadataSource = metadataSource

      const hasUsableMetadata = !!(
        audibleData &&
        ((typeof audibleData.title === 'string' && audibleData.title.trim()) ||
          (typeof audibleData.asin === 'string' && audibleData.asin.trim()) ||
          (Array.isArray(audibleData.authors) && audibleData.authors.length > 0))
      )

      if (!hasUsableMetadata) {
        logger.warn('Metadata payload missing/invalid; falling back to selected result data', {
          asin,
          responseType: typeof audibleData,
        })
        const result = book.searchResult
        const fallbackMetadata: AudibleBookMetadata = {
          asin: asin || '',
          region: getResultRegion(book),
          title: result?.title || book.title || 'Unknown Title',
          subtitle: result?.subtitle,
          authors: result?.artist
            ? [result.artist]
            : Array.isArray(book.author_name)
              ? book.author_name.filter((a): a is string => typeof a === 'string')
              : typeof book.author_name === 'string'
                ? [book.author_name]
                : [],
          narrators: result?.narrator ? [result.narrator] : [],
          publisher: result?.publisher,
          publishedDate:
            result?.publishedDate ||
            (book.first_publish_year ? String(book.first_publish_year) : undefined),
          description: result?.description,
          imageUrl: result?.imageUrl,
          runtime: result?.runtime || result?.lengthMinutes || undefined,
          language: result?.language,
          genres: Array.isArray(result?.genres) ? result.genres : [],
          series: result?.series,
          seriesNumber: result?.seriesNumber,
          abridged: false,
          isbn: undefined,
          source: metadataSource,
          openLibraryId: result?.id || undefined,
        }
        toast.warning('Metadata unavailable', 'Using search result details to continue.')
        await addToLibrary(fallbackMetadata)
        return
      }

      toast.success('Metadata retrieved', `Book details fetched from ${metadataSource}`)

      const publishedDate =
        audibleData.releaseDate || audibleData.publishDate || audibleData.publishedDate

      const metadata: AudibleBookMetadata = {
        asin: audibleData.asin || asin || '',
        region: audibleData.region || getResultRegion(book),
        title: audibleData.title || 'Unknown Title',
        subtitle: audibleData.subtitle,
        authors:
          (audibleData.authors
            ?.map((a: AudibleAuthor) => a.name)
            .filter((n: string | undefined) => n) as string[]) || [],
        narrators:
          (audibleData.narrators
            ?.map((n: AudibleNarrator) => n.name)
            .filter((n: string | undefined) => n) as string[]) || [],
        publisher: audibleData.publisher,
        publishedDate: publishedDate,
        description: audibleData.description,
        imageUrl: audibleData.imageUrl,
        // Audible returns length in minutes; keep runtime in minutes for UI helpers
        runtime: audibleData.runtime || audibleData.lengthMinutes || undefined,
        language: audibleData.language,
        genres:
          (audibleData.genres
            ?.map((g: AudibleGenre) => g.name)
            .filter((n: string | undefined) => n) as string[]) || [],
        series: audibleData.series?.length
          ? audibleData.series
              .map((s) => s.name)
              .filter((n): n is string => !!n)
              .join(', ')
          : undefined,
        seriesList:
          (audibleData.series?.map((s) => s.name).filter((n): n is string => !!n) as string[]) ||
          [],
        seriesNumber:
          audibleData.series?.[0]?.position !== undefined
            ? String(audibleData.series[0].position)
            : undefined, // Extract position from primary series
        abridged:
          typeof audibleData.bookFormat === 'string'
            ? audibleData.bookFormat.toLowerCase().includes('abridged')
            : false,
        isbn: audibleData.isbn,
        source: metadataSource,
        openLibraryId: book.searchResult?.id || undefined,
      }

      // Add to library directly
      await addToLibrary(metadata)
      return
    }

    // Allow adding any book with a title and ISBN (relaxed, not just OpenLibrary)
    if (
      book.title &&
      book.title.trim() &&
      book.isbn &&
      Array.isArray(book.isbn) &&
      book.isbn.length > 0 &&
      typeof book.isbn[0] === 'string' &&
      book.isbn[0].trim()
    ) {
      const getFirstString = (val: unknown) =>
        Array.isArray(val) ? (val[0] || '').toString() : (val || '').toString()
      const bookRecord = book as unknown as Record<string, unknown>
      const metadata: AudibleBookMetadata = {
        asin: '',
        title: book.title,
        subtitle: getOptionalString((book as { subtitle?: unknown }).subtitle),
        authors: Array.isArray(book.author_name)
          ? book.author_name.filter((a): a is string => typeof a === 'string')
          : typeof book.author_name === 'string'
            ? [book.author_name]
            : [],
        publisher: getFirstString(book.publisher),
        publishedDate: book.first_publish_year ? String(book.first_publish_year) : undefined,
        description: getOptionalString((book as { description?: unknown }).description),
        imageUrl: book.cover_i
          ? `https://covers.openlibrary.org/b/id/${book.cover_i}-L.jpg`
          : undefined,
        runtime: undefined,
        language: getFirstString(book.language),
        genres: Array.isArray(book.subject)
          ? book.subject.filter((g): g is string => typeof g === 'string')
          : [],
        series:
          Array.isArray(book.seriesList) && book.seriesList.length > 0
            ? book.seriesList[0]
            : undefined,
        seriesNumber: undefined,
        isbn: getFirstString(book.isbn),
        source: book.metadataSource || 'Unknown',
        openLibraryId:
          typeof bookRecord['openLibraryId'] === 'string'
            ? bookRecord['openLibraryId']
            : typeof bookRecord['key'] === 'string'
              ? bookRecord['key']
              : undefined,
        metadataSource: book.metadataSource || 'unknown',
      }
      await addToLibrary(metadata)
      return
    }
    // Otherwise, block as before
    logger.error(
      'No ASIN, enriched metadata, or OpenLibrary ISBN+title available for selected book',
    )
    toast.warning('Cannot add', 'Cannot add to library: No ASIN, metadata, or ISBN available')
  } catch (error) {
    logger.error('Failed to add audiobook:', error)
    toast.error('Add failed', 'Failed to add audiobook. Please try again.')
  }
}

// Common methods for both search types
const addToLibrary = async (book: AudibleBookMetadata) => {
  // Check if root folder is configured
  if (rootFoldersStore.folders.length === 0 && !configStore.applicationSettings?.outputPath) {
    toast.warning(
      'Root folder not configured',
      'Please configure the root folder in Settings before adding audiobooks.',
    )
    router.push('/settings')
    return
  }

  // Show the add to library modal instead of directly adding
  const categoryGenres = (book.searchResult?.category || '')
    .split(',')
    .map((g) => g.trim())
    .filter((g) => g.length > 0)

  selectedBookForLibrary.value = {
    ...book,
    // Sanitize seriesNumber to filter out the string "null"
    seriesNumber: book.seriesNumber && book.seriesNumber !== 'null' ? book.seriesNumber : undefined,
    genres:
      (book.genres && book.genres.length
        ? book.genres
        : book.searchResult?.genres && book.searchResult.genres.length
          ? book.searchResult.genres
          : categoryGenres) || [],
  }
  showAddLibraryModal.value = true
}

const closeAddLibraryModal = () => {
  showAddLibraryModal.value = false
}

const handleLibraryAdded = (audiobook: Audiobook) => {
  // Mark as added in the UI
  if (audiobook.asin) {
    logger.debug('Marking ASIN as added:', audiobook.asin)
    addedAsins.value.add(audiobook.asin)
  }
  // Mark OpenLibrary ID as added when present
  if (audiobook.openLibraryId) {
    logger.debug('Marking OpenLibrary ID as added:', audiobook.openLibraryId)
    addedOpenLibraryIds.value.add(audiobook.openLibraryId)
  }

  // Reset search if needed
  if (searchType.value === 'asin') {
    searchQuery.value = ''
    audibleResult.value = null
  }
}

const handleSimpleSearchResults = async (results: SearchResult[]) => {
  const filteredResults = filterResultsBySelectedLanguage(results)

  // Convert search results to title results format
  titleResults.value = []
  audibleResult.value = null
  searchType.value = 'title'
  allAudibleResults.value = []

  if (!filteredResults.length) {
    totalTitleResultsCount.value = 0
    searchStatus.value = ''
    await checkExistingInLibrary()
    toast.info('No results found for the selected language', 'Search')
    return
  }

  for (const result of filteredResults) {
    const rr = result as unknown as Record<string, unknown>
    const nestedSearchResult = getNestedResultRecord(rr)

    // Normalize common metadata keys from backend variations so the template
    // consistently finds `subtitle`/`subtitles`, `narrator` and `source`.
    try {
      // subtitles may be provided as `subtitle`, `Subtitle`, `Subtitles` or `subtitles`
      rr['subtitles'] =
        rr['subtitles'] ?? rr['subtitle'] ?? rr['Subtitle'] ?? rr['Subtitles'] ?? undefined
      rr['subtitle'] =
        rr['subtitle'] ?? rr['subtitles'] ?? rr['Subtitle'] ?? rr['Subtitles'] ?? undefined

      // narrators may be provided as array or single string
      if (!rr['narrator']) {
        const narrArr = rr['narrators'] ?? rr['Narrators']
        if (Array.isArray(narrArr) && narrArr.length) {
          rr['narrator'] = (narrArr as unknown[])
            .map((n: unknown) => {
              if (typeof n === 'object' && n)
                return (
                  (n as Record<string, unknown>)['name'] ?? (n as Record<string, unknown>)['Name']
                )
              if (typeof n === 'string') return n
              return undefined
            })
            .filter(Boolean)
            .join(', ')
        } else if (typeof rr['Narrator'] === 'string') {
          rr['narrator'] = rr['Narrator'] as string
        }
      }
    } catch (e) {
      // swallow normalization errors
      logger.debug('Normalization failed for simple result', e)
    }

    const publishYear = getResultPublishYear(result as unknown as LooseResult)

    const authorsFromResult = ((): string[] => {
      const rrec = result as unknown as Record<string, unknown>
      const authorStr =
        typeof rrec['author'] === 'string' && (rrec['author'] as string).trim().length
          ? (rrec['author'] as string).trim()
          : undefined
      if (authorStr) return [authorStr]

      const artistStr =
        typeof rrec['Artist'] === 'string' && (rrec['Artist'] as string).trim().length
          ? (rrec['Artist'] as string).trim()
          : undefined
      if (artistStr) return [artistStr]

      if (typeof rrec['artist'] === 'string' && (rrec['artist'] as string).trim().length)
        return [(rrec['artist'] as string).trim()]

      const maybeAuthors = rrec['authors'] ?? rrec['Authors']
      if (Array.isArray(maybeAuthors) && maybeAuthors.length) {
        return (maybeAuthors as unknown[])
          .map((a: unknown) => {
            if (typeof a === 'string') return (a as string).trim()
            if (typeof a === 'object' && a)
              return (
                ((a as Record<string, unknown>)['name'] as string | undefined) ??
                ((a as Record<string, unknown>)['Name'] as string | undefined) ??
                ''
              )
            return ''
          })
          .filter((n) => !!n) as string[]
      }

      // If the original searchResult contains authors, use those
      const sr = (result as unknown as Record<string, unknown>)['searchResult'] as
        | Record<string, unknown>
        | undefined
      const srAuthors = sr ? ((sr['authors'] ?? sr['Authors']) as unknown[] | undefined) : undefined
      if (Array.isArray(srAuthors) && srAuthors.length) {
        return (srAuthors as unknown[])
          .map((a: unknown) => {
            if (typeof a === 'string') return a.trim()
            if (typeof a === 'object' && a)
              return (
                ((a as Record<string, unknown>)['name'] as string | undefined) ??
                ((a as Record<string, unknown>)['Name'] as string | undefined) ??
                ''
              )
            return String(a)
          })
          .filter((n) => !!n) as string[]
      }

      return []
    })()

    const source = normalizeSource(getResultSourceValue(rr, nestedSearchResult)) || ''
    const metadataSource = getAlignedMetadataSource(rr, source, nestedSearchResult)
    const isAudibleBacked = isAudibleBackedResult(rr, source, nestedSearchResult)
    if (metadataSource?.toLowerCase().includes('audible')) {
      rr['source'] = 'Audible'
    }

    // If the result looks like an Audible-backed audiobook result (or explicitly marked),
    // prefer to populate the richer audiobook-shaped fields so the Add New UI
    // can surface subtitles, narrators, runtime, publish date, etc.
    const looksLikeAudibleMetadata =
      (metadataSource && String(metadataSource).toLowerCase().includes('audible')) ||
      isAudibleBacked

    const titleResult: TitleSearchResult = {
      title: result.title || '',
      author_name: authorsFromResult.length
        ? authorsFromResult
        : [
            ((result as unknown as Record<string, unknown>)['author'] ??
              (result as unknown as Record<string, unknown>)['Artist'] ??
              (result as unknown as Record<string, unknown>)['artist'] ??
              '') as string,
          ],
      first_publish_year: publishYear,
      cover_i: undefined,
      key: String(result.asin || result.id || ''),
      searchResult: result,
      imageUrl: result.imageUrl,
      // Prefer explicit metadataSource, but normalize Audible-backed results to a stable label.
      metadataSource,
      // forward publisher into the top-level TitleSearchResult so template's publisher check works
      publisher: Array.isArray(result.publisher)
        ? result.publisher
        : result.publisher
          ? [result.publisher]
          : undefined,
    }

    if (looksLikeAudibleMetadata) {
      // Populate commonly used Audible-backed fields (flattened to top-level)
      const tr = titleResult as unknown as Record<string, unknown>
      const rrRes = result as unknown as Record<string, unknown>
      const rr = result as unknown as Record<string, unknown>
      tr['subtitle'] =
        rrRes['subtitles'] ??
        rrRes['Subtitles'] ??
        rrRes['subtitle'] ??
        rrRes['Subtitle'] ??
        undefined
      tr['narrator'] = (() => {
        const narr =
          rrRes['narrators'] ?? rrRes['Narrators'] ?? rrRes['narrator'] ?? rrRes['Narrator']
        if (Array.isArray(narr)) {
          return (narr as unknown[])
            .map((n: unknown) => {
              if (typeof n === 'object' && n)
                return (
                  (n as Record<string, unknown>)['name'] ?? (n as Record<string, unknown>)['Name']
                )
              if (typeof n === 'string') return n
              return undefined
            })
            .filter(Boolean)
            .join(', ')
        }
        if (typeof narr === 'string') return narr as string
        return undefined
      })()
      tr['runtime'] = (() => {
        const raw =
          rrRes['runtimeLengthMin'] ??
          rrRes['lengthMinutes'] ??
          rrRes['runtimeMinutes'] ??
          rrRes['RuntimeLengthMin'] ??
          rrRes['runtime'] ??
          rrRes['Runtime'] ??
          rrRes['RuntimeMinutes'] ??
          rrRes['RuntimeSeconds']
        if (raw === undefined || raw === null) return undefined
        const num = Number(raw)
        if (isNaN(num)) return undefined
        // Values > 20000 are likely stored in seconds (legacy); convert to minutes
        return num > 20000 ? Math.round(num / 60) : Math.round(num)
      })()
      // Also set runtime on searchResult for template display
      if (tr['runtime']) {
        ;(tr['searchResult'] as Record<string, unknown>)['runtime'] = tr['runtime']
      }
      tr['publishedDate'] =
        rrRes['releaseDate'] ??
        rrRes['ReleaseDate'] ??
        rrRes['publishedDate'] ??
        rrRes['PublishedDate'] ??
        undefined
      tr['description'] = rrRes['description'] ?? rrRes['Description'] ?? undefined
      tr['asin'] = rrRes['asin'] ?? rrRes['Asin'] ?? undefined
      tr['id'] = rrRes['asin'] ?? rrRes['sku'] ?? rrRes['id'] ?? rrRes['title']
      normalizeResultRegionAndProductUrl(tr, rrRes, titleResult)
      // preserve seriesList for tooltip display when provided as an array
      try {
        const rawSeries = rr['series'] ?? rr['Series']
        if (Array.isArray(rawSeries) && rawSeries.length) {
          const list = (rawSeries as unknown[])
            .map((s: unknown) => {
              if (typeof s === 'object' && s) {
                const srec = s as Record<string, unknown>
                const name = (srec['name'] ?? srec['Name'] ?? String(s)) as string
                const position = srec['position'] ?? srec['Position']
                return position ? `${name} #${position}` : name
              }
              return String(s)
            })
            .filter(Boolean) as string[]
          tr['seriesList'] = list
          tr['searchResult'] = tr['searchResult'] ?? rr
          ;(tr['searchResult'] as Record<string, unknown>)['seriesList'] = list
          // and choose first element as visible series string
          tr['series'] = list[0]
          ;(tr['searchResult'] as Record<string, unknown>)['series'] = list[0]
        } else if (rawSeries && typeof rawSeries === 'object' && !Array.isArray(rawSeries)) {
          // Handle single series object: convert to string by getting name property
          const seriesObj = rawSeries as Record<string, unknown>
          const seriesName = (seriesObj['name'] ?? seriesObj['Name'] ?? String(rawSeries)) as string
          const seriesPosition = seriesObj['position'] ?? seriesObj['Position']
          const displaySeries = seriesPosition ? `${seriesName} #${seriesPosition}` : seriesName
          tr['series'] = displaySeries
          ;(tr['searchResult'] as Record<string, unknown>)['series'] = displaySeries
        } else {
          // Handle string or other primitive types
          const displaySeries = String(rawSeries ?? '')
          tr['series'] = displaySeries
          ;(tr['searchResult'] as Record<string, unknown>)['series'] = displaySeries
        }
      } catch {
        // Fallback if processing fails
        const fallbackSeries = String(rr['series'] ?? '')
        tr['series'] = fallbackSeries
        ;(tr['searchResult'] as Record<string, unknown>)['series'] = fallbackSeries
      }
      tr['seriesNumber'] = rrRes['seriesNumber'] ?? rrRes['seriesPosition'] ?? undefined
      if (!tr['imageUrl'] && rrRes['imageUrl']) tr['imageUrl'] = rrRes['imageUrl']
    }
    titleResults.value.push(titleResult)
  }

  totalTitleResultsCount.value = filteredResults.length
  searchStatus.value = ''

  // Check library status
  await checkExistingInLibrary()

  // Notify user and scroll results into view (match Advanced Search behavior)
  toast.info(`Found ${filteredResults.length} results`, 'Search')
  try {
    await nextTick()
    const el = document.querySelector('.search-results') as HTMLElement | null
    if (el) {
      const topNav = document.querySelector('.top-nav') as HTMLElement | null
      const navOffset = topNav ? topNav.offsetHeight + 12 : 72

      function findScrollableAncestor(node: HTMLElement | null): HTMLElement | Window {
        let current = node?.parentElement || null
        while (current && current !== document.documentElement) {
          const style = getComputedStyle(current)
          const overflowY = style.overflowY
          if (
            (overflowY === 'auto' || overflowY === 'scroll') &&
            current.scrollHeight > current.clientHeight
          )
            return current
          current = current.parentElement
        }
        return window
      }

      const ancestor = findScrollableAncestor(el)
      if (ancestor === window) {
        const top = window.scrollY + el.getBoundingClientRect().top - navOffset
        window.scrollTo({ top: Math.max(0, top), behavior: 'smooth' })
      } else {
        const anc = ancestor as HTMLElement
        const top =
          anc.scrollTop +
          el.getBoundingClientRect().top -
          anc.getBoundingClientRect().top -
          navOffset
        anc.scrollTo({ top: Math.max(0, top), behavior: 'smooth' })
      }
    }
  } catch {
    // ignore scroll failures
  }
}

const retrySearch = async () => {
  errorMessage.value = ''
  searchStatus.value = ''
  await performSearch()
}

// Load application settings and API configurations on mount
onMounted(async () => {
  await configStore.loadApplicationSettings()
  await configStore.loadApiConfigurations()

  const defaultRegion = normalizeSearchRegion(configStore.applicationSettings?.defaultSearchRegion)
  const defaultLanguage = configStore.applicationSettings?.defaultSearchLanguage
  searchLanguage.value = defaultRegion
  preferredSearchLanguage.value =
    typeof defaultLanguage === 'string' && defaultLanguage.trim()
      ? normalizePreferredSearchLanguage(defaultLanguage)
      : getPrimaryPreferredSearchLanguageForRegion(defaultRegion)

  // Audible integration removed: no auth status to check

  // Initialize added status on mount
  await checkExistingInLibrary()

  // Subscribe to server-side search progress updates (ignore automatic background searches by default)
  type ProgressPayload = {
    message: string
    asin?: string | null
    type?: string
    audiobookId?: number
    details?: { rawCount?: number; scoredCount?: number; [key: string]: unknown }
  }

  unsubscribeSearchProgress = signalRService.onSearchProgress((payload: ProgressPayload) => {
    if (!payload || !payload.message) return

    // Prefer structured details when available, but do not use an on-screen progress bar
    const details = payload.details
    if (details) {
      if (typeof details.rawCount === 'number') {
        searchStatus.value = `Found ${details.rawCount} raw results`
        return
      }
      if (typeof details.scoredCount === 'number') {
        searchStatus.value = `Scored ${details.scoredCount} results`
        return
      }
    }

    // Scraping fallback progress (message contains count)
    if (/scrap/i.test(payload.message) && /\d+/.test(payload.message)) {
      const m = payload.message.match(/(scrap(?:ing)?(?: product pages)? for )?(\d+)/i)
      if (m && m[2]) {
        searchStatus.value = `Scraping product pages for ${m[2]} ASINs...`
        return
      }
    }

    // If an ASIN is provided, show ASIN-level progress
    if (payload.asin) {
      searchStatus.value = `Processing ASIN ${payload.asin}...`
      return
    }

    // Fallback to raw message — but sanitize URLs to avoid showing long Amazon stripbooks links
    const sanitizeMessage = (m: string) => {
      if (!m) return m
      try {
        // Extract first URL if present
        const urlMatch = m.match(/https?:\/\/[^\s]+/i)
        if (urlMatch && urlMatch[0]) {
          const urlStr = urlMatch[0]
          try {
            const u = new URL(urlStr)
            const rh = u.searchParams.get('rh')
            if (rh) {
              const isbnMatch = rh.match(/p_66[:%3A]*(\d{10,13})/)
              if (isbnMatch && isbnMatch[1]) {
                return m.replace(urlStr, `ISBN ${isbnMatch[1]}`)
              }
            }
            // Replace URL with short host hint
            return m.replace(urlStr, `${u.hostname.replace(/^www\./, '')} search`)
          } catch {
            // If URL parsing fails, fall back to removing query portion
            return m.replace(urlStr, 'external search')
          }
        }
      } catch {
        // ignore and return original
      }
      return m
    }

    searchStatus.value = sanitizeMessage(payload.message)
  })
  // Watch for library changes to update added status
  stopWatchingLibrary = watch(
    () => libraryStore.audiobooks,
    async (newAudiobooks, oldAudiobooks) => {
      // Only update if the library actually changed (not just on initial load)
      if (oldAudiobooks && oldAudiobooks.length !== newAudiobooks.length) {
        logger.debug('Library changed, updating added status...')
        await checkExistingInLibrary()
      }
    },
    { deep: false }, // We don't need deep watching since we're just checking length
  )
})

onUnmounted(() => {
  try {
    unsubscribeSearchProgress?.()
  } catch {}
  unsubscribeSearchProgress = null

  try {
    stopWatchingLibrary?.()
  } catch {}
  stopWatchingLibrary = null
})
</script>

<style scoped>
.add-new-view {
  padding: 2em;
}

.page-header h1 {
  margin: 0 0 2rem 0;
  color: white;
  font-size: 2rem;
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

.settings-link {
  color: var(--brand-500);
  text-decoration: none;
  font-weight: 500;
}

.settings-link:hover {
  text-decoration: underline;
}

/* Search Tabs */
.search-tabs {
  display: flex;
  gap: 0.5rem;
  margin-bottom: 2rem;
  border-bottom: 1px solid #444;
}

.tab-btn {
  padding: 1rem 1.5rem;
  background: transparent;
  border: none;
  color: #ccc;
  cursor: pointer;
  border-bottom: 2px solid transparent;
  display: flex;
  align-items: center;
  gap: 0.5rem;
  font-weight: 500;
  transition: all 0.2s;
}

.tab-btn:hover {
  color: white;
  background-color: rgba(255, 255, 255, 0.05);
}

.tab-btn.active {
  color: var(--brand-500);
  border-bottom-color: var(--brand-500);
}

/* Search Section */
.search-section {
  margin-bottom: 2.5rem;
  background: linear-gradient(135deg, rgba(42, 42, 42, 0.95) 0%, rgba(35, 35, 35, 0.95) 100%);
  padding: 2rem;
  border-radius: 6px;
  border: 1px solid rgba(255, 255, 255, 0.08);
  box-shadow: 0 8px 32px rgba(0, 0, 0, 0.3);
  backdrop-filter: blur(10px);
}

.search-method {
  margin-bottom: 1.5rem;
}

.search-method-label {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  color: white;
  font-weight: 500;
  font-size: 1.375rem;
  margin-bottom: 0.75rem;
  letter-spacing: -0.025em;
}

.search-method-label svg {
  color: #4dabf7;
  width: 24px;
  height: 24px;
}

.search-help {
  color: #adb5bd;
  font-size: 0.95rem;
  margin: 0;
  line-height: 1.6;
  max-width: 600px;
}

.search-help .settings-link {
  color: #4dabf7;
  text-decoration: none;
  font-weight: 500;
  transition: all 0.2s ease;
  border-bottom: 1px solid transparent;
}

.search-help .settings-link:hover {
  color: #74c0fc;
  text-decoration: none;
  border-bottom-color: #74c0fc;
}

.search-help .settings-link {
  color: #4dabf7;
  text-decoration: none;
  font-weight: 500;
  transition: color 0.2s ease;
}

.search-help .settings-link:hover {
  color: #74c0fc;
  text-decoration: underline;
}

/* Unified Search */
.unified-search-bar {
  gap: 1rem;
  margin-bottom: 1.25rem;
  align-items: stretch;
  position: relative;
}

.unified-search-form {
  display: flex;
  gap: 1rem;
  align-items: center;
  width: 100%;
  flex-wrap: nowrap; /* keep actions on one row until small screens */
}

.unified-search-bar .search-input {
  /* Layout only: let global .form-input provide visuals */
  flex: 1 1 420px;
  min-width: 220px;
  height: var(--control-height);
  padding: 0 0.75rem; /* keep small horizontal padding */
  box-sizing: border-box;
}

.unified-search-bar .search-input:focus {
  outline: none;
}

.unified-search-bar .search-input::placeholder {
  /* Use muted gray for placeholder text for accessibility */
  color: #999;
  font-weight: 400;
}

.unified-search-bar .language-select,
.unified-search-bar .region-select {
  padding: 6px 8px;
  border: 1px solid #444;
  border-radius: 6px;
  background-color: #1a1a1a !important;
  color: #ffffff !important;
  font-size: 0.95rem;
  font-family: inherit;
  font-weight: 500;
  cursor: pointer;
  transition: all 0.18s ease;
  min-width: 140px;
  box-shadow: none;
  padding-right: 2.25rem;
  height: var(--control-height);
  display: inline-flex;
  align-items: center;
  -webkit-appearance: none !important;
  -moz-appearance: none !important;
  appearance: none !important;
  background-clip: padding-box;
  background-image: url("data:image/svg+xml;charset=UTF-8,%3csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='white' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'%3e%3cpolyline points='6,9 12,15 18,9'%3e%3c/polyline%3e%3c/svg%3e") !important;
  background-repeat: no-repeat !important;
  background-position: right 0.75rem center !important;
  background-size: 1.125rem !important;
}

.unified-search-bar .language-select:focus-visible,
.unified-search-bar .region-select:focus-visible {
  outline: none;
  border-color: #2196f3;
  box-shadow: 0 0 0 2px rgba(33, 150, 243, 0.2);
}

.unified-search-bar .language-select:hover,
.unified-search-bar .region-select:hover {
  border-color: #555;
}

.unified-search-bar .language-select:focus,
.unified-search-bar .region-select:focus {
  outline: none;
  border-color: #2196f3;
  box-shadow: 0 0 0 2px rgba(33, 150, 243, 0.2);
}

.unified-search-bar .language-select option,
.unified-search-bar .region-select option {
  background-color: #1a1a1a !important;
  color: white !important;
  padding: 0.5rem !important;
}

.language-select-wrapper,
.region-select-wrapper {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  white-space: nowrap;
}

.language-label,
.region-label {
  color: #b0bec5;
  font-size: 0.85rem;
  font-weight: 500;
  margin: 0;
  padding: 0;
}

.search-hint {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  color: #9ca3af;
  font-size: 0.8rem;
  padding: 0.6rem 1rem;
  background: linear-gradient(135deg, rgba(255, 255, 255, 0.04) 0%, rgba(255, 255, 255, 0.02) 100%);
  border: 1px solid rgba(255, 255, 255, 0.08);
  border-radius: 6px;
  margin-bottom: 0;
  backdrop-filter: blur(8px);
}

.search-hint svg {
  color: #4dabf7;
  width: 16px;
  height: 16px;
  flex-shrink: 0;
  margin-top: 0;
}

/* Advanced Search Inline Section */
.advanced-search-section {
  animation: slideDown 0.4s cubic-bezier(0.4, 0, 0.2, 1);
  position: relative;
}

.simple-search-button {
  position: absolute;
  top: 0;
  right: 0;
  padding: 0.75rem 1.5rem;
  background: rgba(255, 255, 255, 0.15);
  color: white;
  border: 1px solid rgba(255, 255, 255, 0.2);
  border-radius: 6px;
  cursor: pointer;
  font-weight: 500;
  font-size: 0.9rem;
  display: flex;
  align-items: center;
  gap: 0.5rem;
  transition: all 0.3s cubic-bezier(0.4, 0, 0.2, 1);
  box-shadow: 0 2px 8px rgba(0, 0, 0, 0.1);
}

.simple-search-button:hover {
  background: rgba(255, 255, 255, 0.2);
  border-color: rgba(255, 255, 255, 0.3);
  transform: translateY(-1px);
  box-shadow: 0 4px 16px rgba(0, 0, 0, 0.15);
}

@keyframes slideDown {
  from {
    opacity: 0;
    transform: translateY(-20px) scale(0.95);
  }
  to {
    opacity: 1;
    transform: translateY(0) scale(1);
  }
}

.advanced-search-header {
  margin-bottom: 2rem;
  padding-right: 10rem; /* Make room for the Simple Search button */
}

.advanced-search-header h3 {
  margin: 0 0 0.75rem 0;
  color: white;
  font-size: 1.25rem;
  font-weight: 500;
  display: flex;
  align-items: center;
  gap: 0.75rem;
  letter-spacing: -0.025em;
}

.advanced-search-header h3 svg {
  color: #9b59b6;
  width: 24px;
  height: 24px;
}

.advanced-search-header .help-text {
  color: #adb5bd;
  font-size: 0.95rem;
  margin: 0;
  line-height: 1.6;
}

.advanced-search-form {
  display: flex;
  flex-direction: column;
  gap: 1.5rem;
}

.advanced-search-buttons {
  display: flex;
  gap: 1.5rem;
}

.form-row {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
  gap: 1.25rem;
}

.form-group {
  display: flex;
  flex-direction: column;
}

.form-group label {
  color: white;
  font-weight: 500;
  margin-bottom: 0.75rem;
  font-size: 0.9rem;
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

.form-group label::before {
  content: '';
  width: 4px;
  height: 4px;
  background: #9b59b6;
  border-radius: 50%;
  flex-shrink: 0;
}

/* Do NOT override the global `.form-input` baseline here. Keep Add New view non-invasive
   so the app-wide form styles from `src/assets/components.css` remain authoritative.
   Only add minimal, additive adjustments local to this view if necessary. */
.form-input {
  /* Use global baseline; ensure full width and proper box-sizing for layout in this view */
  width: 100%;
  box-sizing: border-box;
}

/* Keep a distinct select appearance only if the select explicitly opts into it via .form-select */

select.form-input:focus {
  outline: none;
  border-color: #2196f3 !important;
  box-shadow: 0 0 0 2px rgba(33, 150, 243, 0.2) !important;
}

.advanced-search-actions {
  display: flex;
  justify-content: end;
  align-items: center;
  margin-top: 2rem;
  padding-top: 1.5rem;
  border-top: 1px solid rgba(255, 255, 255, 0.1);
}

/* Button styles now consolidated in addnew-consolidated.css */
/* .btn-primary, .btn-secondary styles are global and reusable */

/* Responsive Design */
@media (max-width: 1200px) {
  .client-pagination-controls {
    flex-direction: column;
    align-items: stretch;
  }

  .pagination-settings {
    justify-content: center;
  }

  .pagination-nav {
    justify-content: center;
    flex-wrap: wrap;
    margin-left: 0;
  }
}

@media (max-width: 1024px) {
  .unified-search-form {
    flex-direction: column;
    gap: 0.75rem;
    align-items: stretch;
  }

  .unified-search-bar .search-input {
    height: 2.25rem;
    flex-basis: auto;
  }

  .language-select-wrapper,
  .region-select-wrapper {
    width: 100%;
    flex-direction: row;
    align-items: center;
  }

  .language-label,
  .region-label {
    min-width: 60px;
    flex-shrink: 0;
  }

  .unified-search-bar .language-select,
  .unified-search-bar .region-select {
    flex: 1;
    min-width: auto;
    width: auto;
    height: 2.25rem;
  }

  .search-btn {
    width: 100%;
    min-width: auto;
  }

  .search-btn.advanced-btn {
    width: 100%;
  }
}

@media (max-width: 768px) {
  .search-section {
    padding: 1.5rem;
    margin-bottom: 2rem;
  }

  .search-method-label {
    font-size: 1.25rem;
  }

  .search-help {
    font-size: 0.875rem;
  }

  .simple-search-button {
    position: static;
    margin-bottom: 1rem;
    align-self: flex-end;
    justify-content: center;
    width: 100%;
  }

  .advanced-search-header {
    padding-right: 0;
  }

  .form-row {
    grid-template-columns: 1fr;
    gap: 1rem;
  }

  .advanced-search-actions {
    flex-direction: column;
    gap: 1rem;
    align-items: stretch;
  }

  .advanced-search-controls {
    justify-content: center;
  }

  .advanced-search-buttons {
    justify-content: center;
  }
}

@media (max-width: 480px) {
  .search-section {
    padding: 1rem;
  }

  .search-method-label {
    font-size: 1.125rem;
  }

  .search-help {
    font-size: 0.875rem;
  }

  .search-btn {
    padding: 0.875rem 1.5rem;
    font-size: 0.95rem;
  }

  .language-select-wrapper,
  .region-select-wrapper {
    gap: 0.375rem;
  }

  .language-label,
  .region-label {
    min-width: 50px;
    font-size: 0.8rem;
  }

  .unified-search-bar .language-select,
  .unified-search-bar .region-select {
    font-size: 0.85rem;
    padding: 0.5rem 0.75rem;
  }
}

.search-input {
  /* Layout only - visuals handled by global .form-input */
  height: var(--control-height);
  flex: 1;
  padding: 0 0.75rem;
  box-sizing: border-box;
}

.search-input.error {
  /* Keep only the error border treatment, avoid changing background color */
  border-color: #fa5252;
  box-shadow: 0 0 0 3px rgba(250, 82, 82, 0.1);
}
.search-input:focus {
  outline: none;
  border-color: #4dabf7;
  background-color: rgba(0, 0, 0, 0.3);
  box-shadow: 0 0 0 3px rgba(77, 171, 247, 0.1);
}

.search-input::placeholder {
  color: #999;
  text-transform: none;
}

/* Title Search Form */
.title-search-form {
  display: flex;
  flex-direction: column;
  gap: 1rem;
}

.form-row {
  display: flex;
  gap: 1rem;
}

.form-group {
  flex: 1;
}

.form-group label {
  display: block;
  color: white;
  font-weight: 500;
  margin-bottom: 0.5rem;
}

/* Keep inputs in this view layout-friendly but rely on global styles for visuals */
.form-input {
  width: 100%;
  height: var(--control-height);
  box-sizing: border-box;
}

.form-input:focus {
  outline: none;
}

/* Buttons - now consolidated in addnew-consolidated.css */
/* .search-btn, .btn-primary, .btn-secondary styles moved to shared stylesheet */

.search-btn.audible-btn:disabled {
  background: #ccc;
  cursor: not-allowed;
}

.search-btn.audible-catalog-btn {
  background: #ff6b35;
  box-shadow: 0 2px 8px rgba(255, 107, 53, 0.3);
}

.search-btn.audible-catalog-btn:hover:not(:disabled) {
  background: #f7931e;
  box-shadow: 0 4px 12px rgba(255, 107, 53, 0.4);
}

.search-btn.audible-catalog-btn:disabled {
  background: #ccc;
  cursor: not-allowed;
}

.search-btn svg {
  width: 20px;
  height: 20px;
}

.search-btn:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

/* Buttons are now centralized in `src/assets/buttons.css`. Use `.btn`, `.btn-primary`, `.btn-secondary`, etc. */

.error-message svg {
  color: #fa5252;
  width: 20px;
  height: 20px;
  flex-shrink: 0;
}

/* Loading Results */
.loading-results {
  display: flex;
  justify-content: center;
  align-items: center;
  padding: 4rem 2rem;
  min-height: 300px;
  background-color: #2a2a2a;
  border-radius: 6px;
  border: 1px solid rgba(255, 255, 255, 0.05);
}

.loading-spinner {
  text-align: center;
  color: #adb5bd;
}

.loading-spinner svg {
  font-size: 3rem;
  color: #4dabf7;
  margin-bottom: 1rem;
  width: 48px;
  height: 48px;
}

.loading-spinner i {
  font-size: 3rem;
  color: #4dabf7;
  margin-bottom: 1rem;
}

.loading-spinner p {
  font-size: 1.1rem;
  margin: 0;
  font-weight: 500;
}

.search-status {
  font-size: 0.875rem;
  color: #6c757d;
  margin: 0.75rem 0 0 0;
  font-style: italic;
}

/* Results */
.search-results h2 {
  color: white;
  margin-bottom: 1.5rem;
  font-size: 1.5rem;
  font-weight: 500;
}

/* ASIN Result Card */
.result-card {
  display: flex;
  background-color: #2a2a2a;
  border-radius: 6px;
  overflow: hidden;
  padding: 1.25rem;
  gap: 1.25rem;
  transition: all 0.2s ease;
  border: 1px solid transparent;
}

.result-card:hover {
  background-color: #2f2f2f;
  border-color: rgba(33, 150, 243, 0.3);
  box-shadow: 0 4px 12px rgba(0, 0, 0, 0.3);
}

.result-poster {
  width: 140px;
  height: 140px;
  flex-shrink: 0;
  background-color: #555;
  border: 1px solid rgba(255, 255, 255, 0.1);
  border-radius: 8px;
  overflow: hidden;
  display: flex;
  align-items: center;
  justify-content: center;
  box-shadow: 0 2px 8px rgba(0, 0, 0, 0.3);
  transition:
    transform 0.2s ease,
    box-shadow 0.2s ease;
}

.title-result-card:hover .result-poster {
  transform: scale(1.02);
  box-shadow: 0 4px 12px rgba(0, 0, 0, 0.4);
}

.result-poster img {
  width: 100%;
  height: 100%;
  object-fit: cover;
}

.placeholder-cover {
  width: 112px;
  height: 168px;
  display: flex;
  align-items: center;
  justify-content: center;
  background: linear-gradient(135deg, rgba(0, 0, 0, 0.06), rgba(0, 0, 0, 0.02));
  border-radius: 6px;
  color: #9ca3af;
  font-size: 1.6rem;
}

.placeholder-cover-image {
  width: 112px;
  height: 168px;
  object-fit: cover;
  border-radius: 6px;
}

.result-info {
  flex: 1;
  display: flex;
  flex-direction: column;
  min-width: 0;
}

.result-info h3 {
  margin: 0;
  color: white;
  font-size: 1.4rem;
  line-height: 1.3;
  font-weight: 500;
  display: -webkit-box;
  -webkit-line-clamp: 2;
  line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
}

.result-author {
  color: #4dabf7;
  margin: 0;
  font-weight: 500;
  font-size: 0.95rem;
  display: -webkit-box;
  -webkit-line-clamp: 1;
  line-clamp: 1;
  -webkit-box-orient: vertical;
  overflow: hidden;
}

.result-narrator {
  color: #adb5bd;
  margin: 0;
  font-style: italic;
  font-size: 0.9rem;
  display: -webkit-box;
  -webkit-line-clamp: 1;
  line-clamp: 1;
  -webkit-box-orient: vertical;
  overflow: hidden;
}

.result-stats {
  display: flex;
  gap: 0.5rem;
  margin: 0.25rem 0;
  flex-wrap: wrap;
}

.stat-item {
  display: flex;
  align-items: center;
  gap: 0.35rem;
  color: #adb5bd;
  font-size: 0.875rem;
  background-color: rgba(255, 255, 255, 0.05);
  padding: 0.35rem 0.7rem;
  border-radius: 6px;
  white-space: nowrap;
  transition:
    background-color 0.2s ease,
    color 0.2s ease;
}

.result-series {
  display: flex;
  gap: 0.5rem;
  margin: 0.25rem 0;
  flex-wrap: wrap;
}

.series-badge {
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
  color: #adb5bd;
  font-size: 0.875rem;
  background-color: rgba(255, 255, 255, 0.05);
  padding: 0.35rem 0.7rem;
  border-radius: 6px;
  white-space: nowrap;
  transition:
    background-color 0.2s ease,
    color 0.2s ease;
  z-index: 100;
}

.series-badge:hover {
  background-color: rgba(255, 255, 255, 0.08);
}

.series-badge svg {
  width: 14px;
  height: 14px;
}

.metadata-badges {
  display: flex;
  gap: 0.5rem;
  margin: 0.25rem 0;
  flex-wrap: wrap;
}

.metadata-badge {
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
  color: #adb5bd;
  font-size: 0.875rem;
  background-color: rgba(255, 255, 255, 0.05);
  padding: 0.35rem 0.7rem;
  border-radius: 6px;
  white-space: nowrap;
  transition:
    background-color 0.2s ease,
    color 0.2s ease;
}

.metadata-badge:hover {
  background-color: rgba(255, 255, 255, 0.08);
}

.metadata-badge svg {
  width: 14px;
  height: 14px;
}

.stat-item:hover {
  background-color: rgba(255, 255, 255, 0.08);
}

.stat-item svg {
  width: 14px;
  height: 14px;
  flex-shrink: 0;
}

.stat-item i {
  color: #4dabf7;
}

.result-description {
  color: #ccc;
  margin: 0.5rem 0;
  line-height: 1.5;
  flex-grow: 1;
  overflow: hidden;
  display: -webkit-box;
  -webkit-line-clamp: 3;
  line-clamp: 3;
  -webkit-box-orient: vertical;
}

.result-meta {
  display: flex;
  flex-wrap: wrap;
  gap: 0.5rem;
  margin: 0.25rem 0 0 0;
  color: #999;
  font-size: 0.875rem;
}

.result-meta span,
.result-meta a.source-link {
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
  background-color: rgba(33, 150, 243, 0.15) !important;
  color: #4dabf7 !important;
  font-weight: 500;
  padding: 0.35rem 0.7rem !important;
  border-radius: 6px !important;
  text-decoration: none;
  white-space: nowrap;
  transition: all 0.2s ease;
}

.result-meta span:hover,
.result-meta a.source-link:hover {
  background-color: rgba(33, 150, 243, 0.25) !important;
}

.metadata-source-badge,
.metadata-source-link {
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
  background-color: rgba(33, 150, 243, 0.15) !important;
  color: #4dabf7 !important;
  font-weight: 500;
  padding: 0.35rem 0.7rem !important;
  border-radius: 6px !important;
  text-decoration: none;
  transition: all 0.2s ease;
  white-space: nowrap;
}

.metadata-source-link:hover {
  background-color: rgba(33, 150, 243, 0.25) !important;
  color: #74c0fc !important;
  transform: translateY(-1px);
}

.metadata-source-badge svg,
.metadata-source-link svg {
  width: 14px;
  height: 14px;
}

.result-meta a.source-link {
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
  background-color: rgba(255, 255, 255, 0.05);
  color: #adb5bd;
  padding: 0.35rem 0.7rem;
  border-radius: 6px;
  text-decoration: none;
  transition: all 0.2s ease;
}

.result-meta a.source-link:hover {
  background-color: rgba(255, 255, 255, 0.08);
}

.result-meta .source-badge {
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
  color: #adb5bd;
  font-size: 0.875rem;
  background-color: rgba(255, 255, 255, 0.05);
  padding: 0.35rem 0.7rem;
  border-radius: 6px;
  white-space: nowrap;
  transition:
    background-color 0.2s ease,
    color 0.2s ease;
}

.result-meta .source-badge:hover {
  background-color: rgba(255, 255, 255, 0.08);
}

.result-meta .source-badge svg,
.result-meta a.source-link svg {
  width: 14px;
  height: 14px;
}

.result-actions {
  display: flex;
  gap: 0.75rem;
  margin-top: 0.75rem;
}

.result-actions .btn {
  flex: 1;
  min-width: 0;
}

/* Title Search Results */
.title-results {
  display: flex;
  flex-direction: column;
  gap: 1rem;
}

/* Audible pagination controls for advanced searches */
.audible-pagination {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  margin-bottom: 0.75rem;
}

.audible-pagination .page-indicator {
  color: #b6bcc4;
  font-size: 0.95rem;
}

.audible-pagination .btn {
  padding: 0.45rem 0.9rem;
  border-radius: 6px;
  border: 1px solid rgba(255, 255, 255, 0.06);
  background: linear-gradient(135deg, rgba(255, 255, 255, 0.02), rgba(0, 0, 0, 0.02));
  color: white;
}

.title-result-card {
  display: flex;
  background-color: #2a2a2a;
  border: 1px solid rgba(255, 255, 255, 0.1);
  border-radius: 8px;
  padding: 1.25rem;
  gap: 1.25rem;
  align-items: flex-start;
  transition: all 0.2s ease;
  box-shadow: 0 2px 8px rgba(0, 0, 0, 0.3);
}

.title-result-card:hover {
  border-color: rgba(33, 150, 243, 0.3);
  box-shadow: 0 4px 16px rgba(0, 0, 0, 0.4);
  transform: translateY(-2px);
}

.title-result-card .result-info {
  flex: 1;
}

.title-result-card .result-actions {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
  min-width: 150px;
  align-self: flex-start;
}

.title-result-card .result-actions .btn {
  padding: 0.6rem 1rem;
  font-weight: 500;
  border-radius: 6px;
  transition: all 0.2s ease;
}

.title-result-card .result-actions .btn:hover {
  transform: translateY(-1px);
  box-shadow: 0 2px 8px rgba(0, 0, 0, 0.3);
}

.title-result-card h3 {
  margin: 0 0 0.5rem 0;
  font-size: 1.25rem;
  font-weight: 500;
  color: #ffffff;
  line-height: 1.3;
}

.result-author {
  color: #bfc7cc;
  margin: 0 0 0.75rem 0;
  font-size: 1rem;
  font-weight: 500;
}

/* Empty States */
.getting-started,
.empty-state,
.error-state {
  text-align: center;
  padding: 4rem 2rem;
  color: #ccc;
}

.welcome-icon,
.empty-icon,
.error-icon {
  font-size: 4rem;
  margin-bottom: 1rem;
  color: #555;
}

.error-icon {
  color: #e74c3c;
}

.getting-started h2,
.empty-state h2,
.error-state h2 {
  color: white;
  margin-bottom: 1rem;
}

.help-section {
  margin: 2rem 0;
  text-align: left;
  max-width: 500px;
  margin-left: auto;
  margin-right: auto;
}

.help-section h3 {
  color: white;
  margin-bottom: 1rem;
}

.help-section ul {
  color: #ccc;
  line-height: 1.6;
}

.help-section li {
  margin-bottom: 0.5rem;
}

.quick-actions {
  display: flex;
  gap: 1rem;
  justify-content: center;
  margin-top: 2rem;
}

/* Inline status */
.inline-status {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  padding: 1rem;
  background-color: rgba(var(--brand-rgb), 0.1);
  border: 1px solid var(--brand-500);
  border-radius: 6px;
  color: var(--brand-500);
  margin-bottom: 1rem;
}

.inline-status.warning {
  background-color: rgba(241, 196, 15, 0.1);
  border-color: #f1c40f;
  color: #f1c40f;
}

/* Responsive design */
@media (max-width: 768px) {
  .search-tabs {
    flex-direction: column;
  }

  .tab-btn {
    justify-content: center;
  }

  .form-row {
    flex-direction: column;
  }

  .search-bar {
    flex-direction: column;
  }

  .result-card,
  .title-result-card {
    flex-direction: column;
    text-align: center;
  }

  .result-poster {
    width: 100px;
    height: 100px;
    margin: 0 auto;
  }

  .result-info {
    width: 100%;
  }

  .result-series,
  .metadata-badges,
  .result-stats,
  .result-meta {
    margin: 0.25rem auto;
    justify-content: center;
  }

  .result-series,
  .metadata-badges {
    row-gap: 0.5rem;
  }

  .result-actions,
  .helper-actions {
    justify-content: center;
    flex-wrap: wrap;
  }

  .quick-actions {
    flex-direction: column;
    align-items: center;
  }

  /* Mobile improvements: stack unified search and make CTAs full width */
  .unified-search-bar {
    flex-direction: column;
    gap: 0.5rem;
  }

  .unified-search-bar .search-input {
    width: 100%;
    font-size: 1rem;
  }

  .unified-search-bar .search-btn {
    width: 100%;
    min-width: 0;
    padding: 0.875rem;
  }

  /* Make result action buttons stack and be larger on mobile */
  .title-result-card .result-actions,
  .result-card .result-actions {
    flex-direction: column;
    gap: 0.5rem;
    width: 100%;
  }

  .title-result-card .result-actions .btn,
  .result-card .result-actions .btn {
    width: 100%;
    padding: 0.9rem 1rem;
    font-size: 1rem;
  }

  /* Reduce page padding for small devices to maximize content space */
  .add-new-view {
    padding: 1rem;
  }

  /* Ensure results area keeps a stable gutter for scrollbars */
  .search-results,
  .title-results,
  .search-section {
    scrollbar-gutter: stable both-edges;
  }

  /* Keep mobile to a single continuous scroll container so sticky result controls
     lock naturally as the page scrolls instead of inside a nested pane. */
  .search-results {
    max-height: none;
    overflow: visible;
    padding-right: 0;
    scrollbar-width: auto;
  }

  /* Allow long metadata badges and names to wrap instead of overflowing */
  .result-meta span,
  .metadata-source-badge,
  .metadata-source-link {
    white-space: normal;
    overflow-wrap: anywhere;
  }
}

.cancelled {
  text-align: center;
  padding: 2rem;
  color: #e74c3c;
}

.cancelled svg {
  font-size: 2rem;
  display: block;
  margin-bottom: 1rem;
}

/* Pagination Controls */
.results-controls {
  display: flex;
  flex-direction: column;
  gap: 1rem;
  margin-top: 1.5rem;
  padding-top: 0.5rem;
  border-top: 1px solid rgba(255, 255, 255, 0.1);
  position: sticky;
  top: 60px;
  background-color: #1a1a1a;
  z-index: 100;
  padding-bottom: 0.5rem;
  width: 100%;
  box-sizing: border-box;
  box-shadow: 0 2px 8px rgba(0, 0, 0, 0.3);
  backdrop-filter: blur(10px);
}

.client-pagination-controls {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 1rem;
  flex-wrap: wrap;
  width: 100%;
  min-width: 0;
  box-sizing: border-box;
}

.pagination-settings {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  min-width: 0;
}

.pagination-nav {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  min-width: 0;
  margin-left: auto;
}

.page-indicator {
  color: #b6bcc4;
  font-size: 0.95rem;
  white-space: nowrap;
  flex: 0 1 auto;
  min-width: 0;
  text-align: center;
}

.results-controls .btn {
  min-width: 0;
}

.pagination-nav .btn {
  flex: 0 1 auto;
  padding-inline: 1rem;
}

@media (max-width: 1200px) {
  .client-pagination-controls {
    flex-direction: column;
    align-items: stretch;
  }

  .pagination-settings {
    justify-content: center;
  }

  .pagination-nav {
    width: 100%;
    margin-left: 0;
    justify-content: space-between;
    flex-wrap: nowrap;
  }

  .pagination-nav .btn {
    width: 100%;
    max-width: 8.75rem;
  }

  .page-indicator {
    flex: 0 0 auto;
  }
}

.small-label {
  color: white;
  font-weight: 500;
  font-size: 0.875rem;
  margin: 0;
}

.small-select {
  padding: 0.5rem 0.75rem;
  border: 1px solid #444;
  border-radius: 6px;
  background-color: #1a1a1a !important;
  color: white !important;
  font-size: 0.875rem;
  cursor: pointer;
  transition: all 0.2s ease;
  min-width: 80px;
  width: auto;
  -webkit-appearance: none !important;
  -moz-appearance: none !important;
  appearance: none !important;
  background-clip: padding-box;
  background-image: url("data:image/svg+xml;charset=UTF-8,%3csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='white' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'%3e%3cpolyline points='6,9 12,15 18,9'%3e%3c/polyline%3e%3c/svg%3e") !important;
  background-repeat: no-repeat !important;
  background-position: right 0.5rem center !important;
  background-size: 0.75rem !important;
  padding-right: 1.75rem !important;
}

.small-select:focus {
  outline: none;
  border-color: #2196f3;
  box-shadow: 0 0 0 2px rgba(33, 150, 243, 0.2);
}

.small-select option {
  background-color: #1a1a1a !important;
  color: white !important;
  padding: 0.5rem !important;
}

.small-pager {
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

@media (max-width: 768px) {
  .client-pagination-controls {
    flex-direction: column;
    align-items: stretch;
    gap: 1rem;
  }

  .results-controls {
    top: 60px;
    z-index: 200;
    isolation: isolate;
  }

  .pagination-settings {
    justify-content: center;
  }

  .pagination-nav {
    width: 100%;
    margin-left: 0;
    justify-content: center;
  }
}
</style>
