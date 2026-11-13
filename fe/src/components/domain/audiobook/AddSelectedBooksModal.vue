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
<!--
  Bulk-adds a caller-supplied set of missing books (works) to the library. Unlike
  AddSeriesModal this does NOT fetch or de-duplicate a catalog — it is fed the
  series page's already-deduped, ownership-correct missing works, so the user just
  curates (uncheck duplicates/unwanted) and sets shared options. "Already exists"
  (409) is treated as a benign skip. AutoSearch defaults OFF so a bulk add doesn't
  fire dozens of indexer searches unless the user opts in.
-->
<script setup lang="ts">
import { ref, computed, watch } from 'vue'
import { PhPlus, PhX, PhSpinner, PhCheckCircle } from '@phosphor-icons/vue'
import RootFolderSelect from '@/components/form/RootFolderSelect.vue'
import { apiService } from '@/services/api'
import { useToast } from '@/services/toastService'
import { useConfigurationStore } from '@/stores/configuration'
import { useRootFoldersStore } from '@/stores/rootFolders'
import { logger } from '@/utils/logger'
import type { AudibleBookMetadata, QualityProfile } from '@/types'

interface Props {
  visible: boolean
  /** The missing works to offer for adding (already deduped + ownership-checked by the caller). */
  books: AudibleBookMetadata[]
  seriesName?: string
  seriesAsin?: string
  region?: string
}

interface Emits {
  (e: 'close'): void
  (e: 'done', summary: { added: number; skipped: number; failed: number }): void
}

const props = withDefaults(defineProps<Props>(), {
  seriesName: undefined,
  seriesAsin: undefined,
  region: 'us',
})
const emit = defineEmits<Emits>()

const toast = useToast()
const configStore = useConfigurationStore()
const rootStore = useRootFoldersStore()

const qualityProfiles = ref<QualityProfile[]>([])
const qualityProfileId = ref<number | null>(null)
const selectedRootId = ref<number | null>(null)
const customRootPath = ref('')
const monitored = ref(true)
const autoSearch = ref(false) // default OFF: don't fire a bulk of indexer searches unintentionally
const monitorSeriesAfter = ref(false)

const selectedKeys = ref<Set<string>>(new Set())
const adding = ref(false)
const progress = ref({ added: 0, skipped: 0, failed: 0, total: 0 })

function keyOf(book: AudibleBookMetadata): string {
  return book.asin || book.title || ''
}

const selectedCount = computed(() => selectedKeys.value.size)
const allSelected = computed(
  () => props.books.length > 0 && props.books.every((b) => selectedKeys.value.has(keyOf(b))),
)

function toggle(book: AudibleBookMetadata) {
  const k = keyOf(book)
  if (selectedKeys.value.has(k)) selectedKeys.value.delete(k)
  else selectedKeys.value.add(k)
  selectedKeys.value = new Set(selectedKeys.value)
}

function toggleAll() {
  selectedKeys.value = allSelected.value ? new Set() : new Set(props.books.map(keyOf))
}

async function loadDefaults() {
  try {
    if (!configStore.qualityProfiles?.length) await configStore.loadQualityProfiles()
    qualityProfiles.value = configStore.qualityProfiles || []
    qualityProfileId.value = qualityProfiles.value.find((p) => p.isDefault)?.id ?? null
    if (!rootStore.folders?.length) await rootStore.load()
    selectedRootId.value = rootStore.folders.find((f) => f.isDefault)?.id ?? null
  } catch (err) {
    logger.error('AddSelectedBooksModal: failed to load defaults', err)
  }
}

function resolveDestinationPath(): string | undefined {
  if (selectedRootId.value === 0) return customRootPath.value.trim() || undefined
  if (selectedRootId.value && selectedRootId.value > 0) {
    return rootStore.folders.find((f) => f.id === selectedRootId.value)?.path || undefined
  }
  return undefined
}

