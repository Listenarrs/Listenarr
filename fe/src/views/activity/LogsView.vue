<template>
  <div class="logs-view">
    <div class="page-header">
      <div class="header-content">
        <h1><PhFileText /> Application Logs</h1>
        <p>View and search application log entries</p>
      </div>
      <div class="header-actions">
        <button class="action-button btn" @click="refreshLogs" :disabled="loading">
          <template v-if="loading">
            <PhSpinner class="ph-spin" />
          </template>
          <template v-else>
            <PhArrowClockwise />
          </template>
          {{ loading ? 'Refreshing...' : 'Refresh' }}
        </button>
        <button class="action-button primary btn btn-primary" @click="downloadLogs">
          <PhDownloadSimple />
          Download Logs
        </button>
      </div>
    </div>

    <!-- Filters -->
    <div class="filters-section">
      <div class="filter-group">
        <label>Log Level</label>
        <select v-model="selectedLevel" @change="applyFilters">
          <option value="">All Levels</option>
          <option value="Info">Info</option>
          <option value="Warning">Warning</option>
          <option value="Error">Error</option>
          <option value="Debug">Debug</option>
        </select>
      </div>
      <div class="filter-group">
        <label>Search</label>
        <div class="search-input">
          <PhMagnifyingGlass />
          <input
            type="text"
            v-model="searchQuery"
            @input="applyFilters"
            placeholder="Search log messages..."
            class="form-input"
          />
          <button v-if="searchQuery" class="clear-button" @click="clearSearch" title="Clear search">
            <PhX />
          </button>
        </div>
      </div>
    </div>

    <!-- Error State -->
    <div v-if="error" class="error-message">
      <PhWarningCircle />
      <div>
        <strong>Error loading logs</strong>
        <p>{{ error }}</p>
      </div>
    </div>

    <!-- Loading State -->
    <LoadingState v-if="loading && logs.length === 0" message="Loading logs..." />

    <!-- Logs Container -->
    <div v-else-if="filteredLogs.length > 0" class="logs-wrapper">
      <div class="logs-container">
        <div
          v-for="log in paginatedLogs"
          :key="log.id"
          :class="['log-entry', log.level.toLowerCase()]"
        >
          <component :is="getLogIconComponent(log.level)" class="log-icon" />
          <span class="log-time">{{ formatTimestamp(log.timestamp) }}</span>
          <span class="log-level">{{ log.level.toUpperCase() }}</span>
          <span class="log-message">{{ log.message }}</span>
        </div>
      </div>

      <!-- Pagination -->
      <div class="pagination">
        <div class="pagination-info">
          Showing {{ startIndex + 1 }} - {{ endIndex }} of {{ filteredLogs.length }} logs
        </div>
        <div class="pagination-controls">
          <button
            class="page-button"
            @click="previousPage"
            :disabled="currentPage === 1"
            title="Previous page"
          >
            <PhCaretLeft />
          </button>

          <button
            v-for="page in visiblePages"
            :key="page"
            :class="['page-button', { active: page === currentPage }]"
            @click="goToPage(page)"
          >
            {{ page }}
          </button>

          <button
            class="page-button"
            @click="nextPage"
            :disabled="currentPage === totalPages"
            title="Next page"
          >
            <PhCaretRight />
          </button>
        </div>
        <div class="pagination-size">
          <label>Per page:</label>
          <select v-model.number="pageSize" @change="changePageSize">
            <option :value="10">10</option>
            <option :value="25">25</option>
            <option :value="50">50</option>
            <option :value="100">100</option>
          </select>
        </div>
      </div>
    </div>

    <!-- Empty State -->
    <EmptyState
      v-else
      title="No logs found"
      :message="searchQuery || selectedLevel ? 'Try adjusting your filters' : 'No log entries available'"
    >
      <template #icon>
        <PhFileText :size="48" />
      </template>
    </EmptyState>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import {
  PhFileText,
  PhSpinner,
  PhArrowClockwise,
  PhDownloadSimple,
  PhMagnifyingGlass,
  PhX,
  PhWarningCircle,
  PhWarning,
  PhBug,
  PhInfo,
  PhCaretLeft,
  PhCaretRight,
} from '@phosphor-icons/vue'
import { errorTracking } from '@/services/errorTracking'
import { getLogs, downloadLogs as downloadLogsApi } from '@/services/api'
import { EmptyState, LoadingState } from '@/components/base'
import type { LogEntry } from '@/types'

