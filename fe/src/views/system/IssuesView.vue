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
  <div class="issues-view">
    <div class="issues-header">
      <h1><PhWarning class="issues-title-icon" /> Issues</h1>
      <p class="issues-intro">
        Books whose Audible record is incomplete: no title, no ASIN, an ASIN that isn't one, or an
        ASIN without its region. Rescan metadata can't rely on them; Repair points each one at the
        right record.
      </p>
    </div>

    <div v-if="issuesStore.loading && !issuesStore.loaded" class="issues-state">
      <PhSpinner class="ph-spin" /> Loading…
    </div>

    <div v-else-if="!issuesStore.items.length" class="issues-state" data-testid="issues-empty">
      <PhCheckCircle class="issues-ok" /> No issues: every book has a title, an ASIN and its region.
    </div>

    <template v-else>
      <div class="issues-toolbar">
        <label class="issues-select-all">
          <input
            type="checkbox"
            data-testid="issues-select-all"
            :checked="allSelected"
            :indeterminate="someSelected && !allSelected"
            @change="toggleAll"
          />
          Select all
        </label>
        <span class="issues-count">
          {{ selectedIds.length }} of {{ issuesStore.items.length }} selected
        </span>
        <button
          type="button"
          class="btn btn-primary"
          data-testid="issues-repair-selected"
          :disabled="!selectedIds.length"
          @click="openRepair(selectedIds)"
        >
          <PhWrench :size="16" />
          Repair books
        </button>
      </div>

      <div class="issues-list">
        <div
          v-for="book in issuesStore.items"
          :key="book.id"
          class="issues-row"
          :data-testid="`issues-row-${book.id}`"
        >
          <input
            type="checkbox"
            class="issues-checkbox"
            :checked="selected.has(book.id)"
            :aria-label="`Select ${book.title || 'book ' + book.id}`"
            @change="toggle(book.id)"
          />
          <img
            class="issues-cover"
            :src="getProtectedImageSrc(book.imageUrl || '', placeholderUrl)"
            alt=""
          />
          <div class="issues-book">
            <RouterLink :to="`/audiobooks/${book.id}`" class="issues-book-title">
              {{ book.title || `Untitled book #${book.id}` }}
            </RouterLink>
            <span class="issues-book-meta">
              {{ book.authors?.join(', ') || 'Unknown author' }}
              <template v-if="book.series">
                · {{ book.series
                }}<template v-if="book.seriesNumber"> {{ book.seriesNumber }}</template>
              </template>
            </span>
          </div>
          <span class="issues-asin">
            {{ book.asin || '—' }}<template v-if="book.region"> ({{ book.region }})</template>
          </span>
          <span class="issues-pills">
            <Pill v-for="code in book.issues" :key="code" variant="warning">
              {{ issueLabel(code) }}
            </Pill>
          </span>
          <button
            type="button"
            class="btn btn-secondary issues-repair-btn"
            data-testid="issues-repair"
            @click="openRepair([book.id])"
          >
            <PhWrench :size="14" />
            Repair
          </button>
        </div>
      </div>
    </template>

    <RenamePreviewModal
      :visible="showOrganizeModal"
      :audiobook-ids="organizeAudiobookIds"
      @close="showOrganizeModal = false"
      @done="handleOrganizeDone"
    />

    <RepairBooksModal
      :visible="showRepairModal"
      :audiobook-ids="repairAudiobookIds"
      @close="showRepairModal = false"
      @done="handleRepairDone"
      @organize="organizeRepaired"
    />
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { RouterLink, useRouter } from 'vue-router'
import { PhCheckCircle, PhSpinner, PhWarning, PhWrench } from '@phosphor-icons/vue'
import { Pill } from '@/components/base'
import RenamePreviewModal from '@/components/domain/organize/RenamePreviewModal.vue'
import RepairBooksModal from '@/components/domain/repair/RepairBooksModal.vue'
import { useProtectedImages } from '@/composables/useProtectedImages'
import { useLibraryIssuesStore } from '@/stores/libraryIssues'
import { getPlaceholderUrl } from '@/utils/placeholder'
import type { LibraryIssueCode } from '@/types'

const issuesStore = useLibraryIssuesStore()
const router = useRouter()
const { getProtectedImageSrc } = useProtectedImages()
const placeholderUrl = getPlaceholderUrl()

