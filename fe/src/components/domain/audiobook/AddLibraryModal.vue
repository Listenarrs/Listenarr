<template>
  <Modal :visible="visible" size="lg" @close="closeModal">
    <template #header>
      <ModalHeader :title="'Add to Library'" @close="closeModal" />
    </template>

    <template #default>
      <ModalBody>
        <div class="book-layout">
          <!-- Book Image -->
          <div class="book-image">
            <div class="image-viewport">
              <img
                v-if="resolvedImageUrl || enriched?.imageUrl || book.imageUrl"
                :src="imageSrc"
                :alt="book.title"
                loading="lazy"
                @error="onImageError"
                @load="onImageLoad"
                :aria-hidden="!book.title"
              />
              <div v-else class="placeholder-cover">
                <PhImage />
                <span>No Cover</span>
              </div>
            </div>
          </div>

          <!-- Book Details -->
          <div class="book-details">
            <div class="detail-section">
              <h3>{{ book.title }}</h3>
              <p v-if="book.authors?.length" class="authors">by {{ book.authors.join(', ') }}</p>
              <p v-if="book.narrators?.length" class="narrators">
                Narrated by {{ book.narrators.join(', ') }}
              </p>
            </div>

            <div v-if="book.description" class="detail-section">
              <h4>Description</h4>
              <div class="description">{{ stripHtmlAndNormalize(book.description) }}</div>
            </div>

            <div class="detail-section" id="add-library-desc">
              <h4>Publication Information</h4>
              <div class="detail-grid">
                <div v-if="book.publisher" class="detail-item">
                  <span class="label">Publisher:</span>
                  <span class="value">{{ book.publisher }}</span>
                </div>
                <div v-if="publishDate" class="detail-item">
                  <span class="label">Release Date:</span>
                  <span class="value">{{ formatDate(publishDate) }}</span>
                </div>
                <div v-else-if="publishYear" class="detail-item">
                  <span class="label">Release Date:</span>
                  <span class="value">{{ publishYear }}</span>
                </div>
                <div v-if="book.language" class="detail-item">
                  <span class="label">Language:</span>
                  <span class="value">{{ capitalizeFirst(book.language) }}</span>
                </div>
                <div v-if="book.runtime" class="detail-item">
                  <span class="label">Listening Length:</span>
                  <span class="value">{{ formatRuntime(book.runtime) }}</span>
                </div>
              </div>
            </div>

            <div class="detail-section">
              <h4>Identifiers</h4>
              <div class="detail-grid">
                <div v-if="normalizedSourceName" class="detail-item">
                  <span class="label">Metadata Source:</span>
                  <span class="value">
                    <a
                      v-if="audimetaSourceUrl"
                      :href="audimetaSourceUrl"
                      target="_blank"
                      rel="noopener noreferrer"
                    >
                      {{ normalizedSourceName }}
                    </a>
                    <span v-else>{{ normalizedSourceName }}</span>
                  </span>
                </div>
                <div v-if="book.asin" class="detail-item">
                  <span class="label">ASIN:</span>
                  <span class="value">
                    <a
                      :href="audibleProductUrl"
                      target="_blank"
                      rel="noopener noreferrer"
                    >
                      {{ book.asin }}
                    </a>
                  </span>
                </div>
                <div v-if="book.isbn" class="detail-item">
                  <span class="label">ISBN:</span>
                  <span class="value">{{ book.isbn }}</span>
                </div>
                <div v-if="book.openLibraryId && openLibraryUrl" class="detail-item">
                  <span class="label">OpenLibrary ID:</span>
                  <span class="value">
                    <a
                      :href="openLibraryUrl"
                      target="_blank"
                      rel="noopener noreferrer"
                    >
                      {{ book.openLibraryId }}
                    </a>
                  </span>
                </div>
              </div>
            </div>

            <div v-if="book.series || displayGenres.length" class="detail-section">
              <h4>Series & Genre Information</h4>
              <div class="detail-grid">
                <div v-if="book.series" class="detail-item">
                  <span class="label">Series:</span>
                  <span class="value">
                    {{ book.series }}<span v-if="book.seriesNumber"> #{{ book.seriesNumber }}</span>
                  </span>
                </div>
                <div v-if="displayGenres.length" class="detail-item">
                  <span class="label">Genres:</span>
                  <span class="value">{{ displayGenres.join(', ') }}</span>
                </div>
              </div>
            </div>

            <div v-if="hasFlags" class="detail-section">
              <h4>Content Flags</h4>
              <div class="flags">
                <span v-if="book.explicit" class="flag explicit">Explicit</span>
                <span v-if="book.abridged" class="flag abridged">Abridged</span>
              </div>
            </div>
          </div>
        </div>

        <!-- Customization Options -->
        <div class="detail-section library-options">
          <h4>Library Options</h4>

          <FormRow>
            <div class="checkbox-group">
              <Checkbox v-model="options.monitored">
                <strong>Monitor for new releases</strong>
                <small>Automatically search for better quality versions of this audiobook</small>
              </Checkbox>
            </div>
          </FormRow>

          <FormRow>
            <div class="checkbox-group">
              <Checkbox v-model="options.autoSearch">
                <strong>Search for downloads immediately</strong>
                <small>Start searching for available downloads right after adding to library</small>
              </Checkbox>
            </div>
          </FormRow>

          <div class="option-group">
            <label class="form-label">Destination</label>
            <div class="form-control-card">
              <div class="destination-display">
                <div class="destination-row">
                  <div class="root-select">
                    <RootFolderSelect
                      v-model:rootId="selectedRootId"
                      v-model:customPath="customRootPath"
                      hideLabel
                    />
                  </div>
                  <input
                    v-if="selectedRootId === 0"
                    type="text"
                    v-model="customRootPath"
                    class="form-input custom-path-input"
                    placeholder="e.g. C:\\Audiobooks or /mnt/audiobooks"
                  />
                  <input
                    v-else
                    type="text"
                    v-model="options.relativePath"
                    class="form-input relative-input"
                    placeholder="e.g. Author/Title"
                  />
                </div>
                <small class="form-help" v-if="selectedRootId === 0">
                  Enter an absolute path where files will be stored
                </small>
                <small class="form-help" v-else>
                  Select a named root (or custom path) and edit the path relative to it on the
                  right.
                </small>
                <!-- Path length warning -->
                <div v-if="destinationPathWarning" class="path-length-warning">
                  <PhWarning :size="16" />
                  <span>{{ destinationPathWarning }}</span>
                </div>
              </div>
            </div>
          </div>

          <div class="option-group">
            <label class="form-label">Quality Profile</label>
            <select v-model="options.qualityProfileId" class="form-select">
              <option :value="null">Use Default Profile</option>
              <option v-for="profile in qualityProfiles" :key="profile.id" :value="profile.id">
                {{ profile.name }}{{ profile.isDefault ? ' (Default)' : '' }}
              </option>
            </select>
            <small class="form-help">
              Choose which quality profile to use for automatic downloads. Leave as "Use Default
              Profile" to automatically use the default profile.
            </small>
          </div>
        </div>
      </ModalBody>

    </template>

    <template #footer>
      <button class="btn btn-secondary" @click="closeModal">
        <PhX />
        Cancel
      </button>
      <button class="btn btn-primary" @click="addToLibrary" :disabled="isAdding || metadataLoading">
        <PhSpinner v-if="isAdding" class="ph-spin" />
        <PhPlus v-else />
        {{ isAdding ? 'Adding...' : 'Add to Library' }}
      </button>
    </template>
  </Modal>
