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
  <div class="collection-view">
    <!-- Top Navigation Bar -->
    <div v-if="!isMetadataCollection" class="top-nav">
      <div class="nav-title">
        <h1><PhBooks /> {{ name }}</h1>
      </div>
    </div>

    <section v-if="isAuthorCollection" class="hero-section author-hero-section">
      <div class="backdrop author-hero-backdrop" :style="authorHeroBackdropStyle"></div>
      <div class="hero-content author-hero-content">
        <div class="poster-container author-hero-poster-container">
          <img
            :src="authorHeroImageUrl"
            :alt="`${authorHeroName} author portrait`"
            class="poster author-hero-poster"
            loading="lazy"
            decoding="async"
            @error="handleImageError"
          />
        </div>

        <div class="info-section author-hero-info">
          <h1 class="title author-hero-title">{{ authorHeroName }}</h1>

          <div class="meta-info author-hero-meta">
            <span>
              <PhGlobe />
              {{ authorMonitoringContextLabel }}
            </span>
            <span v-if="authorHeroAsin" class="author-hero-asin"> ASIN {{ authorHeroAsin }} </span>
          </div>

          <div class="status-badges author-hero-badges">
            <Pill :variant="isCurrentAuthorMonitored ? 'primary' : 'default'">
              <component :is="isCurrentAuthorMonitored ? PhEye : PhEyeSlash" />
              {{ isCurrentAuthorMonitored ? 'Monitoring Author' : 'Not Monitored' }}
            </Pill>
            <Pill variant="success"> {{ authorLibraryCount }} in library </Pill>
            <Pill v-if="authorNotAddedCount > 0" variant="warning">
              {{ authorNotAddedCount }} ready to add
            </Pill>
            <Pill variant="info">
              {{ authorLanguageLabel }}
            </Pill>
          </div>

          <div v-if="authorHeroBiography" class="description author-hero-description">
            <div
              class="description-content author-hero-description-content"
              :class="{ expanded: showFullAuthorDescription }"
            >
              {{ authorHeroDescriptionText }}
            </div>
            <button
              v-if="authorHeroCanToggleDescription"
              class="show-more-btn author-hero-toggle"
              @click="showFullAuthorDescription = !showFullAuthorDescription"
            >
              {{ showFullAuthorDescription ? 'Show Less' : 'Show More' }}
            </button>
          </div>

          <div v-if="authorSimilarAuthors.length > 0" class="author-similar-authors">
            <div class="author-similar-title">Related Authors</div>
            <div class="author-similar-list">
              <button
                v-for="author in authorSimilarAuthors"
                :key="`${author.asin || author.name}`"
                class="author-similar-chip"
                @click="goToRelatedAuthor(author.name)"
              >
                {{ safeText(author.name) }}
              </button>
            </div>
          </div>
        </div>
      </div>
    </section>

    <section v-if="isSeriesCollection" class="hero-section author-hero-section series-hero-section">
      <div class="backdrop author-hero-backdrop" :style="seriesHeroBackdropStyle"></div>
      <div class="hero-content author-hero-content">
        <div class="poster-container author-hero-poster-container series-hero-poster-container">
          <div v-if="seriesHeroPosterBooks.length > 0" class="series-hero-poster-card">
            <div class="series-hero-covers">
              <template v-if="seriesHeroPosterBooks.length === 1 && seriesHeroSinglePosterBook">
                <div class="series-hero-single-bg" :style="seriesHeroSingleBackgroundStyle"></div>
                <div
                  class="series-hero-cover-item"
                  :class="{ 'is-not-added': !seriesHeroSinglePosterBook.inLibrary }"
                  :style="getSeriesHeroCoverStyle(0, 1)"
                >
                  <img
                    :src="
                      getProtectedImageSrc(seriesHeroSinglePosterBook.imageUrl, getPlaceholderUrl())
                    "
                    :alt="`${seriesHeroSinglePosterBook.title} cover`"
                    class="series-hero-cover-image centered"
                    loading="lazy"
                    decoding="async"
                    @error="handleImageError"
                  />
                </div>
              </template>
              <template v-else>
                <div
                  v-for="(book, index) in seriesHeroPosterBooks"
                  :key="book.key"
                  class="series-hero-cover-item"
                  :class="{ 'is-not-added': !book.inLibrary }"
                  :style="getSeriesHeroCoverStyle(index, seriesHeroPosterBooks.length)"
                >
                  <img
                    :src="getProtectedImageSrc(book.imageUrl, getPlaceholderUrl())"
                    :alt="`${book.title} cover`"
                    class="series-hero-cover-image"
                    loading="lazy"
                    decoding="async"
                    @error="handleImageError"
                  />
                </div>
              </template>
            </div>
            <div v-if="seriesVisibleBookCount > 0" class="series-hero-count-badge">
              {{ seriesVisibleBookCount }}
            </div>
          </div>
          <img
            v-else
            :src="seriesHeroImageUrl"
            :alt="`${seriesHeroName} series artwork`"
            class="poster author-hero-poster"
            loading="lazy"
            decoding="async"
            @error="handleImageError"
          />
        </div>

        <div class="info-section author-hero-info">
          <h1 class="title author-hero-title">{{ seriesHeroName }}</h1>

          <div class="meta-info author-hero-meta">
            <span>
              <PhGlobe />
              {{ seriesMetadataContextLabel }}
            </span>
            <span v-if="seriesHeroAsin" class="author-hero-asin"> ASIN {{ seriesHeroAsin }} </span>
          </div>

          <div class="status-badges author-hero-badges">
            <Pill :variant="isCurrentSeriesMonitored ? 'primary' : 'default'">
              <component :is="isCurrentSeriesMonitored ? PhEye : PhEyeSlash" />
              {{ isCurrentSeriesMonitored ? 'Monitoring Series' : 'Not Monitored' }}
            </Pill>
            <Pill variant="success"> {{ seriesLibraryCount }} in library </Pill>
            <Pill v-if="seriesNotAddedCount > 0" variant="warning">
              {{ seriesNotAddedCount }} ready to add
            </Pill>
            <Pill variant="primary"> {{ seriesCatalogTotalCount }} total books </Pill>
            <Pill variant="info">
              {{ seriesLanguageLabel }}
            </Pill>
          </div>

          <div v-if="seriesHeroBiography" class="description author-hero-description">
            <div
              class="description-content author-hero-description-content"
              :class="{ expanded: showFullSeriesDescription }"
            >
              {{ seriesHeroDescriptionText }}
            </div>
            <button
              v-if="seriesHeroCanToggleDescription"
              class="show-more-btn author-hero-toggle"
              @click="showFullSeriesDescription = !showFullSeriesDescription"
            >
              {{ showFullSeriesDescription ? 'Show Less' : 'Show More' }}
            </button>
          </div>
        </div>
      </div>
    </section>

    <!-- Top Toolbar -->
    <div
      class="toolbar"
      :class="{
        'toolbar-without-top-nav': isMetadataCollection,
        'toolbar-with-monitoring': isAuthorCollection || isSeriesCollection,
      }"
    >
      <div class="toolbar-left">
        <button class="toolbar-btn" @click="goBack">
          <PhArrowLeft />
          Back
        </button>
        <button class="toolbar-btn" @click="toggleViewMode" title="Toggle view">
          <PhGridFour v-if="viewMode === 'list'" />
          <PhList v-else />
        </button>
        <button
          class="toolbar-btn"
          :class="{ active: showItemDetails }"
          @click="toggleItemDetails"
          :aria-pressed="showItemDetails"
          title="Toggle item details"
        >
          <PhInfo />
        </button>
        <span class="count-badge" v-if="audiobooks.length > 0">
          {{ audiobooks.length }} book{{ audiobooks.length !== 1 ? 's' : '' }}
        </span>
        <button class="toolbar-btn" @click="refreshLibrary">
          <PhArrowClockwise />
          Refresh
        </button>
        <button v-if="selectedCount > 0" class="toolbar-btn" @click="libraryStore.clearSelection()">
          <PhX />
          Clear Selection
        </button>
        <button
          v-if="selectableAudiobookCount > 0 && selectedCount === 0"
          class="toolbar-btn"
          @click="selectAllVisible()"
        >
          <PhCheckSquare />
          Select All
        </button>
        <button v-if="selectedCount > 0" class="toolbar-btn edit-btn" @click="showBulkEdit">
          <PhPencil />
          Edit Selected
        </button>
        <button v-if="selectedCount > 0" class="toolbar-btn" @click="showOrganize">
          <PhFolderOpen />
          Organize Selected
        </button>
        <button v-if="selectedCount > 0" class="toolbar-btn delete-btn" @click="confirmBulkDelete">
          <PhTrash />
          Delete Selected ({{ selectedCount }})
        </button>
      </div>
      <div class="toolbar-right">
        <div v-if="isAuthorCollection" class="author-monitoring-controls">
          <div class="author-monitoring-actions">
            <button
              class="toolbar-btn author-refresh-btn"
              :disabled="authorMetadataRefreshBusy"
              @click="refreshAuthorMetadata"
              title="Refresh author metadata"
            >
              <PhArrowClockwise v-if="authorMetadataRefreshBusy" class="spin-icon" />
              <PhArrowClockwise v-else />
              Refresh Author Metadata
            </button>
            <button
              class="toolbar-btn author-monitor-btn"
              :class="{ active: isCurrentAuthorMonitored }"
              :disabled="authorMonitoringBusy || authorMetadataRefreshBusy"
              @click="toggleAuthorMonitoring"
              :title="
                isCurrentAuthorMonitored ? 'Stop monitoring this author' : 'Monitor this author'
              "
            >
              <PhArrowClockwise v-if="authorMonitoringBusy" class="spin-icon" />
              <component v-else :is="isCurrentAuthorMonitored ? PhEye : PhPlus" />
              {{ isCurrentAuthorMonitored ? 'Monitoring Author' : 'Monitor Author' }}
            </button>
          </div>
        </div>
        <div v-else-if="isSeriesCollection" class="author-monitoring-controls">
          <div class="author-monitoring-actions">
            <button
              class="toolbar-btn author-refresh-btn"
              :disabled="seriesMetadataRefreshBusy"
              @click="refreshSeriesMetadata"
              title="Refresh series metadata"
            >
              <PhArrowClockwise v-if="seriesMetadataRefreshBusy" class="spin-icon" />
              <PhArrowClockwise v-else />
              Refresh Series Metadata
            </button>
            <button
              class="toolbar-btn author-monitor-btn"
              :class="{ active: isCurrentSeriesMonitored }"
              :disabled="seriesMonitoringBusy || seriesMetadataRefreshBusy"
              @click="toggleSeriesMonitoring"
              :title="
                isCurrentSeriesMonitored ? 'Stop monitoring this series' : 'Monitor this series'
              "
            >
              <PhArrowClockwise v-if="seriesMonitoringBusy" class="spin-icon" />
              <component v-else :is="isCurrentSeriesMonitored ? PhEye : PhPlus" />
              {{ isCurrentSeriesMonitored ? 'Monitoring Series' : 'Monitor Series' }}
            </button>
          </div>
        </div>
        <div class="toolbar-filters">
          <CustomSelect
            v-model="sortKeyProxy"
            :options="sortOptions"
            class="toolbar-custom-select"
            aria-label="Sort by"
          />
        </div>
      </div>
    </div>

    <!-- Audiobooks Grid -->
    <LoadingState v-if="loading" message="Loading audiobooks..." />

    <div v-else-if="error" class="error-state">
      <div class="error-icon">
        <PhWarningCircle />
      </div>
      <h2>Error Loading Library</h2>
      <p>{{ error }}</p>
      <button @click="refreshLibrary" class="retry-button btn">
        <PhArrowClockwise />
        Retry
      </button>
    </div>

    <EmptyState
      v-else-if="audiobooks.length === 0"
      title="No audiobooks found"
      :message="`No audiobooks found for this ${type}.`"
    >
      <template #icon>
        <PhBookOpen :size="48" />
      </template>
    </EmptyState>

    <div v-else class="audiobooks-container">
      <!-- List View (match AudiobooksView styling) -->
      <div v-if="viewMode === 'list'" class="audiobooks-list">
        <div v-if="audiobooks.length > 0" class="list-header">
          <div class="col-select"></div>
          <div class="col-cover">Cover</div>
          <div class="col-title">Title / Author</div>
          <div class="col-status">Status</div>
          <div class="col-actions">Actions</div>
        </div>

        <template v-for="section in paginatedAudiobookSections" :key="`list-${section.key}`">
          <div
            v-if="shouldShowAvailabilitySections && section.items.length > 0"
            class="list-section-header"
            :class="`is-${section.key}`"
          >
            <span class="section-title">{{ section.title }}</span>
            <span class="section-count">{{ section.count }}</span>
          </div>

          <div
            v-for="audiobook in section.items"
            :key="`${section.key}-${audiobook.key}`"
            tabindex="0"
            @keydown.enter="handleRowClick(audiobook)"
            class="audiobook-list-item"
            :class="{
              selected: audiobook.inLibrary && isSelected(audiobook.id),
              'status-no-file': getAudiobookStatus(audiobook) === 'no-file',
              'status-downloading': getAudiobookStatus(audiobook) === 'downloading',
              'status-quality-mismatch': getAudiobookStatus(audiobook) === 'quality-mismatch',
              'status-quality-match': getAudiobookStatus(audiobook) === 'quality-match',
              'not-in-library': !audiobook.inLibrary,
            }"
            @click="handleRowClick(audiobook)"
          >
            <div
              v-if="audiobook.inLibrary"
              class="selection-checkbox"
              @click.stop="handleCheckboxClick(audiobook, $event)"
              @mousedown.prevent
            >
              <input
                type="checkbox"
                :checked="isSelected(audiobook.id)"
                @change="onCheckboxChange(audiobook, $event)"
                @keydown.space.prevent="
                  handleCheckboxKeydown && handleCheckboxKeydown(audiobook, $event)
                "
              />
            </div>
            <div v-else class="selection-checkbox-spacer" aria-hidden="true"></div>

            <img
              class="list-thumb"
              :src="getProtectedImageSrc(audiobook.imageUrl, getPlaceholderUrl())"
              :alt="audiobook.title"
              loading="lazy"
              decoding="async"
              @error="handleImageError"
            />

            <div class="list-details">
              <div class="audiobook-title">
                <span
                  v-if="type === 'series' && audiobook.seriesNumber"
                  class="list-series-position"
                  >#{{ audiobook.seriesNumber }}</span
                >{{ safeText(audiobook.title) }}
              </div>
              <div class="audiobook-author">
                {{
                  audiobook.authors
                    ?.map((author) => safeText(author))
                    .slice(0, 2)
                    .join(', ') || 'Unknown Author'
                }}
              </div>
              <div v-if="showItemDetails" class="list-extra-details">
                <div class="detail-line small">
                  {{
                    (audiobook.narrators || [])
                      .slice(0, 1)
                      .map((n) => safeText(n))
                      .join(', ') || ''
                  }}
                  <span
                    v-if="
                      audiobook.narrators &&
                      audiobook.narrators.length &&
                      (audiobook.publisher || audiobook.publishYear)
                    "
                  >
                    •
                  </span>
                  {{ safeText(audiobook.publisher)
                  }}<span v-if="audiobook.publishYear">
                    • {{ safeText(audiobook.publishYear?.toString?.() ?? '') }}</span
                  >
                </div>
              </div>
            </div>

            <div class="list-badges">
              <div
                class="status-badge"
                :class="getAudiobookStatus(audiobook)"
                role="button"
                tabindex="0"
                @click.stop="() => {}"
                :aria-label="`Status for ${audiobook.title}`"
              >
                {{ statusText(getAudiobookStatus(audiobook)) }}
              </div>
              <div
                v-if="audiobook.inLibrary && getQualityProfileName(audiobook.qualityProfileId)"
                class="quality-profile-badge"
              >
                <PhStar />
                {{ getQualityProfileName(audiobook.qualityProfileId) }}
              </div>
              <div
                class="monitored-badge"
                :class="{ unmonitored: !audiobook.inLibrary || !audiobook.monitored }"
              >
                <component :is="audiobook.inLibrary && audiobook.monitored ? PhEye : PhEyeSlash" />
                {{ getMonitoringLabel(audiobook) }}
              </div>
            </div>

            <div class="list-actions">
              <button
                v-if="audiobook.inLibrary"
                class="action-btn edit-btn-small"
                @click.stop="editAudiobook(audiobook)"
                title="Edit"
              >
                <PhPencil />
              </button>
              <button
                v-if="audiobook.inLibrary"
                class="action-btn delete-btn-small"
                @click.stop="deleteAudiobook(audiobook)"
                title="Delete"
              >
                <PhTrash />
              </button>
              <button
                v-else
                class="action-btn add-btn-small"
                @click.stop="openAddToLibrary(audiobook)"
                title="Add to Library"
              >
                <PhPlus />
              </button>
            </div>
          </div>
        </template>
      </div>

      <!-- Grid View -->
      <div v-else class="collection-sections">
        <section
          v-for="section in paginatedAudiobookSections"
          :key="`grid-${section.key}`"
          class="collection-section"
        >
          <div
            v-if="shouldShowAvailabilitySections && section.items.length > 0"
            class="collection-section-header"
            :class="`is-${section.key}`"
          >
            <span class="section-title">{{ section.title }}</span>
            <span class="section-count">{{ section.count }}</span>
          </div>

          <div class="grid-view">
            <div
              v-for="audiobook in section.items"
              :key="`${section.key}-${audiobook.key}`"
              class="collection-card"
              :class="{
                selected: audiobook.inLibrary && isSelected(audiobook.id),
                'status-no-file': getAudiobookStatus(audiobook) === 'no-file',
                'status-downloading': getAudiobookStatus(audiobook) === 'downloading',
                'status-quality-mismatch': getAudiobookStatus(audiobook) === 'quality-mismatch',
                'status-quality-match': getAudiobookStatus(audiobook) === 'quality-match',
                'not-in-library': !audiobook.inLibrary,
              }"
              @click="handleCardClick(audiobook)"
            >
              <div
                v-if="audiobook.inLibrary"
                class="selection-checkbox"
                @click.stop="handleCheckboxClick(audiobook, $event)"
                @mousedown.prevent
              >
                <input
                  type="checkbox"
                  :checked="isSelected(audiobook.id)"
                  @change="onCheckboxChange(audiobook, $event)"
                  @keydown.space.prevent="handleCheckboxKeydown(audiobook, $event)"
                />
              </div>
              <div class="collection-cover">
                <div
                  v-if="type === 'series' && audiobook.seriesNumber"
                  class="series-position-badge"
                >
                  #{{ audiobook.seriesNumber }}
                </div>
                <img
                  v-if="audiobook.imageUrl"
                  :src="getProtectedImageSrc(audiobook.imageUrl, getPlaceholderUrl())"
                  :alt="audiobook.title"
                  loading="lazy"
                  decoding="async"
                  @error="handleImageError"
                  class="collection-image"
                />
                <div v-else class="no-cover">
                  <PhBookOpen />
                </div>
                <div class="status-overlay">
                  <div v-if="!showItemDetails" class="audiobook-title collection-title">
                    {{ safeText(audiobook.title) }}
                  </div>
                  <div v-if="!showItemDetails" class="audiobook-author collection-author">
                    {{
                      audiobook.authors?.map((author) => safeText(author)).join(', ') ||
                      'Unknown Author'
                    }}
                  </div>
                  <div
                    v-if="audiobook.inLibrary && getQualityProfileName(audiobook.qualityProfileId)"
                    class="quality-profile-badge"
                  >
                    <PhStar />
                    {{ getQualityProfileName(audiobook.qualityProfileId) }}
                  </div>
                  <div
                    class="monitored-badge"
                    :class="{ unmonitored: !audiobook.inLibrary || !audiobook.monitored }"
                  >
                    <component
                      :is="audiobook.inLibrary && audiobook.monitored ? PhEye : PhEyeSlash"
                    />
                    {{ getMonitoringLabel(audiobook) }}
                  </div>
                </div>
              </div>
              <!-- Bottom placard (only show when item details are enabled) -->
              <div v-if="showItemDetails" class="series-bottom-placard">
                <div class="series-bottom-content">
                  <p class="series-bottom-title">{{ safeText(audiobook.title) }}</p>
                  <p class="series-bottom-author" v-if="audiobook.authors?.[0]">
                    {{ audiobook.authors[0] }}
                  </p>
                  <p class="series-bottom-meta">{{ statusText(getAudiobookStatus(audiobook)) }}</p>
                </div>
              </div>
              <!-- Action buttons -->
              <div class="action-buttons">
                <button
                  v-if="audiobook.inLibrary"
                  class="action-btn edit-btn-small"
                  @click.stop="editAudiobook(audiobook)"
                  title="Edit"
                >
                  <PhPencil />
                </button>
                <button
                  v-if="audiobook.inLibrary"
                  class="action-btn delete-btn-small"
                  @click.stop="deleteAudiobook(audiobook)"
                  title="Delete"
                >
                  <PhTrash />
                </button>
                <button
                  v-else
                  class="action-btn add-btn-small"
                  @click.stop="openAddToLibrary(audiobook)"
                  title="Add to Library"
                >
                  <PhPlus />
                </button>
              </div>
            </div>
          </div>
        </section>
      </div>

      <!-- Pagination -->
      <div v-if="totalPages > 1" class="pagination">
        <button
          @click="currentPage = Math.max(1, currentPage - 1)"
          :disabled="currentPage === 1"
          class="page-btn"
        >
          <PhCaretLeft />
        </button>
        <span class="page-info"> Page {{ currentPage }} of {{ totalPages }} </span>
        <button
          @click="currentPage = Math.min(totalPages, currentPage + 1)"
          :disabled="currentPage === totalPages"
          class="page-btn"
        >
          <PhCaretRight />
        </button>
      </div>
    </div>

    <!-- Modals -->
    <BulkEditModal
      :is-open="showBulkEditModal"
      :selected-count="selectedCount"
      :selected-ids="selectedIdsForView"
      @close="closeBulkEdit"
      @saved="handleBulkEditSaved"
    />

    <RenamePreviewModal
      :visible="showOrganizeModal"
      :audiobook-ids="organizeAudiobookIds"
      @close="closeOrganize"
      @done="handleOrganizeDone"
    />

    <EditAudiobookModal
      v-if="editingAudiobook"
      :isOpen="true"
      :audiobook="editingAudiobook"
      @close="editingAudiobook = null"
      @saved="onAudiobookSaved"
    />

    <DeleteConfirmationModal
      :visible="showDeleteDialog"
      title="Delete Audiobook"
      :confirmText="deleting ? 'Deleting...' : 'Delete'"
      @close="cancelDelete"
      @confirm="executeDelete"
    >
      <template #default>
        <p>
          Are you sure you want to delete
          <strong>{{ deleteTarget?.title || 'this audiobook' }}</strong
          >?
        </p>
        <p class="warning-text">
          This action cannot be undone. The audiobook data and cached images will be permanently
          removed.
        </p>
        <div class="delete-options">
          <div class="checkbox-row">
            <label class="checkbox-wrapper checkbox-label">
              <input
                v-model="deleteFilesOnDisk"
                type="checkbox"
                class="checkbox-input"
                aria-label="Remove all files in the audiobook folder from disk"
              />
              <div class="checkbox-content">
                <span class="checkbox-title"
                  >Remove all files in the audiobook folder from disk</span
                >
                <small
                  >Deletes every file inside the audiobook folder when it can be identified safely.
                  Leave the folder itself unless you also choose the option below.</small
                >
              </div>
            </label>
          </div>

          <div class="checkbox-row">
            <label class="checkbox-wrapper checkbox-label">
              <input
                v-model="deleteFolderOnDisk"
                type="checkbox"
                class="checkbox-input"
                aria-label="Remove audiobook folder from disk"
              />
              <div class="checkbox-content">
                <span class="checkbox-title">Also remove the audiobook folder</span>
                <small
                  >Deletes the audiobook folder itself when it is safe to do so. This also removes
                  everything inside it.</small
                >
              </div>
            </label>
          </div>
        </div>
      </template>
    </DeleteConfirmationModal>

    <AddLibraryModal
      v-if="pendingAddBook"
      :visible="true"
      :book="pendingAddBook"
      @close="closeAddLibraryModal"
      @added="handleBookAdded"
    />
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import {
  PhArrowLeft,
  PhBooks,
  PhGridFour,
  PhList,
  PhCheckSquare,
  PhX,
  PhArrowClockwise,
  PhInfo,
  PhBookOpen,
  PhWarningCircle,
  PhPencil,
  PhTrash,
  PhCaretLeft,
  PhCaretRight,
  PhStar,
  PhEye,
  PhEyeSlash,
  PhPlus,
  PhGlobe,
  PhFolderOpen,
} from '@phosphor-icons/vue'
import { apiService } from '@/services/api'
import { useLibraryStore } from '@/stores/library'
import { useConfigurationStore } from '@/stores/configuration'
import { useDownloadsStore } from '@/stores/downloads'
import { errorTracking } from '@/services/errorTracking'
import { useToast } from '@/services/toastService'
import EditAudiobookModal from '@/components/domain/audiobook/EditAudiobookModal.vue'
import AddLibraryModal from '@/components/domain/audiobook/AddLibraryModal.vue'
import BulkEditModal from '@/components/domain/collection/BulkEditModal.vue'
import RenamePreviewModal from '@/components/domain/organize/RenamePreviewModal.vue'
import DeleteConfirmationModal from '@/components/feedback/DeleteConfirmationModal.vue'
import { showConfirm } from '@/composables/useConfirm'
import { getPlaceholderUrl } from '@/utils/placeholder'
import CustomSelect from '@/components/form/CustomSelect.vue'
import { EmptyState, LoadingState, Pill } from '@/components/base'
import type {
  Audiobook,
  AudiobookStatus,
  AuthorCatalogBook,
  AuthorCatalogResponse,
  AuthorLookupResponse,
  AudibleBookMetadata,
  MonitoredAuthor,
  MonitoredSeries,
  RelatedAuthorItem,
  SeriesCatalogBook,
  SeriesCatalogResponse,
  SeriesLookupResponse,
} from '@/types'
import { computeAudiobookStatus, formatAudiobookStatus } from '@/utils/audiobookStatus'
import { safeText, stripHtmlAndNormalize } from '@/utils/textUtils'
import { useProtectedImages } from '@/composables/useProtectedImages'
import {
  getPreferredSearchLanguageFilter,
  normalizePreferredSearchLanguage,
  normalizeSearchRegion,
  normalizeSearchResultLanguage,
  preferredSearchLanguageOptions,
  searchRegionOptions,
} from '@/utils/languageMapping'

