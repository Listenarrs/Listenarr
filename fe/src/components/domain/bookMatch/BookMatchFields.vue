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
<!-- A book match card's fields: ASIN, Title, Author and Region, then Search (Retry once a search
     ran, until a field is edited; Cancel search while one runs), always sharing the whole row.
     Typing only prepares the next search; Enter in a field, the button and a region change ask
     for it. -->
<template>
  <div class="match-fields" :class="{ 'match-fields-narrow': narrow }">
    <label class="match-field match-field-asin">
      <span class="match-field-label">ASIN</span>
      <ClearableInput
        v-model.trim="match.asin"
        maxlength="10"
        clear-label="Empty the ASIN field"
        clear-test-id="match-asin-clear"
        data-testid="match-asin"
        @input="emit('edited')"
        @cleared="emit('edited')"
        @keydown.enter.prevent="emit('search')"
      />
    </label>
    <label class="match-field">
      <span class="match-field-label">Title</span>
      <ClearableInput
        ref="titleInput"
        v-model="match.query"
        clear-label="Empty the Title field"
        clear-test-id="match-title-clear"
        data-testid="match-title"
        @input="emit('edited')"
        @cleared="emit('edited')"
        @keydown.enter.prevent="emit('search')"
      />
    </label>
    <label class="match-field">
      <span class="match-field-label">Author</span>
      <ClearableInput
        v-model="match.author"
        clear-label="Empty the Author field"
        clear-test-id="match-author-clear"
        data-testid="match-author"
        @input="emit('edited')"
        @cleared="emit('edited')"
        @keydown.enter.prevent="emit('search')"
      />
    </label>
    <label class="match-field">
      <span class="match-field-label">Region</span>
      <select
        v-model="match.region"
        class="form-input"
        data-testid="match-region"
        :disabled="regionLocked"
        :title="
          regionLocked
            ? 'Wait for the search to end: changing the region searches'
            : regionLabel(match.region)
        "
        @change="emit('regionChanged')"
      >
        <option v-for="o in regionOptions" :key="o.value" :value="o.value" :title="o.label">
          {{ o.value.toUpperCase() }}
        </option>
      </select>
    </label>
    <button
      v-if="action === 'cancel'"
      type="button"
      class="btn btn-danger match-search-btn"
      data-testid="match-cancel-search"
      @click="emit('cancel')"
    >
      <PhX :size="16" />
      Cancel search
    </button>
    <button
      v-else-if="action === 'retry'"
      type="button"
      class="btn btn-secondary match-search-btn"
      data-testid="match-retry"
      :disabled="!canSearch"
      :title="
        searchLocked
          ? 'Another book is being searched'
          : 'Ask Audible again with the same values (a new request)'
      "
      @click="emit('search')"
    >
      <PhArrowCounterClockwise :size="16" />
      Retry
    </button>
    <button
      v-else
      type="button"
      class="btn btn-secondary match-search-btn"
      data-testid="match-search"
      :disabled="!canSearch"
      :title="
        searchLocked
          ? 'Another book is being searched'
          : 'The search Add New and Library Import use, with the same rules and priorities'
      "
      @click="emit('search')"
    >
      <PhMagnifyingGlass :size="16" />
      Search
    </button>
  </div>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
import { PhArrowCounterClockwise, PhMagnifyingGlass, PhX } from '@phosphor-icons/vue'
import ClearableInput from '@/components/form/ClearableInput.vue'
import { orderedSearchRegionOptions } from '@/utils/languageMapping'
import { FALLBACK_REGION, regionLabel, type BookMatch } from '@/utils/bookMatch'

const props = defineProps<{
  configuredRegion: string
  // The button at the row's end: Search, Retry (no field edited since the last search) or Cancel
  // search (one runs)
  action: 'search' | 'retry' | 'cancel'
  // Search or Retry can run now
  canSearch: boolean
  // Another book is being searched
  searchLocked: boolean
  // Changing the region searches: not while a search runs, here or on another book
  regionLocked: boolean
  // The dialog is short of room: the narrow screen's layout
  narrow: boolean
}>()

const match = defineModel<BookMatch>('match', { required: true })

const emit = defineEmits<{
  // Any change to a field, even back to what was searched
  edited: []
  search: []
  cancel: []
  regionChanged: []
}>()

const titleInput = ref<InstanceType<typeof ClearableInput> | null>(null)

// Every Audible region stays available, in canary's order (as in Add to Library): the book's
// region, then the preferred (configured) one and the US, then the others alphabetically by name
// (each option's tooltip; the field shows the codes)
const regionOptions = computed(() =>
  orderedSearchRegionOptions([match.value.initial.region, props.configuredRegion, FALLBACK_REGION]),
)

// The title focused with its text selected, ready to be typed over
function focusTitle() {
  titleInput.value?.focusAndSelect()
}

defineExpose({ focusTitle })
</script>

<style scoped>
.match-fields {
  display: grid;
  grid-template-columns:
    minmax(10.5rem, 1.2fr) minmax(0, 2.5fr) minmax(0, 1.5fr) minmax(5rem, 0.5fr)
    auto;
  align-items: end;
  gap: 0.75rem;
  margin-top: 0.75rem;
}

.match-field {
  display: flex;
  flex-direction: column;
  gap: 0.3rem;
  min-width: 0;
}

.match-field .form-input {
  width: 100%;
  box-sizing: border-box;
}

.match-field-asin :deep(input) {
  font-family: monospace;
}

.match-field-label {
  font-size: 0.8rem;
  color: #aaa;
}

/* Search and Cancel search take the same width, so the row doesn't move when they swap */
.match-search-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 0.4rem;
  width: 11rem;
  white-space: nowrap;
}

/* A narrow screen, or a dialog short of room: the fields two by two, Search on a line of its own */
@media (max-width: 720px) {
  .match-fields {
    grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
  }

  .match-search-btn {
    grid-column: 1 / -1;
    width: auto;
  }
}

.match-fields-narrow {
  grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
}

.match-fields-narrow .match-search-btn {
  grid-column: 1 / -1;
  width: auto;
}
</style>
