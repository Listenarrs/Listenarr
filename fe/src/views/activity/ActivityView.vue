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
  <div class="activity-view">
    <div class="page-header">
      <h1>
        <PhActivity />
        Activity
      </h1>
      <div class="activity-actions">
        <div class="filter-input-wrapper">
          <PhMagnifyingGlass class="filter-icon" />
          <input
            v-model="filterText"
            type="text"
            class="filter-input"
            placeholder="Filter downloads..."
          />
          <button v-if="filterText" class="filter-clear" @click="filterText = ''">
            <PhX />
          </button>
        </div>
        <button class="btn btn-secondary" @click="refreshQueue" :disabled="loading">
          <component :is="loading ? PhSpinner : PhArrowClockwise" />
          Refresh
        </button>
      </div>
    </div>

    <div v-if="queueHealthClients.length > 0" class="queue-health-banner">
      <PhWarningCircle />
      <div class="queue-health-copy">
        <strong>{{ queueHealthTitle }}</strong>
        <span>{{ queueHealthMessage }}</span>
      </div>
    </div>

    <!-- Queue Grid -->
    <div
      v-if="filteredQueue.length > 0"
      ref="scrollContainer"
      :class="['queue-grid-container', { 'is-static': !useVirtualActivityList }]"
      @scroll="updateVisibleRange"
    >
      <div class="queue-header">
        <div class="col-title">Title</div>
        <div class="col-quality">Quality</div>
        <div class="col-language">Language</div>
        <div class="col-progress">Progress</div>
        <div class="col-eta">ETA</div>
        <div class="col-status">Status</div>
        <div class="col-actions"></div>
      </div>
      <div
        :class="['queue-body-spacer', { 'is-static': !useVirtualActivityList }]"
        :style="useVirtualActivityList ? { height: `${totalHeight}px` } : undefined"
      >
        <div
          :class="['queue-body', { 'is-static': !useVirtualActivityList }]"
          :style="useVirtualActivityList ? { transform: `translateY(${topPadding}px)` } : undefined"
        >
          <div
            v-for="item in visibleQueueItems"
            :key="item.id"
            v-memo="[item.id, item.status, item.progress, item.eta, item.downloadSpeed]"
            class="queue-row"
          >
            <div class="col-title">
              <div class="title-cell">
                <RouterLink
                  v-if="item.audiobookId"
                  :to="`/audiobooks/${item.audiobookId}`"
                  class="title-link"
                  >{{ getDisplayTitle(item) }}</RouterLink
                >
                <span v-else class="title-text">{{ getDisplayTitle(item) }}</span>
              </div>
            </div>
            <div class="col-quality">
              <span v-if="item.quality && item.quality !== '*'" class="quality-tag">{{
                item.quality
              }}</span>
              <span v-else class="muted">-</span>
            </div>
            <div class="col-language">
              <span v-if="item.language" class="language-text">{{ item.language }}</span>
              <span v-else class="muted">-</span>
            </div>
            <div class="col-progress">
              <div class="progress-cell">
                <ProgressBar
                  :value="item.progress"
                  :downloaded="item.downloaded"
                  :total="item.size"
                  :showPercentage="false"
                  variant="activity"
                  height="small"
                  :animating="item.status === 'downloading'"
                />
              </div>
            </div>

            <div class="col-eta">
              <span v-if="item.eta" class="eta-text">{{ formatEta(item.eta) }}</span>
              <span v-else class="muted">-</span>
            </div>
            <div class="col-status">
              <span :class="['status-badge', item.status]">
                {{ formatStatus(item.status) }}
              </span>
              <span
                v-if="item.isStaleSnapshot"
                class="stale-badge"
                :title="getSnapshotStatusTitle(item)"
              >
                Cached
              </span>
            </div>
            <div class="col-actions">
              <button
                v-if="item.canRemove"
                class="btn-icon btn-danger-icon"
                @click="removeFromQueue(item)"
                title="Remove from Queue"
              >
                <PhX />
              </button>
            </div>
          </div>
        </div>
      </div>
    </div>

    <!-- Empty State -->
    <EmptyState
      v-else-if="filteredQueue.length === 0 && !loading"
      :title="filterText ? 'No Matching Downloads' : 'No Active Downloads'"
      :message="
        filterText
          ? 'No downloads match your filter.'
          : 'Downloads will appear here when you send items to your download clients.'
      "
    >
      <template #icon>
        <PhQueue :size="48" />
      </template>
    </EmptyState>

    <!-- Loading State -->
    <LoadingState v-if="loading && queue.length === 0" message="Loading queue..." />

    <!-- Remove Confirmation Modal -->
    <div v-if="showRemoveModal" class="modal-overlay" @click="showRemoveModal = false">
      <div class="modal-content" @click.stop>
        <div class="modal-header">
          <h3>
            <PhWarningCircle />
            Remove from Queue
          </h3>
          <button class="modal-close" @click="showRemoveModal = false">
            <PhX />
          </button>
        </div>
        <div class="modal-body">
          <p v-if="clientHasQueueEntry === null">Are you sure you want to remove this download?</p>
          <p v-else-if="clientHasQueueEntry === true">
            Are you sure you want to remove this download?
          </p>
          <p v-else>
            This download was not found in the selected download client's queue. Do you want to
            remove it from Listenarr only (this will delete the record from Listenarr's downloads)?
          </p>
          <div class="remove-item-info">
            <strong>{{ itemToRemove ? getDisplayTitle(itemToRemove) : '' }}</strong>
            <div class="item-details">
              <span v-if="itemToRemove?.downloadClient">
                <PhDesktop />
                {{ itemToRemove.downloadClient }}
              </span>
              <span>
                <PhChartBar />
                {{ itemToRemove?.progress.toFixed(1) }}% complete
              </span>
            </div>
          </div>
          <p class="warning-text" v-if="clientHasQueueEntry !== false">
            <PhInfo />
            This will remove the download from your download client. Files may or may not be deleted
            depending on your client settings.
          </p>
          <p class="warning-text" v-else>
            <PhInfo />
            The download was not found in the remote client's queue. Removing it here will only
            delete the Listenarr record (no action will be taken against the remote client).
          </p>
        </div>
        <div class="modal-footer">
          <button class="btn btn-secondary" @click="showRemoveModal = false">Cancel</button>
          <button class="btn btn-danger" @click="confirmRemove" :disabled="removing">
            <component :is="removing ? PhSpinner : PhTrash" />
            {{
              removing
                ? 'Removing...'
                : clientHasQueueEntry === false
                  ? 'Remove from Listenarr'
                  : 'Remove'
            }}
          </button>
        </div>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted, nextTick, unref } from 'vue'
