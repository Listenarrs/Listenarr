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
  buildLibraryImportInitialAuthor,
  buildLibraryImportInitialQuery,
} from '@/utils/libraryImportSearch'
import type { SearchResult } from '@/types'

const props = defineProps<{ item: LibraryImportItem }>()
const emit = defineEmits<{
  close: []
  select: [result: SearchResult]
}>()

const store = useLibraryImportStore()
// The current match's ASIN, else the ASIN read from the files, else the title (filename stem when
// more specific than the folder; the "album" tag is often the series name)
const initialQuery = buildLibraryImportInitialQuery(props.item)
const initialAsin =
  props.item.selectedMatch?.asin ?? (ASIN_PATTERN.test(initialQuery) ? initialQuery : '')
const state = reactive(
  createRecordSearchState({
    asin: initialAsin,
    region: props.item.selectedMatch?.region ?? '',
    query: props.item.selectedMatch?.title ?? (ASIN_PATTERN.test(initialQuery) ? '' : initialQuery),
    author:
      props.item.selectedMatch?.authors?.[0]?.name ?? buildLibraryImportInitialAuthor(props.item),
  }),
)
// The region of the current match, else the configured one; the user can pick another one, which
// the selected match keeps
const configuredRegion = ref('')
const ready = ref(false)
const regionOptions = computed(() =>
  orderedSearchRegionOptions([props.item.selectedMatch?.region, configuredRegion.value, 'us']),
)
const busy = computed(() => state.searching || state.lookingUp)

onMounted(async () => {
  configuredRegion.value = await store.configuredRegion()
  if (!state.region) state.region = configuredRegion.value
  ready.value = true
  // Show the book straight away: its ASIN looked up in the region, else the title searched
  if (ASIN_PATTERN.test(state.asin)) await lookupAsin(state)
  else if (state.query.trim()) await searchRecords(state)
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
