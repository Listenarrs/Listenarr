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
              Point each book at the right Audible record: search by name in the right region, pick
              the result (or type the ASIN), then repair. Repairing saves the ASIN and its region
              and rescans the metadata from it.
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
                  <span class="repair-meta">
                    {{ row.authors.join(', ') || 'Unknown author' }} ·
                    {{ row.currentAsin || 'no ASIN' }}
                    <template v-if="row.currentRegion"> ({{ row.currentRegion }})</template>
                  </span>
                </label>

                <div class="repair-fields">
                  <label class="repair-field repair-field-name">
                    <span>Name</span>
                    <input
                      v-model="row.query"
                      type="text"
                      class="form-input"
                      data-testid="repair-query"
                      @keydown.enter.prevent="search(row)"
                    />
                  </label>
                  <label class="repair-field">
                    <span>Region</span>
                    <select v-model="row.region" class="form-select" data-testid="repair-region">
                      <option v-for="o in searchRegionOptions" :key="o.value" :value="o.value">
                        {{ o.label }}
                      </option>
                    </select>
                  </label>
                  <label class="repair-field">
                    <span>ASIN</span>
                    <input
                      v-model="row.asin"
                      type="text"
                      class="form-input"
                      data-testid="repair-asin"
                    />
                  </label>
                  <button
                    type="button"
                    class="btn btn-secondary repair-search-btn"
                    data-testid="repair-search"
                    :disabled="row.searching || !row.query.trim()"
                    @click="search(row)"
                  >
                    <PhSpinner v-if="row.searching" class="ph-spin" :size="14" />
                    <PhMagnifyingGlass v-else :size="14" />
                    Search
                  </button>
                </div>

                <p v-if="row.searched && !row.results.length" class="repair-empty">
                  No result in {{ regionLabel(row.region) }}.
                </p>
                <ul v-if="row.results.length" class="repair-results">
                  <li v-for="result in row.results" :key="result.asin">
                    <button
                      type="button"
                      class="repair-result"
                      :class="{ picked: result.asin === row.asin.trim().toUpperCase() }"
                      data-testid="repair-result"
                      @click="pick(row, result)"
                    >
                      <span class="repair-result-title">{{ resultTitle(result) }}</span>
                      <span class="repair-result-meta">{{ resultMeta(result) }}</span>
                    </button>
                  </li>
                </ul>
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
            :disabled="loading || executing || selectedCount === 0"
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
  PhCheckCircle,
  PhCheckSquare,
  PhFolderOpen,
  PhInfo,
  PhMagnifyingGlass,
  PhSpinner,
  PhWarningCircle,
  PhWrench,
  PhX,
} from '@phosphor-icons/vue'
import { Modal, ModalBody, ModalFooter, ModalHeader } from '@/components/feedback'
import { apiService } from '@/services/api'
import { useConfigurationStore } from '@/stores/configuration'
import { normalizeSearchRegion, searchRegionOptions } from '@/utils/languageMapping'
import type {
  AudiobookExternalIdentifier,
  AudiobookExternalIdentifierInput,
  SearchResult,
} from '@/types'

interface RepairRow {
  id: number
  title: string
  authors: string[]
  currentAsin: string
  currentRegion: string
  identifiers: AudiobookExternalIdentifier[]
  query: string
  region: string
  asin: string
  selected: boolean
  searching: boolean
  searched: boolean
  results: SearchResult[]
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

function regionLabel(region: string): string {
  return searchRegionOptions.find((o) => o.value === region)?.label ?? region
}

function resultTitle(result: SearchResult): string {
  return [result.title, result.subtitle].filter(Boolean).join(': ')
}

function resultMeta(result: SearchResult): string {
  const names = (list?: Array<{ name?: string } | string>) =>
    (list ?? []).map((x) => (typeof x === 'string' ? x : x?.name)).filter(Boolean)
  const authors = names(result.authors as Array<{ name?: string } | string>)
  const narrators = names(result.narrators as Array<{ name?: string } | string>)
  return [
    authors.join(', '),
    narrators.length ? `read by ${narrators.join(', ')}` : '',
    result.language,
    result.asin,
  ]
    .filter(Boolean)
    .join(' · ')
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
    const configuredRegion = normalizeSearchRegion(
      configStore.applicationSettings?.defaultSearchRegion,
    )
    rows.value = await Promise.all(
      props.audiobookIds.map(async (id) => {
        const [book, ids] = await Promise.all([
          apiService.getAudiobook(id),
          apiService.getAudiobookIdentifiers(id),
        ])
        const asin = primaryAsin(ids.identifiers ?? [])
        return {
          id,
          title: book.title ?? '',
          authors: book.authors ?? [],
          currentAsin: asin?.value ?? book.asin ?? '',
          currentRegion: asin?.region ?? '',
          identifiers: ids.identifiers ?? [],
          query: book.title ?? '',
          region: asin?.region || configuredRegion,
          asin: asin?.value ?? book.asin ?? '',
          selected: true,
          searching: false,
          searched: false,
          results: [],
        }
      }),
    )
  } catch (e) {
    error.value = e instanceof Error ? e.message : 'Failed to load the selected audiobooks'
  } finally {
    loading.value = false
  }
}