import {
  PhActivity,
  PhSpinner,
  PhArrowClockwise,
  PhDesktop,
  PhX,
  PhQueue,
  PhWarningCircle,
  PhInfo,
  PhChartBar,
  PhTrash,
  PhMagnifyingGlass,
} from '@phosphor-icons/vue'
import { useToast } from '@/services/toastService'
import { errorTracking } from '@/services/errorTracking'
import { apiService } from '@/services/api'
import { signalRService } from '@/services/signalr'
import { useDownloadsStore } from '@/stores/downloads'
import { useLibraryStore } from '@/stores/library'
import { EmptyState, LoadingState, ProgressBar } from '@/components/base'
import { useConfigurationStore } from '@/stores/configuration'
import type { QueueClientStatus, QueueItem, QueueUpdatePayload, Download } from '@/types'
import { normalizeQueueSnapshot } from '@/utils/queueSnapshot'

const downloadsStore = useDownloadsStore()
const libraryStore = useLibraryStore()
const configStore = useConfigurationStore()

const filterText = ref('')
const queue = ref<QueueItem[]>([])
const queueClientStatuses = ref<QueueClientStatus[]>([])
const loading = ref(false)
const showRemoveModal = ref(false)
const clientHasQueueEntry = ref<boolean | null>(null)
const itemToRemove = ref<QueueItem | null>(null)
const removing = ref(false)
let unsubscribeQueue: (() => void) | null = null
let queueRefreshInterval: ReturnType<typeof setInterval> | null = null

const applyQueueSnapshot = (payload: QueueUpdatePayload | null | undefined) => {
  const snapshot = normalizeQueueSnapshot(payload)
  queue.value = snapshot.items
  queueClientStatuses.value = snapshot.clients
}

