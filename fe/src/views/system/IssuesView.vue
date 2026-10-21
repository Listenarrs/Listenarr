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

    <!-- The same page with or without issues: nothing to select only disables the selection -->
    <div v-if="issuesStore.loaded" class="issues-toolbar">
      <label class="issues-select-all">
        <input
          type="checkbox"
          data-testid="issues-select-all"
          :checked="allSelected"
          :indeterminate="someSelected && !allSelected"
          :disabled="!visibleItems.length"
          @change="toggleAll"
        />
        Select all
      </label>
      <span class="issues-count">
        {{ selectedIds.length }} of {{ visibleItems.length }} selected
      </span>
      <button
        v-if="issuesStore.ignored.length"
        type="button"
        role="switch"
        class="icon-button action-secondary action-toggle issues-ignored-toggle"
        :class="{ active: issuesStore.showIgnored }"
        :aria-checked="issuesStore.showIgnored"
        :title="issuesStore.showIgnored ? 'Hide ignored books' : 'Show ignored books'"
        data-testid="issues-show-ignored"
        @click="issuesStore.showIgnored = !issuesStore.showIgnored"
      >
        <component :is="issuesStore.showIgnored ? PhToggleRight : PhToggleLeft" :size="22" />
        <span class="issues-ignored-label">
          Show ignored books ({{ issuesStore.ignored.length }})
        </span>
      </button>
      <button
        type="button"
        class="btn btn-primary issues-repair-selected"
        data-testid="issues-repair-selected"
        :disabled="!selectedIds.length"
        @click="openRepair(selectedIds)"
      >
        <PhWrench :size="16" />
        {{ repairSelectedLabel }}
      </button>
    </div>

    <!-- Nothing to list: one row in place of the first book says so -->
    <div v-if="issuesStore.loaded && !visibleItems.length" class="issues-list">
      <div class="issues-row issues-row-empty" data-testid="issues-empty">
        <PhCheckCircle class="issues-ok" :size="20" /> No issues found.
        <template v-if="issuesStore.ignored.length">
          {{ issuesStore.ignored.length }} ignored
          {{ issuesStore.ignored.length === 1 ? 'book is' : 'books are' }} hidden.
        </template>
      </div>
    </div>

    <template v-else-if="visibleItems.length">
      <div ref="listEl" class="issues-list">
        <div
          v-for="book in visibleItems"
          :key="book.id"
          class="issues-row"
          :class="{ 'issues-row-ignored': book.ignored }"
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
            <Pill v-if="book.ignored" variant="subtle" data-testid="issues-ignored-pill"
              >Ignored</Pill
            >
            <Pill v-for="code in book.issues" :key="code" variant="warning">
              {{ issueLabel(code) }}
            </Pill>
          </span>
          <button
            v-if="book.ignored"
            type="button"
            class="btn btn-secondary issues-ignore-btn"
            data-testid="issues-unignore"
            title="Stop ignoring: list this book again when it has issues"
            @click="unignoreBook(book)"
          >
            <PhEye :size="14" />
            Stop ignoring
          </button>
          <button
            v-else
            type="button"
            class="btn btn-secondary issues-ignore-btn"
            data-testid="issues-ignore"
            title="Ignore the problems of this book"
            @click="ignoreBook(book)"
          >
            <PhEyeSlash :size="14" />
            Ignore
          </button>
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

      <!-- The same button under a list that goes past the bottom of the screen -->
      <div v-if="listOverflows" class="issues-toolbar issues-toolbar-bottom">
        <span class="issues-count">
          {{ selectedIds.length }} of {{ visibleItems.length }} selected
        </span>
        <button
          type="button"
          class="btn btn-primary"
          data-testid="issues-repair-selected-bottom"
          :disabled="!selectedIds.length"
          @click="openRepair(selectedIds)"
        >
          <PhWrench :size="16" />
          {{ repairSelectedLabel }}
        </button>
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
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { RouterLink } from 'vue-router'
import {
  PhCheckCircle,
  PhEye,
  PhEyeSlash,
  PhSpinner,
  PhToggleLeft,
  PhToggleRight,
  PhWarning,
  PhWrench,
} from '@phosphor-icons/vue'
import { showConfirm } from '@/composables/confirmService'
import { Pill } from '@/components/base'
import RenamePreviewModal from '@/components/domain/organize/RenamePreviewModal.vue'
import RepairBooksModal from '@/components/domain/repair/RepairBooksModal.vue'
import { useProtectedImages } from '@/composables/useProtectedImages'
import { useLibraryIssuesStore } from '@/stores/libraryIssues'
import { getPlaceholderUrl } from '@/utils/placeholder'
import { issueLabel } from '@/utils/libraryIssues'
import type { LibraryIssue } from '@/types'

const issuesStore = useLibraryIssuesStore()
const { getProtectedImageSrc } = useProtectedImages()
const placeholderUrl = getPlaceholderUrl()

// Ignored books are listed on demand, after the others (the choice is kept by the store)
const visibleItems = computed(() =>
  issuesStore.showIgnored ? [...issuesStore.active, ...issuesStore.ignored] : issuesStore.active,
)

const selected = ref(new Set<number>())
const selectedIds = computed(() =>
  visibleItems.value.filter((b) => selected.value.has(b.id)).map((b) => b.id),
)
const allSelected = computed(
  () => visibleItems.value.length > 0 && selectedIds.value.length === visibleItems.value.length,
)
const someSelected = computed(() => selectedIds.value.length > 0)
const repairSelectedLabel = computed(() => {
  const n = selectedIds.value.length
  return `Repair Selected ${n === 1 ? 'Book' : 'Books'} (${n})`
})