</template>

<script setup lang="ts">
import { ref, onMounted, watch, computed, onBeforeUnmount, nextTick } from 'vue'
import type { AudibleBookMetadata, QualityProfile, Audiobook } from '@/types'
import { apiService } from '@/services/api'
import { useConfigurationStore } from '@/stores/configuration'
import { useToast } from '@/services/toastService'
import { logger } from '@/utils/logger'
import { Modal, ModalHeader, ModalBody } from '@/components/feedback'
import RootFolderSelect from '@/components/form/RootFolderSelect.vue'
import Checkbox from '@/components/form/Checkbox.vue'
import FormRow from '@/components/settings/FormRow.vue'
import { useRootFoldersStore } from '@/stores/rootFolders'
import { PhX, PhSpinner, PhPlus, PhImage, PhWarning } from '@phosphor-icons/vue' 
import { toForward, normalizeForCompare } from '@/utils/path' 
import { formatDate } from '@/utils/searchResultFormatting'
import { stripHtmlAndNormalize } from '@/utils/textUtils'
import { usePathLengthCheck } from '@/composables/usePathLengthCheck'

interface Props {
  visible: boolean
  book: AudibleBookMetadata
  resolvedImageUrl?: string
}

interface Emits {
  (e: 'close'): void
  (e: 'added', audiobook: Audiobook): void
}