async function submit() {
  const targets = props.books.filter((b) => selectedKeys.value.has(keyOf(b)))
  if (targets.length === 0) {
    toast.warning('Nothing selected', 'Pick at least one book to add.')
    return
  }
  adding.value = true
  progress.value = { added: 0, skipped: 0, failed: 0, total: targets.length }
  const destination = resolveDestinationPath()

  for (const book of targets) {
    try {
      await apiService.addToLibrary(book, {
        monitored: monitored.value,
        qualityProfileId: qualityProfileId.value ?? undefined,
        autoSearch: autoSearch.value,
        destinationPath: destination,
      })
      progress.value.added++
    } catch (err) {
      // 409 = already in the library (e.g. owned under another series name) — benign skip.
      if ((err as { status?: number })?.status === 409) {
        progress.value.skipped++
      } else {
        logger.error(`AddSelectedBooksModal: failed to add "${book.title}"`, err)
        progress.value.failed++
      }
    }
  }

  if (monitorSeriesAfter.value && props.seriesName) {
    try {
      await apiService.monitorSeries({
        name: props.seriesName,
        asin: props.seriesAsin,
        region: props.region || 'us',
      })
    } catch (err) {
      logger.warn('AddSelectedBooksModal: monitorSeries failed', err)
    }
  }

  adding.value = false
  const { added, skipped, failed } = progress.value
  const parts = [`${added} added`]
  if (skipped) parts.push(`${skipped} already had`)
  if (failed) parts.push(`${failed} failed`)
  if (failed === 0) toast.success('Books added', parts.join(', ') + '.')
  else if (added > 0) toast.warning('Partially added', parts.join(', ') + '.')
  else toast.error('Add failed', parts.join(', ') + '.')

  emit('done', { added, skipped, failed })
  if (failed === 0) emit('close')
}

function close() {
  if (adding.value) return
  emit('close')
}

watch(
  () => props.visible,
  (v) => {
    if (v) {
      selectedKeys.value = new Set(props.books.map(keyOf))
      progress.value = { added: 0, skipped: 0, failed: 0, total: 0 }
      void loadDefaults()
    }
  },
  { immediate: true },
)
</script>

<template>
  <transition name="modal-fade">
    <div v-if="visible" class="modal-overlay" @click.self="close">
      <div class="modal modal-md" role="dialog" aria-modal="true">
        <header class="modal-header">
          <h2>Add books to library</h2>
          <button class="modal-close" :disabled="adding" @click="close" aria-label="Close">
            <PhX />
          </button>
        </header>

        <div class="modal-body">
          <p class="intro">
            {{ books.length }} missing book{{ books.length === 1 ? '' : 's' }} in this series.
            Uncheck anything you don't want (duplicates, editions you don't care about), then add.
          </p>

          <div class="list-head">
            <label class="checkbox-row">
              <input type="checkbox" :checked="allSelected" @change="toggleAll" />
              <span>Select all</span>
            </label>
            <span class="muted">{{ selectedCount }} selected</span>
          </div>

          <ul class="book-list">
            <li v-for="book in books" :key="keyOf(book)">
              <label class="book-row" :class="{ selected: selectedKeys.has(keyOf(book)) }">
                <input
                  type="checkbox"
                  :checked="selectedKeys.has(keyOf(book))"
                  @change="toggle(book)"
                />
                <img
                  v-if="book.imageUrl"
                  :src="book.imageUrl"
                  :alt="`${book.title} cover`"
                  class="book-cover"
                  loading="lazy"
                />
                <div class="book-meta">
                  <span class="book-title">
                    <span v-if="book.seriesNumber" class="pos">#{{ book.seriesNumber }}</span>
                    {{ book.title }}
                  </span>
                  <span v-if="book.authors?.length" class="book-sub">{{
                    book.authors.join(', ')
                  }}</span>
                </div>
              </label>
            </li>
          </ul>

          <div class="options-section">
            <div class="option-group">
              <label class="form-label">Root folder</label>
              <RootFolderSelect
                v-model:rootId="selectedRootId"
                v-model:customPath="customRootPath"
                hideLabel
              />
            </div>
            <div class="option-group">
              <label class="form-label">Quality profile</label>
              <select v-model="qualityProfileId" class="form-select">
                <option :value="null">Use default profile</option>
                <option v-for="p in qualityProfiles" :key="p.id" :value="p.id">
                  {{ p.name }}{{ p.isDefault ? ' (Default)' : '' }}
                </option>
              </select>
            </div>
            <label class="checkbox-row">
              <input type="checkbox" v-model="monitored" />
              <span>Monitor each book</span>
            </label>
            <label class="checkbox-row">
              <input type="checkbox" v-model="autoSearch" />
              <span>Search for downloads immediately</span>
            </label>
            <label v-if="seriesName" class="checkbox-row">
              <input type="checkbox" v-model="monitorSeriesAfter" />
              <span>Also monitor the series (pick up future releases)</span>
            </label>
          </div>

          <div
            v-if="adding || progress.added || progress.skipped || progress.failed"
            class="progress-row"
          >
            <PhSpinner v-if="adding" class="spin" />
            <PhCheckCircle v-else class="success" />
            <span>
              {{ progress.added }} added<template v-if="progress.skipped"
                >, {{ progress.skipped }} already had</template
              ><template v-if="progress.failed">, {{ progress.failed }} failed</template> of
              {{ progress.total }}
            </span>
          </div>
        </div>

        <footer class="modal-footer">
          <button class="btn" :disabled="adding" @click="close">Cancel</button>
          <button class="btn btn-primary" :disabled="adding || selectedCount === 0" @click="submit">
            <PhSpinner v-if="adding" class="spin" :size="16" />
            <PhPlus v-else :size="16" />
            <span>Add {{ selectedCount }} book{{ selectedCount === 1 ? '' : 's' }}</span>
          </button>
        </footer>
      </div>
    </div>
  </transition>
