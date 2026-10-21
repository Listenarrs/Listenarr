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
        <p>Find the right Audible record for this book.</p>
      </div>

      <BookMatchCard
        v-if="match"
        v-model:match="match"
        :configured-region="configuredRegion"
        :book="book"
        search-on-mount
        data-testid="find-match-card"
        @searching="onSearching"
      >
        <template #heading>{{ bookTitle }}</template>
      </BookMatchCard>
    </ModalBody>
    <template #footer>
      <ModalFooter :showCancel="false">
        <template #left>
          <button type="button" class="btn cancel-button" @click="emit('close')">
            <PhX :size="16" />
            Cancel
          </button>
          <MatchLegend />
        </template>
        <template #default>
          <button
            type="button"
            class="btn btn-primary"
            data-testid="find-match-confirm"
            :disabled="!canConfirm"
            @click="confirm"
          >
            <PhCheckCircle :size="16" />
            Use this match
          </button>
        </template>
      </ModalFooter>
    </template>
  </Modal>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { PhCheckCircle, PhInfo, PhX } from '@phosphor-icons/vue'
import { Modal, ModalBody, ModalFooter, ModalHeader } from '@/components/feedback'
import BookMatchCard from './BookMatchCard.vue'
import MatchLegend from './MatchLegend.vue'
import { useLibraryImportStore, type LibraryImportItem } from '@/stores/libraryImport'
import {
  buildLibraryImportInitialAuthor,
  buildLibraryImportInitialQuery,
} from '@/utils/libraryImportSearch'
import { asinChanged, createBookMatch, matchState, type BookMatch } from '@/utils/bookMatch'
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
const searching = ref(false)
// Importing waits for a running search; counted once, however it ends (also when the modal closes)
let searchCounted = false

// The folder's match when it has one, else what its files say
const book = computed(() => {
  const selected = props.item.selectedMatch
  return {
    title: selected?.title ?? props.item.detectedTitle,
    subtitle: selected?.subtitle,
    author: selected?.authors?.[0]?.name ?? props.item.detectedAuthor,
  }
})

const bookTitle = computed(() => props.item.detectedTitle?.trim() || props.item.folderName)

// A new record, looked up in its region (picked, or the one found for the ASIN); the one the
// folder already has is no change
const canConfirm = computed(
  () =>
    !!match.value &&
    !searching.value &&
    matchState(match.value) === 'matched' &&
    asinChanged(match.value),
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

function confirm() {
  if (!match.value?.picked || !canConfirm.value) return
  emit('select', match.value.picked)
  emit('close')
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
</style>