const props = defineProps<Props>()
const emit = defineEmits<Emits>()

const configStore = useConfigurationStore()
const toast = useToast()

const isAdding = ref(false)
const qualityProfiles = ref<QualityProfile[]>([])

const options = ref({
  monitored: true,
  qualityProfileId: null as number | null,
  autoSearch: false,
  // editable relative path portion (relative to rootPath)
  relativePath: '' as string | null,
})

const publishDate = computed(() => props.book?.publishedDate || undefined)
const publishYear = computed(() => {
  if (props.book?.publishedDate) {
    const match = props.book.publishedDate.match(/\d{4}/)
    return match ? match[0] : undefined
  }
  const legacy = (props.book as unknown as { publishYear?: string }).publishYear
  return legacy || undefined
})

const normalizedSourceName = computed(() => {
  const source = (metadataSource.value || props.book?.source || '').trim()
  if (!source) return ''
  if (source.toLowerCase() === 'audimeta') return 'Audimeta'
  return source
})

const audimetaSourceUrl = computed(() => {
  const source = (metadataSource.value || props.book?.source || '').toLowerCase()
  const asin = props.book?.asin
  if (source !== 'audimeta' || !asin) return null
  return `https://audimeta.de/book/${encodeURIComponent(asin)}`
})

const audibleProductUrl = computed(() => {
  const asin = props.book?.asin
  return asin ? `https://www.audible.com/pd/${asin}` : '#'
})

const openLibraryUrl = computed(() => {
  const olid = props.book?.openLibraryId
  if (!olid) return null

  if (/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(olid)) {
    return null
  }

  if (olid.startsWith('/works/') || olid.startsWith('/books/')) {
    return `https://openlibrary.org${olid}`
  }

  if (/^OL\w+[WM]$/i.test(olid)) {
    const type = olid.toUpperCase().endsWith('W') ? 'works' : 'books'
    return `https://openlibrary.org/${type}/${olid}`
  }

  return `https://openlibrary.org/books/${olid}`
})

const hasFlags = computed(() => Boolean(props.book?.explicit || props.book?.abridged))

const displayGenres = computed(() => {
  if (enriched.value?.genres && enriched.value.genres.length) return enriched.value.genres
  return props.book?.genres || []
})

const rootStore = useRootFoldersStore()
const selectedRootId = ref<number | null>(null)
const customRootPath = ref<string | null>(null)

const rootPath = ref<string>('')
const previewFull = ref<string>('')
const previewRelative = ref<string>('')

