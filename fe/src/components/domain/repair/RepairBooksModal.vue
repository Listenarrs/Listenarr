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
  <Modal :visible="visible" size="lg" @close="handleClose">
    <template #header>
      <ModalHeader title="Repair Books" :icon="PhWrench" @close="handleClose" />
    </template>

    <template #default>
      <ModalBody compact maxHeight="72vh" class="repair-modal-body">
        <div v-if="loading" class="repair-state">
          <PhSpinner class="ph-spin repair-icon" />
          <p>Loading the selected audiobooks…</p>
        </div>

        <div v-else-if="error" class="repair-state repair-error">
          <PhWarningCircle class="repair-icon" />
          <p>{{ error }}</p>
        </div>

        <div v-else-if="executing" class="repair-state">
          <PhSpinner class="ph-spin repair-icon" />
          <p>Repairing {{ progress }} of {{ selectedCount }}…</p>
        </div>

        <div v-else-if="!finished && rows.length" class="repair-list-wrapper">
          <div class="info-section repair-info-section">
            <PhInfo />
            <p>
              Point each book at the right Audible record: search by title and author in the right
              region and pick the result, or type the ASIN (it is looked up in the selected region).
              Repairing saves the ASIN and its region and rescans the metadata from it.
            </p>
          </div>

          <div class="form-group repair-group">
            <label class="form-label">
              <PhCheckSquare />
              Selection
            </label>
            <div class="form-control-card">
              <div class="repair-toolbar">
                <p class="repair-toolbar-title">
                  <strong>{{ selectedCount }}</strong> of {{ rows.length }} audiobook(s) selected
                </p>
                <div class="toolbar-actions">
                  <button
                    type="button"
                    class="btn btn-secondary repair-action-btn"
                    @click="selectAll"
                  >
                    Select All
                  </button>
                  <button
                    type="button"
                    class="btn btn-secondary repair-action-btn"
                    @click="clearSelection"
                  >
                    Clear
                  </button>
                </div>
              </div>
            </div>
          </div>

          <div class="form-group repair-group">
            <label class="form-label">
              <PhWrench />
              Books
            </label>
            <div class="repair-list">
              <div
                v-for="row in rows"
                :key="row.id"
                class="form-control-card repair-card"
                :data-testid="`repair-row-${row.id}`"
              >
                <label class="repair-header">
                  <span class="repair-heading">
                    <input
                      type="checkbox"
                      class="repair-checkbox"
                      :checked="row.selected"
                      @change="row.selected = !row.selected"
                    />
                    <span class="repair-title">{{ row.title || `Audiobook #${row.id}` }}</span>
                  </span>
                  <span class="repair-header-right">
                    <span class="repair-meta">
                      {{ row.authors.join(', ') || 'Unknown author' }}
                      <template v-if="row.currentAsin">
                        · {{ row.currentAsin }}
                        <template v-if="row.currentRegion"> ({{ row.currentRegion }})</template>
                      </template>
                    </span>
                    <!-- The book's current problems, in orange as on the Issues page -->
                    <Pill
                      v-for="code in currentIssues(row)"
                      :key="code"
                      variant="warning"
                      data-testid="repair-issue"
                    >
                      {{ issueLabel(code) }}
                    </Pill>
                    <button
                      type="button"
                      class="btn btn-secondary repair-revert-btn"
                      title="Revert"
                      aria-label="Revert to the values before your changes"
                      data-testid="repair-revert"
                      :disabled="!isEdited(row)"
                      @click.prevent.stop="revert(row)"
                    >
                      <PhArrowCounterClockwise :size="14" />
                    </button>
                  </span>
                </label>

                <AudibleRecordSearch
                  class="repair-record-search"
                  :state="row"
                  :region-options="regionOptionsFor(row)"
                  @pick="row.selected = true"
                  @found="row.selected = true"
                >
                  <template #hint>
                    <p
                      v-if="needsPick(row) && !row.asin.trim() && !row.asinCleared"
                      class="repair-hint"
                      data-testid="repair-needs-pick"
                    >
                      Pick the right book (or type its ASIN) before repairing.
                    </p>
                  </template>
                </AudibleRecordSearch>
              </div>
            </div>
          </div>
        </div>

        <div v-if="finished && results.length" class="form-group repair-results-group">
          <label class="form-label">
            <PhCheckCircle />
            Results
          </label>
          <div class="form-control-card results-list">
            <div
              v-for="result in results"
              :key="result.id"
              class="result-row"
              :class="{ success: result.success, error: !result.success }"
            >
              <component
                :is="result.success ? PhCheckCircle : PhWarningCircle"
                class="result-icon"
              />
              <span class="result-title">{{ result.title }}</span>
              <span class="result-detail">{{ result.detail }}</span>
            </div>
          </div>
        </div>
      </ModalBody>
    </template>

    <template #footer>
      <ModalFooter :showCancel="false">
        <template #left>
          <button
            type="button"
            class="btn cancel-button"
            :disabled="executing"
            @click="handleClose"
          >
            <PhX :size="16" />
            {{ finished ? 'Close' : 'Cancel' }}
          </button>
        </template>
        <template #default>
          <button
            v-if="!finished"
            type="button"
            class="btn btn-primary"
            data-testid="repair-confirm"
            :disabled="loading || executing || selectedCount === 0 || selectedRows.some(needsPick)"
            @click="repair"
          >
            <PhSpinner v-if="executing" class="ph-spin" :size="16" />
            <PhWrench v-else :size="16" />
            {{ executing ? 'Repairing…' : `Repair ${selectedCount}` }}
          </button>
          <template v-else>
            <button
              v-if="repairedIds.length"
              type="button"
              class="btn btn-secondary"
              data-testid="repair-organize"
              @click="emit('organize', repairedIds)"
            >
              <PhFolderOpen :size="16" />
              Organize repaired
            </button>
            <button type="button" class="btn btn-primary" @click="handleDone">
              <PhCheckCircle :size="16" />
              Done
            </button>
          </template>
        </template>
      </ModalFooter>
    </template>
  </Modal>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import {
  PhArrowCounterClockwise,
  PhCheckCircle,
  PhCheckSquare,
  PhFolderOpen,
  PhInfo,
  PhSpinner,
  PhWarningCircle,
  PhWrench,
  PhX,
} from '@phosphor-icons/vue'
import { Modal, ModalBody, ModalFooter, ModalHeader } from '@/components/feedback'
import { apiService } from '@/services/api'
import { useConfigurationStore } from '@/stores/configuration'
import { useLibraryIssuesStore } from '@/stores/libraryIssues'
import { normalizeSearchRegion, orderedSearchRegionOptions } from '@/utils/languageMapping'
import AudibleRecordSearch from '@/components/domain/audiobook/AudibleRecordSearch.vue'
import { Pill } from '@/components/base'
import { issueLabel } from '@/utils/libraryIssues'
import {
  ASIN_PATTERN,
  createRecordSearchState,
  isChosen,
  resetRecordSearch,
  type RecordSearchState,
} from '@/utils/audibleRecordSearch'
import type {
  AudiobookExternalIdentifier,
  AudiobookExternalIdentifierInput,
  LibraryIssueCode,
} from '@/types'