interface CollectionDisplayItem extends Audiobook {
  key: string
  inLibrary: boolean
  addMetadata?: AudibleBookMetadata | null
}

type RemoteCatalogBook = AuthorCatalogBook | SeriesCatalogBook

type CollectionStatus = AudiobookStatus | 'not-added'

interface AvailabilitySection {
  key: 'in-library' | 'not-added' | 'all'
  title: string
  count: number
  items: CollectionDisplayItem[]
}

const route = useRoute()
const router = useRouter()
const libraryStore = useLibraryStore()
const configStore = useConfigurationStore()
const downloadsStore = useDownloadsStore()
const toast = useToast()
const { getProtectedImageSrc } = useProtectedImages()

const type = computed(() => route.params.type as string)
const name = computed(() => decodeURIComponent(route.params.name as string))
const isAuthorCollection = computed(() => type.value === 'author')
const isSeriesCollection = computed(() => type.value === 'series')
const isGenreCollection = computed(() => type.value === 'genre')
const isNarratorCollection = computed(() => type.value === 'narrator')
const isPublisherCollection = computed(() => type.value === 'publisher')
const isMetadataCollection = computed(() => isAuthorCollection.value || isSeriesCollection.value)

const viewMode = ref<'grid' | 'list'>('grid')
const showItemDetails = ref(false)
const searchQuery = ref('')
const sortKey = ref('title')
const currentPage = ref(1)
const pageSize = ref(50)
const editingAudiobook = ref<Audiobook | null>(null)
const authorCatalog = ref<AuthorCatalogResponse | null>(null)
const authorCatalogLoading = ref(false)
const authorCatalogError = ref<string | null>(null)
const authorCatalogRequestId = ref(0)
const authorLookup = ref<AuthorLookupResponse | null>(null)
const authorLookupLoading = ref(false)
const authorLookupRequestId = ref(0)
const authorMetadataRefreshBusy = ref(false)
const seriesCatalog = ref<SeriesCatalogResponse | null>(null)
const seriesCatalogLoading = ref(false)
const seriesCatalogError = ref<string | null>(null)
const seriesCatalogRequestId = ref(0)
const seriesLookup = ref<SeriesLookupResponse | null>(null)
const seriesLookupLoading = ref(false)
const seriesLookupRequestId = ref(0)
const seriesMetadataRefreshBusy = ref(false)
const seriesMonitoringBusy = ref(false)
const seriesMonitoringStatus = ref<MonitoredSeries | null>(null)
const seriesMonitoringStatusRequestId = ref(0)
const authorMonitoringBusy = ref(false)
const authorMonitoringStatus = ref<MonitoredAuthor | null>(null)
const authorMonitoringStatusRequestId = ref(0)
const pendingAddBook = ref<AudibleBookMetadata | null>(null)
const showFullAuthorDescription = ref(false)
const showFullSeriesDescription = ref(false)