// Path length check — reactively compute the full destination path
const estimatedFullPath = computed(() => {
  let root = ''
  if (selectedRootId.value === 0) {
    root = (customRootPath.value || '').trim()
  } else if (selectedRootId.value && selectedRootId.value > 0) {
    const found = rootStore.folders.find((f) => f.id === selectedRootId.value)
    root = found?.path || ''
  } else {
    const defaultRoot = rootStore.folders.find((f) => f.isDefault)
    root = defaultRoot?.path || configStore.applicationSettings?.outputPath || ''
  }
  if (selectedRootId.value === 0) return root
  const rel = (options.value.relativePath || '').trim()
  if (!root) return rel
  if (!rel) return root
  const sep = root.includes('\\') ? '\\' : '/'
  return root.endsWith(sep) ? root + rel : root + sep + rel
})
const { pathLengthWarning: destinationPathWarning } = usePathLengthCheck(estimatedFullPath)

// Hold an enriched metadata object (populate if metadata sources available)
const enriched = ref<AudibleBookMetadata | null>(null)
// Image and metadata UI state
const imageError = ref(false)
const imageLoading = ref(false)
const imageRetryCount = ref(0)
const metadataLoading = ref(false)
const metadataSource = ref<string | null>(null)

const imageSrc = computed(() => {
  // prefer resolvedImageUrl passed from parent
  const base = props.resolvedImageUrl || enriched.value?.imageUrl || props.book?.imageUrl || ''
  if (!base) return ''
  // If we retried, append cache-buster to force reload
  if (imageRetryCount.value > 0) {
    const sep = base.includes('?') ? '&' : '?'
    return `${base}${sep}r=${Date.now()}`
  }
  return base
})

// Local types for audimeta response to avoid `any`
interface AudimetaPerson {
  name?: string
}
interface AudimetaSeries {
  name?: string
  position?: string | number
}
interface AudimetaGenre {
  name?: string
}
interface Audimeta {
  asin?: string
  title?: string
  subtitle?: string
  authors?: AudimetaPerson[]
  narrators?: AudimetaPerson[]
  publisher?: string
  publishDate?: string
  releaseDate?: string
  description?: string
  imageUrl?: string
  lengthMinutes?: number
  language?: string
  genres?: AudimetaGenre[]
  series?: AudimetaSeries[]
  bookFormat?: string
  isbn?: string
}

interface AudimetaMetadataResponse {
  metadata?: Partial<Audimeta>
  source?: string
}

// Helper to map audimeta response to AudibleBookMetadata
const mapAudimetaToAudible = (
  audimeta: Partial<Audimeta> | undefined,
  source?: string,
): AudibleBookMetadata => {
  let publishYear: string | undefined
  let publishedDate: string | undefined
  const dateStr = audimeta?.publishDate || audimeta?.releaseDate
  if (dateStr && typeof dateStr === 'string') {
    publishedDate = dateStr
    const yearMatch = dateStr.match(/\d{4}/)
    publishYear = yearMatch ? yearMatch[0] : undefined
  }

  const authors = (audimeta?.authors || []).map((a) => a?.name).filter(Boolean) as string[]
  const narrators = (audimeta?.narrators || []).map((n) => n?.name).filter(Boolean) as string[]
  const genres = (audimeta?.genres || []).map((g) => g?.name).filter(Boolean) as string[]

  const firstSeries =
    audimeta?.series && audimeta.series.length > 0 ? audimeta.series[0] : undefined

  return {
    asin: audimeta?.asin || props.book?.asin || '',
    title: audimeta?.title || props.book?.title || 'Unknown Title',
    subtitle: audimeta?.subtitle,
    authors: authors.length ? authors : props.book?.authors || [],
    narrators: narrators.length ? narrators : props.book?.narrators || [],
    publisher: audimeta?.publisher || props.book?.publisher,
    publishYear: publishYear || props.book?.publishYear,
    publishedDate: publishedDate || props.book?.publishedDate,
    description: audimeta?.description || props.book?.description,
    imageUrl: audimeta?.imageUrl || props.book?.imageUrl,
    runtime:
      typeof audimeta?.lengthMinutes === 'number'
        ? audimeta.lengthMinutes
        : props.book?.runtime,
    language: audimeta?.language || props.book?.language,
    genres: genres.length ? genres : props.book?.genres || [],
    series: firstSeries?.name || props.book?.series,
    seriesNumber:
      firstSeries?.position !== undefined ? String(firstSeries.position) : (props.book?.seriesNumber && props.book.seriesNumber !== 'null' ? props.book.seriesNumber : undefined),
    abridged:
      typeof audimeta?.bookFormat === 'string'
        ? audimeta.bookFormat.toLowerCase().includes('abridged')
        : Boolean(props.book?.abridged),
    isbn: audimeta?.isbn || props.book?.isbn,
    source: source || props.book?.source,
  }
}