interface RepairRow extends RecordSearchState {
  id: number
  title: string
  authors: string[]
  currentAsin: string
  currentRegion: string
  identifiers: AudiobookExternalIdentifier[]
  // The book's issues from GET /library/issues/{id} (empty when it has none or they can't be read)
  currentIssues: LibraryIssueCode[]
  selected: boolean
  // Values when the modal opened, restored by Revert
  initial: { query: string; author: string; region: string; asin: string }
}

interface RepairResult {
  id: number
  title: string
  success: boolean
  detail: string
}

const props = withDefaults(
  defineProps<{
    visible?: boolean
    audiobookIds?: number[]
  }>(),
  {
    visible: false,
    audiobookIds: () => [],
  },
)

const emit = defineEmits<{
  close: []
  done: []
  organize: [ids: number[]]
}>()

const configStore = useConfigurationStore()
const libraryIssuesStore = useLibraryIssuesStore()
const configuredRegion = ref('us')
// Catalogue used when the configured one has no record, as in the rest of the matching
const FALLBACK_REGION = 'us'
const loading = ref(false)
const executing = ref(false)
const finished = ref(false)
const error = ref<string | null>(null)
const rows = ref<RepairRow[]>([])
const results = ref<RepairResult[]>([])
const progress = ref(0)