const qualityProfiles = computed(() => configStore.qualityProfiles)
const authorCatalogRegion = computed(() =>
  normalizeSearchRegion(configStore.applicationSettings?.defaultSearchRegion ?? 'us'),
)
const authorRegionLabel = computed(
  () =>
    searchRegionOptions.find((option) => option.value === authorCatalogRegion.value)?.label ??
    authorCatalogRegion.value.toUpperCase(),
)
const preferredAuthorMonitoringLanguage = computed(() =>
  normalizePreferredSearchLanguage(
    configStore.applicationSettings?.defaultSearchLanguage ?? 'english',
  ),
)
const preferredAuthorCatalogLanguageFilter = computed(() =>
  getPreferredSearchLanguageFilter(configStore.applicationSettings?.defaultSearchLanguage),
)
const authorLanguageLabel = computed(() => {
  if (preferredAuthorMonitoringLanguage.value === 'all') {
    return 'All Languages'
  }

  return (
    preferredSearchLanguageOptions.find(
      (option) => option.value === preferredAuthorMonitoringLanguage.value,
    )?.label ?? preferredAuthorMonitoringLanguage.value
  )
})
const isCurrentAuthorMonitored = computed(() => Boolean(authorMonitoringStatus.value))
const authorMonitoringContextLabel = computed(() => {
  return `${authorRegionLabel.value} / ${authorLanguageLabel.value}`
})
const seriesCatalogRegion = authorCatalogRegion
const seriesRegionLabel = authorRegionLabel
const preferredSeriesMonitoringLanguage = preferredAuthorMonitoringLanguage
const preferredSeriesCatalogLanguageFilter = preferredAuthorCatalogLanguageFilter
const seriesLanguageLabel = authorLanguageLabel
const isCurrentSeriesMonitored = computed(() => Boolean(seriesMonitoringStatus.value))
const seriesMetadataContextLabel = computed(() => {
  return `${seriesRegionLabel.value} / ${seriesLanguageLabel.value}`
})

function normalizeCollectionText(value: string | undefined | null): string {
  if (!value) return ''
  return value
    .normalize('NFKD')
    .replace(/[\u0300-\u036f]/g, '')
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, ' ')
    .trim()
}

function normalizeIdentifier(value: string | undefined | null): string {
  if (!value) return ''
  return value.replace(/[^A-Za-z0-9]/g, '').toUpperCase()
}

function normalizeAuthorKey(authors: string[] | undefined): string {
  return (authors || [])
    .map((author) => normalizeCollectionText(author))
    .filter(Boolean)
    .sort()
    .join('|')
}

function buildTitleAuthorKey(title: string | undefined, authors: string[] | undefined): string {
  return `${normalizeCollectionText(title)}::${normalizeAuthorKey(authors)}`
}

function createSyntheticId(seed: string): number {
  let hash = 0
  for (let index = 0; index < seed.length; index += 1) {
    hash = (hash * 31 + seed.charCodeAt(index)) | 0
  }
  return -Math.max(1, Math.abs(hash))
}

function matchesCurrentCollection(book: Audiobook): boolean {
  if (isAuthorCollection.value) {
    return (book.authors || []).some(
      (author) => normalizeCollectionText(author) === normalizeCollectionText(name.value),
    )
  }

  if (type.value === 'series') {
    const target = normalizeCollectionText(name.value)
    const memberships = book.seriesMemberships
    if (memberships && memberships.length > 0) {
      return memberships.some(
        (membership) => normalizeCollectionText(membership.seriesName) === target,
      )
    }
    return normalizeCollectionText(book.series) === target
  }

  if (isGenreCollection.value) {
    return (book.genres || []).some(
      (genre) => normalizeCollectionText(genre) === normalizeCollectionText(name.value),
    )
  }

  if (isNarratorCollection.value) {
    return (book.narrators || []).some(
      (narrator) => normalizeCollectionText(narrator) === normalizeCollectionText(name.value),
    )
  }

  if (isPublisherCollection.value) {
    return normalizeCollectionText(book.publisher) === normalizeCollectionText(name.value)
  }

  return false
}

function mapLibraryItem(book: Audiobook): CollectionDisplayItem {
  // In a series collection a book may be matched via a non-primary membership, so show the
  // series name/number for THIS collection rather than the book's primary series.
  const seriesContext = type.value === 'series' ? resolveSeriesForCollection(book) : null
  return {
    ...book,
    ...(seriesContext
      ? { series: seriesContext.seriesName, seriesNumber: seriesContext.seriesNumber }
      : {}),
    key: `library-${book.id}`,
    inLibrary: true,
    addMetadata: null,
  }
}

function resolveSeriesForCollection(
  book: Audiobook,
): { seriesName: string; seriesNumber?: string } | null {
  const target = normalizeCollectionText(name.value)
  const memberships = book.seriesMemberships
  if (memberships && memberships.length > 0) {
    const match = memberships.find(
      (membership) => normalizeCollectionText(membership.seriesName) === target,
    )
    if (match) {
      return { seriesName: match.seriesName, seriesNumber: match.seriesNumber }
    }
  }
  return null
}

function buildCatalogMetadata(book: RemoteCatalogBook): AudibleBookMetadata {
  const authors = (book.authors || []).filter(Boolean)
  const publishYear = book.publishedDate?.match(/\d{4}/)?.[0]

  return {
    asin: book.asin || '',
    title: book.title || 'Unknown Title',
    subtitle: book.subtitle,
    authors,
    imageUrl: book.imageUrl,
    runtime: book.runtime,
    language: book.language,
    publisher: book.publisher,
    narrators: book.narrators || [],
    genres: book.genres || [],
    series: book.series,
    seriesNumber: book.seriesNumber,
    publishedDate: book.publishedDate,
    publishYear,
    isbn: book.isbn,
    source: book.metadataSource || 'Audible',
    sourceLink: book.link,
    metadataSource: book.metadataSource || 'Audible',
  }
}

function mapCatalogItem(
  book: RemoteCatalogBook,
  sourcePrefix: 'author-catalog' | 'series-catalog',
): CollectionDisplayItem {
  const authors = (book.authors || []).filter(Boolean)
  const syntheticKey = book.asin || buildTitleAuthorKey(book.title, authors) || book.title

  return {
    id: createSyntheticId(syntheticKey),
    key: `${sourcePrefix}-${syntheticKey}`,
    title: book.title || 'Unknown Title',
    subtitle: book.subtitle,
    authors,
    imageUrl: book.imageUrl,
    runtime: book.runtime,
    language: book.language,
    publisher: book.publisher,
    narrators: book.narrators || [],
    genres: book.genres || [],
    series: book.series,
    seriesNumber: book.seriesNumber,
    publishedDate: book.publishedDate,
    publishYear: book.publishedDate?.match(/\d{4}/)?.[0],
    isbn: book.isbn,
    asin: book.asin,
    monitored: false,
    inLibrary: false,
    addMetadata: buildCatalogMetadata(book),
  }
}

function findLibraryMatch(
  book: RemoteCatalogBook,
  libraryBooks: Audiobook[],
): Audiobook | undefined {
  const asin = normalizeIdentifier(book.asin)
  if (asin) {
    const asinMatch = libraryBooks.find((candidate) => normalizeIdentifier(candidate.asin) === asin)
    if (asinMatch) return asinMatch
  }

  const isbn = normalizeIdentifier(book.isbn)
  if (isbn) {
    const isbnMatch = libraryBooks.find((candidate) => normalizeIdentifier(candidate.isbn) === isbn)
    if (isbnMatch) return isbnMatch
  }

  const titleAuthorKey = buildTitleAuthorKey(book.title, book.authors)
  if (titleAuthorKey) {
    return libraryBooks.find(
      (candidate) => buildTitleAuthorKey(candidate.title, candidate.authors) === titleAuthorKey,
    )
  }

  return undefined
}

function shouldIncludeRemoteCatalogBook(
  book: RemoteCatalogBook,
  languageFilter: string | null | undefined,
): boolean {
  if (!languageFilter) return true
  const normalizedBookLanguage = normalizeSearchResultLanguage(book.language)

  if (!normalizedBookLanguage) return false

  return normalizedBookLanguage === languageFilter
}

function getSortValue(book: CollectionDisplayItem): string {
  switch (sortKey.value) {
    case 'series-position':
      return seriesPositionSortKey(book.seriesNumber)
    case 'author':
      return book.authors?.[0] || ''
    case 'series':
      return book.series || ''
    case 'added':
      return book.inLibrary ? String(book.id).padStart(12, '0') : 'zzzzzzzzzzzz'
    default:
      return book.title || ''
  }
}

// Build a lexicographically-comparable key from a series position number so a plain string
// sort (localeCompare) yields reading order. Each tier is led by a digit so the tiers sort
// deterministically across locales (a leading symbol like "~" does NOT reliably sort after
// digits — that was the original bug for missing positions):
//   tier 1 = fully-numeric positions ("1", "2.5", "10"), ordered numerically via zero-padding;
//   tier 2 = other non-empty positions ("1-2", "1a"), ordered by their text, after the numbers;
//   tier 3 = missing positions, always sorted last.
function seriesPositionSortKey(value: string | null | undefined): string {
  const raw = (value || '').trim()
  if (!raw) return '3'
  if (/^\d+(\.\d+)?$/.test(raw)) {
    const [intPart, fracPart = ''] = raw.split('.')
    return `1${intPart.padStart(8, '0')}${fracPart ? `.${fracPart}` : ''}`
  }
  return `2${raw.toLowerCase()}`
}

const libraryCollectionAudiobooks = computed(() =>
  libraryStore.audiobooks.filter((book) => matchesCurrentCollection(book)),
)

const remoteCatalogBooks = computed<RemoteCatalogBook[]>(() => {
  if (isAuthorCollection.value) {
    return authorCatalog.value?.books || []
  }

  if (isSeriesCollection.value) {
    return seriesCatalog.value?.books || []
  }

  return []
})

const loading = computed(
  () =>
    libraryStore.loading ||
    (isAuthorCollection.value && (authorCatalogLoading.value || authorLookupLoading.value)) ||
    (isSeriesCollection.value && (seriesCatalogLoading.value || seriesLookupLoading.value)),
)

const error = computed(() => {
  if (libraryStore.error) return libraryStore.error
  if (libraryCollectionAudiobooks.value.length > 0) return null
  if (isAuthorCollection.value) return authorCatalogError.value
  if (isSeriesCollection.value) return seriesCatalogError.value
  return null
})

const audiobooks = computed<CollectionDisplayItem[]>(() => {
  const localItems = libraryCollectionAudiobooks.value

  let mergedItems: CollectionDisplayItem[]
  if (isMetadataCollection.value) {
    const matchedLibraryIds = new Set<number>()
    const seenRemoteCatalogKeys = new Set<string>()
    const sourcePrefix = isAuthorCollection.value ? 'author-catalog' : 'series-catalog'
    const languageFilter = isAuthorCollection.value
      ? preferredAuthorCatalogLanguageFilter.value
      : preferredSeriesCatalogLanguageFilter.value
    const catalogItems = remoteCatalogBooks.value.flatMap((book) => {
      const libraryMatch = findLibraryMatch(book, localItems)
      if (libraryMatch) {
        if (matchedLibraryIds.has(libraryMatch.id)) {
          return []
        }
        matchedLibraryIds.add(libraryMatch.id)
        return [mapLibraryItem(libraryMatch)]
      }

      if (!shouldIncludeRemoteCatalogBook(book, languageFilter)) {
        return []
      }

      const catalogItem = mapCatalogItem(book, sourcePrefix)
      if (seenRemoteCatalogKeys.has(catalogItem.key)) {
        return []
      }

      seenRemoteCatalogKeys.add(catalogItem.key)
      return [catalogItem]
    })

    const unmatchedLibraryItems = localItems
      .filter((book) => !matchedLibraryIds.has(book.id))
      .map(mapLibraryItem)

    mergedItems = [...catalogItems, ...unmatchedLibraryItems]
  } else {
    mergedItems = localItems.map(mapLibraryItem)
  }

  const searched = mergedItems.filter((book) =>
    book.title.toLowerCase().includes(searchQuery.value.toLowerCase()),
  )

  return searched.sort((a, b) => {
    // Metadata collections group owned books ahead of not-added ones (the view also renders
    // these as separate "In Library" / "Not Added" sections); the active sort — series
    // position by default — then orders books within each group.
    if (isMetadataCollection.value && a.inLibrary !== b.inLibrary) {
      return a.inLibrary ? -1 : 1
    }

    const aVal = safeText(getSortValue(a))
    const bVal = safeText(getSortValue(b))
    return aVal.localeCompare(bVal)
  })
})

const baseSortOptions = [
  { value: 'series-position', label: 'Series Position' },
  { value: 'title', label: 'Title' },
  { value: 'author', label: 'Author' },
  { value: 'series', label: 'Series' },
  { value: 'added', label: 'Date Added' },
]

const sortOptions = computed(() => {
  return baseSortOptions.filter((o) => {
    // Reading-order sort only makes sense inside a single series.
    if (o.value === 'series-position') return type.value === 'series'
    if (type.value === 'author' && o.value === 'author') return false
    // Sorting by series name is meaningless when every book shares the series.
    if (type.value === 'series' && o.value === 'series') return false
    return true
  })
})

// A series collection defaults to reading order (#626); everything else to title.
function defaultSortForType(collectionType: string): string {
  return collectionType === 'series' ? 'series-position' : 'title'
}

watch(
  type,
  (newType) => {
    sortKey.value = defaultSortForType(newType)
  },
  { immediate: true },
)

// Ensure current sortKey is valid for the current view; reset to the type default if not
watch(sortOptions, (newOpts) => {
  const vals = newOpts.map((o) => o.value)
  if (!vals.includes(sortKey.value)) {
    sortKey.value = defaultSortForType(type.value)
  }
})

const sortKeyProxy = computed({
  get: () => sortKey.value,
  set: (value: string) => {
    sortKey.value = value
    currentPage.value = 1
  },
})

const paginatedAudiobooks = computed(() => {
  const start = (currentPage.value - 1) * pageSize.value
  return audiobooks.value.slice(start, start + pageSize.value)
})

const totalPages = computed(() => Math.ceil(audiobooks.value.length / pageSize.value))
const totalAddedAudiobooks = computed(() => audiobooks.value.filter((book) => book.inLibrary))
const totalNotAddedAudiobooks = computed(() => audiobooks.value.filter((book) => !book.inLibrary))
const authorLibraryCount = computed(() => totalAddedAudiobooks.value.length)
const authorNotAddedCount = computed(() => totalNotAddedAudiobooks.value.length)
const seriesLibraryCount = computed(() => totalAddedAudiobooks.value.length)
const seriesNotAddedCount = computed(() => totalNotAddedAudiobooks.value.length)
const seriesVisibleBookCount = computed(() => audiobooks.value.length)
const seriesCatalogTotalCount = computed(
  () =>
    seriesCatalog.value?.totalBooks ??
    seriesLookup.value?.totalBooks ??
    seriesVisibleBookCount.value,
)