const formatSnapshotAge = (seconds?: number): string => {
  if (!seconds || seconds <= 0) return 'just now'
  if (seconds < 60) return `${seconds}s old`
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m old`
  return `${Math.floor(seconds / 3600)}h old`
}

const formatSnapshotReason = (reason?: string): string => {
  if (!reason) return 'client issue'
  if (reason === 'timeout') return 'a timeout'
  if (reason === 'canceled') return 'a canceled request'
  return 'a client error'
}

const getSnapshotStatusTitle = (item: QueueItem): string => {
  if (!item.isStaleSnapshot) return ''

  const parts = ['Showing cached queue data']
  if (item.downloadClient) parts.push(`for ${item.downloadClient}`)
  if (item.snapshotAgeSeconds != null)
    parts.push(`captured ${formatSnapshotAge(item.snapshotAgeSeconds)}`)
  if (item.snapshotFailureReason)
    parts.push(`after ${formatSnapshotReason(item.snapshotFailureReason)}`)
  return parts.join(' ')
}

const queueHealthClients = computed(() => {
  if (queueClientStatuses.value.length > 0) {
    return queueClientStatuses.value
      .filter((client) => client.isStaleSnapshot || client.isUnavailable)
      .map((client) => ({
        id: client.clientId || client.clientName,
        name: client.clientName || 'Download client',
        ageSeconds: client.snapshotAgeSeconds,
        failureReason: client.snapshotFailureReason,
        snapshotState: client.snapshotState,
        isUnavailable: client.isUnavailable,
      }))
  }

  const map = new Map<
    string,
    {
      id: string
      name: string
      ageSeconds?: number
      failureReason?: string
      snapshotState?: string
      isUnavailable?: boolean
    }
  >()

  queue.value.forEach((item) => {
    if (!item.isStaleSnapshot) return

    const key = item.downloadClientId || item.downloadClient || item.id
    if (!map.has(key)) {
      map.set(key, {
        id: key,
        name: item.downloadClient || 'Download client',
        ageSeconds: item.snapshotAgeSeconds,
        failureReason: item.snapshotFailureReason,
        snapshotState: item.snapshotState,
        isUnavailable: false,
      })
    }
  })

  return Array.from(map.values())
})

const queueHealthTitle = computed(() => {
  return queueHealthClients.value.some((client) => client.isUnavailable)
    ? 'Some queue data is unavailable'
    : 'Using cached queue data'
})

const queueHealthMessage = computed(() => {
  return queueHealthClients.value
    .map((client) => {
      const parts = [client.name]
      if (client.isUnavailable) {
        parts.push('unavailable')
      } else if (client.snapshotState === 'cached') {
        parts.push('using cached data')
      }
      if (client.ageSeconds != null) parts.push(formatSnapshotAge(client.ageSeconds))
      if (client.failureReason) parts.push(`after ${formatSnapshotReason(client.failureReason)}`)
      return parts.join(' ')
    })
    .join(', ')
})

// Virtual scrolling setup
const scrollContainer = ref<HTMLElement | null>(null)
const ROW_HEIGHT = 37
const BUFFER_ROWS = 5
const MOBILE_ACTIVITY_BREAKPOINT = 768

const visibleRange = ref({ start: 0, end: 30 })

const isMobileActivityLayout = ref(false)
const useVirtualActivityList = computed(() => !isMobileActivityLayout.value)

const updateActivityLayoutMode = () => {
  if (typeof window === 'undefined') return

  if (typeof window.matchMedia === 'function') {
    isMobileActivityLayout.value = window.matchMedia(
      `(max-width: ${MOBILE_ACTIVITY_BREAKPOINT}px)`,
    ).matches
    return
  }

  isMobileActivityLayout.value = window.innerWidth <= MOBILE_ACTIVITY_BREAKPOINT
}

const visibleQueueItems = computed(() => {
  if (!useVirtualActivityList.value) {
    return filteredQueue.value
  }

  return filteredQueue.value.slice(visibleRange.value.start, visibleRange.value.end)
})

const updateVisibleRange = () => {
  if (!useVirtualActivityList.value) {
    visibleRange.value = { start: 0, end: filteredQueue.value.length }
    return
  }

  if (!scrollContainer.value) return

  const scrollTop = scrollContainer.value.scrollTop
  const viewportHeight = scrollContainer.value.clientHeight

  const firstVisibleIndex = Math.floor(scrollTop / ROW_HEIGHT)
  const visibleItemCount = Math.ceil(viewportHeight / ROW_HEIGHT)

  const startIndex = Math.max(0, firstVisibleIndex - BUFFER_ROWS)
  const endIndex = Math.min(
    firstVisibleIndex + visibleItemCount + BUFFER_ROWS,
    filteredQueue.value.length,
  )

  visibleRange.value = { start: startIndex, end: endIndex }
}

const totalHeight = computed(() => {
  return filteredQueue.value.length * ROW_HEIGHT
})

const topPadding = computed(() => {
  return visibleRange.value.start * ROW_HEIGHT
})

const syncActivityLayout = async () => {
  await nextTick()
  updateVisibleRange()
}

const handleViewportResize = () => {
  updateActivityLayoutMode()
  void syncActivityLayout()
}

// Convert Download to QueueItem format for unified display
const convertDownloadToQueueItem = (download: Download): QueueItem => {
  const statusMap: Record<string, string> = {
    Queued: 'queued',
    Downloading: 'downloading',
    Paused: 'paused',
    Processing: 'processing',
    Completed: 'completed',
    Ready: 'completed',
    ImportPending: 'importpending',
    ImportBlocked: 'importblocked',
    Moved: 'imported',
    Failed: 'failed',
  }
  const status = statusMap[download.status] ?? 'downloading'

  const clientName = (download as unknown as Record<string, unknown>)['downloadClientName'] as
    | string
    | undefined
  return {
    id: download.id,
    title: download.title,
    audiobookId: download.audiobookId,
    status: status,
    progress: download.progress,
    size: download.totalSize,
    downloaded: download.downloadedSize,
    downloadSpeed: 0,
    eta: undefined,
    quality: '',
    downloadClient: clientName ?? download.downloadClientId ?? 'Unknown Client',
    downloadClientId: download.downloadClientId,
    downloadClientType:
      (download.downloadClientId || '').toString().toUpperCase() === 'DDL' ? 'DDL' : 'external',
    addedAt: download.startedAt,
    canPause: false,
    canRemove: true,
  }
}

// Read user preference from configuration store
const showCompletedExternalDownloads = computed(
  () => configStore.applicationSettings?.showCompletedExternalDownloads ?? false,
)

// All activity items — unified list of queue + downloads
const isDirectDownload = (downloadClientId?: string): boolean =>
  (downloadClientId || '').toString().toUpperCase() === 'DDL'

const allActivityItems = computed(() => {
  const queueItems = [...queue.value]
  const trackedQueueIds = new Set(queueItems.map((item) => item.id))

  const activeDownloadsList = unref(downloadsStore.activeDownloads || [])
  const failedDownloadsList = unref(downloadsStore.failedDownloads || [])

  const ddlDownloadItems = activeDownloadsList
    .filter((d) => isDirectDownload(d.downloadClientId))
    .filter((d) => !trackedQueueIds.has(d.id))
    .map(convertDownloadToQueueItem)

  const failedDDLItems = failedDownloadsList
    .filter((d) => isDirectDownload(d.downloadClientId))
    .filter((d) => !trackedQueueIds.has(d.id))
    .map(convertDownloadToQueueItem)

  const externalActiveDownloads = activeDownloadsList
    .filter((d) => d.downloadClientId && !isDirectDownload(d.downloadClientId))
    .filter((d) => !trackedQueueIds.has(d.id))
    .map(convertDownloadToQueueItem)

  let combined = [...queueItems, ...ddlDownloadItems, ...failedDDLItems, ...externalActiveDownloads]

  if (showCompletedExternalDownloads.value) {
    const completedExternal = (downloadsStore.completedDownloads || [])
      .filter(
        (d) => d.downloadClientId && (d.downloadClientId || '').toString().toUpperCase() !== 'DDL',
      )
      .map(convertDownloadToQueueItem)

    const map = new Map<string, QueueItem>()
    for (const it of combined) map.set(it.id, it)
    for (const it of completedExternal) {
      if (!map.has(it.id)) map.set(it.id, it)
    }

    combined = Array.from(map.values())
  } else {
    combined = combined.filter((it) => {
      if (
        (it.downloadClientType || '').toString().toLowerCase() !== 'ddl' &&
        it.status === 'completed'
      )
        return false
      return true
    })
  }

  // Also include completed external downloads from the downloads store
  // so completed items are visible in the unified table
  const completedExternal = (downloadsStore.completedDownloads || [])
    .filter(
      (d) => d.downloadClientId && (d.downloadClientId || '').toString().toUpperCase() !== 'DDL',
    )
    .map(convertDownloadToQueueItem)

  const failedFromDownloads = (downloadsStore.failedDownloads || []).map(convertDownloadToQueueItem)

  const finalMap = new Map<string, QueueItem>()
  for (const it of combined) finalMap.set(it.id, it)
  for (const it of completedExternal) if (!finalMap.has(it.id)) finalMap.set(it.id, it)
  for (const it of failedFromDownloads) if (!finalMap.has(it.id)) finalMap.set(it.id, it)

  return Array.from(finalMap.values())
})

// Build a lookup map from audiobook ID -> title for resolving friendly names
const audiobookTitleMap = computed(() => {
  const map = new Map<number, string>()
  for (const ab of libraryStore.audiobooks) {
    if (ab.id && ab.title) map.set(ab.id, ab.title)
  }
  return map
})

const getDisplayTitle = (item: QueueItem): string => {
  if (item.audiobookId) {
    const abTitle = audiobookTitleMap.value.get(item.audiobookId)
    if (abTitle) return abTitle
  }
  return item.title
}

// Filter by text search across title, client, status
const filteredQueue = computed(() => {
  const text = filterText.value.trim().toLowerCase()
  if (!text) return allActivityItems.value

  return allActivityItems.value.filter((item) => {
    const displayTitle = getDisplayTitle(item)
    return (
      (displayTitle && displayTitle.toLowerCase().includes(text)) ||
      (item.title && item.title.toLowerCase().includes(text)) ||
      (item.downloadClient && item.downloadClient.toLowerCase().includes(text)) ||
      (item.status && item.status.toLowerCase().includes(text)) ||
      (item.quality && item.quality.toLowerCase().includes(text))
    )
  })
})

const refreshQueue = async () => {
  loading.value = true
  try {
    const [queueSnapshot] = await Promise.all([
      apiService.getQueue(),
      downloadsStore.loadDownloads(),
    ])
    applyQueueSnapshot(queueSnapshot)
  } catch (err) {
    errorTracking.captureException(err as Error, {
      component: 'ActivityView',
      operation: 'refreshQueue',
    })
  } finally {
    loading.value = false
  }
}

const removeFromQueue = async (item: QueueItem) => {
  itemToRemove.value = item

  if (
    (item.downloadClientId || '').toString().toUpperCase() === 'DDL' ||
    (item.downloadClientType || '').toString().toUpperCase() === 'DDL'
  ) {
    clientHasQueueEntry.value = true
    showRemoveModal.value = true
    return
  }

  const found = queue.value.some((q) => q.id === item.id)
  clientHasQueueEntry.value = found
  showRemoveModal.value = true
}

const confirmRemove = async () => {
  if (!itemToRemove.value) return

  removing.value = true
  try {
    if (
      (itemToRemove.value.downloadClientId || '').toString().toUpperCase() === 'DDL' ||
      (itemToRemove.value.downloadClientType || '').toString().toUpperCase() === 'DDL'
    ) {
      await apiService.cancelDownload(itemToRemove.value.id)
      await downloadsStore.loadDownloads()
    } else {
      if (clientHasQueueEntry.value === false) {
        await apiService.cancelDownload(itemToRemove.value.id)
        await downloadsStore.loadDownloads()
      } else {
        await apiService.removeFromQueue(itemToRemove.value.id, itemToRemove.value.downloadClientId)
        await refreshQueue()
      }
    }

    showRemoveModal.value = false
    itemToRemove.value = null
    clientHasQueueEntry.value = null
  } catch (err) {
    errorTracking.captureException(err as Error, {
      component: 'ActivityView',
      operation: 'removeFromQueue',
    })
    const toast = useToast()
    toast.error('Remove failed', (err as Error).message)
  } finally {
    removing.value = false
  }
}

const formatStatus = (status: string): string => {
  const labels: Record<string, string> = {
    downloading: 'Downloading',
    queued: 'Queued',
    paused: 'Paused',
    completed: 'Completed',
    failed: 'Failed',
    processing: 'Processing',
    importpending: 'Importing',
    importblocked: 'Import Blocked',
    imported: 'Imported',
  }
  return labels[status] ?? status.charAt(0).toUpperCase() + status.slice(1)
}

const formatEta = (seconds: number): string => {
  if (seconds < 60) return `${seconds}s`
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m`
  if (seconds < 86400) return `${Math.floor(seconds / 3600)}h ${Math.floor((seconds % 3600) / 60)}m`
  return `${Math.floor(seconds / 86400)}d`
}

