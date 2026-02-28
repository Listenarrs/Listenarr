<template>
  <div class="audiobook-detail" v-if="!loading && audiobook">
    <!-- Top Navigation Bar -->
    <div class="top-nav">
      <button class="nav-btn" @click="goBack">
        <PhArrowLeft />
        Back
      </button>
      <div class="nav-actions">
        <div class="primary-actions">
          <button
            v-for="action in primaryTopActions"
            :key="`primary-${action.key}`"
            :class="['nav-btn', 'icon-button', action.desktopClass]"
            :disabled="action.disabled"
            @click="runTopAction(action)"
            :title="action.title"
            :aria-label="action.ariaLabel"
            :aria-pressed="action.key === 'monitor' ? audiobook.monitored : undefined"
          >
            <component :is="action.icon" v-bind="action.iconProps || {}" :class="action.iconClass" />
          </button>
        </div>

        <!-- Desktop: show all actions inline -->
        <div class="secondary-actions tabs-desktop">
          <button
            v-for="action in secondaryTopActions"
            :key="`secondary-${action.key}`"
            :class="['nav-btn', 'icon-button', action.desktopClass]"
            :disabled="action.disabled"
            @click="runTopAction(action)"
            :title="action.title"
            :aria-label="action.ariaLabel"
          >
            <component :is="action.icon" v-bind="action.iconProps || {}" :class="action.iconClass" />
          </button>
        </div>

        <!-- Mobile: collapse remaining actions into a More dropdown -->
        <div class="more-wrapper tabs-mobile">
          <button class="nav-btn more-btn" @click.stop="showMoreActions = !showMoreActions"
            :aria-expanded="showMoreActions" title="More actions">
            <PhCaretDown />
            More
          </button>
          <div v-if="showMoreActions" class="more-dropdown" @click.stop>
            <button
              v-for="action in topActions"
              :key="`more-${action.key}`"
              :class="['dropdown-item', action.mobileClass]"
              :disabled="action.disabled"
              @click="runTopAction(action, true)"
            >
              <component :is="action.icon" v-bind="action.iconProps || {}" :class="action.iconClass" />
              <span>{{ action.label }}</span>
            </button>
          </div>
        </div>
      </div>
    </div>

    <!-- Hero Section -->
    <div class="hero-section">
      <div class="backdrop" :style="{ backgroundImage: `url(${coverImageUrl})` }"></div>
      <div class="hero-content">
        <div class="poster-container">
          <img :src="coverImageUrl" :alt="audiobook.title" class="poster" loading="lazy" decoding="async"
            @error="handleImageError" />
        </div>
        <div class="info-section">
          <h1 class="title">{{ safeText(audiobook.title) }}</h1>
          <div class="subtitle" v-if="audiobook.subtitle">{{ audiobook.subtitle }}</div>

          <div class="meta-info">
            <span class="runtime" v-if="audiobook.runtime">
              <PhClock />
              {{ formatRuntime(audiobook.runtime) }}
            </span>
          </div>

          <div class="key-details">
            <div class="detail-item" v-if="displayBasePath">
              <PhFolder />
              <span class="file-path">{{ displayBasePath }}</span>
            </div>
            <div class="detail-item" v-if="audiobook.fileSize">
              <PhDatabase />
              <span>{{ formatFileSize(audiobook.fileSize) }}</span>
            </div>
            <div class="detail-item" v-if="audiobook.quality">
              <PhSpeakerHigh />
              <span>{{ audiobook.quality }}</span>
            </div>
            <div class="detail-item" v-if="audiobook.language">
              <PhGlobe />
              <span>{{ capitalizeFirst(audiobook.language) }}</span>
            </div>
            <div class="detail-item">
              <PhTag />
              <span>{{ audiobook.abridged ? 'Abridged' : 'Unabridged' }}</span>
            </div>
          </div>

          <div class="status-badges">
            <Pill variant="primary" v-if="audiobook.monitored">
              <PhBookmark weight="fill" />
              Monitored
            </Pill>
            <Pill variant="success" v-if="assignedProfileName">
              <PhStar />
              Quality: {{ assignedProfileName }}
            </Pill>
            <Pill variant="info">
              <PhChatCircle />
              {{ capitalizeFirst(audiobook.language) || 'English' }}
            </Pill>
            <Pill variant="default" v-if="audiobook.version">
              <PhMusicNotes />
              {{ audiobook.version }}
            </Pill>
          </div>

          <div class="description" v-if="audiobook.description">
            <div class="description-content" :class="{ expanded: showFullDescription }">{{ stripHtmlAndNormalize(audiobook.description) }}
            </div>
            <button v-if="!showFullDescription" class="show-more-btn" @click="showFullDescription = true">
              Show More
            </button>
            <button v-else class="show-more-btn" @click="showFullDescription = false">
              Show Less
            </button>
          </div>
        </div>
      </div>
    </div>

    <!-- Tabs Section -->
    <div class="tabs-container">
      <!-- Mobile dropdown -->
      <div class="tabs-mobile">
        <CustomSelect v-model="activeTab" :options="mobileTabOptions" class="tab-dropdown" />
      </div>

      <!-- Desktop tabs -->
      <div class="tabs-desktop">
        <div class="tabs">
          <button class="tab" :class="{ active: activeTab === 'details' }" @click="activeTab = 'details'">
            <PhInfo />
            Details
          </button>
          <button class="tab" :class="{ active: activeTab === 'files' }" @click="activeTab = 'files'">
            <PhFile />
            Files
          </button>
          <button class="tab" :class="{ active: activeTab === 'history' }" @click="activeTab = 'history'">
            <PhClockCounterClockwise />
            History
          </button>
        </div>
      </div>
    </div>

    <!-- Tab Content -->
    <div class="tab-content">
      <!-- Details Tab -->
      <div id="details" v-if="activeTab === 'details'" class="details-content">
        <div class="details-grid">
          <div class="detail-card">
            <h3>Author Information</h3>
            <div class="detail-row" v-if="audiobook.authors">
              <span class="label">Author(s):</span>
              <span class="value">{{ audiobook.authors.map(safeText).join(', ') }}</span>
            </div>
            <div class="detail-row" v-if="audiobook.narrators">
              <span class="label">Narrator(s):</span>
              <span class="value">{{ audiobook.narrators.map(safeText).join(', ') }}</span>
            </div>
          </div>

          <div class="detail-card">
            <h3>Publication Details</h3>
            <div class="detail-row" v-if="audiobook.publisher">
              <span class="label">Publisher:</span>
              <span class="value">{{ safeText(audiobook.publisher) }}</span>
            </div>
            <div class="detail-row" v-if="audiobook.publishedDate || audiobook.publishYear">
              <span class="label">Release Date:</span>
              <span class="value">{{ audiobook.publishedDate ? formatDate(audiobook.publishedDate) : audiobook.publishYear }}</span>
            </div>
            <div class="detail-row" v-if="audiobook.language">
              <span class="label">Language:</span>
              <span class="value">{{ capitalizeFirst(audiobook.language) }}</span>
            </div>
          </div>

          <div class="detail-card" v-if="audiobook.series">
            <h3>Series Information</h3>
            <div class="detail-row">
              <span class="label">Series:</span>
              <span class="value">{{ safeText(audiobook.series) }}</span>
            </div>
            <div class="detail-row" v-if="audiobook.seriesNumber && audiobook.seriesNumber.trim()">
              <span class="label">Book #:</span>
              <span class="value">{{ audiobook.seriesNumber }}</span>
            </div>
          </div>

          <div class="detail-card">
            <h3>Identifiers</h3>
            <div class="detail-row" v-if="audimetaSourceUrl">
              <span class="label">Metadata Source:</span>
              <span class="value">
                <a :href="audimetaSourceUrl" target="_blank" rel="noopener noreferrer">Audimeta</a>
              </span>
            </div>
            <div class="detail-row detail-row-stacked" v-if="displayIdentifiers.length">
              <span class="label">Associated IDs:</span>
              <div class="value identifiers-list">
                <div v-for="identifier in displayIdentifiers" :key="identifier.key" class="identifier-item">
                  <span class="identifier-type">{{ identifier.typeLabel }}</span>
                  <a
                    v-if="identifier.href"
                    :href="identifier.href"
                    target="_blank"
                    rel="noopener noreferrer"
                    class="identifier-link"
                  >
                    {{ identifier.value }}
                  </a>
                  <span v-else class="identifier-link">{{ identifier.value }}</span>
                  <span v-if="identifier.isPrimary" class="identifier-badge primary">Primary</span>
                </div>
              </div>
            </div>
            <div class="detail-row" v-else-if="audiobook.asin || audiobook.isbn || audiobook.openLibraryId">
              <span class="label">Associated IDs:</span>
              <span class="value">Unavailable</span>
            </div>
          </div>

          <div class="detail-card" v-if="audiobook.genres && audiobook.genres.length">
            <h3>Genres</h3>
            <div class="genre-tags">
              <span v-for="genre in audiobook.genres" :key="genre" class="genre-tag">
                {{ genre }}
              </span>
            </div>
          </div>

          <div class="detail-card" v-if="audiobook.tags && audiobook.tags.length">
            <h3>Tags</h3>
            <div class="tags-list">
              <span v-for="tag in audiobook.tags" :key="tag" class="tag-badge">
                {{ tag }}
              </span>
            </div>
          </div>
        </div>
      </div>

      <!-- Files Tab -->
      <div id="files" v-if="activeTab === 'files'" class="files-content">
        <div class="files-header">
          <h3>Files</h3>
          <div class="files-actions">
            <!-- Scan job status (updated via SignalR) -->
            <div v-if="scanJobId" class="scan-job-status">
              <div class="job-row">
                <PhClock />
                <strong>Scan job:</strong>
                <span class="job-id">{{ scanJobId }}</span>
              </div>
              <div class="job-status">
                <span v-if="scanQueued" class="status queued">Queued / Processing</span>
                <span v-else class="status completed">No active scan</span>
              </div>
            </div>
          </div>
        </div>
        <div v-if="audiobook.files && audiobook.files.length" class="file-list">
          <div v-for="f in audiobook.files" :key="f.id" class="file-item"
            :class="{ expanded: isFileAccordionExpanded(f.id) }">
            <div class="file-header" @click="toggleFileAccordion(f.id)">
              <div class="file-info">
                <PhFileAudio />
                <span class="file-name">{{ getFileName(f.path) }}</span>
                <small class="file-meta">• {{ f.format ? f.format.toUpperCase() : '' }}
                  {{ f.durationSeconds ? '• ' + formatDuration(f.durationSeconds) : '' }}</small>
              </div>
              <div class="file-actions">
                <span class="file-size" v-if="f.size">{{ formatFileSize(f.size) }}</span>
                <span class="file-size" v-else>Unknown size</span>
                <PhCaretDown class="accordion-toggle" :class="{ rotated: isFileAccordionExpanded(f.id) }" />
              </div>
            </div>
            <div v-if="isFileAccordionExpanded(f.id)" class="file-accordion">
              <table class="metadata-table">
                <tbody>
                  <tr v-if="f.path">
                    <td class="metadata-label">Path:</td>
                    <td class="metadata-value">{{ getFullPath(f.path) }}</td>
                  </tr>
                  <tr v-if="f.size !== undefined">
                    <td class="metadata-label">Size:</td>
                    <td class="metadata-value">{{ formatFileSize(f.size) }}</td>
                  </tr>
                  <tr v-if="f.durationSeconds !== undefined">
                    <td class="metadata-label">Duration:</td>
                    <td class="metadata-value">{{ formatDuration(f.durationSeconds) }}</td>
                  </tr>
                  <tr v-if="f.format">
                    <td class="metadata-label">Format:</td>
                    <td class="metadata-value">{{ f.format.toUpperCase() }}</td>
                  </tr>
                  <tr v-if="f.container">
                    <td class="metadata-label">Container:</td>
                    <td class="metadata-value">{{ f.container }}</td>
                  </tr>
                  <tr v-if="f.codec">
                    <td class="metadata-label">Codec:</td>
                    <td class="metadata-value">{{ f.codec }}</td>
                  </tr>
                  <tr v-if="f.bitrate !== undefined">
                    <td class="metadata-label">Bitrate:</td>
                    <td class="metadata-value">{{ f.bitrate }} kbps</td>
                  </tr>
                  <tr v-if="f.sampleRate !== undefined">
                    <td class="metadata-label">Sample Rate:</td>
                    <td class="metadata-value">{{ f.sampleRate }} Hz</td>
                  </tr>
                  <tr v-if="f.channels !== undefined">
                    <td class="metadata-label">Channels:</td>
                    <td class="metadata-value">{{ f.channels }}</td>
                  </tr>
                  <tr v-if="f.createdAt">
                    <td class="metadata-label">Created:</td>
                    <td class="metadata-value">{{ formatDate(f.createdAt) }}</td>
                  </tr>
                  <tr v-if="f.source">
                    <td class="metadata-label">Source:</td>
                    <td class="metadata-value">{{ f.source }}</td>
                  </tr>
                </tbody>
              </table>
            </div>
          </div>
        </div>
        <div v-else class="empty-files">
          <PhFileDashed />
          <p>No files available</p>
          <p class="hint">This audiobook hasn't been downloaded yet</p>
        </div>
      </div>

      <!-- History Tab -->
      <div id="history" v-if="activeTab === 'history'" class="history-content">
        <div class="history-header">
          <h3>History</h3>
          <button v-if="historyEntries.length > 0" class="refresh-btn" @click="loadHistory" :disabled="historyLoading">
            <PhArrowClockwise :class="{ 'ph-spin': historyLoading }" />
            Refresh
          </button>
        </div>

        <!-- Loading State -->
        <div v-if="historyLoading" class="history-loading">
          <PhSpinner class="ph-spin" />
          <p>Loading history...</p>
        </div>

        <!-- Error State -->
        <div v-else-if="historyError" class="history-error">
          <PhWarningCircle />
          <p>{{ historyError }}</p>
          <button class="retry-btn" @click="loadHistory">Retry</button>
        </div>

        <!-- History List -->
        <div v-else-if="historyEntries.length > 0" class="history-list">
          <div v-for="entry in historyEntries" :key="entry.id" class="history-entry">
            <div class="history-icon" :class="getEventTypeClass(entry.eventType)">
              <component :is="getEventIconComponent(entry.eventType)" />
            </div>
            <div class="history-details">
              <div class="history-event">
                <span class="event-type">{{ formatEventTitle(entry.eventType) }}</span>
                <span v-if="entry.notificationSent" class="discord-pill">
                  <PhDiscordLogo :size="14" />
                  Notified
                </span>
              </div>
              <div v-if="entry.message" class="history-message">{{ entry.message }}</div>
              <div class="history-time">{{ formatHistoryTime(entry.timestamp) }}</div>
            </div>
          </div>
        </div>

        <!-- Empty State -->
        <div v-else class="empty-history">
          <PhClockCounterClockwise />
          <p>No history available</p>
          <p class="hint">Activity for this audiobook will appear here</p>
        </div>
      </div>
    </div>

    <DeleteConfirmationModal :visible="showDeleteDialog" title="Delete Audiobook" @close="cancelDelete"
      @confirm="executeDelete">
      <template #default>
        <p>Are you sure you want to delete <strong>{{ audiobook.title }}</strong>?</p>
        <p class="warning-text">This action cannot be undone. The audiobook data and cached images will be permanently
          removed.</p>
      </template>
    </DeleteConfirmationModal>
  </div>

  <!-- Loading State -->
  <div v-else-if="loading" class="loading-container">
    <PhSpinner class="ph-spin" />
    <p>Loading audiobook details...</p>
  </div>

  <!-- Error State -->
  <div v-else-if="error" class="error-container">
    <PhWarningCircle />
    <h2>Error Loading Audiobook</h2>
    <p>{{ error }}</p>
    <button @click="goBack" class="back-btn">
      <PhArrowLeft />
      Back to Library
    </button>
  </div>

  <!-- Edit Audiobook Modal -->
  <EditAudiobookModal :is-open="showEditModal" :audiobook="audiobook" @close="closeEditModal"
    @saved="handleEditSaved" />

  <!-- Manual Search Modal -->
  <ManualSearchModal
    :is-open="showManualSearchModal"
    :audiobook="audiobook"
    @close="closeManualSearch"
    @downloaded="handleDownloaded"
  />