// helper to load profiles/settings and seed preview
const seedPreview = async () => {
  await configStore.loadQualityProfiles()
  qualityProfiles.value = configStore.qualityProfiles

  // Load application settings to get default root
  await configStore.loadApplicationSettings()
  // Load named root folders if available
  await rootStore.load()
  if (rootStore.folders.length > 0) {
    const def = rootStore.folders.find((f) => f.isDefault) || rootStore.folders[0]
    selectedRootId.value = def?.id ?? null
    // override rootPath for preview
    rootPath.value = def?.path || configStore.applicationSettings?.outputPath || ''
  } else {
    // Fallback to legacy outputPath if no root folders
    rootPath.value = configStore.applicationSettings?.outputPath || ''
  }

  // Attempt to fetch enriched metadata for the ASIN (if present) so preview/add use metadata sources
  try {
    if (props.book?.asin) {
      metadataLoading.value = true
      try {
        const resp = await apiService.getAudibleMetadata<AudimetaMetadataResponse | Partial<Audimeta>>(
          props.book.asin,
        )
        const payload = (resp && typeof resp === 'object' ? resp : {}) as
          | AudimetaMetadataResponse
          | Partial<Audimeta>
        const source = 'source' in payload && typeof payload.source === 'string' ? payload.source : undefined
        const metadata =
          'metadata' in payload && payload.metadata && typeof payload.metadata === 'object'
            ? payload.metadata
            : (payload as Partial<Audimeta>)

        if (metadata && typeof metadata === 'object') {
          const enrichedMeta = mapAudimetaToAudible(metadata, source)
          // Sanitize seriesNumber to filter out the string "null"
          if (enrichedMeta.seriesNumber === 'null') {
            enrichedMeta.seriesNumber = undefined
          }
          enriched.value = enrichedMeta
          metadataSource.value = source || null
        }
      } catch (metaErr) {
        // ignore metadata fetch errors - we'll fall back to provided book
        logger.debug('Metadata fetch failed in AddLibraryModal:', metaErr)
      } finally {
        metadataLoading.value = false
      }
    }

    const metadataForPreview = (enriched.value || props.book) as AudibleBookMetadata
    // Compute a preview path using server logic
    const resp2 = await apiService.previewLibraryPath(
      metadataForPreview,
      rootPath.value || undefined,
    )
    previewFull.value = resp2?.fullPath || ''
    previewRelative.value = resp2?.relativePath || ''
    // Seed editable relative path — prefer server-relative, otherwise derive from full preview and configured root
    options.value.relativePath = deriveRelative(
      previewRelative.value,
      previewFull.value,
      rootPath.value,
    )
  } catch (e) {
    console.error('Failed to preview path:', e)
  }
}

// Load when mounted
onMounted(() => {
  seedPreview()
})

// Watch for resolvedImageUrl changes to reset image error state
watch(
  () => props.resolvedImageUrl,
  () => {
    imageError.value = false
    imageRetryCount.value = 0
  },
)

function onImageError() {
  imageLoading.value = false
  imageError.value = true
}

function onImageLoad() {
  imageLoading.value = false
  imageError.value = false
}