async function search(row: RepairRow) {
  const query = row.query.trim()
  if (!query) return
  row.searching = true
  try {
    const isAsin = /^[A-Z0-9]{10}$/i.test(query)
    const found = await apiService.advancedSearch(
      isAsin
        ? { asin: query.toUpperCase(), region: row.region, cap: 5 }
        : { title: query, region: row.region, cap: 8 },
    )
    // An ASIN the catalogue doesn't sell comes back as an empty stub without a title
    row.results = (found ?? []).filter((r) => r.asin && r.title)
  } catch {
    row.results = []
  } finally {
    row.searching = false
    row.searched = true
  }
}

function pick(row: RepairRow, result: SearchResult) {
  row.asin = (result.asin ?? '').toUpperCase()
  if (result.region) row.region = result.region
  row.selected = true
}

function asinChanged(row: RepairRow): boolean {
  return (
    row.asin.trim().toUpperCase() !== row.currentAsin.toUpperCase() ||
    row.region !== row.currentRegion
  )
}

function identifiersWithPrimaryAsin(row: RepairRow): AudiobookExternalIdentifierInput[] {
  const asin = row.asin.trim().toUpperCase()
  const others = row.identifiers
    .filter((i) => !(i.type === 'Asin' && i.value.toUpperCase() === asin))
    .map((i) => ({
      type: i.type,
      value: i.value,
      region: i.type === 'Asin' ? i.region || null : null,
      isPrimary: i.type === 'Asin' ? false : Boolean(i.isPrimary),
      source: i.source || 'Manual',
    }))
  return [
    { type: 'Asin', value: asin, region: row.region, isPrimary: true, source: 'Manual' },
    ...others,
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
        if (!/^[A-Z0-9]{10}$/i.test(row.asin.trim()))
          throw new Error('The ASIN must be 10 characters')
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

.repair-meta {
  font-size: 0.8rem;
  opacity: 0.75;
}

.repair-fields {
  display: grid;
  grid-template-columns: minmax(10rem, 2fr) minmax(8rem, 1fr) minmax(8rem, 1fr) auto;
  gap: 0.5rem;
  align-items: end;
  margin-top: 0.75rem;
}

.repair-field {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
  font-size: 0.8rem;
}

.repair-search-btn {
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
}

.repair-empty {
  margin: 0.5rem 0 0;
  font-size: 0.8rem;
  opacity: 0.75;
}

.repair-results {
  list-style: none;
  margin: 0.5rem 0 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
}

.repair-result {
  width: 100%;
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 0.1rem;
  padding: 0.4rem 0.6rem;
  border-radius: 6px;
  border: 1px solid rgba(255, 255, 255, 0.08);
  background: transparent;
  color: inherit;
  text-align: left;
  cursor: pointer;
}

.repair-result.picked {
  border-color: rgba(76, 175, 80, 0.6);
  background-color: rgba(76, 175, 80, 0.08);
}

.repair-result-meta {
  font-size: 0.75rem;
  opacity: 0.75;
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

@media (max-width: 720px) {
  .repair-fields {
    grid-template-columns: 1fr;
  }
}
</style>
