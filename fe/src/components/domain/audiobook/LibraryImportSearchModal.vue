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
  <Modal :visible="true" size="lg" @close="emit('close')">
    <template #header>
      <ModalHeader :title="`Find Match — ${item.folderName}`" @close="emit('close')" />
    </template>
    <ModalBody>
      <div class="info-section find-match-info">
        <PhInfo />
        <p>
          Search the title and author in the region and pick the result under the book, or type the
          ASIN and its region (it is checked in that region). Changing the title, author or region
          empties the ASIN, which no longer matches: search and pick the book again.
        </p>
      </div>

      <BookMatchCard
        v-if="match"
        v-model:match="match"
        :configured-region="configuredRegion"
        search-on-mount
        data-testid="find-match-card"
        @searching="onSearching"
      >
        <template #heading>{{ bookTitle }}</template>
        <template #meta>
          {{ item.detectedAuthor || 'Unknown author' }}
          <template v-if="match.current.asin">
            · {{ match.current.asin }}
            <template v-if="match.current.region"> ({{ match.current.region }})</template>
          </template>
        </template>
      </BookMatchCard>

      <p v-if="error" class="find-match-error" data-testid="find-match-error">
        <PhWarningCircle :size="16" />
        {{ error }}
      </p>
    </ModalBody>
    <template #footer>
      <ModalFooter :showCancel="false">
        <template #left>
          <button type="button" class="btn cancel-button" @click="emit('close')">
            <PhX :size="16" />
            Cancel
          </button>
        </template>
        <template #default>
          <button
            type="button"
            class="btn btn-primary"
            data-testid="find-match-confirm"
            :disabled="!canConfirm"
            @click="confirm"
          >
            <PhSpinner v-if="confirming" class="ph-spin" :size="16" />
            <PhCheckCircle v-else :size="16" />
            Use this match
          </button>
        </template>
      </ModalFooter>
    </template>
  </Modal>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { PhCheckCircle, PhInfo, PhSpinner, PhWarningCircle, PhX } from '@phosphor-icons/vue'
import { Modal, ModalBody, ModalFooter, ModalHeader } from '@/components/feedback'
import BookMatchCard from './BookMatchCard.vue'
import { useLibraryImportStore, type LibraryImportItem } from '@/stores/libraryImport'
import {
  buildLibraryImportInitialAuthor,
  buildLibraryImportInitialQuery,
} from '@/utils/libraryImportSearch'
import {
  asinChanged,
  asinValid,
  createBookMatch,
  resolveRecord,
  type BookMatch,
} from '@/utils/bookMatch'
import type { SearchResult } from '@/types'

const props = defineProps<{ item: LibraryImportItem }>()
const emit = defineEmits<{
  close: []
  select: [result: SearchResult]
}>()

const store = useLibraryImportStore()
const configuredRegion = ref('')
// Built once the configured region is known: the card searches as soon as it shows
const match = ref<BookMatch | null>(null)
const confirming = ref(false)
const searching = ref(false)
const error = ref<string | null>(null)
// Importing waits for a running search; counted once, however it ends (also when the modal closes)
let searchCounted = false

const bookTitle = computed(() => props.item.detectedTitle?.trim() || props.item.folderName)

// A new record, picked or typed as a valid ASIN; the one the folder already has is no change
const canConfirm = computed(
  () =>
    !!match.value &&
    !confirming.value &&
    !searching.value &&
    asinChanged(match.value) &&
    asinValid(match.value),
)

onMounted(async () => {
  configuredRegion.value = await store.configuredRegion()
  const selected = props.item.selectedMatch
  match.value = createBookMatch(
    {
      asin: selected?.asin ?? '',
      // The configured region by default; the user can pick another one, which the match keeps
      region: selected?.region || configuredRegion.value,
      // ASIN → filename stem (when more specific than folder) → folderName
      query: buildLibraryImportInitialQuery(props.item),
      author: buildLibraryImportInitialAuthor(props.item),
    },
    { asin: selected?.asin ?? '', region: selected?.region ?? '' },
  )
})

function onSearching(value: boolean) {
  searching.value = value
  if (value && !searchCounted) {
    searchCounted = true
    store.findMatchSearchStarted()
  } else if (!value && searchCounted) {
    searchCounted = false
    store.findMatchSearchEnded()
  }
}

// The picked record, or the typed ASIN once its region is checked to have it
async function confirm() {
  if (!match.value || !canConfirm.value) return
  confirming.value = true
  error.value = null
  onSearching(true)
  try {
    emit('select', await resolveRecord(match.value, store.searchInRegion))
    emit('close')
  } catch (e) {
    error.value = e instanceof Error ? e.message : 'The ASIN could not be checked'
  } finally {
    confirming.value = false
    onSearching(false)
  }
}

onBeforeUnmount(() => onSearching(false))
</script>

<style scoped>
.find-match-info {
  display: flex;
  align-items: flex-start;
  gap: 0.6rem;
  margin-bottom: 0.75rem;
  padding: 0.75rem;
  background-color: rgba(52, 152, 219, 0.09);
  border: 1px solid rgba(52, 152, 219, 0.28);
  border-radius: 6px;
  color: #3498db;
}

.find-match-info p {
  margin: 0;
  font-size: 0.95rem;
  line-height: 1.4;
  color: #ccc;
}

.find-match-error {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  margin: 0.75rem 0 0;
  font-size: 0.85rem;
  color: #ef4444;
}
</style>