const selectedRows = computed(() => rows.value.filter((r) => r.selected))
const selectedCount = computed(() => selectedRows.value.length)
const repairedIds = computed(() => results.value.filter((r) => r.success).map((r) => r.id))

// Every Audible region stays available, ordered by usefulness: the book's current region first,
// then the configured one and the US fallback, then the others alphabetically.
function regionOptionsFor(row: RepairRow) {
  return orderedSearchRegionOptions([row.initial.region, configuredRegion.value, FALLBACK_REGION])
}

// What is wrong with the book as it is now (before the repair), as the server reports it
function currentIssues(row: RepairRow) {
  return row.currentIssues
}

function primaryAsin(identifiers: AudiobookExternalIdentifier[]) {
  const asins = identifiers.filter((i) => i.type === 'Asin')
  return asins.find((i) => i.isPrimary) ?? asins[0]
}

async function load() {
  loading.value = true
  finished.value = false
  error.value = null
  results.value = []
  rows.value = []
  try {
    if (!configStore.applicationSettings) await configStore.loadApplicationSettings()
    configuredRegion.value = normalizeSearchRegion(
      configStore.applicationSettings?.defaultSearchRegion,
    )
    rows.value = await Promise.all(
      props.audiobookIds.map(async (id) => {
        const [book, ids, issue] = await Promise.all([
          apiService.getAudiobook(id),
          apiService.getAudiobookIdentifiers(id),
          apiService.getLibraryIssue(id).catch(() => null),
        ])
        const asin = primaryAsin(ids.identifiers ?? [])
        const initial = {
          query: book.title ?? '',
          author: book.authors?.[0] ?? '',
          region: asin?.region || configuredRegion.value,
          asin: asin?.value ?? book.asin ?? '',
        }
        return {
          ...createRecordSearchState(initial),
          id,
          title: book.title ?? '',
          authors: book.authors ?? [],
          currentAsin: asin?.value ?? book.asin ?? '',
          currentRegion: asin?.region ?? '',
          identifiers: ids.identifiers ?? [],
          currentIssues: issue?.issues ?? [],
          initial,
          selected: true,
        }
      }),
    )
  } catch (e) {
    error.value = e instanceof Error ? e.message : 'Failed to load the selected audiobooks'
  } finally {
    loading.value = false
  }
}

function isEdited(row: RepairRow): boolean {
  return (
    row.query !== row.initial.query ||
    row.author !== row.initial.author ||
    row.region !== row.initial.region ||
    row.asin !== row.initial.asin
  )
}

function revert(row: RepairRow) {
  resetRecordSearch(row, row.initial)
}

// A new ASIN or region is repaired only once that ASIN is known in that region: picked from the
// results, or looked up successfully. Keeping the current ASIN and region only rescans.
function needsPick(row: RepairRow): boolean {
  if (!row.selected || !asinChanged(row)) return false
  return !isChosen(row)
}

function asinChanged(row: RepairRow): boolean {
  return (
    row.asin.trim().toUpperCase() !== row.currentAsin.toUpperCase() ||
    row.region !== row.currentRegion
  )
}