const authorHeroName = computed(
  () =>
    safeText(authorCatalog.value?.author?.name || authorLookup.value?.name || name.value) ||
    name.value,
)
const authorHeroAsin = computed(
  () =>
    safeText(
      authorLookup.value?.asin ||
        authorCatalog.value?.author?.asin ||
        authorMonitoringStatus.value?.authorAsin ||
        '',
    ) || '',
)
const authorHeroRawImageUrl = computed(() => {
  const asin = authorHeroAsin.value

  return (
    authorLookup.value?.cachedPath ||
    (asin ? `/images/${encodeURIComponent(asin)}` : undefined) ||
    authorLookup.value?.image ||
    authorCatalog.value?.author?.image
  )
})
const authorHeroImageUrl = computed(() =>
  getProtectedImageSrc(authorHeroRawImageUrl.value, getPlaceholderUrl()),
)
const authorHeroBackdropStyle = computed(() => ({
  backgroundImage: `linear-gradient(90deg, rgba(10, 12, 18, 0.9), rgba(10, 12, 18, 0.55)), url(${authorHeroImageUrl.value})`,
}))
const authorHeroBiography = computed(() => {
  const raw = stripHtmlAndNormalize(authorLookup.value?.description)
  return raw || ''
})
const authorHeroCanToggleDescription = computed(() => authorHeroBiography.value.length > 360)
const authorHeroDescriptionText = computed(() =>
  showFullAuthorDescription.value || !authorHeroCanToggleDescription.value
    ? authorHeroBiography.value
    : `${authorHeroBiography.value.slice(0, 360).trimEnd()}...`,
)
const authorSimilarAuthors = computed<RelatedAuthorItem[]>(() =>
  (authorLookup.value?.similarAuthors || []).filter(
    (author) =>
      Boolean(author?.name) &&
      normalizeCollectionText(author.name) !== normalizeCollectionText(authorHeroName.value),
  ),
)
const seriesHeroName = computed(
  () =>
    safeText(seriesCatalog.value?.series?.name || seriesLookup.value?.name || name.value) ||
    name.value,
)
const seriesHeroAsin = computed(
  () => safeText(seriesLookup.value?.asin || seriesCatalog.value?.series?.asin || '') || '',
)
const seriesHeroRawImageUrl = computed(() => {
  return (
    seriesLookup.value?.cachedPath ||
    seriesLookup.value?.image ||
    seriesCatalog.value?.series?.image ||
    seriesCatalog.value?.books.find((book) => Boolean(book.imageUrl))?.imageUrl
  )
})
const seriesHeroImageUrl = computed(() =>
  getProtectedImageSrc(seriesHeroRawImageUrl.value, getPlaceholderUrl()),
)
const seriesHeroBackdropStyle = computed(() => ({
  backgroundImage: `linear-gradient(90deg, rgba(10, 12, 18, 0.9), rgba(10, 12, 18, 0.55)), url(${seriesHeroImageUrl.value})`,
}))
const seriesHeroBiography = computed(() => {
  const raw = stripHtmlAndNormalize(
    seriesLookup.value?.description || seriesCatalog.value?.series?.description,
  )
  return raw || ''
})
const seriesHeroCanToggleDescription = computed(() => seriesHeroBiography.value.length > 360)
const seriesHeroDescriptionText = computed(() =>
  showFullSeriesDescription.value || !seriesHeroCanToggleDescription.value
    ? seriesHeroBiography.value
    : `${seriesHeroBiography.value.slice(0, 360).trimEnd()}...`,
)
const seriesHeroPosterBooks = computed(() =>
  audiobooks.value.filter((book) => Boolean(book.imageUrl)).slice(0, 8),
)
const seriesHeroSinglePosterBook = computed(() => seriesHeroPosterBooks.value[0] ?? null)
const seriesHeroSingleBackgroundStyle = computed(() => ({
  backgroundImage: `url(${getProtectedImageSrc(
    seriesHeroSinglePosterBook.value?.imageUrl,
    getPlaceholderUrl(),
  )})`,
}))

function getSeriesHeroCoverStyle(index: number, count: number) {
  const left = count <= 1 ? 25 : (index * 50) / Math.max(1, count - 1)
  const zIndex = count <= 1 ? 1 : Math.max(1, 100 - index)

  return {
    width: '50%',
    height: '100%',
    top: '0%',
    left: `${left}%`,
    zIndex,
    boxShadow: 'rgba(17, 17, 17, 0.4) 4px 0px 10px',
    borderRadius: '12px',
  }
}
const shouldShowAvailabilitySections = computed(
  () =>
    isMetadataCollection.value &&
    totalAddedAudiobooks.value.length > 0 &&
    totalNotAddedAudiobooks.value.length > 0,
)
const paginatedAudiobookSections = computed<AvailabilitySection[]>(() => {
  if (!shouldShowAvailabilitySections.value) {
    return [
      {
        key: 'all',
        title: '',
        count: paginatedAudiobooks.value.length,
        items: paginatedAudiobooks.value,
      },
    ]
  }

  const sections: AvailabilitySection[] = []
  const addedItems = paginatedAudiobooks.value.filter((book) => book.inLibrary)
  const notAddedItems = paginatedAudiobooks.value.filter((book) => !book.inLibrary)

  if (addedItems.length > 0) {
    sections.push({
      key: 'in-library',
      title: 'In Library',
      count: totalAddedAudiobooks.value.length,
      items: addedItems,
    })
  }

  if (notAddedItems.length > 0) {
    sections.push({
      key: 'not-added',
      title: 'Not Added',
      count: totalNotAddedAudiobooks.value.length,
      items: notAddedItems,
    })
  }

  return sections
})

const selectedIdsForView = computed(
  () =>
    new Set(
      audiobooks.value
        .filter((book) => book.inLibrary && libraryStore.isSelected(book.id))
        .map((book) => book.id),
    ),
)

const selectedCount = computed(() => selectedIdsForView.value.size)
const selectableAudiobookCount = computed(
  () => audiobooks.value.filter((book) => book.inLibrary).length,
)

const isSelected = (id: number) => id > 0 && libraryStore.isSelected(id)

const toggleSelection = (id: number) => {
  if (id > 0) {
    libraryStore.toggleSelection(id)
  }
}

const toggleViewMode = () => {
  viewMode.value = viewMode.value === 'grid' ? 'list' : 'grid'
}

const toggleItemDetails = () => {
  showItemDetails.value = !showItemDetails.value
}

const showBulkEditModal = ref(false)
const showOrganizeModal = ref(false)
const organizeAudiobookIds = ref<number[]>([])
const deleting = ref(false)
const showDeleteDialog = ref(false)
const deleteTarget = ref<Audiobook | null>(null)
const deleteFilesOnDisk = ref(false)
const deleteFolderOnDisk = ref(false)
const lastClickedIndex = ref<number | null>(null)

function showBulkEdit() {
  showBulkEditModal.value = true
}

function closeBulkEdit() {
  showBulkEditModal.value = false
}

function showOrganize() {
  organizeAudiobookIds.value = Array.from(selectedIdsForView.value)
  showOrganizeModal.value = true
}

function closeOrganize() {
  showOrganizeModal.value = false
}

async function handleOrganizeDone() {
  showOrganizeModal.value = false
  await libraryStore.fetchLibrary()
  libraryStore.clearSelection()
}

async function loadAuthorCatalog(refresh = false): Promise<AuthorCatalogResponse | null> {
  if (!isAuthorCollection.value) {
    authorCatalog.value = null
    authorCatalogError.value = null
    authorCatalogLoading.value = false
    return null
  }

  const requestId = ++authorCatalogRequestId.value
  const previousCatalog = authorCatalog.value
  authorCatalogLoading.value = true
  authorCatalogError.value = null

  try {
    const response = await apiService.getAuthorCatalog(
      name.value,
      authorCatalogRegion.value,
      refresh,
    )
    if (requestId !== authorCatalogRequestId.value) return null

    if (!response) {
      if (!refresh) {
        authorCatalog.value = null
      }
      authorCatalogError.value = 'Failed to load the full author catalog.'
      return null
    }

    authorCatalog.value = response
    return response
  } catch (err) {
    if (requestId !== authorCatalogRequestId.value) return null

    if (!refresh) {
      authorCatalog.value = null
    } else {
      authorCatalog.value = previousCatalog
    }
    authorCatalogError.value =
      err instanceof Error ? err.message : 'Failed to load the full author catalog.'
    errorTracking.captureException(err as Error, {
      component: 'CollectionView',
      operation: 'loadAuthorCatalog',
      metadata: { author: name.value, region: authorCatalogRegion.value, refresh },
    })
    return null
  } finally {
    if (requestId === authorCatalogRequestId.value) {
      authorCatalogLoading.value = false
    }
  }
}

async function loadAuthorLookup(
  authorAsin?: string,
  refresh = false,
): Promise<AuthorLookupResponse | null> {
  if (!isAuthorCollection.value) {
    authorLookup.value = null
    authorLookupLoading.value = false
    return null
  }

  const requestId = ++authorLookupRequestId.value
  const previousLookup = authorLookup.value
  authorLookupLoading.value = true

  try {
    const response = await apiService.getAuthorLookup(
      name.value,
      authorCatalogRegion.value,
      authorAsin,
      refresh,
    )
    if (requestId !== authorLookupRequestId.value) return null
    authorLookup.value = response ?? (refresh ? previousLookup : null)
    return response ?? null
  } catch (err) {
    if (requestId !== authorLookupRequestId.value) return null

    if (!refresh) {
      authorLookup.value = null
    } else {
      authorLookup.value = previousLookup
    }
    errorTracking.captureException(err as Error, {
      component: 'CollectionView',
      operation: 'loadAuthorLookup',
      metadata: { author: name.value, region: authorCatalogRegion.value, refresh },
    })
    return null
  } finally {
    if (requestId === authorLookupRequestId.value) {
      authorLookupLoading.value = false
    }
  }
}

async function loadSeriesCatalog(refresh = false): Promise<SeriesCatalogResponse | null> {
  if (!isSeriesCollection.value) {
    seriesCatalog.value = null
    seriesCatalogError.value = null
    seriesCatalogLoading.value = false
    return null
  }

  const requestId = ++seriesCatalogRequestId.value
  const previousCatalog = seriesCatalog.value
  seriesCatalogLoading.value = true
  seriesCatalogError.value = null

  try {
    const response = await apiService.getSeriesCatalog(
      name.value,
      seriesCatalogRegion.value,
      refresh,
    )
    if (requestId !== seriesCatalogRequestId.value) return null

    if (!response) {
      if (!refresh) {
        seriesCatalog.value = null
      }
      seriesCatalogError.value = 'Failed to load the full series catalog.'
      return null
    }

    seriesCatalog.value = response
    return response
  } catch (err) {
    if (requestId !== seriesCatalogRequestId.value) return null

    if (!refresh) {
      seriesCatalog.value = null
    } else {
      seriesCatalog.value = previousCatalog
    }
    seriesCatalogError.value =
      err instanceof Error ? err.message : 'Failed to load the full series catalog.'
    errorTracking.captureException(err as Error, {
      component: 'CollectionView',
      operation: 'loadSeriesCatalog',
      metadata: { series: name.value, region: seriesCatalogRegion.value, refresh },
    })
    return null
  } finally {
    if (requestId === seriesCatalogRequestId.value) {
      seriesCatalogLoading.value = false
    }
  }
}

async function loadSeriesLookup(
  seriesAsin?: string,
  refresh = false,
): Promise<SeriesLookupResponse | null> {
  if (!isSeriesCollection.value) {
    seriesLookup.value = null
    seriesLookupLoading.value = false
    return null
  }

  const requestId = ++seriesLookupRequestId.value
  const previousLookup = seriesLookup.value
  seriesLookupLoading.value = true

  try {
    const response = await apiService.getSeriesLookup(
      name.value,
      seriesCatalogRegion.value,
      seriesAsin,
      refresh,
    )
    if (requestId !== seriesLookupRequestId.value) return null
    seriesLookup.value = response ?? (refresh ? previousLookup : null)
    return response ?? null
  } catch (err) {
    if (requestId !== seriesLookupRequestId.value) return null

    if (!refresh) {
      seriesLookup.value = null
    } else {
      seriesLookup.value = previousLookup
    }
    errorTracking.captureException(err as Error, {
      component: 'CollectionView',
      operation: 'loadSeriesLookup',
      metadata: { series: name.value, region: seriesCatalogRegion.value, refresh },
    })
    return null
  } finally {
    if (requestId === seriesLookupRequestId.value) {
      seriesLookupLoading.value = false
    }
  }
}

async function loadAuthorMonitoringStatus() {
  if (!isAuthorCollection.value) {
    authorMonitoringStatus.value = null
    return
  }

  const requestId = ++authorMonitoringStatusRequestId.value

  try {
    const response = await apiService.getAuthorMonitoringStatus(
      name.value,
      authorCatalogRegion.value,
      preferredAuthorMonitoringLanguage.value,
    )

    if (requestId !== authorMonitoringStatusRequestId.value) return
    authorMonitoringStatus.value = response.monitoredAuthor ?? null
  } catch (err) {
    if (requestId !== authorMonitoringStatusRequestId.value) return

    authorMonitoringStatus.value = null
    errorTracking.captureException(err as Error, {
      component: 'CollectionView',
      operation: 'loadAuthorMonitoringStatus',
      metadata: {
        author: name.value,
        region: authorCatalogRegion.value,
        language: preferredAuthorMonitoringLanguage.value,
      },
    })
  }
}

async function loadSeriesMonitoringStatus() {
  if (!isSeriesCollection.value) {
    seriesMonitoringStatus.value = null
    return
  }

  const requestId = ++seriesMonitoringStatusRequestId.value

  try {
    const response = await apiService.getSeriesMonitoringStatus(
      name.value,
      seriesCatalogRegion.value,
      preferredSeriesMonitoringLanguage.value,
    )

    if (requestId !== seriesMonitoringStatusRequestId.value) return
    seriesMonitoringStatus.value = response.monitoredSeries ?? null
  } catch (err) {
    if (requestId !== seriesMonitoringStatusRequestId.value) return

    seriesMonitoringStatus.value = null
    errorTracking.captureException(err as Error, {
      component: 'CollectionView',
      operation: 'loadSeriesMonitoringStatus',
      metadata: {
        series: name.value,
        region: seriesCatalogRegion.value,
        language: preferredSeriesMonitoringLanguage.value,
      },
    })
  }
}

async function loadCollectionData(forceLibrary = false, forceAuthorMetadataRefresh = false) {
  const setupTasks: Promise<unknown>[] = []

  if (forceLibrary || libraryStore.audiobooks.length === 0) {
    setupTasks.push(libraryStore.fetchLibrary())
  }

  if (!configStore.applicationSettings) {
    setupTasks.push(configStore.loadApplicationSettings())
  }

  await Promise.all(setupTasks)

  if (isAuthorCollection.value) {
    const refreshedCatalog = await loadAuthorCatalog(forceAuthorMetadataRefresh)
    await loadAuthorMonitoringStatus()
    await loadAuthorLookup(
      refreshedCatalog?.author?.asin || authorCatalog.value?.author?.asin,
      forceAuthorMetadataRefresh,
    )
  } else if (isSeriesCollection.value) {
    const refreshedCatalog = await loadSeriesCatalog(forceAuthorMetadataRefresh)
    await loadSeriesMonitoringStatus()
    await loadSeriesLookup(
      refreshedCatalog?.series?.asin || seriesCatalog.value?.series?.asin,
      forceAuthorMetadataRefresh,
    )
  } else {
    authorCatalog.value = null
    authorCatalogError.value = null
    authorLookup.value = null
    authorLookupLoading.value = false
    authorMonitoringStatus.value = null
    seriesCatalog.value = null
    seriesCatalogError.value = null
    seriesLookup.value = null
    seriesLookupLoading.value = false
    seriesMonitoringStatus.value = null
  }
}

