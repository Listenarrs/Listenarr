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
      <NoticeBox class="find-match-info">
        <template #icon><PhInfo /></template>
        <p>Find the right Audible record for this book.</p>
        <p data-testid="find-match-info-asin">
          A filled ASIN is searched on its own: the title and author are left out of that search.
        </p>
      </NoticeBox>

      <BookMatchCard
        v-if="bookMatch"
        v-model:book-match="bookMatch"
        :configured-region="configuredRegion"
        :book="book"
        should-search-on-mount
        should-keep-results-room
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
import { computed, onBeforeUnmount, onMounted, provide, ref } from 'vue'
import { PhCheckCircle, PhInfo, PhX } from '@phosphor-icons/vue'
import { Modal, ModalBody, ModalFooter, ModalHeader } from '@/components/feedback'
import NoticeBox from '@/components/base/NoticeBox.vue'
import BookMatchCard from '@/components/domain/bookMatch/BookMatchCard.vue'
import MatchLegend from '@/components/domain/bookMatch/MatchLegend.vue'
import { useLibraryImportStore, type LibraryImportItem } from '@/stores/libraryImport'
import {
  buildLibraryImportInitialAuthor,
  buildLibraryImportInitialQuery,
} from '@/utils/libraryImportSearch'
import {
  ASIN_PATTERN,
  hasAsinChanged,
  createBookMatch,
  matchState,
  recordFromResult,
  SEARCH_CACHE,
  type BookMatch,
  type BookRecord,
  type SearchCache,
} from '@/utils/bookMatch'
import type { SearchResult } from '@/types'

const props = defineProps<{ item: LibraryImportItem }>()
const emit = defineEmits<{
  close: []
  select: [result: SearchResult]
}>()

const store = useLibraryImportStore()
// The searches answered while this dialog is open (it is created each time it opens)
provide<SearchCache>(SEARCH_CACHE, new Map())
const configuredRegion = ref('')
// Built once the configured region is known: the card searches as soon as it shows
const bookMatch = ref<BookMatch | null>(null)
const isSearching = ref(false)
// Importing waits for a running search; counted once, however it ends (also when the modal closes)
let isSearchCounted = false

// The folder's match when it has one (read like New), else what its files say
const book = computed<BookRecord>(() => {
  const selectedMatch = props.item.selectedMatch
  if (selectedMatch) return recordFromResult(selectedMatch, selectedMatch.region ?? '')
  return { title: props.item.detectedTitle, authors: [props.item.detectedAuthor ?? ''] }
})

const bookTitle = computed(() => props.item.detectedTitle?.trim() || props.item.folderName)

// A new record, looked up in its region (picked, or the one found for the ASIN); the one the
// folder already has is no change
const canConfirm = computed(
  () =>
    !!bookMatch.value &&
    !isSearching.value &&
    matchState(bookMatch.value) === 'matched' &&
    hasAsinChanged(bookMatch.value),
)

onMounted(async () => {
  configuredRegion.value = await store.configuredRegion()
  const selectedMatch = props.item.selectedMatch
  // Always the same search, from the folder's scan, whatever match the folder has (it stays its
  // current record, tagged Current): the scan's ASIN, else its title and author, in the configured
  // region. The user can pick another region, which the match keeps
  const scanAsin = props.item.detectedAsin?.trim() ?? ''
  // The scan's ASIN in the ASIN field, searched as one ("No record for … in Audible France" when
  // the region doesn't sell it), its title and author ready for a search without it. A value the
  // scan took for an ASIN but isn't one stays a title, so the card never opens on "Not an ASIN"
  const isScanAsin = ASIN_PATTERN.test(scanAsin)
  const scanWithoutAsin = { ...props.item, detectedAsin: undefined }
  bookMatch.value = createBookMatch(
    {
      asin: isScanAsin ? scanAsin : '',
      region: configuredRegion.value,
      // Title: filename stem (when more specific than folder) → folderName
      query: buildLibraryImportInitialQuery(isScanAsin ? scanWithoutAsin : props.item),
      author: buildLibraryImportInitialAuthor(isScanAsin ? scanWithoutAsin : props.item),
    },
    { asin: selectedMatch?.asin ?? '', region: selectedMatch?.region ?? '' },
  )
})

function onSearching(isSearchRunning: boolean) {
  isSearching.value = isSearchRunning
  if (isSearchRunning && !isSearchCounted) {
    isSearchCounted = true
    store.findMatchSearchStarted()
  } else if (!isSearchRunning && isSearchCounted) {
    isSearchCounted = false
    store.findMatchSearchEnded()
  }
}

function confirm() {
  if (!bookMatch.value?.pickedSearchResult || !canConfirm.value) return
  emit('select', bookMatch.value.pickedSearchResult)
  emit('close')
}

onBeforeUnmount(() => onSearching(false))
</script>

<style scoped>
.find-match-info {
  margin-bottom: 0.75rem;
}
</style>