const logs = ref<LogEntry[]>([])
const loading = ref(true)
const error = ref<string | null>(null)
const searchQuery = ref('')
const selectedLevel = ref('')
const currentPage = ref(1)
const pageSize = ref(25)

// Load logs
const loadLogs = async () => {
  loading.value = true
  error.value = null

  try {
    const data = await getLogs(1000) // Get up to 1000 logs
    logs.value = data
  } catch (err) {
    error.value = err instanceof Error ? err.message : 'Failed to load logs'
    errorTracking.captureException(err as Error, { component: 'LogsView', operation: 'loadLogs' })
  } finally {
    loading.value = false
  }
}

// Filter logs
const filteredLogs = computed(() => {
  let filtered = [...logs.value]

  // Filter by level
  if (selectedLevel.value) {
    filtered = filtered.filter(
      (log) => log.level.toLowerCase() === selectedLevel.value.toLowerCase(),
    )
  }

  // Filter by search query
  if (searchQuery.value) {
    const query = searchQuery.value.toLowerCase()
    filtered = filtered.filter(
      (log) =>
        log.message.toLowerCase().includes(query) ||
        log.source?.toLowerCase().includes(query) ||
        log.exception?.toLowerCase().includes(query),
    )
  }

  return filtered
})

// Pagination
const totalPages = computed(() => Math.ceil(filteredLogs.value.length / pageSize.value))

const startIndex = computed(() => (currentPage.value - 1) * pageSize.value)
const endIndex = computed(() =>
  Math.min(startIndex.value + pageSize.value, filteredLogs.value.length),
)

const paginatedLogs = computed(() => {
  return filteredLogs.value.slice(startIndex.value, endIndex.value)
})

const visiblePages = computed(() => {
  const pages: number[] = []
  const maxVisible = 5
  let start = Math.max(1, currentPage.value - Math.floor(maxVisible / 2))
  const end = Math.min(totalPages.value, start + maxVisible - 1)

  if (end - start < maxVisible - 1) {
    start = Math.max(1, end - maxVisible + 1)
  }

  for (let i = start; i <= end; i++) {
    pages.push(i)
  }

  return pages
})

const goToPage = (page: number) => {
  if (page >= 1 && page <= totalPages.value) {
    currentPage.value = page
    window.scrollTo({ top: 0, behavior: 'smooth' })
  }
}

const nextPage = () => {
  if (currentPage.value < totalPages.value) {
    goToPage(currentPage.value + 1)
  }
}

const previousPage = () => {
  if (currentPage.value > 1) {
    goToPage(currentPage.value - 1)
  }
}

const changePageSize = () => {
  currentPage.value = 1 // Reset to first page when changing page size
}

// Actions
const refreshLogs = async () => {
  await loadLogs()
}

const downloadLogs = () => {
  downloadLogsApi()
}

const applyFilters = () => {
  currentPage.value = 1 // Reset to first page when filtering
}

const clearSearch = () => {
  searchQuery.value = ''
  applyFilters()
}

// Formatting
const formatTimestamp = (timestamp: string): string => {
  const date = new Date(timestamp)
  return date.toLocaleString('en-US', {
    month: 'short',
    day: 'numeric',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
  })
}

const getLogIconComponent = (level: string) => {
  switch (level.toLowerCase()) {
    case 'info':
      return PhInfo
    case 'warning':
      return PhWarning
    case 'error':
      return PhX
    case 'debug':
      return PhBug
    default:
      return PhInfo
  }
}

// Load logs on mount
onMounted(() => {
  loadLogs()
})
</script>

<style scoped>
.logs-view {
  padding: 2rem;
  max-width: 1600px;
  margin: 0 auto;
}

/* Page Header */
.page-header {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  margin-bottom: 2rem;
  gap: 2rem;
}

.header-content h1 {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  margin: 0 0 0.5rem 0;
  color: #fff;
  font-size: 2rem;
  font-weight: 500;
}

.header-content h1 i {
  font-size: 2rem;
  color: var(--brand-500);
}

.header-content p {
  color: #999;
  font-size: 1rem;
  margin: 0;
}

.header-actions {
  display: flex;
  gap: 0.75rem;
}