</template>

<script setup lang="ts">
import { ref, onMounted, onUnmounted, watch, computed, nextTick, type Component } from 'vue'
import { useToast } from '@/services/toastService'
import type { Audiobook as AudiobookType } from '@/types'
import { useRoute, useRouter } from 'vue-router'
import { useLibraryStore } from '@/stores/library'
import { useConfigurationStore } from '@/stores/configuration'
import { useRootFoldersStore } from '@/stores/rootFolders'
import { apiService, ensureImageCached } from '@/services/api'
import { handleImageError } from '@/utils/imageFallback'
import { getPlaceholderUrl } from '@/utils/placeholder'
import { joinPaths, isAbsolutePath } from '@/utils/path'
import { observeLazyImages, ensureVisibleImagesLoad } from '@/utils/lazyLoad'
import { signalRService } from '@/services/signalr'
import type { Audiobook, AudiobookExternalIdentifier, History, SearchResult } from '@/types'
import { safeText, stripHtmlAndNormalize } from '@/utils/textUtils'
import { logger } from '@/utils/logger'
import { errorTracking } from '@/services/errorTracking'
import { useProtectedImages } from '@/composables/useProtectedImages'
import EditAudiobookModal from '@/components/domain/audiobook/EditAudiobookModal.vue'
import ManualSearchModal from '@/components/domain/search/ManualSearchModal.vue'
import CustomSelect from '@/components/form/CustomSelect.vue'
import DeleteConfirmationModal from '@/components/feedback/DeleteConfirmationModal.vue'
import { Pill } from '@/components/base'
import {
  PhArrowLeft,
  PhArrowClockwise,
  PhBookmark,
  PhSpinner,
  PhMagnifyingGlass,
  PhFolderOpen,
  PhTrash,
  PhClock,
  PhFolder,
  PhDatabase,
  PhSpeakerHigh,
  PhGlobe,
  PhTag,
  PhBookmarkSimple,
  PhStar,
  PhChatCircle,
  PhMusicNotes,
  PhInfo,
  PhFile,
  PhClockCounterClockwise,
  PhFileAudio,
  PhCaretDown,
  PhFileDashed,
  PhWarningCircle,
  PhPlusCircle,
  PhDownload,
  PhUpload,
  PhPencil,
  PhHandGrabbing,
  PhFilePlus,
  PhFileMinus,
  PhCircle,
  PhDiscordLogo,
} from '@phosphor-icons/vue'

