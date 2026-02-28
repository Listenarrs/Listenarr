<template>
  <div class="collection-view">
    <!-- Top Navigation Bar -->
    <div class="top-nav">
      <div class="nav-title">
        <h1>
          <PhUser v-if="type === 'author'" />
          <PhBooks v-else /> {{ name }}
        </h1>
      </div>
    </div>

    <!-- Top Toolbar -->
    <div class="toolbar">
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
          v-if="audiobooks.length > 0 && selectedCount === 0"
          class="toolbar-btn"
          @click="libraryStore.selectAll()"
        >
          <PhCheckSquare />
          Select All
        </button>
        <button v-if="selectedCount > 0" class="toolbar-btn edit-btn" @click="showBulkEdit">
          <PhPencil />
          Edit Selected
        </button>
        <button v-if="selectedCount > 0" class="toolbar-btn delete-btn" @click="confirmBulkDelete">
          <PhTrash />
          Delete Selected ({{ selectedCount }})
        </button>
      </div>
      <div class="toolbar-right">
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

        <div
          v-for="audiobook in paginatedAudiobooks"
          :key="audiobook.id"
          tabindex="0"
          @keydown.enter="handleRowClick(audiobook)"
          class="audiobook-list-item"
          :class="{
            selected: isSelected(audiobook.id),
            'status-no-file': getAudiobookStatus(audiobook) === 'no-file',
            'status-downloading': getAudiobookStatus(audiobook) === 'downloading',
            'status-quality-mismatch': getAudiobookStatus(audiobook) === 'quality-mismatch',
            'status-quality-match': getAudiobookStatus(audiobook) === 'quality-match',
          }"
          @click="handleRowClick(audiobook)"
        >
          <div
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

          <img
            class="list-thumb"
            :src="getProtectedImageSrc(audiobook.imageUrl, `collection-${audiobook.id}`, getPlaceholderUrl())"
            :alt="audiobook.title"
            loading="lazy"
            decoding="async"
            @error="handleImageError"
          />

          <div class="list-details">
            <div class="audiobook-title">{{ safeText(audiobook.title) }}</div>
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
              v-if="getQualityProfileName(audiobook.qualityProfileId)"
              class="quality-profile-badge"
            >
              <PhStar />
              {{ getQualityProfileName(audiobook.qualityProfileId) }}
            </div>
            <div class="monitored-badge" :class="{ unmonitored: !audiobook.monitored }">
              <component :is="audiobook.monitored ? PhEye : PhEyeSlash" />
              {{ audiobook.monitored ? 'Monitored' : 'Unmonitored' }}
            </div>
          </div>

          <div class="list-actions">
            <button
              class="action-btn edit-btn-small"
              @click.stop="editAudiobook(audiobook)"
              title="Edit"
            >
              <PhPencil />
            </button>
            <button
              class="action-btn delete-btn-small"
              @click.stop="deleteAudiobook(audiobook)"
              title="Delete"
            >
              <PhTrash />
            </button>
          </div>
        </div>
      </div>

      <!-- Grid View -->
      <div v-else class="grid-view">
        <div
          v-for="audiobook in paginatedAudiobooks"
          :key="audiobook.id"
          class="collection-card"
          :class="{
            selected: isSelected(audiobook.id),
            'status-no-file': getAudiobookStatus(audiobook) === 'no-file',
            'status-downloading': getAudiobookStatus(audiobook) === 'downloading',
            'status-quality-mismatch': getAudiobookStatus(audiobook) === 'quality-mismatch',
            'status-quality-match': getAudiobookStatus(audiobook) === 'quality-match',
          }"
          @click="handleCardClick(audiobook)"
        >
          <div
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
            <img
              v-if="audiobook.imageUrl"
              :src="getProtectedImageSrc(audiobook.imageUrl, `collection-${audiobook.id}`, getPlaceholderUrl())"
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
                v-if="getQualityProfileName(audiobook.qualityProfileId)"
                class="quality-profile-badge"
              >
                <PhStar />
                {{ getQualityProfileName(audiobook.qualityProfileId) }}
              </div>
              <div class="monitored-badge" :class="{ unmonitored: !audiobook.monitored }">
                <component :is="audiobook.monitored ? PhEye : PhEyeSlash" />
                {{ audiobook.monitored ? 'Monitored' : 'Unmonitored' }}
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
              class="action-btn edit-btn-small"
              @click.stop="editAudiobook(audiobook)"
              title="Edit"
            >
              <PhPencil />
            </button>
            <button
              class="action-btn delete-btn-small"
              @click.stop="deleteAudiobook(audiobook)"
              title="Delete"
            >
              <PhTrash />
            </button>
          </div>
        </div>
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
      :selected-ids="libraryStore.selectedIds"
      @close="closeBulkEdit"
      @saved="handleBulkEditSaved"
    />

    <EditAudiobookModal
      v-if="editingAudiobook"
      :isOpen="true"
      :audiobook="editingAudiobook"
      @close="editingAudiobook = null"
      @saved="onAudiobookSaved"
    />
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import {
  PhArrowLeft,
  PhUser,
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
} from '@phosphor-icons/vue'
import { useLibraryStore } from '@/stores/library'
import { useConfigurationStore } from '@/stores/configuration'
import { useDownloadsStore } from '@/stores/downloads'
import { apiService } from '@/services/api'
import { errorTracking } from '@/services/errorTracking'
import EditAudiobookModal from '@/components/domain/audiobook/EditAudiobookModal.vue'
import BulkEditModal from '@/components/domain/collection/BulkEditModal.vue'
import { showConfirm } from '@/composables/useConfirm'
import { getPlaceholderUrl } from '@/utils/placeholder'
import CustomSelect from '@/components/form/CustomSelect.vue'
import { EmptyState, LoadingState } from '@/components/base'
import type { Audiobook } from '@/types'
import { safeText } from '@/utils/textUtils'
import { useProtectedImages } from '@/composables/useProtectedImages'