async function handleBulkEditSaved() {
  await loadCollectionData(true)
  libraryStore.clearSelection()
  showBulkEditModal.value = false
}

async function confirmBulkDelete() {
  const idsToDelete = Array.from(selectedIdsForView.value)
  if (idsToDelete.length === 0) return

  const message = `Are you sure you want to delete ${idsToDelete.length} audiobook${idsToDelete.length !== 1 ? 's' : ''}? This action cannot be undone.`
  const ok = await showConfirm(message, 'Confirm Deletion', {
    danger: true,
    confirmText: 'Delete',
    cancelText: 'Cancel',
  })
  if (!ok) return
  deleting.value = true
  try {
    await libraryStore.bulkRemoveFromLibrary(idsToDelete)
  } catch (err) {
    errorTracking.captureException(err as Error, {
      component: 'CollectionView',
      operation: 'confirmBulkDelete',
      metadata: { count: idsToDelete.length },
    })
  } finally {
    deleting.value = false
  }
}

const refreshLibrary = async () => {
  libraryStore.clearSelection()
  await loadCollectionData(true)
}

async function refreshAuthorMetadata() {
  if (!isAuthorCollection.value || authorMetadataRefreshBusy.value) return

  authorMetadataRefreshBusy.value = true
  try {
    const refreshedCatalog = await loadAuthorCatalog(true)
    const refreshedLookup = await loadAuthorLookup(
      refreshedCatalog?.author?.asin || authorCatalog.value?.author?.asin || authorHeroAsin.value,
      true,
    )

    if (!refreshedCatalog && !refreshedLookup) {
      toast.error(
        'Author metadata refresh failed',
        'Listenarr kept the existing author details because the refresh could not complete.',
      )
      return
    }

    if (!refreshedCatalog || !refreshedLookup) {
      toast.warning(
        'Author metadata partially refreshed',
        'Some author details were updated, but at least one metadata source could not be refreshed.',
      )
      return
    }

    toast.success(
      'Author metadata refreshed',
      'Updated the author image, description, related authors, and catalog.',
    )
  } catch (err) {
    const message = err instanceof Error ? err.message : 'Failed to refresh author metadata.'
    toast.error('Author metadata refresh failed', message)
    errorTracking.captureException(err as Error, {
      component: 'CollectionView',
      operation: 'refreshAuthorMetadata',
      metadata: {
        author: name.value,
        region: authorCatalogRegion.value,
        language: preferredAuthorMonitoringLanguage.value,
      },
    })
  } finally {
    authorMetadataRefreshBusy.value = false
  }
}

async function refreshSeriesMetadata() {
  if (!isSeriesCollection.value || seriesMetadataRefreshBusy.value) return

  seriesMetadataRefreshBusy.value = true
  try {
    const refreshedCatalog = await loadSeriesCatalog(true)
    const refreshedLookup = await loadSeriesLookup(
      refreshedCatalog?.series?.asin || seriesCatalog.value?.series?.asin || seriesHeroAsin.value,
      true,
    )

    if (!refreshedCatalog && !refreshedLookup) {
      toast.error(
        'Series metadata refresh failed',
        'Listenarr kept the existing series details because the refresh could not complete.',
      )
      return
    }

    if (!refreshedCatalog || !refreshedLookup) {
      toast.warning(
        'Series metadata partially refreshed',
        'Some series details were updated, but at least one metadata source could not be refreshed.',
      )
      return
    }

    toast.success(
      'Series metadata refreshed',
      'Updated the series image, description, and catalog.',
    )
  } catch (err) {
    const message = err instanceof Error ? err.message : 'Failed to refresh series metadata.'
    toast.error('Series metadata refresh failed', message)
    errorTracking.captureException(err as Error, {
      component: 'CollectionView',
      operation: 'refreshSeriesMetadata',
      metadata: {
        series: name.value,
        region: seriesCatalogRegion.value,
        language: seriesLanguageLabel.value,
      },
    })
  } finally {
    seriesMetadataRefreshBusy.value = false
  }
}

const goBack = () => {
  router.back()
}

function goToRelatedAuthor(authorName: string) {
  const trimmed = safeText(authorName)
  if (!trimmed) return
  if (normalizeCollectionText(trimmed) === normalizeCollectionText(name.value)) return
  authorCatalogLoading.value = true
  authorLookupLoading.value = true
  void router.push(`/collection/author/${encodeURIComponent(trimmed)}`)
}

function selectAllVisible() {
  libraryStore.clearSelection()
  for (const book of audiobooks.value) {
    if (book.inLibrary) {
      libraryStore.toggleSelection(book.id)
    }
  }
}

function openAddToLibrary(audiobook: CollectionDisplayItem) {
  if (audiobook.inLibrary || !audiobook.addMetadata) return
  pendingAddBook.value = audiobook.addMetadata
}

async function toggleAuthorMonitoring() {
  if (!isAuthorCollection.value || authorMonitoringBusy.value) return

  authorMonitoringBusy.value = true
  try {
    if (authorMonitoringStatus.value) {
      await apiService.unmonitorAuthor(authorMonitoringStatus.value.id)
      authorMonitoringStatus.value = null
      toast.success(
        'Author unmonitored',
        `"${name.value}" will no longer be checked for future audiobooks.`,
      )
      return
    }

    const response = await apiService.monitorAuthor({
      name: name.value,
      asin: authorCatalog.value?.author?.asin,
      region: authorCatalogRegion.value,
      language: preferredAuthorMonitoringLanguage.value,
    })

    authorMonitoringStatus.value = response.monitoredAuthor

    const details =
      response.addedCount > 0
        ? `Added ${response.addedCount} audiobook${response.addedCount === 1 ? '' : 's'} from the current catalog.`
        : 'No new audiobooks needed to be added from the current catalog.'

    toast.success('Author monitored', details)

    if (response.failedCount > 0 || response.errorMessage) {
      const warningMessage =
        response.errorMessage ||
        `${response.failedCount} audiobook${response.failedCount === 1 ? '' : 's'} could not be added automatically.`
      toast.warning('Monitoring completed with warnings', warningMessage)
    }

    await loadCollectionData(true)
  } catch (err) {
    const message = err instanceof Error ? err.message : 'Failed to update author monitoring.'
    toast.error('Author monitoring failed', message)
    errorTracking.captureException(err as Error, {
      component: 'CollectionView',
      operation: 'toggleAuthorMonitoring',
      metadata: {
        author: name.value,
        region: authorCatalogRegion.value,
        language: preferredAuthorMonitoringLanguage.value,
      },
    })
  } finally {
    authorMonitoringBusy.value = false
  }
}

async function toggleSeriesMonitoring() {
  if (!isSeriesCollection.value || seriesMonitoringBusy.value) return

  seriesMonitoringBusy.value = true
  try {
    if (seriesMonitoringStatus.value) {
      await apiService.unmonitorSeries(seriesMonitoringStatus.value.id)
      seriesMonitoringStatus.value = null
      toast.success(
        'Series unmonitored',
        `"${name.value}" will no longer be checked for future audiobooks.`,
      )
      return
    }

    const response = await apiService.monitorSeries({
      name: name.value,
      asin: seriesCatalog.value?.series?.asin || seriesLookup.value?.asin,
      region: seriesCatalogRegion.value,
      language: preferredSeriesMonitoringLanguage.value,
    })

    seriesMonitoringStatus.value = response.monitoredSeries

    const details =
      response.addedCount > 0
        ? `Added ${response.addedCount} audiobook${response.addedCount === 1 ? '' : 's'} from the current series catalog.`
        : 'No new audiobooks needed to be added from the current series catalog.'

    toast.success('Series monitored', details)

    if (response.failedCount > 0 || response.errorMessage) {
      const warningMessage =
        response.errorMessage ||
        `${response.failedCount} audiobook${response.failedCount === 1 ? '' : 's'} could not be added automatically.`
      toast.warning('Monitoring completed with warnings', warningMessage)
    }

    await loadCollectionData(true)
  } catch (err) {
    const message = err instanceof Error ? err.message : 'Failed to update series monitoring.'
    toast.error('Series monitoring failed', message)
    errorTracking.captureException(err as Error, {
      component: 'CollectionView',
      operation: 'toggleSeriesMonitoring',
      metadata: {
        series: name.value,
        region: seriesCatalogRegion.value,
        language: preferredSeriesMonitoringLanguage.value,
      },
    })
  } finally {
    seriesMonitoringBusy.value = false
  }
}

const handleRowClick = (audiobook: CollectionDisplayItem) => {
  if (selectedCount.value > 0 && audiobook.inLibrary) {
    toggleSelection(audiobook.id)
    return
  }

  if (audiobook.inLibrary) {
    router.push(`/audiobooks/${audiobook.id}`)
    return
  }

  openAddToLibrary(audiobook)
}

const handleCardClick = (audiobook: CollectionDisplayItem) => {
  handleRowClick(audiobook)
}

function handleCheckboxClick(audiobook: CollectionDisplayItem, event: MouseEvent) {
  if (!audiobook.inLibrary) return
  event.preventDefault()

  const currentIndex = audiobooks.value.findIndex((book) => book.key === audiobook.key)
  if (event.shiftKey && lastClickedIndex.value !== null) {
    const start = Math.min(lastClickedIndex.value, currentIndex)
    const end = Math.max(lastClickedIndex.value, currentIndex)
    libraryStore.clearSelection()
    for (let i = start; i <= end; i++) {
      const b = audiobooks.value[i]
      if (b?.inLibrary) libraryStore.toggleSelection(b.id)
    }
  } else {
    libraryStore.toggleSelection(audiobook.id)
  }

  lastClickedIndex.value = currentIndex
}

function onCheckboxChange(audiobook: CollectionDisplayItem, event: Event) {
  if (!audiobook.inLibrary) return

  const currentIndex = audiobooks.value.findIndex((book) => book.key === audiobook.key)
  const shift = (event as MouseEvent | KeyboardEvent).shiftKey
  if (shift && lastClickedIndex.value !== null) {
    const start = Math.min(lastClickedIndex.value, currentIndex)
    const end = Math.max(lastClickedIndex.value, currentIndex)
    libraryStore.clearSelection()
    for (let i = start; i <= end; i++) {
      const b = audiobooks.value[i]
      if (b?.inLibrary) libraryStore.toggleSelection(b.id)
    }
  } else {
    libraryStore.toggleSelection(audiobook.id)
  }

  lastClickedIndex.value = currentIndex
}

const editAudiobook = (audiobook: CollectionDisplayItem) => {
  if (!audiobook.inLibrary) return
  editingAudiobook.value = audiobook
}

const deleteAudiobook = (audiobook: CollectionDisplayItem) => {
  if (!audiobook.inLibrary) return
  deleteTarget.value = audiobook
  resetDeleteOptions()
  showDeleteDialog.value = true
}

function cancelDelete() {
  resetDeleteOptions()
  deleteTarget.value = null
  showDeleteDialog.value = false
}

async function executeDelete() {
  if (deleting.value || !deleteTarget.value) return

  deleting.value = true
  try {
    const shouldDeleteFolder = deleteFolderOnDisk.value
    const shouldDeleteFiles = deleteFilesOnDisk.value || shouldDeleteFolder
    await libraryStore.removeFromLibrary(deleteTarget.value.id, {
      deleteFiles: shouldDeleteFiles,
      deleteFolder: shouldDeleteFolder,
    })
  } catch (err) {
    errorTracking.captureException(err as Error, {
      component: 'CollectionView',
      operation: 'executeDelete',
      metadata: { audiobookId: deleteTarget.value?.id },
    })
  } finally {
    deleting.value = false
    resetDeleteOptions()
    deleteTarget.value = null
    showDeleteDialog.value = false
  }
}

const onAudiobookSaved = () => {
  editingAudiobook.value = null
  void refreshLibrary()
}

const handleImageError = (event: Event) => {
  try {
    const img = event.target as HTMLImageElement
    if (!img) return
    // prevent repeated handling on same element
    try {
      if ((img as unknown as { __imageFallbackDone?: boolean }).__imageFallbackDone) return
      ;(img as unknown as { __imageFallbackDone?: boolean }).__imageFallbackDone = true
    } catch (e: unknown) {
      errorTracking.captureException(e as Error, {
        component: 'CollectionView',
        operation: 'handleImageError',
      })
    }

    // set placeholder
    try {
      img.src = getPlaceholderUrl()
    } catch {}
    try {
      ;(img as unknown as { onerror?: null }).onerror = null
    } catch (e: unknown) {
      errorTracking.captureException(e as Error, {
        component: 'CollectionView',
        operation: 'handleImageErrorCleanup',
      })
    }
  } catch {}
}

function getQualityProfileName(profileId?: number): string | null {
  if (!profileId) return null
  const profile = qualityProfiles.value.find((p) => p.id === profileId)
  return profile?.name ?? null
}

const activeDownloadAudiobookIds = computed(() => {
  const ids = new Set<number>()
  for (const download of downloadsStore.activeDownloads || []) {
    if (typeof download?.audiobookId === 'number') {
      ids.add(download.audiobookId)
    }
  }
  return ids
})

function statusText(status: CollectionStatus): string {
  return status === 'not-added' ? 'Not Added' : formatAudiobookStatus(status)
}

function getAudiobookStatus(audiobook: CollectionDisplayItem): CollectionStatus {
  if (!audiobook.inLibrary) {
    return 'not-added'
  }

  return computeAudiobookStatus(audiobook, activeDownloadAudiobookIds.value)
}

function getMonitoringLabel(audiobook: CollectionDisplayItem): string {
  if (!audiobook.inLibrary) return 'Not Added'
  return audiobook.monitored ? 'Monitored' : 'Unmonitored'
}

function handleCheckboxKeydown(audiobook: CollectionDisplayItem, event: KeyboardEvent) {
  if (!audiobook.inLibrary) return
  if (event.key === ' ') {
    event.preventDefault()
    toggleSelection(audiobook.id)
  }
}

onMounted(async () => {
  await loadCollectionData(false)
})

watch(searchQuery, () => {
  currentPage.value = 1
})

watch([type, name], async () => {
  currentPage.value = 1
  lastClickedIndex.value = null
  showFullAuthorDescription.value = false
  showFullSeriesDescription.value = false
  libraryStore.clearSelection()
  await loadCollectionData(false)
})

function resetDeleteOptions() {
  deleteFilesOnDisk.value = false
  deleteFolderOnDisk.value = false
}

watch(deleteFolderOnDisk, (checked) => {
  if (checked && !deleteFilesOnDisk.value) {
    deleteFilesOnDisk.value = true
  }
})

watch(deleteFilesOnDisk, (checked) => {
  if (!checked && deleteFolderOnDisk.value) {
    deleteFolderOnDisk.value = false
  }
})

function closeAddLibraryModal() {
  pendingAddBook.value = null
}

async function handleBookAdded() {
  pendingAddBook.value = null
  await loadCollectionData(true)
}

defineExpose({
  viewMode,
  showItemDetails,
  toggleItemDetails,
})
</script>

<style scoped>
.collection-view {
  background-color: #1a1a1a;
  min-height: calc(100vh - 120px);
}