// The new ASIN becomes the primary one. Identifiers that came from the previous match (provider or
// import) are dropped: an ISBN of the wrong book would let the rescan find it again. Manual ones stay.
function identifiersWithPrimaryAsin(row: RepairRow): AudiobookExternalIdentifierInput[] {
  const asin = row.asin.trim().toUpperCase()
  const manual = row.identifiers
    .filter((i) => i.source === 'Manual')
    .filter((i) => !(i.type === 'Asin' && i.value.toUpperCase() === asin))
    .map((i) => ({
      type: i.type,
      value: i.value,
      region: i.type === 'Asin' ? i.region || null : null,
      isPrimary: i.type === 'Asin' ? false : Boolean(i.isPrimary),
      source: 'Manual' as const,
    }))
  return [
    { type: 'Asin', value: asin, region: row.region, isPrimary: true, source: 'Manual' },
    ...manual,
  ]
}

async function rescan(id: number) {
  try {
    return await apiService.rescanAudiobookMetadata(id)
  } catch (e) {
    // A rescan right after another one for the same book is refused (429): wait and retry once
    if ((e as { status?: number })?.status !== 429) throw e
    await new Promise((resolve) => setTimeout(resolve, 3000))
    return apiService.rescanAudiobookMetadata(id)
  }
}

async function repair() {
  executing.value = true
  progress.value = 0
  results.value = []
  for (const row of selectedRows.value) {
    progress.value++
    try {
      if (asinChanged(row)) {
        if (!ASIN_PATTERN.test(row.asin.trim())) throw new Error('The ASIN must be 10 characters')
        await apiService.updateAudiobookIdentifiers(row.id, identifiersWithPrimaryAsin(row))
      }
      const rescanned = await rescan(row.id)
      const book = await apiService.getAudiobook(row.id)
      results.value.push({
        id: row.id,
        title: book.title || row.title,
        success: true,
        detail: `${rescanned.asin ?? row.asin} · ${rescanned.region ?? row.region}`,
      })
    } catch (e) {
      results.value.push({
        id: row.id,
        title: row.title,
        success: false,
        detail: e instanceof Error ? e.message : 'Repair failed',
      })
    }
  }
  executing.value = false
  finished.value = true
  // Repaired books leave the Issues list (and its count under System)
  void libraryIssuesStore.load()
}

function selectAll() {
  rows.value.forEach((r) => (r.selected = true))
}

function clearSelection() {
  rows.value.forEach((r) => (r.selected = false))
}

function handleClose() {
  if (executing.value) return
  if (finished.value) emit('done')
  else emit('close')
}

function handleDone() {
  emit('done')
}

watch(
  () => props.visible,
  (visible) => {
    if (visible) void load()
  },
  { immediate: true },
)
</script>

<style scoped>
.repair-state {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 0.75rem;
  padding: 2rem 1rem;
  text-align: center;
}

.repair-icon {
  font-size: 2rem;
}

.repair-error {
  color: #ef9a9a;
}

.repair-group {
  margin-bottom: 1rem;
}

.repair-toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 0.75rem;
  flex-wrap: wrap;
}

.repair-toolbar-title {
  margin: 0;
}

.toolbar-actions {
  display: flex;
  gap: 0.5rem;
}

.repair-list {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
}

.repair-header {
  display: flex;
  justify-content: space-between;
  gap: 0.75rem;
  flex-wrap: wrap;
  cursor: pointer;
}

.repair-heading {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  font-weight: 600;
}

.repair-header-right {
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

.repair-revert-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 1.75rem;
  height: 1.75rem;
  padding: 0;
}

.repair-meta {
  font-size: 0.8rem;
  opacity: 0.75;
}

.repair-record-search {
  margin-top: 0.75rem;
}

.repair-hint {
  margin: 0.5rem 0 0;
  font-size: 0.8rem;
  color: #ffb74d;
}

.results-list {
  display: flex;
  flex-direction: column;
  gap: 0.35rem;
}

.result-row {
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

.result-row.success .result-icon {
  color: #81c784;
}

.result-row.error .result-icon {
  color: #ef9a9a;
}

.result-detail {
  margin-left: auto;
  font-size: 0.8rem;
  opacity: 0.75;
}
</style>
