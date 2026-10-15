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
import { Modal, ModalHeader, ModalBody } from '@/components/feedback'
import AudibleRecordSearch from '@/components/domain/audiobook/AudibleRecordSearch.vue'
import { useLibraryImportStore, type LibraryImportItem } from '@/stores/libraryImport'
import {
  ASIN_PATTERN,
  createRecordSearchState,
  lookupAsin,
  searchRecords,
} from '@/utils/audibleRecordSearch'
import { orderedSearchRegionOptions } from '@/utils/languageMapping'
import {
  buildLibraryImportInitialQuery,
  buildLibraryImportTitle,
} from '@/utils/libraryImportSearch'
import type { SearchResult } from '@/types'

const props = defineProps<{ item: LibraryImportItem }>()
const emit = defineEmits<{
  close: []
  select: [result: SearchResult]
}>()

const store = useLibraryImportStore()
const match = props.item.selectedMatch
// The ASIN read from the files, when there is no match yet
const detectedAsin = ASIN_PATTERN.test(buildLibraryImportInitialQuery(props.item))
  ? buildLibraryImportInitialQuery(props.item)
  : ''
const state = reactive(
  createRecordSearchState({
    asin: match?.asin ?? detectedAsin,
    region: match?.region ?? '',
    // The folder name for a book of several audio files, the file name for a single file
    query: buildLibraryImportTitle(props.item),
    author: props.item.detectedAuthor?.trim() || match?.authors?.[0]?.name || '',
  }),
)
// The region of the current match, else the configured one; the user can pick another one, which
// the selected match keeps
const configuredRegion = ref('')
const ready = ref(false)
const regionOptions = computed(() =>
  orderedSearchRegionOptions([match?.region, configuredRegion.value, 'us']),
)
const busy = computed(() => state.searching || state.lookingUp)

onMounted(async () => {
  configuredRegion.value = await store.configuredRegion()
  if (!state.region) state.region = configuredRegion.value
  ready.value = true
  if (match?.asin) {
    // Search the title, showing the current match as the picked one
    state.chosen = { asin: match.asin.toUpperCase(), region: state.region }
    await searchRecords(state)
  } else if (ASIN_PATTERN.test(state.asin)) {
    // An ASIN read from the files: show which book it is in this region
    await lookupAsin(state)
  } else if (state.query.trim()) {
    await searchRecords(state)
  }
})

function select(result: SearchResult) {
  emit('select', result)
  emit('close')
}
</script>

<style scoped>
.hint-text {
  margin: 0.5rem 0 0;
  font-size: 0.8rem;
  opacity: 0.75;
}
</style>