.top-nav {
  display: flex;
  align-items: center;
  gap: 1rem;
  margin-bottom: 0; /* toolbar provides separation */
  padding: 10px 20px;
  background-color: #2a2a2a;
  border-bottom: 1px solid #333;
  position: sticky; /* stick below global top nav */
  top: 60px; /* account for fixed global top-nav height */
  z-index: 100; /* sit above content but below global top nav */
  height: 52px;
}

.nav-btn {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  padding: 0.5rem 1rem;
  background: var(--button-bg);
  border: 1px solid var(--border-color);
  border-radius: 6px;
  color: var(--text-color);
  cursor: pointer;
  transition: all 0.2s;
}

.nav-btn:hover {
  background: var(--button-hover-bg);
}

.nav-title {
  display: flex;
  align-items: center;
  gap: 1rem;
}

.nav-title h1 {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  margin: 0;
  font-size: 1.5rem;
  font-weight: 500;
}

.count-badge {
  padding: 6px 12px;
  background-color: var(--brand-500);
  border-radius: 6px;
  color: #fff;
  font-size: 12px;
  transition: background-color 0.12s ease;
}

.count-badge:hover,
.count-badge:focus {
  background-color: var(--brand-700);
}

.toolbar-left,
.toolbar-right {
  display: flex;
  align-items: center;
  gap: 8px;
}

.toolbar-btn {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  padding: 8px 14px;
  background-color: transparent;
  border: 1px solid rgba(255, 255, 255, 0.06);
  border-radius: 6px;
  color: #e6eef8;
  font-size: 12px;
  cursor: pointer;
  transition:
    background-color 0.12s ease,
    transform 0.08s ease,
    box-shadow 0.12s ease;
}

.toolbar-btn:hover {
  background-color: rgba(255, 255, 255, 0.03);
  transform: translateY(-1px);
  box-shadow: 0 6px 18px rgba(0, 0, 0, 0.45);
}

.toolbar-btn.active {
  background-color: #2196f3;
  border-color: #2196f3;
  color: #fff;
}

.toolbar-btn.edit-btn {
  background-color: #2196f3;
  border-color: #1976d2;
  color: #fff;
}

.toolbar-btn.edit-btn:hover {
  background-color: #1976d2;
}

.toolbar-btn.delete-btn {
  background-color: #e74c3c;
  border-color: #c0392b;
  color: #fff;
}

.toolbar-btn.delete-btn:hover {
  background-color: #c0392b;
}

/* Accessibility: strong focus ring for keyboard users */
.toolbar-btn:focus-visible {
  outline: 3px solid rgba(33, 150, 243, 0.18);
  outline-offset: 2px;
}

/* Mobile-friendly toolbar: hide text, show only icons on screens 1154px and below */
@media (max-width: 1154px) {
  .toolbar-btn {
    padding: 8px;
    min-width: 36px;
    justify-content: center;
    font-size: 0;
    gap: unset;
  }

  .toolbar-btn svg {
    font-size: 16px;
    width: 16px;
    height: 16px;
  }

  .count-badge {
    display: none;
  }

  .toolbar-search {
    min-width: 120px;
  }

  .select-trigger {
    width: fit-content;
  }

  .select-dropdown {
    min-width: 120px;
    max-width: 160px;
  }
}

.toolbar-filters {
  display: inline-flex;
  align-items: center;
}

.toolbar-search {
  background: rgba(255, 255, 255, 0.02);
  border: 1px solid rgba(255, 255, 255, 0.04);
  color: #e6eef8;
  padding: 8px 10px;
  border-radius: 6px;
  min-width: 220px; /* wider to match Audiobooks view */
}
.toolbar-select {
  background-color: #2a2a2a; /* match CustomSelect trigger */
  border: 1px solid rgba(255, 255, 255, 0.08);
  color: #e6eef8;
  padding: 8px 10px;
  border-radius: 6px;
  min-height: 36px;
  -webkit-appearance: none;
  -moz-appearance: none;
  appearance: none;
  background-image:
    linear-gradient(45deg, transparent 50%, rgba(255, 255, 255, 0.12) 50%),
    linear-gradient(135deg, rgba(255, 255, 255, 0.12) 50%, transparent 50%);
  background-position:
    calc(100% - 14px) calc(1em + 2px),
    calc(100% - 10px) calc(1em + 2px);
  background-size:
    6px 6px,
    6px 6px;
  background-repeat: no-repeat;
}

.toolbar-custom-select {
  width: auto;
  display: inline-block;
}
.toolbar-select option {
  background: #2a2a2a;
  color: #e6eef8;
}

.toolbar {
  display: flex;
  justify-content: space-between;
  align-items: center;
  height: 52px; /* consistent toolbar height with Audiobooks view */
  padding: 8px 20px;
  background-color: #2a2a2a;
  border-bottom: 1px solid #333;
  margin-bottom: 16px;
  position: sticky; /* stick below collection top-nav */
  top: calc(60px + 52px); /* below global top-nav + collection top-nav */
  z-index: 99; /* below .top-nav */
}

.toolbar.toolbar-without-top-nav {
  top: 60px;
}

.hero-section {
  position: relative;
  padding: 40px 20px 30px;
  overflow: hidden;
  border-bottom: 1px solid rgba(255, 255, 255, 0.06);
}

.backdrop {
  position: absolute;
  inset: 0;
  background-size: cover;
  background-position: center;
  filter: blur(20px) brightness(0.3);
  transform: scale(1.1);
}

.hero-content {
  position: relative;
  display: flex;
  gap: 40px;
  max-width: 1600px;
  margin: 0 auto;
  z-index: 1;
}

.poster-container {
  flex-shrink: 0;
}

.poster {
  width: 320px;
  height: 320px;
  object-fit: cover;
  border-radius: 10px;
  box-shadow: 0 8px 32px rgba(0, 0, 0, 0.6);
}

.info-section {
  flex: 1;
  color: #fff;
  min-width: 0;
}

.title {
  font-size: 3rem;
  font-weight: 500;
  margin: 0 0 12px 0;
  color: #fff;
  line-height: 1.15;
}

.subtitle {
  font-size: 1.2rem;
  color: #ccc;
  margin-bottom: 20px;
}

.meta-info {
  display: flex;
  align-items: center;
  gap: 20px;
  margin-bottom: 24px;
  font-size: 15px;
  color: #ccc;
  flex-wrap: wrap;
}

.meta-info span {
  display: flex;
  align-items: center;
  gap: 6px;
}

.key-details {
  display: flex;
  gap: 12px;
  margin-bottom: 20px;
  flex-wrap: wrap;
}

.detail-item {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 10px 14px;
  background-color: rgba(255, 255, 255, 0.05);
  border-radius: 6px;
  font-size: 14px;
}

.status-badges {
  display: flex;
  gap: 8px;
  margin-bottom: 16px;
  flex-wrap: wrap;
}

.description {
  color: #ccc;
  line-height: 1.6;
  max-width: 920px;
}

.description-content {
  white-space: pre-wrap;
}

.show-more-btn {
  margin-top: 12px;
  padding: 8px 16px;
  background-color: rgba(var(--brand-rgb), 0.1);
  border: 1px solid var(--brand-500);
  border-radius: 6px;
  color: var(--brand-500);
  font-size: 13px;
  cursor: pointer;
  transition: all 0.2s;
}

.show-more-btn:hover {
  background-color: rgba(var(--brand-rgb), 0.2);
  transform: translateY(-1px);
}

.author-hero-section {
  padding-bottom: 28px;
}

.author-hero-backdrop {
  filter: blur(22px) brightness(0.26);
}

.author-hero-content {
  align-items: center;
}

.author-hero-poster-container {
  align-self: flex-start;
}

.author-hero-poster {
  width: 320px;
  height: 320px;
  background: rgba(255, 255, 255, 0.04);
  border: 1px solid rgba(255, 255, 255, 0.08);
}

.series-hero-poster-container {
  width: min(384px, calc(100vw - 32px));
}

.series-hero-poster-card {
  position: relative;
  width: 100%;
  aspect-ratio: 2 / 1;
  overflow: hidden;
  border-radius: 12px;
}

.series-hero-covers {
  position: relative;
  width: 100%;
  height: 100%;
}

.series-hero-cover-item {
  position: absolute;
  top: 0;
  transition:
    transform 0.18s ease,
    filter 0.18s ease,
    opacity 0.18s ease;
}

.series-hero-cover-image {
  width: 100%;
  height: 100%;
  object-fit: cover;
  border-radius: 12px;
}

.series-hero-cover-image.centered {
  position: relative;
}

.series-hero-cover-item.is-not-added .series-hero-cover-image {
  filter: grayscale(0.58) brightness(0.56) saturate(0.72);
  opacity: 0.76;
}

.series-hero-single-bg {
  position: absolute;
  inset: 0;
  background-size: cover;
  background-position: center;
  filter: blur(10px) contrast(0.9) brightness(0.7);
  transform: scale(1.05);
}

.series-hero-count-badge {
  position: absolute;
  top: 10px;
  right: 10px;
  min-width: 30px;
  padding: 3px 9px;
  border-radius: 999px;
  background: rgba(var(--brand-rgb), 0.95);
  color: #fff;
  font-size: 0.85rem;
  font-weight: 600;
  line-height: 1.3;
  text-align: center;
  z-index: 120;
  box-shadow: 0 10px 24px rgba(0, 0, 0, 0.22);
}

.author-hero-kicker {
  font-size: 12px;
  font-weight: 600;
  letter-spacing: 0.16em;
  text-transform: uppercase;
  color: rgba(230, 238, 248, 0.72);
  margin-bottom: 12px;
}

.author-hero-title {
  max-width: 860px;
}

.author-hero-subtitle {
  color: rgba(230, 238, 248, 0.82);
}

.author-hero-asin {
  opacity: 0.86;
}

.author-hero-eyebrow {
  margin-bottom: 10px;
  color: rgba(230, 238, 248, 0.72);
  font-size: 0.78rem;
  font-weight: 700;
  letter-spacing: 0.14em;
  text-transform: uppercase;
}

.author-hero-summary {
  max-width: 900px;
  margin-bottom: 18px;
  color: rgba(230, 238, 248, 0.82);
  line-height: 1.6;
}

.author-hero-detail-item {
  background-color: rgba(255, 255, 255, 0.08);
}

.author-hero-badges {
  margin-bottom: 20px;
}

.author-hero-description {
  margin-bottom: 18px;
}

.author-hero-description-content {
  min-height: 3rem;
}

.author-similar-authors {
  max-width: 920px;
}

.author-similar-title {
  margin-bottom: 10px;
  font-size: 0.78rem;
  letter-spacing: 0.12em;
  text-transform: uppercase;
  color: rgba(230, 238, 248, 0.66);
}

.author-similar-list {
  display: flex;
  flex-wrap: wrap;
  gap: 10px;
}

.author-similar-chip {
  padding: 9px 14px;
  border-radius: 999px;
  border: 1px solid rgba(255, 255, 255, 0.12);
  background: rgba(255, 255, 255, 0.06);
  color: #f3f6fb;
  cursor: pointer;
  transition:
    transform 0.12s ease,
    background-color 0.12s ease,
    border-color 0.12s ease;
}

.author-similar-chip:hover {
  transform: translateY(-1px);
  background: rgba(255, 255, 255, 0.1);
  border-color: rgba(255, 255, 255, 0.22);
}

@media (max-width: 768px) {
  .hero-section {
    padding: 30px 16px 24px;
  }

  .hero-content {
    flex-direction: column;
    gap: 20px;
    align-items: center;
  }

  .poster-container {
    margin: 0 auto;
  }

  .poster {
    width: 240px;
    height: 240px;
  }

  .title {
    font-size: 2rem;
    text-align: center;
  }

  .subtitle,
  .meta-info,
  .key-details,
  .status-badges,
  .author-similar-list {
    justify-content: center;
  }

  .info-section,
  .author-similar-authors {
    text-align: center;
  }

  .author-hero-section {
    padding-bottom: 22px;
  }

  .author-hero-poster {
    width: 240px;
    height: 240px;
  }

  .series-hero-poster-container {
    width: min(100%, 320px);
  }

  .toolbar {
    left: 0; /* Full width on mobile */
    gap: 8px;
    align-items: stretch;
    height: auto;
    padding: 12px;
  }
}

.toolbar-left {
  display: flex;
  align-items: center;
  gap: 10px;
}

.toolbar-right {
  display: flex;
  align-items: center;
  gap: 10px;
  margin-left: auto; /* push filters to the right */
  justify-content: flex-end;
  flex-wrap: wrap;
}

.toolbar-with-monitoring .toolbar-left,
.toolbar-with-monitoring .toolbar-right {
  min-width: 0;
}

.author-monitoring-controls {
  display: inline-flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
  padding: 0;
  border: none;
  border-radius: 0;
  background: transparent;
  box-shadow: none;
}

.author-monitoring-context {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
  min-width: 0;
}

.author-monitoring-label {
  color: rgba(230, 238, 248, 0.68);
  font-size: 12px;
  white-space: nowrap;
}

.author-monitoring-pill {
  flex-shrink: 0;
}

.author-monitoring-actions {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  margin-left: auto;
}

.author-monitor-btn {
  border-color: rgba(72, 187, 120, 0.3);
}

.author-monitor-btn.active {
  border-color: rgba(72, 187, 120, 0.42);
}

.spin-icon {
  animation: collection-toolbar-spin 0.9s linear infinite;
}

.toolbar-btn {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  padding: 6px 12px; /* slightly tighter */
  min-height: 36px;
  background-color: transparent;
  border: 1px solid rgba(255, 255, 255, 0.06);
  border-radius: 6px;
  color: #e6eef8;
  font-size: 13px;
  cursor: pointer;
  transition:
    background-color 0.12s ease,
    transform 0.08s ease,
    box-shadow 0.12s ease;
}

.toolbar-btn:hover {
  background-color: rgba(255, 255, 255, 0.03);
  transform: translateY(-1px);
  box-shadow: 0 6px 18px rgba(0, 0, 0, 0.45);
}

.toolbar-btn.active {
  background-color: #2196f3;
  border-color: #2196f3;
  color: #fff;
}

.toolbar-btn.edit-btn {
  background-color: #2196f3;
  border-color: #1976d2;
  color: #fff;
}

.toolbar-btn.edit-btn:hover {
  background-color: #1976d2;
}

.toolbar-btn.delete-btn {
  background-color: #e74c3c;
  border-color: #c0392b;
  color: #fff;
}

.toolbar-btn.delete-btn:hover {
  background-color: #c0392b;
}

/* Accessibility: strong focus ring for keyboard users */
.toolbar-btn:focus-visible {
  outline: 3px solid rgba(33, 150, 243, 0.18);
  outline-offset: 2px;
}

/* Mobile-friendly toolbar: hide text, show only icons on screens 1154px and below */
@media (max-width: 1280px) {
  .toolbar-with-monitoring {
    flex-wrap: nowrap;
    gap: 10px;
    height: auto;
    align-items: center;
    overflow-x: auto;
    overflow-y: hidden;
  }

  .toolbar-with-monitoring .toolbar-left,
  .toolbar-with-monitoring .toolbar-right {
    width: auto;
    flex: 0 0 auto;
    flex-wrap: nowrap;
  }

  .toolbar-with-monitoring .toolbar-right {
    margin-left: auto;
    justify-content: flex-end;
  }

  .toolbar-with-monitoring .author-monitoring-controls {
    flex: 0 0 auto;
    justify-content: flex-start;
    flex-wrap: nowrap;
  }

  .toolbar-with-monitoring .author-monitoring-actions {
    margin-left: 0;
    flex-wrap: nowrap;
  }

  .toolbar-with-monitoring .toolbar-btn,
  .toolbar-with-monitoring .toolbar-filters {
    flex: 0 0 auto;
  }
}