const route = useRoute()
const router = useRouter()
const libraryStore = useLibraryStore()
const configStore = useConfigurationStore()
const rootFoldersStore = useRootFoldersStore()
const { getProtectedImageSrc } = useProtectedImages()

type DetailTab = 'details' | 'files' | 'history'

const audiobook = ref<Audiobook | null>(null)
const loading = ref(true)
const error = ref<string | null>(null)
const activeTab = ref<DetailTab>('details')
const showDeleteDialog = ref(false)
const showManualSearchModal = ref(false)
const deleting = ref(false)
const showFullDescription = ref(false)
const scanning = ref(false)
const rescanningMetadata = ref(false)
const scanQueued = ref(false)
const scanJobId = ref<string | null>(null)
const showEditModal = ref(false)
const showMoreActions = ref(false)

// History state
const historyEntries = ref<History[]>([])
const historyLoading = ref(false)
const historyError = ref<string | null>(null)
const qualityProfiles = ref<import('@/types').QualityProfile[]>([])
const expandedFileAccordions = ref<Set<number>>(new Set())

// Mobile tab options for CustomSelect
const mobileTabOptions = computed(() => [
  { value: 'details', label: 'Details', icon: PhInfo },
  { value: 'files', label: 'Files', icon: PhFile },
  { value: 'history', label: 'History', icon: PhClockCounterClockwise },
])

const topActions = computed<DetailTopAction[]>(() => [
  {
    key: 'refresh',
    label: 'Refresh',
    title: 'Refresh',
    ariaLabel: 'Refresh',
    icon: PhArrowClockwise,
    desktopGroup: 'primary',
    onClick: () => { void refresh() },
  },
  {
    key: 'manual-search',
    label: 'Manual Search',
    title: 'Manual Search',
    ariaLabel: 'Manual Search',
    icon: PhMagnifyingGlass,
    desktopGroup: 'primary',
    onClick: openManualSearch,
  },
  {
    key: 'scan',
    label: scanning.value ? 'Scanning...' : scanQueued.value ? 'Scan queued' : 'Scan Folder',
    title: scanning.value ? 'Scanning...' : scanQueued.value ? 'Scan queued' : 'Scan Folder',
    ariaLabel: 'Scan Folder',
    icon: scanning.value ? PhSpinner : scanQueued.value ? PhClock : PhFolderOpen,
    iconClass: scanning.value ? 'ph-spin' : undefined,
    disabled: scanning.value || scanQueued.value,
    desktopGroup: 'primary',
    onClick: () => { void scanFiles() },
  },
  {
    key: 'monitor',
    label: audiobook.value?.monitored ? 'Monitored' : 'Monitor',
    title: audiobook.value?.monitored ? 'Unmonitor' : 'Monitor',
    ariaLabel: 'Toggle Monitor',
    icon: PhBookmark,
    iconProps: { weight: audiobook.value?.monitored ? 'fill' : 'regular' },
    desktopGroup: 'primary',
    desktopClass: 'primary',
    onClick: toggleMonitored,
  },
  {
    key: 'edit',
    label: 'Edit',
    title: 'Edit',
    ariaLabel: 'Edit',
    icon: PhPencil,
    desktopGroup: 'secondary',
    desktopClass: 'primary',
    onClick: openEditModal,
  },
  {
    key: 'rescan-metadata',
    label: rescanningMetadata.value ? 'Rescanning Metadata...' : 'Rescan Metadata',
    title: rescanningMetadata.value ? 'Rescanning Metadata...' : 'Rescan Metadata',
    ariaLabel: 'Rescan Metadata',
    icon: rescanningMetadata.value ? PhSpinner : PhArrowClockwise,
    iconClass: rescanningMetadata.value ? 'ph-spin' : undefined,
    disabled: rescanningMetadata.value || !audiobook.value,
    desktopGroup: 'secondary',
    onClick: () => { void rescanMetadata() },
  },
  {
    key: 'delete',
    label: 'Delete',
    title: 'Delete',
    ariaLabel: 'Delete',
    icon: PhTrash,
    desktopGroup: 'secondary',
    desktopClass: 'danger delete-btn',
    mobileClass: 'delete',
    onClick: confirmDelete,
  },
])

const primaryTopActions = computed(() => topActions.value.filter((a) => a.desktopGroup === 'primary'))
const secondaryTopActions = computed(() => topActions.value.filter((a) => a.desktopGroup === 'secondary'))

function runTopAction(action: DetailTopAction, closeMoreMenu = false) {
  action.onClick()
  if (closeMoreMenu) {
    showMoreActions.value = false
  }
}

type DetailIdentifierItem = {
  key: string
  type: AudiobookExternalIdentifier['type']
  typeLabel: string
  value: string
  href: string | null
  isPrimary: boolean
}

type DetailTopAction = {
  key: 'refresh' | 'manual-search' | 'scan' | 'monitor' | 'edit' | 'rescan-metadata' | 'delete'
  label: string
  title: string
  ariaLabel: string
  icon: Component
  iconClass?: string
  iconProps?: Record<string, unknown>
  disabled?: boolean
  desktopGroup: 'primary' | 'secondary'
  desktopClass?: string
  mobileClass?: string
  onClick: () => void
}



const assignedProfileName = computed(() => {
  if (!audiobook.value) return null
  const id = audiobook.value.qualityProfileId
  if (!id) return null
  const p = qualityProfiles.value.find((q) => q.id === id)
  return p ? p.name : null
})

const primaryAsin = computed(() => {
  const ids = audiobook.value?.identifiers || []
  const explicitPrimary = ids.find((id) => id.type === 'Asin' && id.isPrimary && id.value?.trim())
  if (explicitPrimary) return explicitPrimary.value.trim()

  const firstAsin = ids.find((id) => id.type === 'Asin' && id.value?.trim())
  if (firstAsin) return firstAsin.value.trim()

  const legacy = (audiobook.value?.asin || '').trim()
  return legacy || null
})

const audimetaSourceUrl = computed(() => {
  const asin = primaryAsin.value
  if (!asin) return null
  return `https://audimeta.de/book/${encodeURIComponent(asin)}`
})

const audibleProductUrl = computed(() => {
  const asin = primaryAsin.value
  if (!asin) return null
  return `https://www.audible.com/pd/${encodeURIComponent(asin)}`
})

const displayIdentifiers = computed<DetailIdentifierItem[]>(() => {
  const book = audiobook.value
  if (!book) return []

  const items: DetailIdentifierItem[] = []
  const seen = new Set<string>()
  let hasPrimaryAsin = false

  const addIdentifier = (
    type: AudiobookExternalIdentifier['type'],
    rawValue: unknown,
    isPrimary = false,
  ) => {
    const value = typeof rawValue === 'string' ? rawValue.trim() : ''
    if (!value) return

    const key = normalizeIdentifierKey(type, value)
    if (seen.has(key)) return
    seen.add(key)

    if (type === 'Asin' && isPrimary) hasPrimaryAsin = true

    items.push({
      key,
      type,
      typeLabel: formatIdentifierType(type),
      value,
      href: getIdentifierHref(type, value),
      isPrimary,
    })
  }

  for (const identifier of book.identifiers || []) {
    addIdentifier(identifier.type, identifier.value, Boolean(identifier.isPrimary))
  }

  if (book.asin) {
    addIdentifier('Asin', book.asin, !hasPrimaryAsin)
  }

  for (const isbn of getLegacyIsbnValues(book.isbn as unknown)) {
    addIdentifier('Isbn', isbn)
  }

  if (book.openLibraryId) {
    addIdentifier('OpenLibraryId', book.openLibraryId)
  }

  return items.sort((a, b) => {
    const orderDelta = getIdentifierSortOrder(a.type) - getIdentifierSortOrder(b.type)
    if (orderDelta !== 0) return orderDelta
    if (a.isPrimary !== b.isPrimary) return a.isPrimary ? -1 : 1
    return a.value.localeCompare(b.value)
  })
})