// Subscribe to SignalR for real-time updates
onMounted(async () => {
  updateActivityLayoutMode()
  if (typeof window !== 'undefined') {
    window.addEventListener('resize', handleViewportResize, { passive: true })
  }

  await downloadsStore.loadDownloads()
  await configStore.loadApplicationSettings()

  unsubscribeQueue = signalRService.onQueueUpdate((updatedQueue) => {
    applyQueueSnapshot(updatedQueue)
  })

  await refreshQueue()
  await syncActivityLayout()

  queueRefreshInterval = setInterval(async () => {
    try {
      await refreshQueue()
    } catch (err) {
      errorTracking.captureException(err as Error, {
        component: 'ActivityView',
        operation: 'queueRefreshInterval',
      })
    }
  }, 30000)
})

onUnmounted(() => {
  if (typeof window !== 'undefined') {
    window.removeEventListener('resize', handleViewportResize)
  }

  if (unsubscribeQueue) {
    unsubscribeQueue()
  }

  if (queueRefreshInterval) {
    clearInterval(queueRefreshInterval)
    queueRefreshInterval = null
  }
})
</script>

<style scoped>
.activity-view {
  padding: 1em;
}

.page-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 0.75rem;
  flex-wrap: wrap;
  gap: 0.75rem;
}