@media (max-width: 1154px) {
  .toolbar-btn {
    padding: 8px;
    min-width: 36px;
    justify-content: center;
    font-size: 0;
    gap: unset;
  }

  .toolbar-btn svg {
    font-size: 16px;
    width: 16px;
    height: 16px;
  }

  .count-badge {
    display: none;
  }

  .toolbar-search {
    min-width: 120px;
  }

  .select-trigger {
    width: fit-content;
  }

  .select-dropdown {
    min-width: 120px;
    max-width: 160px;
  }

  .author-monitoring-controls {
    width: 100%;
    justify-content: space-between;
    align-items: center;
  }

  .author-monitoring-label {
    font-size: 11px;
  }

  .author-monitoring-actions {
    margin-left: 0;
    width: 100%;
    justify-content: flex-end;
  }

  .toolbar-with-monitoring .author-monitoring-controls {
    width: auto;
    flex: 0 0 auto;
    justify-content: flex-start;
  }

  .toolbar-with-monitoring .author-monitoring-actions {
    width: auto;
    justify-content: flex-start;
    flex-wrap: nowrap;
  }
}

@keyframes collection-toolbar-spin {
  from {
    transform: rotate(0deg);
  }
  to {
    transform: rotate(360deg);
  }
}

.toolbar-filters {
  display: inline-flex;
  align-items: center;
}

.toolbar-search {
  background: rgba(255, 255, 255, 0.02);
  border: 1px solid rgba(255, 255, 255, 0.04);
  color: #e6eef8;
  padding: 8px 10px;
  border-radius: 6px;
  min-width: 220px; /* wider to match Audiobooks view */
}
.toolbar-select {
  background-color: #2a2a2a; /* match CustomSelect trigger */
  border: 1px solid rgba(255, 255, 255, 0.08);
  color: #e6eef8;
  padding: 8px 10px;
  border-radius: 6px;
  min-height: 36px;
  -webkit-appearance: none;
  -moz-appearance: none;
  appearance: none;
  background-image:
    linear-gradient(45deg, transparent 50%, rgba(255, 255, 255, 0.12) 50%),
    linear-gradient(135deg, rgba(255, 255, 255, 0.12) 50%, transparent 50%);
  background-position:
    calc(100% - 14px) calc(1em + 2px),
    calc(100% - 10px) calc(1em + 2px);
  background-size:
    6px 6px,
    6px 6px;
  background-repeat: no-repeat;
}

.toolbar-custom-select {
  width: auto;
  display: inline-block;
}
.toolbar-select option {
  background: #2a2a2a;
  color: #e6eef8;
}

.loading-state,
.error-state,
.empty-state {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  padding: 3rem;
  text-align: center;
}

.empty-icon {
  font-size: 3rem;
  color: var(--text-muted);
  margin-bottom: 1rem;
}

.audiobooks-container {
  display: flex;
  flex-direction: column;
  gap: 1rem;
  padding: 12px 20px;
}

.list-view {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
}

.audiobook-row {
  display: flex;
  align-items: center;
  gap: 1rem;
  padding: 1rem;
  background: var(--card-bg);
  border: 1px solid var(--border-color);
  border-radius: 6px;
  cursor: pointer;
  transition: all 0.2s;
}

.audiobook-row:hover {
  border-color: var(--primary-color);
}

.audiobook-row.selected {
  border-color: var(--primary-color);
  background: var(--selected-bg);
}

.row-checkbox {
  flex-shrink: 0;
}

.row-cover {
  flex-shrink: 0;
  height: 80px;
  border-radius: 6px;
  overflow: hidden;
}

.row-cover img {
  width: 100%;
  height: 100%;
  object-fit: cover;
}

.no-cover {
  width: 100%;
  height: 100%;
  display: flex;
  align-items: center;
  justify-content: center;
  background: var(--muted-bg);
  color: var(--text-muted);
}

.row-details {
  flex: 1;
  min-width: 0;
}