.action-button {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  padding: 0.65rem 1.25rem;
  background: #2a2a2a;
  color: #fff;
  border: 1px solid #444;
  border-radius: 6px;
  font-size: 0.95rem;
  cursor: pointer;
  transition: all 0.2s;
}

.action-button:hover:not(:disabled) {
  background: #333;
  border-color: var(--brand-500);
  transform: translateY(-1px);
}

.action-button.primary {
  background: var(--brand-500);
  border-color: var(--brand-500);
}

.action-button.primary:hover:not(:disabled) {
  background: var(--brand-700);
}

.action-button:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

.action-button i {
  font-size: 1.1rem;
}

/* Filters Section */
.filters-section {
  display: flex;
  gap: 1rem;
  margin-bottom: 1.5rem;
  padding: 1.25rem;
  background: #1e1e1e;
  border: 1px solid #333;
  border-radius: 6px;
}

.filter-group {
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
}

.filter-group label {
  color: #999;
  font-size: 0.85rem;
  font-weight: 500;
  text-transform: uppercase;
  letter-spacing: 0.5px;
}

.filter-group select,
.filter-group input {
  padding: 0.65rem;
  background: #252525;
  color: #fff;
  border: 1px solid #444;
  border-radius: 6px;
  font-size: 0.95rem;
  transition: all 0.2s;
}

.filter-group select:focus,
.filter-group input:focus {
  outline: none;
  border-color: var(--brand-focus);
  background: #2a2a2a;
}

.search-input {
  position: relative;
  display: block;
}

.search-input > svg {
  position: absolute;
  left: 0.75rem;
  top: 50%;
  transform: translateY(-50%);
  color: #666;
  width: 16px;
  height: 16px;
  pointer-events: none;
  z-index: 1;
}

.search-input input {
  width: 100%;
  padding-right: 2.75rem; /* room for clear button */
  padding-left: 2.5rem; /* keep space for search icon */
  box-sizing: border-box;
}

.search-input input:focus {
  outline: none;
}

.clear-button {
  position: absolute;
  right: 0.5rem;
  top: 50%;
  transform: translateY(-50%);
  padding: 0.35rem;
  background: transparent;
  color: #666;
  border: none;
  border-radius: 6px;
  cursor: pointer;
  display: flex;
  align-items: center;
  justify-content: center;
  transition: all 0.2s;
  z-index: 1;
}

.clear-button:hover {
  color: #fff;
  background: #333;
}

.clear-button svg {
  width: 16px;
  height: 16px;
}

/* Error Message */
.error-message {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  padding: 1rem;
  background: rgba(231, 76, 60, 0.1);
  border: 1px solid rgba(231, 76, 60, 0.3);
  border-radius: 6px;
  color: #e74c3c;
  margin-bottom: 2rem;
}

.error-message i {
  font-size: 1.5rem;
  flex-shrink: 0;
}

.error-message strong {
  display: block;
  margin-bottom: 0.25rem;
}

.error-message p {
  margin: 0;
  font-size: 0.9rem;
}

/* Loading State */
.loading-state {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 1rem;
  padding: 4rem;
  color: #999;
}

.loading-state i {
  font-size: 2.5rem;
  color: var(--brand-500);
}

/* Logs Container */
.logs-wrapper {
  background: #1e1e1e;
  border: 1px solid #333;
  border-radius: 6px;
  overflow: hidden;
}

.logs-container {
  max-height: calc(100vh - 400px);
  overflow-y: auto;
  background: #252525;
}

.log-entry {
  display: grid;
  grid-template-columns: auto auto auto 1fr;
  gap: 1rem;
  padding: 0.85rem 1rem;
  border-bottom: 1px solid #2a2a2a;
  font-family: 'Consolas', 'Monaco', 'Courier New', monospace;
  font-size: 0.85rem;
  align-items: center;
  transition: background 0.2s;
}

.log-entry:hover {
  background: #2a2a2a;
}

.log-entry:last-child {
  border-bottom: none;
}

.log-entry.info {
  border-left: 3px solid var(--brand-500);
}

.log-entry.warning {
  border-left: 3px solid #f39c12;
}

.log-entry.error {
  border-left: 3px solid #e74c3c;
}

.log-entry.debug {
  border-left: 3px solid #9b59b6;
}

.log-icon svg {
  width: 18px;
  height: 18px;
}