const route = useRoute()
const router = useRouter()
const libraryStore = useLibraryStore()
const configStore = useConfigurationStore()
const downloadsStore = useDownloadsStore()
const { getProtectedImageSrc } = useProtectedImages()

const type = computed(() => route.params.type as string)
const name = computed(() => decodeURIComponent(route.params.name as string))

const viewMode = ref<'grid' | 'list'>('grid')
const showItemDetails = ref(false)
const searchQuery = ref('')
const sortKey = ref('title')
const currentPage = ref(1)
const pageSize = ref(50)
const editingAudiobook = ref<Audiobook | null>(null)

const loading = computed(() => libraryStore.loading)
const error = computed(() => libraryStore.error)

const qualityProfiles = computed(() => configStore.qualityProfiles)

const audiobooks = computed(() => {
  const allBooks = libraryStore.audiobooks
  const filtered = allBooks.filter((book) => {
    if (type.value === 'author') {
      return book.authors?.includes(name.value)
    } else if (type.value === 'series') {
      return book.series === name.value
    }
    return false
  })

  // Apply search
  const searched = filtered.filter((book) =>
    book.title.toLowerCase().includes(searchQuery.value.toLowerCase()),
  )

  // Apply sorting
  return searched.sort((a, b) => {
    const aVal = safeText((a[sortKey.value as keyof Audiobook] as string) || '')
    const bVal = safeText((b[sortKey.value as keyof Audiobook] as string) || '')
    return aVal.localeCompare(bVal)
  })
})

const baseSortOptions = [
  { value: 'title', label: 'Title' },
  { value: 'author', label: 'Author' },
  { value: 'series', label: 'Series' },
  { value: 'added', label: 'Date Added' },
]

const sortOptions = computed(() => {
  return baseSortOptions.filter((o) => {
    if (type.value === 'author' && o.value === 'author') return false
    if (type.value === 'series' && o.value === 'series') return false
    return true
  })
})