// The list goes past the bottom of the screen: the button is repeated under it
const listEl = ref<HTMLElement | null>(null)
const listOverflows = ref(false)
function measureList() {
  const el = listEl.value
  listOverflows.value = Boolean(el && el.getBoundingClientRect().bottom > window.innerHeight)
}
let resizeObserver: ResizeObserver | null = null
watch(listEl, (el) => {
  resizeObserver?.disconnect()
  if (el && typeof ResizeObserver !== 'undefined') {
    resizeObserver = new ResizeObserver(measureList)
    resizeObserver.observe(el)
  }
  measureList()
})
watch(
  () => visibleItems.value.length,
  () => void nextTick(measureList),
)

const showRepairModal = ref(false)
const repairAudiobookIds = ref<number[]>([])
const showOrganizeModal = ref(false)
const organizeAudiobookIds = ref<number[]>([])

function toggle(id: number) {
  const next = new Set(selected.value)
  if (next.has(id)) next.delete(id)
  else next.add(id)
  selected.value = next
}

function toggleAll() {
  selected.value = allSelected.value ? new Set() : new Set(visibleItems.value.map((b) => b.id))
}

const bookName = (book: LibraryIssue) => book.title || `book #${book.id}`

async function ignoreBook(book: LibraryIssue) {
  const ok = await showConfirm(
    `Ignore the problems of "${bookName(book)}"? It won't appear in this list or in the Issues ` +
      'count any more. "Show ignored books" lists it again, to stop ignoring it.',
    'Ignore this book',
    { confirmText: 'Ignore' },
  )
  if (!ok) return
  await issuesStore.setIgnored(book.id, true)
  const next = new Set(selected.value)
  next.delete(book.id)
  selected.value = next
}

async function unignoreBook(book: LibraryIssue) {
  await issuesStore.setIgnored(book.id, false)
}

// Repairing ignored books: ask whether they stay ignored from the repair checks, whatever the result
async function openRepair(ids: number[]) {
  if (!ids.length) return
  const ignoredBooks = issuesStore.ignored.filter((b) => ids.includes(b.id))
  if (ignoredBooks.length) {
    const subject =
      ignoredBooks.length === 1
        ? `"${bookName(ignoredBooks[0]!)}" is ignored`
        : `${ignoredBooks.length} of these books are ignored`
    const keep = await showConfirm(
      `${subject}. Keep ${ignoredBooks.length === 1 ? 'it' : 'them'} ignored from the repair ` +
        'checks after the repair, whatever the result?',
      'Repair ignored books',
      { confirmText: 'Keep ignored', cancelText: 'Stop ignoring' },
    )
    if (!keep) {
      for (const book of ignoredBooks) await issuesStore.setIgnored(book.id, false)
    }
  }
  repairAudiobookIds.value = [...ids]
  showRepairModal.value = true
}

// After a repair the list is read again: repaired books leave it, the selection keeps the others
async function refresh() {
  await issuesStore.load()
  const remaining = new Set(visibleItems.value.map((b) => b.id))
  selected.value = new Set([...selected.value].filter((id) => remaining.has(id)))
}

async function handleRepairDone() {
  showRepairModal.value = false
  await refresh()
}

async function organizeRepaired(ids: number[]) {
  showRepairModal.value = false
  await issuesStore.load()
  organizeAudiobookIds.value = ids
  showOrganizeModal.value = true
}

async function handleOrganizeDone() {
  showOrganizeModal.value = false
  await refresh()
}

onMounted(() => {
  void issuesStore.load()
  window.addEventListener('resize', measureList)
})

onBeforeUnmount(() => {
  window.removeEventListener('resize', measureList)
  resizeObserver?.disconnect()
})
</script>

<style scoped>
.issues-view {
  padding: 1.5rem;
  width: 100%;
  box-sizing: border-box;
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

/* Same height as a book row, its text centered (not the book grid, whose narrow columns would wrap
   it word by word) */
.issues-row.issues-row-empty {
  display: flex;
  justify-content: center;
  align-items: center;
  gap: 0.5rem;
  min-height: 4rem;
  border-bottom: none;
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
}

/* The selection and the ignored-books switch on the left, Repair on the right */
.issues-repair-selected,
.issues-toolbar-bottom .btn {
  margin-left: auto;
}

/* The switch keeps the app's colors (green on, red off) on its icon. .icon-button is a 36px square
   for an icon alone: this one also carries its label, so it takes its content's width and must not
   shrink, or the label overflows onto the selection count */
.issues-ignored-toggle {
  width: auto;
  height: auto;
  flex-shrink: 0;
  gap: 0.4rem;
  padding: 0.25rem 0.5rem;
  font-size: inherit;
  white-space: nowrap;
}

.issues-ignored-toggle svg {
  flex-shrink: 0;
  width: 22px;
  height: 22px;
}

/* Same color as the rest of the toolbar (the button's own color is the switch state) */
.issues-ignored-label {
  color: #ffffff;
}

.issues-toolbar .btn,
.issues-repair-btn {
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
}

.issues-toolbar-bottom {
  margin: 0.75rem 0 0;
}

.issues-list {
  display: flex;
  flex-direction: column;
  border: 1px solid #333;
  border-radius: 6px;
}

.issues-row {
  display: grid;
  grid-template-columns:
    auto 48px minmax(12rem, 3fr) minmax(10rem, 1fr) minmax(10rem, 1.5fr)
    auto auto;
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

.issues-row-ignored {
  opacity: 0.7;
}

.issues-ignore-btn {
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
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