.log-entry.info .log-icon svg {
  color: var(--brand-500);
}

.log-entry.warning .log-icon svg {
  color: #f39c12;
}

.log-entry.error .log-icon svg {
  color: #e74c3c;
}

.log-entry.debug .log-icon svg {
  color: #9b59b6;
}

.log-time {
  color: #666;
  font-size: 0.8rem;
}

.log-level {
  font-weight: 500;
  font-size: 0.75rem;
  padding: 0.25rem 0.5rem;
  border-radius: 6px;
  letter-spacing: 0.5px;
}

.log-entry.info .log-level {
  background: rgba(var(--brand-rgb), 0.15);
  color: var(--brand-500);
}

.log-entry.warning .log-level {
  background: rgba(243, 156, 18, 0.15);
  color: #f39c12;
}

.log-entry.error .log-level {
  background: rgba(231, 76, 60, 0.15);
  color: #e74c3c;
}

.log-entry.debug .log-level {
  background: rgba(155, 89, 182, 0.15);
  color: #9b59b6;
}

.log-message {
  color: #ccc;
}

/* Scrollbar Styles */
.logs-container::-webkit-scrollbar {
  width: 8px;
}

.logs-container::-webkit-scrollbar-track {
  background: #1e1e1e;
  border-radius: 6px;
}

.logs-container::-webkit-scrollbar-thumb {
  background: #444;
  border-radius: 6px;
}

.logs-container::-webkit-scrollbar-thumb:hover {
  background: #555;
}

/* Pagination */
.pagination {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 1rem;
  background: #1e1e1e;
  border-top: 1px solid #333;
}

.pagination-info {
  color: #999;
  font-size: 0.9rem;
}

.pagination-controls {
  display: flex;
  gap: 0.5rem;
}

.page-button {
  padding: 0.5rem 0.75rem;
  background: transparent;
  color: #999;
  border: 1px solid #444;
  border-radius: 6px;
  font-size: 0.9rem;
  cursor: pointer;
  transition: all 0.2s;
  min-width: 40px;
  display: flex;
  align-items: center;
  justify-content: center;
}

.page-button:hover:not(:disabled) {
  background: #333;
  color: #fff;
  border-color: var(--brand-500);
}

.page-button.active {
  background: var(--brand-500);
  color: #fff;
  border-color: var(--brand-500);
}

.page-button:disabled {
  opacity: 0.4;
  cursor: not-allowed;
}

.pagination-size {
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

.pagination-size label {
  color: #999;
  font-size: 0.9rem;
}

.pagination-size select {
  padding: 0.5rem;
  background: #1e1e1e;
  color: #fff;
  border: 1px solid #444;
  border-radius: 6px;
  font-size: 0.9rem;
  cursor: pointer;
}

.pagination-size select:focus {
  outline: none;
  border-color: var(--brand-focus);
}

/* Empty State */
.empty-state {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 1rem;
  padding: 4rem;
  color: #666;
  text-align: center;
}

.empty-state i {
  font-size: 4rem;
  color: #444;
}

.empty-state h3 {
  margin: 0;
  color: #999;
  font-size: 1.25rem;
}

.empty-state p {
  margin: 0;
  color: #666;
  font-size: 0.95rem;
}

/* Responsive */
@media (max-width: 1200px) {
  .table-header,
  .log-row {
    grid-template-columns: 180px 90px 120px 1fr;
  }
}

@media (max-width: 768px) {
  .logs-view {
    padding: 1rem;
  }

  .page-header {
    flex-direction: column;
    gap: 1rem;
  }

  .header-actions {
    width: 100%;
  }

  .action-button {
    flex: 1;
  }

  .filters-section {
    flex-direction: column;
  }

  .log-entry {
    grid-template-columns: 1fr;
    gap: 0.5rem;
    padding: 0.75rem;
  }

  .log-time {
    order: 1;
  }

  .log-level {
    order: 2;
  }

  .log-message {
    order: 3;
    grid-column: 1;
  }

  .pagination {
    flex-direction: column;
    gap: 1rem;
  }

  .pagination-controls {
    order: 2;
  }

  .pagination-info {
    order: 1;
  }

  .pagination-size {
    order: 3;
  }

  /* Avoid overly tall log container on small viewports — allow more breathing room */
  .logs-container {
    max-height: calc(100vh - 300px);
  }
}
</style>