.page-header h1 {
  margin: 0;
  color: white;
  font-size: 2rem;
  display: flex;
  align-items: center;
  gap: 0.5rem;
  font-weight: 500;
}

.page-header h1 svg {
  width: 32px;
  height: 32px;
}

.activity-actions {
  display: flex;
  gap: 0.75rem;
  align-items: center;
}

.filter-input-wrapper {
  position: relative;
  display: flex;
  align-items: center;
}

.filter-icon {
  position: absolute;
  left: 0.75rem;
  color: #868e96;
  width: 16px;
  height: 16px;
  pointer-events: none;
}

.filter-input {
  background: #2a2a2a;
  border: 1px solid rgba(255, 255, 255, 0.1);
  border-radius: 6px;
  color: #fff;
  padding: 0.5rem 2rem 0.5rem 2.25rem;
  font-size: 0.875rem;
  width: 220px;
  height: var(--control-height, 40px);
  box-sizing: border-box;
  transition:
    border-color 0.2s,
    box-shadow 0.2s;
}

.filter-input::placeholder {
  color: #868e96;
}

.filter-input:focus {
  outline: none;
  border-color: #4dabf7;
  box-shadow: 0 0 0 2px rgba(77, 171, 247, 0.15);
}

.filter-clear {
  position: absolute;
  right: 0.5rem;
  background: none;
  border: none;
  color: #868e96;
  cursor: pointer;
  padding: 0.25rem;
  border-radius: 4px;
  display: flex;
  align-items: center;
  justify-content: center;
  transition: color 0.2s;
}