// Utility function to capitalize first letter
const capitalizeFirst = (str: string | undefined): string => {
  if (!str) return ''
  return str.charAt(0).toUpperCase() + str.slice(1).toLowerCase()
}

// Computed property for cover image URL
const coverImageUrl = computed(() => {
  return getProtectedImageSrc(
    audiobook.value?.imageUrl,
    `audiobook-detail-${audiobook.value?.id ?? 'none'}`,
    getPlaceholderUrl(),
  )
})

// Show a base path even when no files exist yet by falling back to configured default root folder
const displayBasePath = computed(() => {
  // Prefer server-provided basePath
  const server = audiobook.value?.basePath
  if (server && server.length > 0) return server

  const settings = configStore.applicationSettings
  if (!settings) return ''

  // Use default root folder path, fallback to legacy outputPath
  const defaultRoot = rootFoldersStore.defaultFolder
  const root = (defaultRoot?.path || settings.outputPath || '').trim()
  const pattern = (settings.folderNamingPattern || settings.fileNamingPattern || '').trim()
  if (!root || !pattern) return root || ''

  const author =
    audiobook.value?.authors && audiobook.value.authors[0]
      ? audiobook.value.authors[0]
      : 'Unknown Author'
  const series = audiobook.value?.series || ''
  const title = audiobook.value?.title || 'Unknown Title'
  const year = audiobook.value?.publishYear || ''
  const seriesNumber = audiobook.value?.seriesNumber || ''

  // Basic variable replacement mirroring server pattern keys
  let relative = pattern
    .replace(/\{Author(?::[^}]+)?\}/gi, sanitizePathComponent(author))
    .replace(/\{Series(?::[^}]+)?\}/gi, sanitizePathComponent(series))
    .replace(/\{Title(?::[^}]+)?\}/gi, sanitizePathComponent(title))
    .replace(/\{Year(?::[^}]+)?\}/gi, year)
    .replace(/\{SeriesNumber(?::[^}]+)?\}/gi, seriesNumber)

  // Remove file-level variables (Disk/Chapter/Quality) if present
  relative = relative
    .replace(/\{DiskNumber(?::[^}]+)?\}/gi, '')
    .replace(/\{ChapterNumber(?::[^}]+)?\}/gi, '')
    .replace(/\{Quality(?::[^}]+)?\}/gi, '')

  // Normalize repeated slashes and trim
  relative = relative.replace(/[\\/]{2,}/g, '/').replace(/^\/+|\/+$/g, '')

  const combined = joinPaths(root, relative)
  // Base path should be the directory containing the files -> strip the last segment
  const parts = combined.split(/[/\\]+/).filter(Boolean)
  if (parts.length <= 1) return combined
  const dir = parts.slice(0, -1).join('/')
  return dir
})

