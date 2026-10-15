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
      <div v-if="ready" class="find-match-toolbar">
        <button
          type="button"
          class="btn btn-secondary find-match-revert-btn"
          title="Revert"
          aria-label="Revert to the values the search opened with"
          data-testid="find-match-revert"
          :disabled="!isEdited"
          @click="revert"
        >
          <PhArrowCounterClockwise :size="14" />
          Revert
        </button>
      </div>
      <AudibleRecordSearch
        v-if="ready"
        :state="state"
        :region-options="regionOptions"
        autofocus
        @pick="select"
        @escape="emit('close')"
      />
      <p v-if="ready && !state.searched && !state.results.length && !busy" class="hint-text">
        Search by title and author, or type the ASIN: it is looked up in the selected region.
      </p>
    </ModalBody>
  </Modal>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { PhArrowCounterClockwise } from '@phosphor-icons/vue'
import { Modal, ModalHeader, ModalBody } from '@/components/feedback'
import AudibleRecordSearch from '@/components/domain/audiobook/AudibleRecordSearch.vue'
import { useLibraryImportStore, type LibraryImportItem } from '@/stores/libraryImport'
import {
  ASIN_PATTERN,
  createRecordSearchState,
  lookupAsin,
  resetRecordSearch,
  searchRecords,
} from '@/utils/audibleRecordSearch'
import { orderedSearchRegionOptions } from '@/utils/languageMapping'
import { buildLibraryImportQuery, buildLibraryImportTitle } from '@/utils/libraryImportSearch'
import type { SearchResult } from '@/types'

const props = defineProps<{ item: LibraryImportItem }>()
const emit = defineEmits<{
  close: []
  select: [result: SearchResult]
}>()

const store = useLibraryImportStore()
const match = props.item.selectedMatch
// The same query Start Matching uses for this row
const rowQuery = buildLibraryImportQuery(props.item)
// The values the search opens with, restored by Revert (the region is set once configured is known)
const initial = {
  asin: match?.asin ?? rowQuery.asin ?? '',
  region: match?.region ?? '',
  // The cleaned folder name for a book of several audio files, the file name for a single file
  query: rowQuery.title ?? buildLibraryImportTitle(props.item),
  // The author read from the files only: the current match may be the wrong book
  author: props.item.detectedAuthor?.trim() ?? '',
}
const state = reactive(createRecordSearchState(initial))
// The region of the current match, else the configured one; the user can pick another one, which
// the selected match keeps
const configuredRegion = ref('')
const ready = ref(false)
const regionOptions = computed(() =>
  orderedSearchRegionOptions([match?.region, configuredRegion.value, 'us']),
)
const busy = computed(() => state.searching || state.lookingUp)

const isEdited = computed(
  () =>
    state.asin !== initial.asin ||
    state.region !== initial.region ||
    state.query !== initial.query ||
    state.author !== initial.author,
)

// What the search shows on open: the title searched with the current match picked, or the ASIN read
// from the files looked up, or the title searched
async function openingSearch() {
  if (match?.asin) {
    state.chosen = { asin: match.asin.toUpperCase(), region: state.region }
    await searchRecords(state)
  } else if (ASIN_PATTERN.test(state.asin)) {
    await lookupAsin(state)
  } else if (state.query.trim()) {
    await searchRecords(state)
  }
}

onMounted(async () => {
  configuredRegion.value = await store.configuredRegion()
  if (!initial.region) initial.region = configuredRegion.value
  state.region = initial.region
  ready.value = true
  await openingSearch()
})

async function revert() {
  resetRecordSearch(state, initial)
  await openingSearch()
}

function select(result: SearchResult) {
  emit('select', result)
  emit('close')
}
</script>

<style scoped>
.find-match-toolbar {
  display: flex;
  justify-content: flex-end;
  margin-bottom: 0.5rem;
}

.find-match-revert-btn {
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
}

.hint-text {
  margin: 0.5rem 0 0;
  font-size: 0.8rem;
  opacity: 0.75;
}
</style>