</template>

<style scoped>
.modal-overlay {
  position: fixed;
  inset: 0;
  background: rgba(0, 0, 0, 0.6);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 1000;
  padding: 1rem;
}
.modal {
  background: var(--color-surface, #1e1e1e);
  border-radius: 12px;
  width: 100%;
  max-width: 640px;
  max-height: 90vh;
  display: flex;
  flex-direction: column;
  box-shadow: 0 12px 40px rgba(0, 0, 0, 0.45);
}
.modal-header,
.modal-footer {
  display: flex;
  align-items: center;
  padding: 1rem 1.25rem;
}
.modal-header {
  justify-content: space-between;
  border-bottom: 1px solid var(--color-border, #333);
}
.modal-header h2 {
  margin: 0;
  font-size: 1.15rem;
}
.modal-close {
  background: none;
  border: none;
  color: inherit;
  cursor: pointer;
  padding: 0.25rem;
  border-radius: 6px;
}
.modal-close:hover:not(:disabled) {
  background: var(--color-surface-hover, #2a2a2a);
}
.modal-body {
  padding: 1rem 1.25rem;
  overflow-y: auto;
}
.modal-footer {
  justify-content: flex-end;
  gap: 0.5rem;
  border-top: 1px solid var(--color-border, #333);
}
.intro {
  margin: 0 0 0.75rem;
  color: var(--color-text-secondary, #bbb);
}
.list-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 0.4rem;
}
.muted {
  color: var(--color-text-secondary, #999);
  font-size: 0.85rem;
}
.book-list {
  list-style: none;
  margin: 0 0 1rem;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 0.35rem;
  max-height: 38vh;
  overflow-y: auto;
}
.book-row {
  display: flex;
  align-items: center;
  gap: 0.6rem;
  padding: 0.35rem 0.5rem;
  border: 1px solid var(--color-border, #333);
  border-radius: 6px;
  cursor: pointer;
}
.book-row.selected {
  border-color: var(--color-primary, #2196f3);
  background: var(--color-surface-hover, #2a2a2a);
}
.book-cover {
  width: 34px;
  height: 34px;
  object-fit: cover;
  border-radius: 3px;
  flex: none;
}
.book-meta {
  display: flex;
  flex-direction: column;
  min-width: 0;
}
.book-title {
  font-weight: 600;
  font-size: 0.9rem;
}
.book-title .pos {
  color: var(--color-text-secondary, #999);
  margin-right: 0.25rem;
}
.book-sub {
  font-size: 0.8rem;
  color: var(--color-text-secondary, #999);
}
.options-section {
  display: flex;
  flex-direction: column;
  gap: 0.6rem;
  border-top: 1px solid var(--color-border, #333);
  padding-top: 0.85rem;
}
.option-group {
  display: flex;
  flex-direction: column;
  gap: 0.3rem;
}
.form-label {
  font-size: 0.85rem;
  color: var(--color-text-secondary, #bbb);
}
.form-select {
  padding: 0.4rem 0.6rem;
  background: var(--color-surface-alt, #2a2a2a);
  border: 1px solid var(--color-border, #333);
  border-radius: 6px;
  color: inherit;
}
.checkbox-row {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  cursor: pointer;
  font-size: 0.9rem;
}
.progress-row {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  margin-top: 0.75rem;
  color: var(--color-text-secondary, #bbb);
}
.progress-row .success {
  color: #81c784;
}
.spin {
  animation: spin 1s linear infinite;
}
@keyframes spin {
  to {
    transform: rotate(360deg);
  }
}
</style>