// Ensure current sortKey is valid for the current view; reset to title if not
watch(sortOptions, (newOpts) => {
  const vals = newOpts.map((o) => o.value)
  if (!vals.includes(sortKey.value)) {
    sortKey.value = 'title'
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

// Use the library store selection so changes are reactive across the app
const selectedCount = computed(() => libraryStore.selectedIds.size)

const isSelected = (id: number) => libraryStore.isSelected(id)
const toggleSelection = (id: number) => libraryStore.toggleSelection(id)

const toggleViewMode = () => {
  viewMode.value = viewMode.value === 'grid' ? 'list' : 'grid'
}

const toggleItemDetails = () => {
  showItemDetails.value = !showItemDetails.value
}

const showBulkEditModal = ref(false)
const deleting = ref(false)

function showBulkEdit() {
  showBulkEditModal.value = true
}

function closeBulkEdit() {
  showBulkEditModal.value = false
}

async function handleBulkEditSaved() {
  await libraryStore.fetchLibrary()
  libraryStore.clearSelection()
  showBulkEditModal.value = false
}

async function confirmBulkDelete() {
  const count = libraryStore.selectedIds.size
  if (count === 0) return
  const message = `Are you sure you want to delete ${count} audiobook${count !== 1 ? 's' : ''}? This action cannot be undone.`
  const ok = await showConfirm(message, 'Confirm Deletion', {
    danger: true,
    confirmText: 'Delete',
    cancelText: 'Cancel',
  })
  if (!ok) return
  deleting.value = true
  try {
    const idsToDelete = Array.from(libraryStore.selectedIds)
    await libraryStore.bulkRemoveFromLibrary(idsToDelete)
  } catch (err) {
    console.error('Bulk delete failed:', err)
  } finally {
    deleting.value = false
  }
}

const refreshLibrary = async () => {
  await libraryStore.fetchLibrary()
}

const goBack = () => {
  router.back()
}

const lastClickedIndex = ref<number | null>(null)

const handleRowClick = (audiobook: Audiobook) => {
  if (selectedCount.value > 0) {
    toggleSelection(audiobook.id)
  } else {
    router.push(`/audiobooks/${audiobook.id}`)
  }
}

const handleCardClick = (audiobook: Audiobook) => {
  if (selectedCount.value > 0) {
    toggleSelection(audiobook.id)
  } else {
    router.push(`/audiobooks/${audiobook.id}`)
  }
}

function handleCheckboxClick(audiobook: Audiobook, event: MouseEvent) {
  // Prevent default native checkbox toggle; handle selection consistently
  event.preventDefault()

  const currentIndex = audiobooks.value.findIndex((book) => book.id === audiobook.id)
  if (event.shiftKey && lastClickedIndex.value !== null) {
    const start = Math.min(lastClickedIndex.value, currentIndex)
    const end = Math.max(lastClickedIndex.value, currentIndex)
    libraryStore.clearSelection()
    for (let i = start; i <= end; i++) {
      const b = audiobooks.value[i]
      if (b) libraryStore.toggleSelection(b.id)
    }
  } else {
    libraryStore.toggleSelection(audiobook.id)
  }

  lastClickedIndex.value = currentIndex
}

function onCheckboxChange(audiobook: Audiobook, event: Event) {
  const currentIndex = audiobooks.value.findIndex((book) => book.id === audiobook.id)
  const shift = (event as MouseEvent | KeyboardEvent).shiftKey
  if (shift && lastClickedIndex.value !== null) {
    const start = Math.min(lastClickedIndex.value, currentIndex)
    const end = Math.max(lastClickedIndex.value, currentIndex)
    libraryStore.clearSelection()
    for (let i = start; i <= end; i++) {
      const b = audiobooks.value[i]
      if (b) libraryStore.toggleSelection(b.id)
    }
  } else {
    libraryStore.toggleSelection(audiobook.id)
  }

  lastClickedIndex.value = currentIndex
}

const editAudiobook = (audiobook: Audiobook) => {
  editingAudiobook.value = audiobook
}

const deleteAudiobook = async (audiobook: Audiobook) => {
  if (confirm(`Are you sure you want to delete "${audiobook.title}"?`)) {
    await libraryStore.removeFromLibrary(audiobook.id)
  }
}

const onAudiobookSaved = () => {
  editingAudiobook.value = null
  refreshLibrary()
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

    // set placeholder and clear lazy attributes
    try {
      img.src = getPlaceholderUrl()
    } catch {}
    try {
      img.removeAttribute('data-src')
    } catch {}
    try {
      img.removeAttribute('data-original-src')
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

function statusText(
  status: 'downloading' | 'no-file' | 'quality-mismatch' | 'quality-match',
): string {
  switch (status) {
    case 'downloading':
      return 'Downloading'
    case 'no-file':
      return 'Missing'
    case 'quality-mismatch':
      return 'Below Cutoff'
    case 'quality-match':
      return 'Downloaded'
    default:
      return ''
  }
}

function getAudiobookStatus(
  audiobook: Audiobook,
): 'downloading' | 'no-file' | 'quality-mismatch' | 'quality-match' {
  // Check if this audiobook is currently being downloaded
  const isDownloading = downloadsStore.activeDownloads.some((d) => d.audiobookId === audiobook.id)
  if (isDownloading) {
    return 'downloading'
  }

  // Use server's wanted flag if available (it checks File.Exists() on backend)
  // wanted === true means files are missing or invalid
  // wanted === false means files exist and are valid
  if (audiobook.wanted === true) {
    return 'no-file'
  }

  // If there are no files at all, treat as no-file
  if (!audiobook.files || !Array.isArray(audiobook.files) || audiobook.files.length === 0) {
    return 'no-file'
  }

  const profile = qualityProfiles.value.find((p) => p.id === audiobook.qualityProfileId)

  // If no profile or no preferredFormats defined, fall back to the simple existing behavior
  if (!profile) {
    const hasFile = audiobook.filePath && audiobook.fileSize && audiobook.fileSize > 0
    return hasFile ? 'quality-match' : 'no-file'
  }

  // Helper: normalize strings
  const normalize = (s?: string) => (s || '').toString().toLowerCase()

  // Find any file that matches one of the profile's preferred formats
  const preferredFormats = (profile.preferredFormats || []).map((f) => normalize(f))

  // If no preferred formats configured, treat any file as a candidate
  const candidateFiles = audiobook.files.filter((f) => {
    if (!f) return false
    const fileFormat = normalize(f.format) || normalize(f.container) || ''
    if (preferredFormats.length === 0) return true
    return (
      preferredFormats.includes(fileFormat) ||
      preferredFormats.some((pf) => fileFormat.includes(pf))
    )
  })

  if (candidateFiles.length === 0) {
    // Files exist but none match preferred formats; treat as mismatch instead of missing.
    return 'quality-mismatch'
  }

  // If no cutoff defined, assume match
  if (!profile.cutoffQuality || !profile.qualities || profile.qualities.length === 0) {
    return 'quality-match'
  }

  // Build a map of quality -> priority for quick lookup
  const qualityPriority = new Map<string, number>()
  for (const q of profile.qualities) {
    if (!q || !q.quality) continue
    qualityPriority.set(normalize(q.quality), q.priority)
  }

  const cutoff = normalize(profile.cutoffQuality)
  const cutoffPriority = qualityPriority.has(cutoff)
    ? qualityPriority.get(cutoff)!
    : Number.POSITIVE_INFINITY

  // Helper to derive a quality string for a given file/audiobook
  type FileInfo = {
    bitrate?: number | string
    container?: string
    codec?: string
    format?: string
  }

  function deriveQualityLabel(file: FileInfo | undefined): string {
    // Prefer the denormalized audiobook.quality if present
    if (audiobook.quality) return normalize(audiobook.quality)

    if (file && file.bitrate) {
      const br = Number(file.bitrate)
      if (!isNaN(br)) {
        if (br >= 320) return '320kbps'
        if (br >= 256) return '256kbps'
        if (br >= 192) return '192kbps'
        return `${Math.round(br)}kbps`
      }
    }

    // If container or codec suggests lossless
    const container = normalize(file?.container)
    const codec = normalize(file?.codec)
    if (
      container.includes('flac') ||
      codec.includes('flac') ||
      codec.includes('alac') ||
      codec.includes('wav')
    ) {
      return 'lossless'
    }

    // Fallback: use format string
    if (file && file.format) return normalize(file.format)

    return ''
  }

  // If any candidate file meets or exceeds the cutoff (lower priority number == better), return match
  for (const f of candidateFiles) {
    const label = deriveQualityLabel(f)
    if (!label) continue
    const p = qualityPriority.has(label) ? qualityPriority.get(label)! : Number.POSITIVE_INFINITY
    if (p <= cutoffPriority) {
      return 'quality-match'
    }
  }

  // Otherwise at least one preferred-format file exists but doesn't meet cutoff
  return 'quality-mismatch'
}

function handleCheckboxKeydown(audiobook: Audiobook, event: KeyboardEvent) {
  if (event.key === ' ') {
    event.preventDefault()
    toggleSelection(audiobook.id)
  }
}

onMounted(async () => {
  if (libraryStore.audiobooks.length === 0) {
    await libraryStore.fetchLibrary()
  }
})

// No need to watch for page changes - native loading="lazy" handles everything
watch(searchQuery, () => {
  currentPage.value = 1
})

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

/* Mobile-friendly toolbar: hide text, show only icons on screens 1024px and below */
@media (max-width: 1024px) {
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

@media (max-width: 768px) {
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

/* Mobile-friendly toolbar: hide text, show only icons on screens 1024px and below */
@media (max-width: 1024px) {
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