function sanitizePathComponent(s?: string): string {
  if (!s) return 'Unknown'
  // Replace invalid filename chars with underscore
  return s.replace(/[\\/:*?"<>|]/g, '_').trim() || 'Unknown'
}

function getLegacyIsbnValues(raw: unknown): string[] {
  if (Array.isArray(raw)) {
    return raw
      .map((value) => (typeof value === 'string' ? value.trim() : ''))
      .filter(Boolean)
  }

  if (typeof raw !== 'string') return []

  return raw
    .split(',')
    .map((value) => value.trim())
    .filter(Boolean)
}

function formatIdentifierType(type: AudiobookExternalIdentifier['type']): string {
  if (type === 'Asin') return 'ASIN'
  if (type === 'Isbn') return 'ISBN'
  return 'Open Library'
}

function getIdentifierSortOrder(type: AudiobookExternalIdentifier['type']): number {
  if (type === 'Asin') return 0
  if (type === 'Isbn') return 1
  return 2
}

function normalizeIdentifierKey(type: AudiobookExternalIdentifier['type'], value: string): string {
  const normalizedValue = type === 'Isbn'
    ? value.replace(/[-\s]/g, '').toUpperCase()
    : value.trim().toUpperCase()
  return `${type}:${normalizedValue}`
}

function getIdentifierHref(type: AudiobookExternalIdentifier['type'], value: string): string | null {
  if (type === 'Asin') {
    return `https://www.audible.com/pd/${encodeURIComponent(value)}`
  }

  if (type === 'OpenLibraryId') {
    const trimmed = value.trim()
    if (!trimmed) return null
    if (/^https?:\/\//i.test(trimmed)) return trimmed
    const normalized = trimmed.replace(/^\/+/, '')
    return `https://openlibrary.org/books/${encodeURIComponent(normalized)}`
  }

  return null
}

function normalizeDetailTabCandidate(value: unknown): DetailTab | null {
  if (typeof value !== 'string') return null
  const normalized = value.trim().toLowerCase()
  if (normalized === 'downloads') return 'history'
  if (normalized === 'details' || normalized === 'files' || normalized === 'history') {
    return normalized
  }
  return null
}

function syncActiveTabFromRoute() {
  const fromQuery = normalizeDetailTabCandidate(route.query?.tab)
  if (fromQuery) {
    activeTab.value = fromQuery
    return
  }

  const fromHash = normalizeDetailTabCandidate((route.hash || '').replace(/^#/, ''))
  if (fromHash) {
    activeTab.value = fromHash
  }
}

// Watch for tab changes to load history when needed
watch(activeTab, async (newTab) => {
  if (newTab === 'history' && audiobook.value && historyEntries.value.length === 0) {
    await loadHistory()
  }
  try {
    history.replaceState(null, '', `#${newTab}`)
  } catch { }
})

// Handle dropdown tab change
// const onTabChange = (event: Event) => {
//   const target = event.target as HTMLSelectElement
//   const newTab = target.value as 'details' | 'files' | 'history'
//   activeTab.value = newTab
// }

let audiobookUpdateUnsub: (() => void) | null = null

onMounted(async () => {
  syncActiveTabFromRoute()
  document.addEventListener('click', handleClickOutside)

  await loadAudiobook()

  // Setup lazy loading for images
  await nextTick()
  observeLazyImages()
  ensureVisibleImagesLoad()

  // subscribe to scan job updates
  signalRService.onScanJobUpdate((job) => {
    if (!audiobook.value) return
    if (String(job.audiobookId) !== String(audiobook.value.id)) return
    // update local job state
    scanJobId.value = job.jobId
    scanQueued.value = job.status === 'Queued' || job.status === 'Processing'
    // if completed or failed, clear queued flag when appropriate
    if (job.status === 'Completed' || job.status === 'Failed') {
      // small delay to allow AudiobookUpdate to arrive and merge files
      setTimeout(() => {
        scanQueued.value = false
      }, 500)
    }
  })

  // subscribe to AudiobookUpdate messages and merge detail when this audiobook is updated (e.g., after a move)
  audiobookUpdateUnsub = signalRService.onAudiobookUpdate(async (updated) => {
    if (!audiobook.value) return
    const updatedAudiobook = updated as unknown as import('@/types').Audiobook | undefined
    if (!updatedAudiobook || String(updatedAudiobook.id) !== String(audiobook.value.id)) return

    // Merge server-provided audiobook fields into local detail object to update instantly without reloading
    try {
      const upd = updated as unknown as import('@/types').Audiobook
      const prev = audiobook.value
      if (!prev) return

      // Create merged object, preferring server values when provided
      const merged = { ...prev, ...upd }

      // Replace files array only when server provides non-empty array (prevents accidental clearing)
      if (upd.files && upd.files.length > 0) {
        merged.files = upd.files
      }

      // Preserve basePath if server omitted it or sent empty
      if ((!('basePath' in upd) || !upd.basePath) && prev.basePath) {
        merged.basePath = prev.basePath
      }

      // Apply merged object reactively
      audiobook.value = merged
    }
    catch {
      // Fallback: if merge fails, try a full reload
      setTimeout(async () => { try { await loadAudiobook() } catch { } }, 250)
    }
  })
})

function handleClickOutside() {
  if (showMoreActions.value) {
    showMoreActions.value = false
  }
}

onUnmounted(() => {
  document.removeEventListener('click', handleClickOutside)
  try {
    if (audiobookUpdateUnsub) audiobookUpdateUnsub()
  } catch { }
})

watch(
  () => [route.hash, route.query?.tab],
  () => {
    syncActiveTabFromRoute()
  },
)



async function loadAudiobook() {
  loading.value = true
  error.value = null

  try {
    const id = parseInt(route.params.id as string)
    let loadedBook: Audiobook | null = null

    // Prefer the dedicated detail endpoint when available.
    if (typeof apiService.getAudiobook === 'function') {
      try {
        loadedBook = await apiService.getAudiobook(id)
      } catch (apiErr) {
        logger.debug('Detail endpoint load failed, falling back to library store', apiErr)
      }
    }

    if (!loadedBook) {
      // Fallback path for tests / older mocks / endpoint failures
      if (libraryStore.audiobooks.length === 0) {
        await libraryStore.fetchLibrary()
      }
      loadedBook = libraryStore.audiobooks.find((b) => b.id === id) || null
    }

    if (loadedBook) {
      audiobook.value = loadedBook
      await afterLoad()
    } else {
      error.value = 'Audiobook not found'
    }
  } catch (err) {
    error.value = err instanceof Error ? err.message : 'Failed to load audiobook'
    errorTracking.captureException(err as Error, {
      component: 'AudiobookDetailView',
      operation: 'loadAudiobook',
      metadata: { audiobookId: route.params.id },
    })
  } finally {
    loading.value = false
  }
}

// After loading audiobook, also fetch quality profiles so we can display the assigned profile
async function afterLoad() {
  await loadQualityProfilesForDetail()
  await loadIdentifiersForDetail()
  try {
    const img = audiobook.value?.imageUrl
    if (img) {
      const url = apiService.getImageUrl(img)
      if (url && url.includes('/api/images/')) {
        // fire-and-forget: ensure backend cached copy exists for this image
        void ensureImageCached(url).catch(() => { })
      }
    }
  } catch { }
}



async function loadQualityProfilesForDetail() {
  try {
    qualityProfiles.value = await apiService.getQualityProfiles()
  } catch (err) {
    logger.warn('Failed to load quality profiles for detail view:', err)
  }
}

async function loadIdentifiersForDetail() {
  const id = audiobook.value?.id
  if (!id || typeof apiService.getAudiobookIdentifiers !== 'function') return

  try {
    const response = await apiService.getAudiobookIdentifiers(id)
    if (!audiobook.value || audiobook.value.id !== id) return
    audiobook.value = {
      ...audiobook.value,
      identifiers: Array.isArray(response?.identifiers) ? response.identifiers : [],
    }
  } catch (err) {
    logger.debug('Failed to load audiobook identifiers for detail view', err)
  }
}

function goBack() {
  router.push('/audiobooks')
}

async function refresh() {
  await loadAudiobook()
  // Reload history if history tab is active
  if (activeTab.value === 'history') {
    await loadHistory()
  }
}

async function rescanMetadata() {
  if (!audiobook.value || rescanningMetadata.value) return

  rescanningMetadata.value = true
  const toast = useToast()
  try {
    const response = await apiService.rescanAudiobookMetadata(audiobook.value.id)
    await loadAudiobook()

    const details: string[] = []
    if (response?.source) details.push(`Source: ${response.source}`)
    if (response?.asin) details.push(`ASIN: ${response.asin}`)

    toast.success(
      'Metadata rescanned',
      details.length > 0 ? details.join(' • ') : 'Audiobook metadata refreshed successfully.',
    )
  } catch (err) {
    errorTracking.captureException(err as Error, {
      component: 'AudiobookDetailView',
      operation: 'rescanMetadata',
      metadata: { audiobookId: audiobook.value?.id },
    })
    toast.error('Metadata rescan failed', err instanceof Error ? err.message : String(err))
  } finally {
    rescanningMetadata.value = false
  }
}

async function loadHistory() {
  if (!audiobook.value) return

  historyLoading.value = true
  historyError.value = null

  try {
    historyEntries.value = await apiService.getHistoryByAudiobookId(audiobook.value.id)
    logger.debug('Loaded history:', historyEntries.value)
  } catch (err) {
    historyError.value = err instanceof Error ? err.message : 'Failed to load history'
    logger.error('Failed to load history:', err)
  } finally {
    historyLoading.value = false
  }
}

function openManualSearch() {
  showManualSearchModal.value = true
}

function closeManualSearch() {
  showManualSearchModal.value = false
}

function handleDownloaded(result: SearchResult) {
  logger.debug('Download initiated from manual search:', result.title)
  const toast = useToast()
  toast.success('Download Added', `${result.title} has been sent to your download client`)
  closeManualSearch()
}

async function scanFiles() {
  if (!audiobook.value) return
  scanning.value = true
  scanQueued.value = false
  scanJobId.value = null
  try {
    const res = (await apiService.scanAudiobook(audiobook.value.id)) as {
      message: string
      scannedPath?: string
      found: number
      created: number
      audiobook?: AudiobookType
      jobId?: string
    }
    logger.debug('Scan result:', res)
    // If backend enqueued the job it will return 202 Accepted with { jobId }
    if (res?.jobId) {
      scanQueued.value = true
      scanJobId.value = res.jobId
      // keep scanning spinner off - queued state shows separately
    }

    // If API returned updated audiobook (blocking fallback), apply it
    if (res?.audiobook) {
      audiobook.value = res.audiobook
    } else if (!scanQueued.value) {
      // If neither queued nor audiobook returned, refresh to pick up any changes
      await loadAudiobook()
    }
  } catch (err) {
    errorTracking.captureException(err as Error, {
      component: 'AudiobookDetailView',
      operation: 'scanFiles',
      metadata: { audiobookId: audiobook.value?.id },
    })
    // Show a non-blocking toast instead of an alert
    const toast = useToast()
    toast.error('Scan failed', err instanceof Error ? err.message : String(err))
  } finally {
    scanning.value = false
  }
}

// Watch library store for updates (SignalR pushes) and refresh audiobook object reactively
watch(
  () => libraryStore.audiobooks,
  () => {
    if (!audiobook.value) return
    const updated = libraryStore.audiobooks.find((b) => b.id === audiobook.value!.id)
    if (updated) {
      // Merge fields to preserve reactivity where possible
      audiobook.value = { ...audiobook.value, ...updated }
      // If files were added, clear queued indicators
      if (scanQueued.value && updated.files && updated.files.length > 0) {
        scanQueued.value = false
        scanJobId.value = null
      }
    }
  },
  { deep: true },
)

function toggleMonitored() {
  if (audiobook.value) {
    const newMonitoredValue = !audiobook.value.monitored
    audiobook.value = { ...audiobook.value, monitored: newMonitoredValue }

    // Persist to API
    apiService
      .updateAudiobook(audiobook.value.id, { monitored: newMonitoredValue })
      .then(() => {
        logger.debug('Monitored status updated successfully')
      })
      .catch((err) => {
        logger.error('Failed to update monitored status:', err)
        // Revert on error
        if (audiobook.value) {
          audiobook.value = { ...audiobook.value, monitored: !newMonitoredValue }
        }
      })
  }
}

function confirmDelete() {
  showDeleteDialog.value = true
}

function cancelDelete() {
  showDeleteDialog.value = false
}

async function executeDelete() {
  if (!audiobook.value) return

  deleting.value = true
  try {
    const success = await libraryStore.removeFromLibrary(audiobook.value.id)
    if (success) {
      // Navigate back to library after successful deletion
      router.push('/audiobooks')
    }
  } catch (err) {
    errorTracking.captureException(err as Error, {
      component: 'AudiobookDetailView',
      operation: 'executeDelete',
      metadata: { audiobookId: audiobook.value?.id },
    })
  } finally {
    deleting.value = false
    showDeleteDialog.value = false
  }
}

function openEditModal() {
  showEditModal.value = true
}

function closeEditModal() {
  showEditModal.value = false
}

async function handleEditSaved() {
  // Refresh the audiobook data after edit
  await loadAudiobook()
}



function formatRuntime(minutes: number): string {
  const totalMinutes = Math.floor(minutes)
  const hours = Math.floor(totalMinutes / 60)
  const mins = totalMinutes % 60
  return `${hours}h ${mins}m`
}

function formatFileSize(bytes?: number): string {
  if (!bytes || bytes === 0) return 'Unknown'

  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let size = bytes
  let unitIndex = 0

  while (size >= 1024 && unitIndex < units.length - 1) {
    size /= 1024
    unitIndex++
  }

  return `${size.toFixed(1)} ${units[unitIndex]}`
}

function formatHistoryTime(timestamp: string): string {
  const date = new Date(timestamp)
  const now = new Date()
  const diffMs = now.getTime() - date.getTime()
  const diffMins = Math.floor(diffMs / 60000)
  const diffHours = Math.floor(diffMins / 60)
  const diffDays = Math.floor(diffHours / 24)

  if (diffMins < 1) return 'Just now'
  if (diffMins < 60) return `${diffMins} minute${diffMins !== 1 ? 's' : ''} ago`
  if (diffHours < 24) return `${diffHours} hour${diffHours !== 1 ? 's' : ''} ago`
  if (diffDays < 7) return `${diffDays} day${diffDays !== 1 ? 's' : ''} ago`

  return date.toLocaleDateString('en-US', {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  })
}

function getEventIconComponent(eventType: string): Component {
  const icons: Record<string, Component> = {
    Added: PhPlusCircle,
    Downloaded: PhDownload,
    Imported: PhUpload,
    Deleted: PhTrash,
    Updated: PhPencil,
    Monitored: PhBookmark,
    Unmonitored: PhBookmarkSimple,
    Grabbed: PhHandGrabbing,
    Failed: PhWarningCircle,
    'File Added': PhFilePlus,
    'File Removed': PhFileMinus,
  }
  return icons[eventType] || PhCircle
}

function getEventTypeClass(eventType: string): string {
  const classes: Record<string, string> = {
    Added: 'event-success',
    Downloaded: 'event-success',
    Imported: 'event-info',
    Deleted: 'event-danger',
    Updated: 'event-info',
    Monitored: 'event-info',
    Unmonitored: 'event-warning',
    Grabbed: 'event-info',
    Failed: 'event-danger',
    'File Added': 'event-success',
    'File Removed': 'event-warning',
  }
  return classes[eventType] || 'event-default'
}

function formatEventTitle(eventType: string): string {
  const titles: Record<string, string> = {
    Added: 'Added to Library',
    Downloaded: 'Downloaded',
    Imported: 'Imported',
    Deleted: 'Deleted from Library',
    Updated: 'Updated',
    Monitored: 'Monitoring Enabled',
    Unmonitored: 'Monitoring Disabled',
    Grabbed: 'Download Started',
    Failed: 'Failed',
    'File Added': 'File Added',
    'File Removed': 'File Removed',
  }
  return titles[eventType] || eventType
}

function getFileName(filePath?: string): string {
  if (!filePath) return 'Unknown'
  const parts = filePath.split(/[\\/]/)
  const fileName = parts[parts.length - 1]
  return fileName || 'Unknown'
}

function formatDuration(seconds?: number): string {
  if (!seconds || seconds <= 0) return ''
  const sec = Math.floor(seconds)
  const hrs = Math.floor(sec / 3600)
  const mins = Math.floor((sec % 3600) / 60)
  const s = sec % 60
  if (hrs > 0) return `${hrs}h ${mins}m ${s}s`
  if (mins > 0) return `${mins}m ${s}s`
  return `${s}s`
}

function isFileAccordionExpanded(fileId: number): boolean {
  return expandedFileAccordions.value.has(fileId)
}

function toggleFileAccordion(fileId: number): void {
  if (expandedFileAccordions.value.has(fileId)) {
    expandedFileAccordions.value.delete(fileId)
  } else {
    expandedFileAccordions.value.add(fileId)
  }
}

function getFullPath(relativePath?: string): string {
  if (!relativePath) return 'Unknown'

  const isAbsolute = isAbsolutePath(relativePath)
  if (isAbsolute) return relativePath
  if (!audiobook.value?.basePath) return relativePath
  return joinPaths(audiobook.value.basePath, relativePath)
}

function formatDate(dateString?: string): string {
  if (!dateString) return 'Unknown'
  // If the string already includes a timezone (Z or ±HH:MM), parse as-is
  const hasTimezone = /[zZ]|[+-]\d{2}:?\d{2}$/.test(dateString)
  const date = new Date(hasTimezone ? dateString : `${dateString}Z`)
  if (Number.isNaN(date.getTime())) return 'Unknown'
  return date.toLocaleDateString('en-US', {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
    timeZone: 'UTC',
  })
}
</script>

<style scoped>
.audiobook-detail {
  min-height: 100vh;
  background-color: #1a1a1a;
  padding-top: 60px;
  /* Add padding to account for fixed local nav */
}

.top-nav {
  position: fixed;
  top: 60px;
  /* Account for global header nav */
  left: 200px;
  /* Account for sidebar width */
  right: 0;
  z-index: 99;
  /* Below global nav (1000) but above content */
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 12px 20px;
  background-color: #2a2a2a;
  border-bottom: 1px solid #333;
}

@media (max-width: 768px) {
  .top-nav {
    left: 0;
    /* Full width on mobile */
  }
}

.nav-actions {
  display: flex;
  gap: 8px;
  align-items: center;
  flex-wrap: wrap;
}

/* Desktop alignment tweaks: ensure primary and secondary actions line up and align to the right */
@media (min-width: 769px) {
  .nav-actions {
    align-items: center;
    display: flex;
    gap: 8px;
    flex-wrap: nowrap;
  }

  .primary-actions {
    display: flex;
    gap: 8px;
    align-items: center;
  }

  .secondary-actions {
    display: flex;
    gap: 12px;
    align-items: center;
  }

  .more-wrapper {
    display: inline-flex;
    align-items: center;
  }
}

/* Desktop: tighter, consistent sizing and ordering for nav buttons */
@media (min-width: 769px) {
  .top-nav {
    padding: 12px 20px;
  }

  /* Ensure nav-actions stays on the right and items don't wrap */
  .nav-actions {
    margin-left: auto;
    display: flex;
    gap: 8px;
    align-items: center;
    flex-wrap: nowrap;
  }

  /* Make each button a uniform height and inline-flex for better baseline alignment */
  .nav-actions .nav-btn {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    height: 36px;
    padding: 8px 12px;
    white-space: nowrap;
  }

  /* Icon-only nav buttons (use .icon-button) */
  .nav-actions .nav-btn.icon-button {
    padding: 0;
    width: 36px;
    height: 36px;
    gap: 0;
    justify-content: center;
  }

  /* Remove extra margins */
  .primary-actions {
    margin-right: 0;
  }

  .secondary-actions {
    margin-left: 0;
  }

  /* Make delete button always appear last and slightly emphasized */
  .secondary-actions .delete-btn {
    order: 99;
    padding-left: 10px;
    padding-right: 10px;
  }
}

.nav-btn {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 8px 12px;
  background-color: #3a3a3a;
  border: 1px solid #555;
  border-radius: 6px;
  color: #fff;
  font-size: 13px;
  cursor: pointer;
  transition: background-color 0.2s;
}

.nav-btn:hover {
  background-color: #4a4a4a;
}

.nav-btn.delete-btn {
  background-color: #e74c3c;
  border-color: #c0392b;
}

.nav-btn.delete-btn:hover {
  background-color: #c0392b;
}

.nav-btn.debug-btn {
  background-color: #5865f2;
  border-color: #4752c4;
}

.nav-btn.debug-btn:hover {
  background-color: #4752c4;
}

/* Test Menu Styles */
.test-menu-container {
  position: relative;
}

.test-menu-btn {
  background-color: #5865f2;
  border-color: #4752c4;
}

.test-menu-btn:hover {
  background-color: #4752c4;
}

.test-dropdown {
  position: absolute;
  top: 100%;
  right: 0;
  margin-top: 4px;
  background-color: #2a2a2a;
  border: 1px solid #555;
  border-radius: 6px;
  box-shadow: 0 4px 12px rgba(0, 0, 0, 0.3);
  min-width: 180px;
  z-index: 100;
}

/* More dropdown (mobile) should be absolutely positioned so it doesn't expand the top-nav */
.more-wrapper {
  position: relative;
}

.more-dropdown {
  position: absolute;
  top: calc(100% + 6px);
  right: 0;
  margin-top: 4px;
  background-color: #2a2a2a;
  border: 1px solid #555;
  border-radius: 6px;
  box-shadow: 0 6px 18px rgba(0, 0, 0, 0.35);
  min-width: 200px;
  z-index: 1100;
  display: flex;
  flex-direction: column;
}

.more-dropdown .dropdown-item {
  border-radius: 6px;
}

.dropdown-item {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  padding: 10px 14px;
  background: none;
  border: none;
  color: #fff;
  font-size: 13px;
  cursor: pointer;
  transition: background-color 0.2s;
  text-align: left;
}

.dropdown-item:first-child {
  border-radius: 6px;
}

.dropdown-item:last-child {
  border-radius: 6px;
}

.dropdown-item:hover:not(:disabled) {
  background-color: #3a3a3a;
}

.dropdown-item:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

/* Webhook Selector Modal */
.webhook-selector-modal {
  max-width: 500px;
}

.modal-description {
  margin-bottom: 16px;
  color: #aaa;
  font-size: 14px;
}

.webhook-list {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.webhook-item {
  display: flex;
  align-items: center;
  gap: 12px;
  width: 100%;
  padding: 14px 16px;
  background-color: #2a2a2a;
  border: 1px solid #555;
  border-radius: 6px;
  color: #fff;
  font-size: 14px;
  cursor: pointer;
  transition: all 0.2s;
  text-align: left;
}

.webhook-item:hover:not(:disabled) {
  background-color: #3a3a3a;
  border-color: #5865f2;
  transform: translateX(4px);
}

.webhook-item:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.webhook-name {
  flex: 1;
  font-weight: 500;
}

.hero-section {
  position: relative;
  padding: 40px 40px;
  overflow: hidden;
}

@media (max-width: 768px) {
  .hero-section {
    padding: 40px 20px;
  }
}

.backdrop {
  position: absolute;
  top: 0;
  left: 0;
  right: 0;
  bottom: 0;
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

@media (min-width: 1200px) {
  .hero-content {
    gap: 40px;
  }
}

@media (max-width: 768px) {
  .hero-content {
    flex-direction: column;
    gap: 20px;
  }
}

.poster-container {
  flex-shrink: 0;
}

@media (max-width: 768px) {
  .poster-container {
    margin: 0 auto;
  }
}

.poster {
  width: 350px;
  height: 350px;
  object-fit: cover;
  border-radius: 6px;
  box-shadow: 0 8px 32px rgba(0, 0, 0, 0.6);
}

@media (max-width: 768px) {
  .poster {
    width: 250px;
    height: 250px;
  }
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
  line-height: 1.2;
}

@media (max-width: 768px) {
  .title {
    font-size: 2rem;
    text-align: center;
  }
}

.subtitle {
  font-size: 1.4rem;
  color: #ccc;
  margin-bottom: 20px;
}

@media (max-width: 768px) {
  .subtitle {
    font-size: 1rem;
    text-align: center;
  }
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
  gap: 4px;
}

.runtime i,
.rating i {
  color: var(--brand-500);
}

@media (max-width: 768px) {
  .meta-info {
    justify-content: center;
  }
}

.file-path {
  padding: 2px 6px;
  border-radius: 6px;
  font-size: 13px;
  color: #aaa;
}

.key-details {
  display: flex;
  gap: 12px;
  margin-bottom: 20px;
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

.detail-item span {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.detail-item i {
  color: var(--brand-500);
}

/* Status badges - Now using Pill component from @/components/base */
.status-badges {
  display: flex;
  gap: 8px;
  margin-bottom: 20px;
  flex-wrap: wrap;
}

.description {
  color: #ccc;
  line-height: 1.6;
  max-width: 900px;
  position: relative;
}

.description-content {
  position: relative;
  max-height: 140px;
  overflow: hidden;
  transition: max-height 0.3s ease;
  white-space: pre-wrap;
}

@media (max-width: 768px) {
  .description-content {
    max-height: 100px;
  }
}

.description-content:not(.expanded)::after {
  content: '';
  position: absolute;
  bottom: 0;
  left: 0;
  right: 0;
  height: 40px;
  pointer-events: none;
}

.description-content:not(.expanded) {
  mask-image: linear-gradient(to bottom, white 70%, transparent 100%);
  -webkit-mask-image: linear-gradient(to bottom, white 70%, transparent 100%);
}

.description-content.expanded {
  max-height: none;
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

.description :deep(p) {
  margin: 0 0 12px 0;
}

.description :deep(br) {
  display: block;
  margin: 8px 0;
}

.description :deep(strong),
.description :deep(b) {
  color: #fff;
  font-weight: 500;
}

.description :deep(em),
.description :deep(i) {
  font-style: italic;
}

.description :deep(a) {
  color: var(--brand-500);
}

.description :deep(a:hover) {
  text-decoration: underline;
}

.description :deep(ul),
.description :deep(ol) {
  margin: 12px 0;
  padding-left: 24px;
}

.description :deep(li) {
  margin: 4px 0;
}

.tabs-container {
  background-color: #2a2a2a;
  border-bottom: 1px solid #333;
  padding: 0 40px;
}

@media (max-width: 768px) {
  .tabs-container {
    padding: 0 20px;
  }
}

/* Show mobile select and hide desktop tabs where appropriate */
.tabs-mobile {
  display: none;
}

.tabs-desktop {
  display: block;
}

@media (max-width: 768px) {
  .tabs-mobile {
    display: block;
  }

  .tab-dropdown {
    width: 100%;
  }

  .tabs-desktop {
    display: none;
  }

  /* Make top nav buttons wrap and be touch friendly on small screens */
  .top-nav {
    padding: 10px 12px;
    right: 0;
  }

  .nav-actions {
    flex-wrap: wrap;
    gap: 6px;
  }

  .nav-btn {
    padding: 8px 10px;
    min-width: 44px;
  }
}

/* Improved mobile layout for nav actions: keep nav and actions inline on mobile */
@media (max-width: 768px) {
  .top-nav {
    display: flex;
    flex-direction: row;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
    padding: 10px 12px;
  }

  /* Keep the back button prominent but inline with actions on mobile */
  .top-nav>.nav-btn:first-of-type {
    width: auto;
    justify-content: flex-start;
    gap: 10px;
    padding: 10px 12px;
    font-weight: 500;
    min-width: 0;
  }

  /* On mobile hide the primary-actions container (we surface primary actions inside the More menu) */
  .primary-actions {
    display: none;
  }

  /* Make nav-actions size to content so they stay inline with the back button */
  .nav-actions {
    display: grid;
    grid-auto-flow: column;
    grid-auto-columns: auto;
    gap: 8px;
    width: auto;
    align-items: center;
  }

  @media (max-width: 480px) {
    .nav-actions {
      grid-auto-columns: auto;
    }
  }

  .nav-actions .nav-btn {
    width: auto;
    justify-content: center;
    padding: 10px 8px;
    font-size: 14px;
    border-radius: 6px;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  /* Make icon slightly larger to improve affordance */
  .nav-actions .nav-btn svg,
  .top-nav>.nav-btn svg {
    width: 20px;
    height: 20px;
  }

  /* Reduce visual noise for disabled buttons and keep them tappable */
  .nav-actions .nav-btn:disabled {
    opacity: 0.6;
    cursor: not-allowed;
  }
}

.tabs {
  display: flex;
  gap: 4px;
  max-width: 1600px;
  margin: 0 auto;
}

.tab {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 12px 20px;
  background: transparent;
  border: none;
  border-bottom: 2px solid transparent;
  color: #999;
  cursor: pointer;
  transition: all 0.2s;
  font-size: 14px;
}

.tab:hover {
  color: #fff;
}

.tab.active {
  color: var(--brand-500);
  border-bottom-color: var(--brand-500);
}

.tab-content {
  padding: 40px 40px;
  max-width: 1600px;
  margin: 0 auto;
}

@media (max-width: 768px) {
  .tab-content {
    padding: 30px 20px;
  }
}

.details-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(350px, 1fr));
  gap: 24px;
}

@media (min-width: 1200px) {
  .details-grid {
    grid-template-columns: repeat(3, 1fr);
  }
}

@media (max-width: 768px) {
  .details-grid {
    grid-template-columns: 1fr;
  }
}

.detail-card {
  background-color: #2a2a2a;
  border: 1px solid #333;
  border-radius: 6px;
  padding: 20px;
}

.detail-card h3 {
  margin: 0 0 16px 0;
  color: #fff;
  font-size: 16px;
  border-bottom: 1px solid #333;
  padding-bottom: 12px;
}

.detail-row {
  display: flex;
  justify-content: space-between;
  padding: 8px 0;
  border-bottom: 1px solid #333;
}

.detail-row:last-child {
  border-bottom: none;
}

.detail-row .label {
  color: #999;
  font-size: 14px;
}

.detail-row .value {
  color: #fff;
  font-size: 14px;
  text-align: right;
}

.detail-row-stacked {
  align-items: flex-start;
  gap: 12px;
}

.detail-row-stacked .label {
  padding-top: 4px;
}

.detail-row-stacked .value {
  text-align: right;
}

.identifiers-list {
  display: flex;
  flex-direction: column;
  align-items: flex-end;
  gap: 8px;
  max-width: 70%;
}

.identifier-item {
  display: inline-flex;
  align-items: center;
  justify-content: flex-end;
  gap: 8px;
  flex-wrap: wrap;
}

.identifier-type {
  color: #b3b3b3;
  font-size: 12px;
  font-weight: 600;
  letter-spacing: 0.02em;
  text-transform: uppercase;
}

.identifier-link {
  color: #fff;
  font-size: 14px;
  word-break: break-word;
}

a.identifier-link:hover {
  color: var(--brand-300);
}

.identifier-badge {
  display: inline-flex;
  align-items: center;
  padding: 2px 8px;
  border-radius: 999px;
  font-size: 11px;
  font-weight: 600;
}

.identifier-badge.primary {
  background: rgba(59, 130, 246, 0.16);
  border: 1px solid rgba(59, 130, 246, 0.45);
  color: #bfdbfe;
}

.genre-tags {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}

.genre-tag {
  padding: 6px 12px;
  background-color: #3a3a3a;
  border: 1px solid #555;
  border-radius: 6px;
  color: #fff;
  font-size: 12px;
}

.tags-list {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}

.tag-badge {
  display: inline-flex;
  align-items: center;
  padding: 6px 12px;
  background-color: #2a2a2a;
  border: 1px solid #3a3a3a;
  border-radius: 6px;
  color: #e0e0e0;
  font-size: 12px;
  font-weight: 500;
  transition: all 0.2s ease;
}

.tag-badge:hover {
  background-color: #333;
  border-color: var(--brand-500);
  color: white;
}

.files-content,
.history-content {
  background-color: #2a2a2a;
  border: 1px solid #333;
  border-radius: 6px;
  padding: 20px;
}

.files-header,
.history-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 20px;
  padding-bottom: 12px;
  border-bottom: 1px solid #333;
}

.files-header h3,
.history-header h3 {
  margin: 0;
  color: #fff;
}

.action-btn {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 8px 12px;
  background-color: #3a3a3a;
  border: 1px solid #555;
  border-radius: 6px;
  color: #fff;
  font-size: 13px;
  cursor: pointer;
  transition: background-color 0.2s;
}

.action-btn:hover {
  background-color: #4a4a4a;
}

.file-list {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.file-item {
  display: flex;
  flex-direction: column;
  padding: 12px;
  background-color: #333;
  border-radius: 6px;
  transition: all 0.2s ease;
}

.file-item.expanded {
  background-color: #3a3a3a;
}

.file-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  cursor: pointer;
  width: 100%;
}

.file-info {
  display: flex;
  align-items: center;
  gap: 12px;
  color: #fff;
  flex: 1;
}

.file-info i {
  font-size: 24px;
  color: var(--brand-500);
}

.file-name {
  font-weight: 500;
}

.file-meta {
  color: #999;
}

.file-actions {
  display: flex;
  align-items: center;
  gap: 12px;
}

.accordion-toggle {
  color: #999;
  transition: transform 0.2s ease;
  font-size: 16px;
}

.accordion-toggle.rotated {
  transform: rotate(180deg);
}

.file-accordion {
  margin-top: 12px;
  padding-top: 12px;
  border-top: 1px solid #444;
  animation: slideDown 0.2s ease-out;
}

@keyframes slideDown {
  from {
    opacity: 0;
    max-height: 0;
  }

  to {
    opacity: 1;
    max-height: 500px;
  }
}

.metadata-table {
  width: 100%;
  border-collapse: collapse;
  font-size: 14px;
}

.metadata-table tbody tr {
  border-bottom: 1px solid #444;
}

.metadata-table tbody tr:last-child {
  border-bottom: none;
}

.metadata-label {
  color: #999;
  padding: 8px 12px 8px 0;
  font-weight: 500;
  width: 120px;
  vertical-align: top;
}

.metadata-value {
  color: #fff;
  padding: 8px 0;
  word-break: break-word;
}

.file-info {
  display: flex;
  align-items: center;
  gap: 12px;
  color: #fff;
}

.file-info i {
  font-size: 24px;
  color: var(--brand-500);
}

.file-size {
  color: #999;
  font-size: 14px;
}

.empty-history {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  padding: 60px 20px;
  color: #666;
}

.empty-history i {
  font-size: 48px;
  margin-bottom: 12px;
}

.empty-history .hint {
  font-size: 14px;
  color: #555;
  margin-top: 8px;
}

.empty-files {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  padding: 60px 20px;
  color: #666;
}

.empty-files i {
  font-size: 48px;
  margin-bottom: 12px;
}

.empty-files .hint {
  font-size: 14px;
  color: #555;
  margin-top: 8px;
}

/* Mobile-specific refinements to improve layout and prevent overflow */
@media (max-width: 768px) {

  /* Make poster a bit smaller and centered for narrow viewports */
  .poster {
    width: 200px;
    height: 200px;
    margin: 0 auto;
    display: block;
  }

  .hero-content {
    align-items: flex-start;
  }

  .info-section {
    padding: 0 8px;
  }

  /* Allow long titles and metadata to wrap instead of causing horizontal scroll */
  .title {
    word-break: break-word;
    overflow-wrap: anywhere;
  }

  .detail-item span {
    white-space: normal;
    overflow-wrap: anywhere;
    min-width: 0;
  }

  /* Stack metadata table rows on small screens so the table doesn't overflow */
  .metadata-table tbody tr {
    display: block;
    padding: 8px 0;
    border-bottom: 1px solid #444;
  }

  .metadata-label {
    display: block;
    width: auto;
    padding-bottom: 6px;
  }

  .metadata-value {
    display: block;
    padding-bottom: 12px;
    word-break: break-word;
  }

  /* Make file lists and tab content reserve space for scrollbars to avoid layout shifts */
  .file-list,
  .tab-content,
  .search-results-inline {
    scrollbar-gutter: stable;
  }

  /* Ensure dropdowns and test menus sit above the fixed top-nav */
  .test-dropdown,
  .test-menu-container .test-dropdown,
  .test-menu-container .test-dropdown .dropdown-item {
    z-index: 1200;
  }

  /* Tweak top nav spacing for very small screens */
  .nav-actions {
    gap: 8px;
  }

  .nav-btn {
    min-width: 0;
    padding: 8px 10px;
    font-size: 13px;
  }
}

@media (max-width: 480px) {
  .poster {
    width: 160px;
    height: 160px;
  }

  .title {
    font-size: 1.4rem;
  }

  .nav-btn {
    padding: 8px 10px;
    font-size: 12px;
  }
}

/* History Styles */
.history-loading,
.history-error {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  padding: 40px 20px;
  color: #999;
}

.history-loading i {
  font-size: 36px;
  margin-bottom: 12px;
}

.history-error i {
  font-size: 36px;
  margin-bottom: 12px;
  color: #e74c3c;
}

.retry-btn,
.refresh-btn {
  margin-top: 12px;
  padding: 8px 16px;
  background-color: var(--brand-500);
  border: none;
  border-radius: 6px;
  color: #fff;
  cursor: pointer;
  font-size: 14px;
  transition: background-color 0.2s;
}

.retry-btn:hover,
.refresh-btn:hover {
  background-color: #005fa3;
}

.refresh-btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.history-list {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.history-entry {
  display: flex;
  gap: 16px;
  padding: 16px;
  background-color: #333;
  border-radius: 6px;
  border-left: 3px solid #555;
  transition:
    transform 0.2s,
    box-shadow 0.2s;
}

.history-entry:hover {
  transform: translateX(4px);
  box-shadow: 0 4px 12px rgba(0, 0, 0, 0.3);
}

.history-icon {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 40px;
  height: 40px;
  border-radius: 50%;
  flex-shrink: 0;
}

.history-icon i {
  font-size: 20px;
}

.event-success {
  background-color: rgba(46, 204, 113, 0.2);
  color: #2ecc71;
}

.event-info {
  background-color: rgba(52, 152, 219, 0.2);
  color: #3498db;
}

.event-warning {
  background-color: rgba(241, 196, 15, 0.2);
  color: #f1c40f;
}

.event-danger {
  background-color: rgba(231, 76, 60, 0.2);
  color: #e74c3c;
}

.event-default {
  background-color: rgba(149, 165, 166, 0.2);
  color: #95a5a6;
}

.history-details {
  flex: 1;
}

.history-event {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 4px;
}

.event-type {
  font-weight: 500;
  color: #fff;
  font-size: 14px;
}

.discord-pill {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  font-size: 11px;
  color: #5865f2;
  background-color: rgba(88, 101, 242, 0.15);
  padding: 2px 8px;
  border-radius: 6px;
  border: 1px solid rgba(88, 101, 242, 0.3);
  font-weight: 500;
}

.event-source {
  font-size: 12px;
  color: #999;
  padding: 2px 8px;
  background-color: rgba(255, 255, 255, 0.05);
  border-radius: 6px;
}

.history-message {
  color: #ccc;
  font-size: 14px;
  margin-bottom: 8px;
  line-height: 1.4;
}

.history-time {
  color: #777;
  font-size: 12px;
}

.loading-container,
.error-container {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  min-height: 100vh;
  color: #ccc;
  background-color: #1a1a1a;
}

.loading-container i,
.error-container i {
  font-size: 48px;
  margin-bottom: 16px;
}

.loading-container i {
  color: var(--brand-500);
}

.error-container i {
  color: #e74c3c;
}

.error-container h2 {
  color: #fff;
  margin: 0 0 8px 0;
}

.back-btn {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-top: 20px;
  padding: 12px 24px;
  background-color: var(--brand-500);
  border: none;
  border-radius: 6px;
  color: #fff;
  cursor: pointer;
  font-size: 14px;
  transition: background-color 0.2s;
}

.back-btn:hover {
  background-color: #005fa3;
}

/* Delete dialog styling is centralized in `src/assets/modals.css` */
/* Legacy .dialog classes are still used in a few places (e.g., Audiobook detail delete), but visual styles are now centralized. */

/* Ensure visible spacing between secondary action buttons across breakpoints */
.secondary-actions {
  display: flex;
  gap: 0.5rem;
}

/* Keep delete button padding consistent */
.secondary-actions .delete-btn {
  padding-left: 10px;
  padding-right: 10px;
}
</style>