// Helper to derive a relative path from server preview/paths
function deriveRelative(
  serverRelative: string | undefined | null,
  serverFull: string | undefined | null,
  root: string | undefined | null,
): string {
  const rootVal = root || ''
  // Prefer explicit server-provided relative
  if (serverRelative && String(serverRelative).trim().length > 0) return serverRelative

  // If no root configured, fall back to showing the full path
  if (!rootVal) return serverFull || ''
  if (!serverFull) return ''

  // Normalize separators to forward slash for comparison
  const normRoot = toForward(rootVal)
  const normFull = toForward(serverFull)

  // Ensure trailing slash on root for slicing
  const rootWithSlash = normRoot.endsWith('/') ? normRoot : normRoot + '/'

  if (normalizeForCompare(normFull) === normalizeForCompare(normRoot)) return ''
  if (normalizeForCompare(normFull).startsWith(normalizeForCompare(rootWithSlash))) {
    const rel = normFull.slice(rootWithSlash.length).replace(/^\/+/, '')
    // Preserve user's original separator preference from configured root
    const useBackslash = rootVal.includes('\\')
    return useBackslash ? rel.replace(/\//g, '\\') : rel
  }

  // Not under root: show full path so user can edit it
  return serverFull
}

// Re-seed preview if the passed book changes after mount (parent may update props)
watch(
  () => props.book,
  (newVal) => {
    if (!newVal) return
    seedPreview()
  },
)

const modalRef = ref<HTMLElement | null>(null)

const closeModal = () => {
  emit('close')
}

// Focus management for accessibility: trap focus inside modal and restore on close
let previousActiveElement: HTMLElement | null = null

const getFocusable = (container: HTMLElement | null): HTMLElement[] => {
  if (!container) return []
  const selectors = [
    'a[href]',
    'button:not([disabled])',
    'textarea:not([disabled])',
    'input:not([disabled])',
    'select:not([disabled])',
    '[tabindex]:not([tabindex="-1"])',
  ].join(',')
  return Array.from(container.querySelectorAll<HTMLElement>(selectors))
}

function onKeyDown(e: KeyboardEvent) {
  if (!modalRef.value) return
  if (e.key === 'Escape') {
    e.stopPropagation()
    closeModal()
    return
  }

  if (e.key === 'Tab') {
    const focusable = getFocusable(modalRef.value)
    if (focusable.length === 0) {
      e.preventDefault()
      return
    }
    const first = focusable[0]
    const last = focusable[focusable.length - 1]
    const active = document.activeElement as HTMLElement | null
    if (e.shiftKey) {
      if (!active || active === first) {
        e.preventDefault()
        last?.focus()
      }
    } else {
      if (!active || active === last) {
        e.preventDefault()
        first?.focus()
      }
    }
  }
}

watch(
  () => props.visible,
  async (val) => {
    if (val) {
      previousActiveElement = document.activeElement as HTMLElement | null
      await nextTick()
      if (modalRef.value) {
        modalRef.value.focus()
      }
      document.addEventListener('keydown', onKeyDown, { capture: true })
    } else {
      document.removeEventListener('keydown', onKeyDown, { capture: true })
      if (previousActiveElement && typeof previousActiveElement.focus === 'function') {
        previousActiveElement.focus()
      }
    }
  },
)

onBeforeUnmount(() => {
  document.removeEventListener('keydown', onKeyDown, { capture: true })
})

const addToLibrary = async () => {
  if (!props.book) return

  isAdding.value = true
  // Combine rootPath + relativePath into full destination path
  let destination: string | undefined = undefined
  try {
    const rel = (options.value.relativePath || '').trim()
    // Resolve selected root (custom, named, or default)
    let root = null
    if (selectedRootId.value === 0) root = customRootPath.value || ''
    else if (selectedRootId.value && selectedRootId.value > 0) {
      const found = rootStore.folders.find((f) => f.id === selectedRootId.value)
      root = found?.path || ''
    } else {
      // Use default root folder, fallback to legacy outputPath for compatibility
      const defaultRoot = rootStore.folders.find((f) => f.isDefault)
      root = defaultRoot?.path || configStore.applicationSettings?.outputPath || ''
    }

    if (selectedRootId.value === 0) {
      // Custom path: use exactly what the user entered (no pattern/relative path)
      const cleaned = (root || '').trim()
      destination = cleaned.length ? cleaned : undefined
    } else if (root && rel) {
      const sep = root.includes('\\') ? '\\' : '/'
      const cleanedRel = rel.replace(/\\|\//g, sep)
      destination = root.endsWith(sep) ? root + cleanedRel : root + sep + cleanedRel
    } else if (root && !rel) {
      destination = root
    }

    const metadataToSend = (enriched.value || props.book) as AudibleBookMetadata
    const result = await apiService.addToLibrary(metadataToSend, {
      monitored: options.value.monitored,
      qualityProfileId: options.value.qualityProfileId || undefined,
      autoSearch: options.value.autoSearch,
      destinationPath: destination || undefined,
    })
    toast.success('Added', `"${metadataToSend.title}" has been added to your library!`)
    emit('added', result.audiobook)
    closeModal()
  } catch (err: unknown) {
    console.error('Failed to add audiobook:', err)
    const errorMessage =
      err instanceof Error ? err.message : 'Failed to add audiobook. Please try again.'
    toast.error('Add failed', errorMessage)
  } finally {
    isAdding.value = false
  }
}

const formatRuntime = (minutes: number): string => {
  if (!minutes) return 'Unknown'
  // Guard against legacy data stored in seconds
  const normalized = minutes >= 20000 ? Math.round(minutes / 60) : minutes
  const hours = Math.floor(normalized / 60)
  const mins = normalized % 60
  return `${hours}h ${mins}m`
}

const capitalizeFirst = (str: string): string => {
  if (!str) return ''
  return str.charAt(0).toUpperCase() + str.slice(1).toLowerCase()
}
</script>

<style scoped>
/* Keep only layout and content-related styles; modal wrapper styles come from shared modal stylesheet */
.image-viewport {
  width: 100%;
  aspect-ratio: 1/1;
  position: relative;
  border-radius: 6px;
  overflow: hidden;
  background: #333;
  display: flex;
  align-items: center;
  justify-content: center;
  box-shadow: 0 4px 8px rgba(0, 0, 0, 0.3);
}
.image-viewport img {
  width: 100%;
  height: 100%;
  object-fit: cover;
  display: block;
}
.placeholder-cover {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  color: var(--text-muted);
}
.image-loading-overlay {
  position: absolute;
  inset: 0;
  display: flex;
  align-items: center;
  justify-content: center;
  background: rgba(0, 0, 0, 0.25);
  color: white;
}
.image-error-overlay {
  position: absolute;
  inset: 0;
  display: flex;
  align-items: center;
  justify-content: center;
  background: rgba(0, 0, 0, 0.6);
  color: white;
}
.image-error-overlay .error-inner {
  text-align: center;
}
.image-error-overlay .error-inner .btn.small {
  margin-top: 0.5rem;
}

.meta-source-row {
  margin-bottom: 0.5rem;
}

.book-layout {
  display: grid;
  grid-template-columns: 200px 1fr;
  gap: 2rem;
  align-items: start;
}

.book-image {
  position: sticky;
  top: 0;
}

.book-details {
  display: flex;
  flex-direction: column;
  gap: 1.5rem;
}

.detail-section h3 {
  margin: 0 0 0.5rem 0;
  color: white;
  font-size: 1.75rem;
  line-height: 1.2;
}

.detail-section h4 {
  margin: 0 0 1rem 0;
  color: white;
  font-size: 1.1rem;
  font-weight: 500;
  border-bottom: 1px solid #333;
  padding-bottom: 0.5rem;
}

.authors {
  color: var(--brand-500);
  font-size: 1.1rem;
  font-weight: 500;
  margin: 0 0 0.25rem 0;
}

.narrators {
  color: #ccc;
  font-style: italic;
  margin: 0;
}

.description {
  color: #ccc;
  line-height: 1.6;
  margin: 0;
  white-space: pre-wrap;
}

.detail-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
  gap: 1rem;
}

.detail-item {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
}

.detail-item .label {
  color: #999;
  font-size: 0.9rem;
  font-weight: 500;
}

.detail-item .value {
  color: white;
  font-weight: 400;
}

.flags {
  display: flex;
  gap: 0.5rem;
  flex-wrap: wrap;
}

.flag {
  padding: 0.25rem 0.75rem;
  border-radius: 6px;
  font-size: 0.8rem;
  font-weight: 500;
}

.flag.explicit {
  background-color: rgba(231, 76, 60, 0.2);
  color: #e74c3c;
  border: 1px solid #e74c3c;
}

.flag.abridged {
  background-color: rgba(243, 156, 18, 0.2);
  color: #f39c12;
  border: 1px solid #f39c12;
}

.library-options {
  margin-top: 2rem;
}

.form-label {
  display: block;
  color: white;
  font-weight: 500;
  margin-bottom: 0.5rem;
}

.form-select {
  width: 100%;
  padding: 0.75rem;
  border: 1px solid #555;
  border-radius: 6px;
  background-color: #333;
  color: white;
  font-size: 1rem;
}

.form-select:focus {
  outline: none;
  border-color: var(--brand-focus);
  box-shadow: 0 0 0 2px rgba(var(--brand-rgb), 0.2);
}

.form-help {
  display: block;
  color: #ccc;
  font-size: 0.85rem;
  margin-top: 0.5rem;
}

.option-group {
  margin: 2rem 0;
}

.modal-content .form-group {
  margin-bottom: 0.25rem;
}
/* Destination display styles */
.destination-display {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
  padding: 0.5rem 0;
}

/* root-label is used instead of readonly-path */

.form-input {
  width: 100%;
  padding: 0.6rem 0.75rem;
  border-radius: 6px;
  border: 1px solid #3a3a3a;
  background-color: #2a2a2a;
  color: #fff;
  font-size: 0.95rem;
}

.form-input:focus {
  outline: none;
  border-color: var(--brand-focus);
  box-shadow: 0 0 0 3px rgba(var(--brand-rgb), 0.06);
}

/* Row layout for destination: root left, input right */
.destination-row {
  display: flex;
  gap: 0.75rem;
  align-items: center;
}

.root-label {
  padding: 0.45rem 0 0.45rem 0.6rem;
  color: #ccc;
  font-family:
    ui-monospace, SFMono-Regular, Menlo, Monaco, 'Roboto Mono', 'Segoe UI Mono', monospace;
  font-size: 0.9rem;
  width: fit-content;
  white-space: nowrap;
}

.relative-input {
  flex: 1 1 auto;
}

/* Buttons are centralized in `src/assets/buttons.css` and `src/assets/modals.css`. Use `.btn` / `.btn-primary` here. */

/* Button color variants centralized in `src/assets/modals.css` */

/* Responsive design */
@media (max-width: 768px) {
  .book-layout {
    grid-template-columns: 1fr;
    gap: 1.5rem;
  }

  .book-image {
    position: static;
    max-width: 200px;
    margin: 0 auto;
  }

  .detail-grid {
    grid-template-columns: 1fr;
  }

  .modal-footer {
    flex-direction: column-reverse;
  }

  .btn {
    justify-content: center;
  }
}

.path-length-warning {
  display: flex;
  align-items: flex-start;
  gap: 0.5rem;
  margin-top: 0.5rem;
  padding: 0.625rem 0.75rem;
  background-color: rgba(255, 152, 0, 0.08);
  border: 1px solid rgba(255, 152, 0, 0.35);
  border-radius: 6px;
  color: #ffb74d;
  font-size: 0.8rem;
  line-height: 1.5;
}

.path-length-warning svg {
  flex-shrink: 0;
  margin-top: 0.125rem;
}
</style>