.filter-clear:hover {
  color: #fa5252;
  background: rgba(250, 82, 82, 0.15);
}

.filter-clear svg {
  width: 14px;
  height: 14px;
}

/* Queue health banner */
.queue-health-banner {
  display: flex;
  align-items: flex-start;
  gap: 0.75rem;
  padding: 0.9rem 1rem;
  margin-bottom: 1rem;
  border: 1px solid rgba(217, 119, 6, 0.28);
  background: rgba(245, 158, 11, 0.12);
  color: var(--color-text, #1f2937);
  border-radius: 0.85rem;
}

.queue-health-copy {
  display: flex;
  flex-direction: column;
  gap: 0.2rem;
}

.queue-health-copy strong {
  font-size: 0.95rem;
}

.queue-health-copy span {
  font-size: 0.88rem;
  opacity: 0.86;
}

/* Grid container with virtual scrolling */
.queue-grid-container {
  height: calc(100vh - 200px);
  overflow-y: auto;
  position: relative;
  border: 1px solid rgba(255, 255, 255, 0.06);
  border-radius: 6px;
  background: #1e1e1e;
}

.queue-grid-container.is-static {
  height: auto;
  overflow-y: visible;
}

/* Desktop grid columns shared by header and rows */
.queue-header,
.queue-row {
  display: grid;
  grid-template-columns:
    minmax(0, 3fr) minmax(0, 1fr) minmax(0, 1fr) minmax(0, 2fr) minmax(0, 1fr)
    minmax(0, 1fr) 40px;
  align-items: center;
}

.queue-header {
  position: sticky;
  top: 0;
  z-index: 2;
  background: #252525;
  padding: 0.5rem 0;
  border-bottom: 1px solid rgba(255, 255, 255, 0.08);
}

.queue-header > div {
  padding: 0 0.75rem;
  font-size: 0.75rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.5px;
  color: #868e96;
  white-space: nowrap;
}

.queue-body-spacer {
  position: relative;
  width: 100%;
}

.queue-body-spacer.is-static {
  position: static;
}

.queue-body {
  position: absolute;
  top: 0;
  left: 0;
  width: 100%;
  overflow: hidden;
}

.queue-body.is-static {
  position: static;
}

/* Grid rows */
.queue-row {
  transition: background-color 0.15s;
}

.queue-row:hover {
  background-color: rgba(255, 255, 255, 0.03);
}

.queue-row > div {
  padding: 0.5rem 0.75rem;
  font-size: 0.85rem;
  color: #adb5bd;
  border-bottom: 1px solid rgba(255, 255, 255, 0.04);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  display: flex;
  align-items: center;
  align-self: stretch;
}

/* Progress column needs visible overflow for its flex layout */
.queue-row > .col-progress {
  overflow: visible;
}

/* Actions column: center the button */
.queue-row > .col-actions {
  justify-content: center;
  overflow: visible;
}

/* Title cell */
.title-cell {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  min-width: 0;
}

.title-text,
.title-link {
  color: white;
  font-weight: 500;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.title-link {
  text-decoration: none;
}

.title-link:hover {
  color: #4dabf7;
}

.quality-tag {
  flex-shrink: 0;
  font-size: 0.7rem;
  padding: 0.1rem 0.4rem;
  border-radius: 4px;
  background: rgba(81, 207, 102, 0.12);
  color: #51cf66;
  font-weight: 500;
}

/* Language cell */
.language-text {
  color: #adb5bd;
  font-size: 0.8rem;
}

/* Progress cell */
.progress-cell {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  width: 100%;
  min-width: 0;
}

.progress-cell :deep(.progress-wrapper) {
  flex: 1;
  min-width: 0;
}

.progress-label {
  font-size: 0.8rem;
  font-weight: 500;
  color: white;
  white-space: nowrap;
  min-width: 40px;
  text-align: right;
}

/* Size */
.size-text {
  font-size: 0.8rem;
  color: #868e96;
}

/* Speed */
.speed-text {
  color: #51cf66;
  font-size: 0.8rem;
}

/* ETA */
.eta-text {
  color: #ffd43b;
  font-size: 0.8rem;
}

.muted {
  color: #495057;
  font-size: 0.8rem;
}

/* Status badges */
.status-badge {
  padding: 0.2rem 0.5rem;
  border-radius: 4px;
  font-size: 0.7rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.3px;
  display: inline-block;
}

.status-badge.completed {
  background-color: rgba(81, 207, 102, 0.15);
  color: #51cf66;
}

.status-badge.downloading {
  background-color: rgba(77, 171, 247, 0.15);
  color: #4dabf7;
}

.status-badge.failed {
  background-color: rgba(250, 82, 82, 0.15);
  color: #fa5252;
}

.status-badge.paused {
  background-color: rgba(255, 212, 59, 0.15);
  color: #ffd43b;
}

.status-badge.queued {
  background-color: rgba(134, 142, 150, 0.15);
  color: #868e96;
}

.status-badge.processing {
  background-color: rgba(190, 75, 219, 0.15);
  color: #be4bdb;
}

.status-badge.importpending {
  background-color: rgba(255, 146, 43, 0.15);
  color: #ff922b;
}

.status-badge.importblocked {
  background-color: rgba(250, 82, 82, 0.15);
  color: #fa5252;
}

.status-badge.imported {
  background-color: rgba(32, 201, 151, 0.15);
  color: #20c997;
}

.stale-badge {
  display: inline-block;
  margin-left: 0.35rem;
  padding: 0.1rem 0.35rem;
  border-radius: 4px;
  background: rgba(245, 158, 11, 0.16);
  color: #b45309;
  font-size: 0.65rem;
  font-weight: 600;
}

/* Actions */
.btn-danger-icon {
  background: none;
  border: none;
  color: #868e96;
  cursor: pointer;
  padding: 0.35rem;
  border-radius: 4px;
  display: flex;
  align-items: center;
  justify-content: center;
  transition: all 0.2s;
}

.btn-danger-icon:hover {
  background: rgba(250, 82, 82, 0.15);
  color: #fa5252;
}

.btn-danger-icon svg {
  width: 16px;
  height: 16px;
}

/* Modal Styles */
.modal-overlay {
  position: fixed;
  top: 0;
  left: 0;
  right: 0;
  bottom: 0;
  background-color: rgba(0, 0, 0, 0.85);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 1000;
  animation: fadeIn 0.2s ease;
  backdrop-filter: blur(4px);
}

.modal-content {
  background-color: #2a2a2a;
  border-radius: 6px;
  width: 90%;
  max-width: 500px;
  box-shadow: 0 20px 60px rgba(0, 0, 0, 0.5);
  animation: slideUp 0.3s ease;
  border: 1px solid rgba(255, 255, 255, 0.1);
}

@keyframes slideUp {
  from {
    transform: translateY(20px);
    opacity: 0;
  }
  to {
    transform: translateY(0);
    opacity: 1;
  }
}

.modal-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 1.5rem;
  border-bottom: 1px solid rgba(255, 255, 255, 0.1);
}

.modal-header h3 {
  margin: 0;
  color: white;
  font-size: 1.25rem;
  font-weight: 500;
  display: flex;
  align-items: center;
  gap: 0.625rem;
}

.modal-header h3 svg {
  color: #ffd43b;
  width: 24px;
  height: 24px;
}

.modal-close {
  background: none;
  border: none;
  color: #868e96;
  cursor: pointer;
  padding: 0.5rem;
  border-radius: 6px;
  transition: all 0.2s;
  font-size: 1.5rem;
  line-height: 1;
}

.modal-close:hover {
  background-color: rgba(255, 255, 255, 0.08);
  color: white;
}

.modal-close svg {
  width: 20px;
  height: 20px;
}

.modal-body {
  padding: 1.5rem;
}

.modal-body > p:first-child {
  color: #adb5bd;
  margin: 0 0 1rem 0;
  font-size: 1rem;
  line-height: 1.5;
}

.remove-item-info {
  background-color: rgba(0, 0, 0, 0.3);
  border-left: 4px solid #ffd43b;
  padding: 1rem;
  border-radius: 6px;
  margin-bottom: 1rem;
}

.remove-item-info strong {
  color: white;
  display: block;
  margin-bottom: 0.5rem;
  font-size: 1rem;
  font-weight: 500;
}

.item-details {
  display: flex;
  gap: 1rem;
  font-size: 0.875rem;
  color: #868e96;
  flex-wrap: wrap;
}

.item-details span {
  display: flex;
  align-items: center;
  gap: 0.35rem;
}

.item-details svg {
  width: 14px;
  height: 14px;
}

.warning-text {
  color: #ffd43b;
  font-size: 0.875rem;
  display: flex;
  align-items: flex-start;
  gap: 0.625rem;
  background-color: rgba(255, 212, 59, 0.1);
  padding: 0.875rem;
  border-radius: 6px;
  margin: 0;
  border: 1px solid rgba(255, 212, 59, 0.25);
  line-height: 1.5;
}

.warning-text svg {
  flex-shrink: 0;
  margin-top: 0.1rem;
  width: 18px;
  height: 18px;
}

.modal-footer {
  justify-content: flex-end;
  gap: 0.75rem;
}

.btn-danger {
  background: #fa5252;
  color: white;
  border: none;
  padding: 0.65rem 1.25rem;
  border-radius: 6px;
  cursor: pointer;
  font-weight: 500;
  display: flex;
  align-items: center;
  gap: 0.5rem;
  transition: all 0.2s;
  font-size: 0.9rem;
  box-shadow: 0 2px 8px rgba(250, 82, 82, 0.3);
}

.btn-danger:hover:not(:disabled) {
  background: #e03131;
  transform: translateY(-1px);
  box-shadow: 0 4px 12px rgba(250, 82, 82, 0.4);
}

.btn-danger:active:not(:disabled) {
  transform: translateY(0);
}

.btn-danger:disabled {
  opacity: 0.5;
  cursor: not-allowed;
  transform: none;
}

.btn-danger svg {
  width: 16px;
  height: 16px;
}

/* Mobile responsive */
@media (max-width: 768px) {
  .page-header {
    flex-direction: column;
    align-items: flex-start;
    gap: 0.75rem;
    margin-bottom: 1rem;
  }

  .activity-actions {
    width: 100%;
    flex-wrap: wrap;
  }

  .filter-input-wrapper {
    flex: 1;
    min-width: 0;
  }

  .filter-input {
    width: 100%;
  }

  .queue-grid-container {
    height: auto;
    overflow-y: visible;
    border: none;
    background: transparent;
  }

  .queue-header {
    display: none;
  }

  .queue-body-spacer {
    height: auto !important;
    position: static !important;
  }

  .queue-body {
    position: static !important;
    transform: none !important;
  }

  /* Each row becomes a card */
  .queue-row {
    grid-template-columns: 1fr auto;
    grid-template-rows: auto auto auto;
    gap: 0.3rem 0.5rem;
    padding: 0.75rem;
    margin-bottom: 0.5rem;
    background: #2a2a2a;
    border-radius: 6px;
    border: 1px solid rgba(255, 255, 255, 0.06);
  }

  .queue-row:hover {
    background-color: #2f2f2f;
  }

  .queue-row > div {
    padding: 0;
    border: none;
    overflow: visible;
    white-space: normal;
  }

  /* Hide columns that don't fit mobile */
  .queue-row .col-quality,
  .queue-row .col-language,
  .queue-row .col-eta {
    display: none;
  }

  /* Row 1: Title (left) + Status badge (right) */
  .queue-row .col-title {
    grid-column: 1;
    grid-row: 1;
    min-width: 0;
  }

  .queue-row .col-status {
    grid-column: 2;
    grid-row: 1;
    display: flex;
    align-items: center;
    justify-content: flex-end;
    white-space: nowrap;
  }

  /* Row 2: Progress bar (left) + Actions (right) */
  .queue-row .col-progress {
    grid-column: 1;
    grid-row: 2;
  }

  .queue-row .col-actions {
    grid-column: 2;
    grid-row: 2;
    display: flex;
    align-items: center;
    justify-content: flex-end;
  }
}
</style>