const selected = ref(new Set<number>())
const selectedIds = computed(() =>
  issuesStore.items.filter((b) => selected.value.has(b.id)).map((b) => b.id),
)
const allSelected = computed(
  () => issuesStore.items.length > 0 && selectedIds.value.length === issuesStore.items.length,
)
const someSelected = computed(() => selectedIds.value.length > 0)

const showRepairModal = ref(false)
const repairAudiobookIds = ref<number[]>([])
const showOrganizeModal = ref(false)
const organizeAudiobookIds = ref<number[]>([])

const ISSUE_LABELS: Record<LibraryIssueCode, string> = {
  missing_title: 'No title',
  missing_asin: 'No ASIN',
  invalid_asin: 'Invalid ASIN',
  missing_region: 'No region',
}

function issueLabel(code: LibraryIssueCode): string {
  return ISSUE_LABELS[code] ?? code
}

function toggle(id: number) {
  const next = new Set(selected.value)
  if (next.has(id)) next.delete(id)
  else next.add(id)
  selected.value = next
}

function toggleAll() {
  selected.value = allSelected.value ? new Set() : new Set(issuesStore.items.map((b) => b.id))
}

function openRepair(ids: number[]) {
  if (!ids.length) return
  repairAudiobookIds.value = [...ids]
  showRepairModal.value = true
}

// The page only exists while there is something to repair: without issues, back to the books
async function loadOrLeave() {
  await issuesStore.load()
  if (issuesStore.loaded && issuesStore.count === 0) {
    void router.replace({ path: '/audiobooks', query: { group: 'books' } })
  }
}

// After a repair the list is read again: repaired books leave it, the selection keeps the others
async function refresh() {
  await loadOrLeave()
  const remaining = new Set(issuesStore.items.map((b) => b.id))
  selected.value = new Set([...selected.value].filter((id) => remaining.has(id)))
}

async function handleRepairDone() {
  showRepairModal.value = false
  await refresh()
}

async function organizeRepaired(ids: number[]) {
  showRepairModal.value = false
  // Stay on the page until Organize is done, even when no issue is left
  await issuesStore.load()
  organizeAudiobookIds.value = ids
  showOrganizeModal.value = true
}

async function handleOrganizeDone() {
  showOrganizeModal.value = false
  await refresh()
}

onMounted(() => {
  void loadOrLeave()
})
</script>

<style scoped>
.issues-view {
  padding: 1.5rem;
  max-width: 1200px;
}

.issues-header h1 {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  margin: 0 0 0.5rem;
}

.issues-title-icon {
  color: #f39c12;
}

.issues-intro {
  margin: 0 0 1rem;
  opacity: 0.8;
}

.issues-state {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  padding: 2rem 0;
}

.issues-ok {
  color: #2ecc71;
}

.issues-toolbar {
  display: flex;
  align-items: center;
  gap: 1rem;
  margin-bottom: 0.75rem;
}

.issues-select-all {
  display: inline-flex;
  align-items: center;
  gap: 0.4rem;
}

.issues-count {
  opacity: 0.75;
  margin-right: auto;
}

.issues-toolbar .btn,
.issues-repair-btn {
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
}

.issues-list {
  display: flex;
  flex-direction: column;
  border: 1px solid #333;
  border-radius: 6px;
}

.issues-row {
  display: grid;
  grid-template-columns: auto 48px minmax(12rem, 2fr) minmax(8rem, 1fr) minmax(8rem, 1fr) auto;
  align-items: center;
  gap: 0.75rem;
  padding: 0.5rem 0.75rem;
  border-bottom: 1px solid #2a2a2a;
}

.issues-row:last-child {
  border-bottom: none;
}

.issues-cover {
  width: 48px;
  height: 48px;
  object-fit: cover;
  border-radius: 4px;
}

.issues-book {
  display: flex;
  flex-direction: column;
  min-width: 0;
}

.issues-book-title {
  color: inherit;
  font-weight: 600;
  text-decoration: none;
}

.issues-book-title:hover {
  text-decoration: underline;
}

.issues-book-meta {
  font-size: 0.8rem;
  opacity: 0.75;
}

.issues-asin {
  font-family: monospace;
  font-size: 0.85rem;
}

.issues-pills {
  display: flex;
  flex-wrap: wrap;
  gap: 0.25rem;
}

@media (max-width: 720px) {
  .issues-row {
    grid-template-columns: auto 40px 1fr auto;
  }

  .issues-asin,
  .issues-pills {
    grid-column: 3;
  }
}
</style>