.row-title {
  font-weight: 500;
  margin-bottom: 0.25rem;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.row-author {
  color: var(--text-muted);
  margin-bottom: 0.25rem;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.row-meta {
  font-size: 0.875rem;
  color: var(--text-muted);
}

.row-actions {
  display: flex;
  gap: 0.5rem;
  flex-shrink: 0;
}

.action-btn {
  padding: 0.5rem;
  background: transparent;
  border: 1px solid var(--border-color);
  border-radius: 6px;
  color: var(--text-color);
  cursor: pointer;
  transition: all 0.2s;
}

.action-btn:hover {
  background: var(--button-hover-bg);
}

.grid-view {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(200px, 1fr));
  gap: 1rem;
}

.collection-sections {
  display: flex;
  flex-direction: column;
  gap: 1.5rem;
}

.collection-section {
  display: flex;
  flex-direction: column;
  gap: 0.85rem;
}

.collection-section-header,
.list-section-header {
  display: flex;
  align-items: center;
  gap: 12px;
  color: #cfd5df;
  font-size: 12px;
  letter-spacing: 0.08em;
  text-transform: uppercase;
}

.collection-section-header {
  margin-bottom: 0.1rem;
}

.list-section-header {
  margin: 0.85rem 0 0.2rem;
}

.collection-section-header::after,
.list-section-header::after {
  content: '';
  flex: 1;
  height: 1px;
  background: linear-gradient(90deg, rgba(255, 255, 255, 0.18), rgba(255, 255, 255, 0.02));
}

.section-title {
  font-weight: 700;
}

.section-count {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-width: 28px;
  padding: 3px 8px;
  border-radius: 999px;
  background: rgba(255, 255, 255, 0.06);
  border: 1px solid rgba(255, 255, 255, 0.08);
  color: #f3f6fb;
  font-size: 11px;
  font-weight: 700;
  letter-spacing: normal;
  text-transform: none;
}

.collection-section-header.is-in-library .section-count,
.list-section-header.is-in-library .section-count {
  background: var(--brand-500);
  border-color: rgba(46, 204, 113, 0.32);
  color: #fff;
}

.collection-section-header.is-not-added .section-count,
.list-section-header.is-not-added .section-count {
  background: var(--brand-500);
  border-color: rgba(46, 204, 113, 0.32);
  color: #fff;
}

.collection-card {
  background: var(--card-bg);
  border-radius: 6px;
  overflow: visible;
  cursor: pointer;
  transition: all 0.2s;
  position: relative;
  border-radius: 6px;
}

.collection-card:hover {
  transform: translateY(-2px);
}

.collection-card.selected {
  background: var(--selected-bg);
}

.collection-cover {
  aspect-ratio: 1/1;
  overflow: hidden;
  position: relative;
  border-radius: 6px;
  box-shadow: inset 0 8px 20px rgba(0, 0, 0, 0.6);
}

.collection-cover img {
  width: 100%;
  height: 100%;
  object-fit: cover;
  display: block;
}

.collection-card.selected .collection-cover {
  outline: 3px solid var(--brand-focus);
  outline-offset: 2px;
}

.collection-card.not-in-library {
  border: 1px dashed rgba(255, 255, 255, 0.18);
  background: rgba(255, 255, 255, 0.03);
}

.collection-card.not-in-library .collection-image {
  filter: grayscale(0.5) brightness(0.5);
}

.collection-card.not-in-library:hover .collection-image {
  filter: unset;
}

.collection-image {
  display: block;
  width: 100%;
  height: 100%;
  border-radius: 6px;
}

.collection-card.status-no-file .collection-cover {
  border-bottom: 4px solid #e74c3c;
}
.collection-card.status-downloading .collection-cover {
  border-bottom: 3px solid #3498db;
  animation: pulse 2s ease-in-out infinite;
}
.collection-card.status-quality-mismatch .collection-cover {
  border-bottom: 4px solid #f39c12;
}
.collection-card.status-quality-match .collection-cover {
  border-bottom: 4px solid #2ecc71;
}

@keyframes pulse {
  0%,
  100% {
    border-bottom-color: #3498db;
  }
  50% {
    border-bottom-color: #5dade2;
  }
}

.collection-card.status-quality-mismatch .audiobook-poster-container {
  border-bottom: 3px solid #f39c12;
}

.collection-card.status-quality-match .audiobook-poster-container {
  border-bottom: 3px solid #2ecc71;
}

/* List view status borders */
.audiobook-list-item.status-no-file .list-thumb {
  border-bottom: 3px solid #e74c3c;
}

.audiobook-list-item.status-downloading .list-thumb {
  border-bottom: 3px solid #3498db;
  animation: pulse 2s ease-in-out infinite;
}

.audiobook-list-item.status-quality-mismatch .list-thumb {
  border-bottom: 3px solid #f39c12;
}

.audiobook-list-item.status-quality-match .list-thumb {
  border-bottom: 3px solid #2ecc71;
}

.status-overlay {
  position: absolute;
  bottom: 0;
  left: 0;
  right: 0;
  background: linear-gradient(transparent, rgba(0, 0, 0, 0.9));
  padding: 8px;
  transition: padding 0.2s ease;
}

.collection-cover:hover .status-overlay {
  padding: 80px 8px 8px;
}

/* When 'show-details' class is present, render overlay expanded */
.collection-cover.show-details .status-overlay {
  padding: 80px 8px 8px;
}

.collection-cover .audiobook-title,
.collection-cover .audiobook-author {
  opacity: 0;
  transition: opacity 0.2s ease;
}

.collection-cover.show-details .audiobook-title,
.collection-cover.show-details .audiobook-author {
  opacity: 1;
}

.audiobook-extra-details {
  margin-top: 8px;
  color: #e6eef8;
}
.audiobook-extra-details .detail-line {
  font-size: 12px;
  line-height: 1.2;
  margin: 2px 0;
  color: #cfd8e3;
}
.audiobook-extra-details .detail-line.title {
  font-weight: 500;
  color: #fff;
}
.audiobook-extra-details .detail-line.small {
  font-size: 11px;
  color: #bfcad6;
}
.list-extra-details {
  margin-top: 6px;
  color: #e6eef8;
}
.list-extra-details .detail-line {
  font-size: 12px;
  color: #bfcad6;
}
.grid-bottom-details {
  margin-top: 8px;
  color: #e6eef8;
  padding: 0 4px;
  width: 100%;
}
.grid-bottom-details .detail-line {
  font-size: 12px;
  color: #bfcad6;
  text-align: center;
}
.grid-bottom-details .detail-line.title {
  color: #fff;
  font-weight: 500;
  margin-bottom: 4px;
}

.audiobook-title {
  font-size: 13px;
  font-weight: 500;
  color: #fff;
  margin-bottom: 4px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  opacity: 0;
  transition: opacity 0.2s ease;
}

.audiobook-author {
  font-size: 11px;
  color: #ccc;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  opacity: 0;
  transition: opacity 0.2s ease;
}

.collection-cover:hover .audiobook-title,
.collection-cover:hover .audiobook-author {
  opacity: 1;
}

.quality-profile-badge {
  display: inline-flex;
  align-items: center;
  gap: 0.25rem;
  margin-top: 0.5rem;
  padding: 0.25rem 0.5rem;
  margin-right: 0.5rem;
  background-color: rgba(52, 152, 219, 0.2);
  border: 1px solid rgba(52, 152, 219, 0.4);
  border-radius: 6px;
  font-size: 10px;
  font-weight: 500;
  color: #3498db;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  max-width: 100%;
}

.status-badge {
  display: inline-flex;
  align-items: center;
  gap: 0.25rem;
  padding: 0.25rem 0.5rem;
  margin-right: 0.5rem;
  background-color: rgba(255, 255, 255, 0.03);
  border: 1px solid rgba(255, 255, 255, 0.06);
  border-radius: 6px;
  font-size: 10px;
  font-weight: 500;
  color: #cfcfcf;
  margin-top: 0.5rem;
  cursor: pointer;
  white-space: nowrap;
}

.status-badge.no-file {
  background-color: rgba(231, 76, 60, 0.12);
  border-color: rgba(231, 76, 60, 0.18);
  color: #e74c3c;
}

.status-badge.downloading {
  background-color: rgba(52, 152, 219, 0.1);
  border-color: rgba(52, 152, 219, 0.2);
  color: #3498db;
}

.status-badge.quality-mismatch {
  background-color: rgba(243, 156, 18, 0.1);
  border-color: rgba(243, 156, 18, 0.18);
  color: #f39c12;
}

.status-badge.quality-match {
  background-color: rgba(46, 204, 113, 0.1);
  border-color: rgba(46, 204, 113, 0.18);
  color: #2ecc71;
}

.status-badge.not-added {
  background-color: rgba(255, 255, 255, 0.05);
  border-color: rgba(255, 255, 255, 0.14);
  color: #d3d7de;
}

.quality-profile-badge i {
  font-size: 12px;
  flex-shrink: 0;
}

.monitored-badge {
  display: inline-flex;
  align-items: center;
  gap: 0.25rem;
  margin-top: 0.5rem;
  padding: 0.25rem 0.5rem;
  margin-left: 0.25rem;
  background-color: rgba(46, 204, 113, 0.2);
  border: 1px solid rgba(46, 204, 113, 0.4);
  border-radius: 6px;
  font-size: 10px;
  font-weight: 500;
  color: #2ecc71;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  max-width: 100%;
}

.monitored-badge.unmonitored {
  background-color: rgba(231, 76, 60, 0.2);
  border-color: rgba(231, 76, 60, 0.4);
  color: #e74c3c;
}

.monitored-badge i {
  font-size: 12px;
  flex-shrink: 0;
}

.action-buttons {
  position: absolute;
  top: 8px;
  right: 8px;
  display: flex;
  gap: 4px;
  opacity: 0;
  transition: opacity 0.2s;
  z-index: 30; /* keep action buttons above the row click overlay */
}

.audiobook-item:hover .action-buttons {
  opacity: 1;
}

.action-btn {
  padding: 6px 8px;
  border-radius: 6px;
  color: white;
  cursor: pointer;
  font-size: 14px;
  transition: background-color 0.2s;
}

.delete-btn-small {
  background-color: rgba(231, 76, 60, 0.9);
  border-color: rgba(192, 57, 43, 0.5);
}

.delete-btn-small:hover {
  background-color: rgba(192, 57, 43, 1);
}

.edit-btn-small {
  background-color: rgba(52, 152, 219, 0.9);
  border-color: rgba(41, 128, 185, 0.5);
}

.edit-btn-small:hover {
  background-color: rgba(41, 128, 185, 1);
}

.add-btn-small {
  background-color: rgba(46, 204, 113, 0.9);
  border-color: rgba(39, 174, 96, 0.5);
}

.add-btn-small:hover {
  background-color: rgba(39, 174, 96, 1);
}

.loading-state,
.empty-state,
.error-state {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  height: calc(100vh - 164px);
  color: #ccc;
  text-align: center;
}

.loading-state i,
.empty-icon,
.error-icon {
  font-size: 4rem;
  color: #868e96;
  margin-bottom: 1rem;
}

.loading-state i {
  color: var(--brand-500);
}

.error-icon {
  color: #e74c3c;
}

.error-state h2 {
  color: white;
  margin-bottom: 0.5rem;
}

.error-state p {
  margin-bottom: 2rem;
  color: #e74c3c;
}

.retry-button {
  display: inline-flex;
  align-items: center;
  gap: 0.5rem;
  padding: 12px 24px;
  background-color: var(--brand-500);
  color: white;
  border: none;
  border-radius: 6px;
  cursor: pointer;
  font-weight: 500;
  transition: background-color 0.2s;
}

.retry-button:hover {
  background-color: var(--brand-700);
}

.empty-state h2 {
  color: white;
  margin-bottom: 0.5rem;
}

.empty-state p {
  margin-bottom: 2rem;
}

.add-button {
  display: inline-flex;
  align-items: center;
  gap: 0.5rem;
  padding: 12px 24px;
  background-color: var(--brand-500);
  color: white;
  border-radius: 6px;
  text-decoration: none;
  font-weight: 500;
  transition: background-color 0.2s;
}

.add-button:hover {
  background-color: var(--brand-700);
}

.collection-cover:hover .status-overlay {
  padding: 56px 8px 8px;
}
.status-overlay .overlay-title {
  color: #fff;
  font-weight: 500;
}
.status-overlay .overlay-author {
  color: #bfcad6;
  font-size: 13px;
}
.overlay-badges {
  display: flex;
  gap: 6px;
  align-items: center;
  margin-top: 4px;
}

.collection-content {
  padding: 1rem;
}

.collection-title {
  font-weight: 500;
  margin-bottom: 0.5rem;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.collection-author {
  color: var(--text-muted);
  margin-bottom: 0.5rem;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.collection-meta {
  display: flex;
  flex-wrap: wrap;
  gap: 0.5rem;
  margin-top: 0.5rem;
}

.series-bottom-placard {
  margin-top: 0.5rem;
  display: flex;
  justify-content: center;
  z-index: 10;
}

.series-bottom-content {
  width: 200px;
  height: 100%;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  padding: 0 0.5rem;
}

.series-bottom-title {
  font-size: 12px;
  color: #fff;
  margin: 0 0 4px 0;
  font-weight: 500;
  text-align: center;
}

.series-bottom-author {
  font-size: 11px;
  color: #bfcad6;
  margin: 0 0 2px 0;
  text-align: center;
}

.series-bottom-meta {
  font-size: 11px;
  color: #bfcad6;
  margin: 0;
  text-align: center;
}

.selection-checkbox {
  /* default used in grid; overridden in list below */
  position: absolute;
  top: 8px;
  left: 8px;
  z-index: 40; /* keep checkbox above row click overlay */
  height: 22px;
  width: 22px;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 0;
  box-sizing: border-box;
  background-color: rgba(0, 0, 0, 0.45);
  border: 1px solid rgba(255, 255, 255, 0.12);
  border-radius: 6px;
  cursor: pointer;
  transition: all 0.12s ease;
  opacity: 0;
  user-select: none;
  -webkit-user-select: none;
  -moz-user-select: none;
  -ms-user-select: none;
}
/* Hide the native input visually but keep it accessible and interactive */
.selection-checkbox input[type='checkbox'] {
  position: absolute;
  inset: 0;
  margin: 0;
  padding: 0;
  width: 100%;
  height: 100%;
  opacity: 0;
  cursor: pointer;
  z-index: 41; /* ensure native input is above overlay and container pseudo-elements */
}

/* Draw a custom box and checkmark using container pseudo-elements */
.selection-checkbox::before {
  content: '';
  position: absolute;
  left: 50%;
  top: 50%;
  transform: translate(-50%, -50%);
  width: 14px;
  height: 14px;
  border-radius: 4px;
  border: 2px solid rgba(255, 255, 255, 0.14);
  background: transparent;
  box-sizing: border-box;
  transition:
    border-color 0.12s ease,
    background-color 0.12s ease,
    box-shadow 0.12s ease;
  z-index: 1;
}

/* Custom checkmark uses pseudo-element ::after - no need to hide it */

.selection-checkbox:hover {
  background-color: rgba(0, 0, 0, 0.6);
  border-color: rgba(255, 255, 255, 0.18);
}

/* Custom checkmark */

/* Remove container hover darkening when focusing the native checkbox so contrast stays good */
.selection-checkbox:hover input[type='checkbox'] {
  transform: translateY(0);
}

/* Only show checkbox when hovered or selected */

.collection-card:hover .selection-checkbox,
.collection-card.selected .selection-checkbox,
.audiobook-list-item:hover .selection-checkbox,
.audiobook-list-item.selected .selection-checkbox,
.audiobooks-scroll-container.has-selection .selection-checkbox {
  opacity: 1;
}

/* When the item is selected, style the custom box and show the check */
.collection-card.selected .selection-checkbox::before,
.audiobook-list-item.selected .selection-checkbox::before {
  background-color: var(--brand-500);
  border-color: var(--brand-500);
  box-shadow: 0 0 0 4px rgba(var(--brand-rgb), 0.12);
}

.audiobook-item.selected .selection-checkbox::after,
.audiobook-list-item.selected .selection-checkbox::after {
  border-right-color: #fff;
  border-bottom-color: #fff;
  transform: translate(-50%, -50%) rotate(45deg) scale(1);
}

/* Focus outlines for keyboard navigation */
.selection-checkbox input[type='checkbox']:focus-visible {
  outline: 2px solid rgba(var(--brand-rgb), 0.3);
  outline-offset: 2px;
}

.audiobook-list-item:focus,
.audiobook-list-item:focus-within,
.collection-card:focus,
.collection-card:focus-within {
  outline: 2px solid rgba(var(--brand-rgb), 0.18);
  outline-offset: 2px;
  background-color: rgba(255, 255, 255, 0.02);
}

/* List-specific override for the checkbox so it participates in the grid */
.audiobooks-list .selection-checkbox {
  position: relative;
  top: auto;
  left: auto;
  z-index: 40; /* ensure list checkboxes stay above the row overlay */
  height: 20px;
  width: 20px;
  margin: 0;
  background-color: rgba(0, 0, 0, 0);
  border: 1px solid rgba(255, 255, 255, 0.06);
  display: flex;
  align-items: center;
  justify-content: center;
}

/* In list view, always show checkboxes (outline). Filled/checkmark still only shows for selected items */
.audiobooks-list .selection-checkbox {
  opacity: 1;
}
.audiobooks-list .selection-checkbox::before {
  opacity: 1;
}
.audiobooks-list .selection-checkbox input[type='checkbox'] {
  opacity: 0; /* native input remains visually hidden */
}

.audiobooks-list .selection-checkbox {
  justify-self: center;
}

.audiobooks-list .selection-checkbox-spacer {
  width: 20px;
  height: 20px;
  justify-self: center;
}

.audiobooks-list .selection-checkbox::after {
  left: 6px;
  top: 2px;
}

.collection-card:focus,
.collection-card:focus-within {
  outline: 2px solid var(--primary-color);
  outline-offset: 2px;
}

.action-buttons {
  position: absolute;
  top: 8px;
  right: 8px;
  display: flex;
  gap: 4px;
  opacity: 0;
  transition: opacity 0.2s ease;
}

.collection-card:hover .action-buttons {
  opacity: 1;
}

.action-btn {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 32px;
  height: 32px;
  border-radius: 6px;
  color: #fff;
  cursor: pointer;
  transition: all 0.2s ease;
}

.quality-profile-badge {
  display: inline-flex;
  align-items: center;
  gap: 0.25rem;
  margin-top: 0.5rem;
  padding: 0.25rem 0.5rem;
  margin-right: 0.5rem;
  background-color: rgba(52, 152, 219, 0.2);
  border: 1px solid rgba(52, 152, 219, 0.4);
  border-radius: 6px;
  font-size: 10px;
  font-weight: 500;
  color: #3498db;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  max-width: 100%;
}

.status-badge {
  display: inline-flex;
  align-items: center;
  gap: 0.25rem;
  padding: 0.25rem 0.5rem;
  margin-right: 0.5rem;
  background-color: rgba(255, 255, 255, 0.03);
  border: 1px solid rgba(255, 255, 255, 0.06);
  border-radius: 6px;
  font-size: 10px;
  font-weight: 500;
  color: #cfcfcf;
  margin-top: 0.5rem;
  cursor: pointer;
  white-space: nowrap;
}

.status-badge.no-file {
  background-color: rgba(231, 76, 60, 0.12);
  border-color: rgba(231, 76, 60, 0.18);
  color: #e74c3c;
}

.status-badge.downloading {
  background-color: rgba(52, 152, 219, 0.1);
  border-color: rgba(52, 152, 219, 0.2);
  color: #3498db;
}

.status-badge.quality-mismatch {
  background-color: rgba(243, 156, 18, 0.1);
  border-color: rgba(243, 156, 18, 0.18);
  color: #f39c12;
}

.status-badge.quality-match {
  background-color: rgba(46, 204, 113, 0.1);
  border-color: rgba(46, 204, 113, 0.18);
  color: #2ecc71;
}

.quality-profile-badge i {
  font-size: 12px;
  flex-shrink: 0;
}

.monitored-badge {
  display: inline-flex;
  align-items: center;
  gap: 0.25rem;
  margin-top: 0.5rem;
  padding: 0.25rem 0.5rem;
  margin-left: 0.25rem;
  background-color: rgba(46, 204, 113, 0.2);
  border: 1px solid rgba(46, 204, 113, 0.4);
  border-radius: 6px;
  font-size: 10px;
  font-weight: 500;
  color: #2ecc71;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  max-width: 100%;
}

.monitored-badge.unmonitored {
  background-color: rgba(231, 76, 60, 0.2);
  border-color: rgba(231, 76, 60, 0.4);
  color: #e74c3c;
}

.monitored-badge i {
  font-size: 12px;
  flex-shrink: 0;
}

.monitored-badge.unmonitored {
  background: rgba(244, 67, 54, 0.9);
  color: #fff;
}

.grid-bottom-details {
  margin-top: 8px;
  color: var(--text-color);
  padding: 0 4px;
  width: 100%;
}

.grid-bottom-details .detail-line {
  font-size: 12px;
  color: #bfcad6;
  text-align: center;
}

.grid-bottom-details .detail-line.title {
  color: #fff;
  font-weight: 500;
  margin-bottom: 4px;
}

.grid-bottom-details .detail-line.small {
  font-size: 11px;
  margin-bottom: 2px;
}

/* List view styles copied from AudiobooksView to match visuals */
.audiobooks-list {
  display: flex;
  flex-direction: column;
  padding: 8px 0;
}

.audiobook-list-item {
  display: grid;
  grid-template-columns: 40px 64px 1fr auto 120px;
  gap: 12px;
  align-items: center;
  padding: 10px 12px;
  background-color: transparent;
  border-radius: 6px;
  transition:
    background-color 0.12s,
    transform 0.12s;
  border-bottom: 1px solid rgba(255, 255, 255, 0.03);
  cursor: pointer;
}

.audiobook-list-item:hover {
  background-color: rgba(255, 255, 255, 0.02);
  transform: translateY(-1px);
}

.audiobook-list-item.selected {
  background-color: rgba(255, 255, 255, 0.02);
  transform: translateY(-1px);
}

.audiobook-list-item.not-in-library {
  opacity: 0.92;
  border: 1px dashed rgba(255, 255, 255, 0.14);
  background-color: rgba(255, 255, 255, 0.02);
}

.audiobook-list-item.not-in-library .list-thumb {
  filter: grayscale(0.12) brightness(0.88);
}

.list-thumb {
  width: 56px;
  height: 56px;
  object-fit: cover;
  border-radius: 6px;
  flex-shrink: 0;
}

.list-details {
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

/* Series position indicator (only shown inside a single-series collection) */
.list-series-position {
  display: inline-block;
  margin-right: 0.4rem;
  padding: 0 0.35rem;
  border-radius: 4px;
  font-size: 0.8em;
  font-weight: 700;
  color: var(--brand-500);
  background-color: rgba(var(--brand-rgb), 0.16);
}

.series-position-badge {
  position: absolute;
  top: 8px;
  left: 8px;
  z-index: 2;
  padding: 0.15rem 0.45rem;
  border-radius: 6px;
  font-size: 11px;
  font-weight: 700;
  letter-spacing: 0.02em;
  color: #fff;
  background-color: rgba(var(--brand-rgb), 0.92);
  pointer-events: none;
}

.list-details .audiobook-title {
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  font-size: 14px;
  color: #fff;
}

.list-details .audiobook-author {
  font-size: 12px;
  color: #ccc;
}

.list-actions {
  margin-left: 0;
  display: flex;
  gap: 8px;
  align-items: center;
  justify-self: end;
}

.list-header {
  display: grid;
  grid-template-columns: 40px 64px 1fr auto 120px;
  gap: 12px;
  padding: 8px 12px;
  color: #aaa;
  font-size: 12px;
  border-bottom: 1px solid rgba(255, 255, 255, 0.04);
  align-items: center;
}

.list-header .col-cover {
  opacity: 0.9;
  text-align: center;
}
.list-header .col-title {
  opacity: 0.9;
}
.list-header .col-status {
  opacity: 0.9;
}
.list-header .col-actions {
  text-align: right;
}

.list-badges {
  display: flex;
  gap: 8px;
  align-items: center;
  margin-left: 12px;
  justify-self: start;
}

@media (max-width: 978px) {
  .list-badges {
    flex-direction: column;
    gap: 4px;
    align-items: flex-start;
    margin-left: 0;
    margin-top: 8px;
  }
}

/* Ensure list view titles/badges and checkboxes are visible (override poster overlay rules) */
.audiobooks-list .audiobook-title,
.audiobooks-list .audiobook-author {
  opacity: 1;
  transition: none;
  color: inherit;
}

.pagination {
  display: flex;
  justify-content: center;
  align-items: center;
  gap: 1rem;
  margin-top: 2rem;
}

.page-btn {
  display: flex;
  align-items: center;
  padding: 0.5rem;
  background: var(--button-bg);
  border: 1px solid var(--border-color);
  border-radius: 6px;
  color: var(--text-color);
  cursor: pointer;
  transition: all 0.2s;
}

.page-btn:hover:not(:disabled) {
  background: var(--button-hover-bg);
}

.page-btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.page-info {
  font-weight: 500;
}

@media (max-width: 768px) {
  .title-bar {
    flex-direction: column;
    align-items: stretch;
    gap: 1rem;
  }

  .nav-title {
    justify-content: center;
  }

  .title-filters {
    justify-content: center;
  }

  .grid-view {
    grid-template-columns: repeat(auto-fill, minmax(150px, 1fr));
  }

  .audiobook-row {
    flex-direction: column;
    align-items: flex-start;
    gap: 0.5rem;
  }

  .row-cover {
    width: 100%;
    height: 120px;
  }
}
</style>
