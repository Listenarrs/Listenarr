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
<!-- A book match card's fields: a search box read like Add New's and the Region, then Search
     (Retry once a search ran, until the search box or region changes; Cancel search while one
     runs), always sharing the whole row. Typing only prepares the next search; Enter in the search
     box, the button and a region change ask for it. -->
<template>
  <div class="match-fields" :class="{ 'match-fields-narrow': isNarrow }">
    <label class="match-field match-field-query">
      <span class="match-field-label">Search</span>
      <ClearableInput
        ref="queryInputRef"
        v-model="bookMatch.query"
        placeholder="Title, ASIN, or TITLE:… AUTHOR:… SERIES:…"
        title="As in Add New: a title, an ASIN, or TITLE:, AUTHOR:, ASIN:, SERIES: and ISBN: tokens"
        clear-label="Empty the Search field"
        clear-test-id="match-query-clear"
        data-testid="match-query"
        @input="emit('edited')"
        @cleared="emit('edited')"
        @keydown.enter.prevent="emit('search')"
      />
    </label>
    <label class="match-field">
      <span class="match-field-label">Region</span>
      <select
        v-model="bookMatch.region"
        class="form-input"
        data-testid="match-region"
        :disabled="isRegionLocked"
        :title="
          isRegionLocked
            ? 'Wait for the search to end: changing the region searches'
            : regionLabel(bookMatch.region)
        "
        @change="emit('regionChanged')"
      >
        <option
          v-for="regionOption in regionOptions"
          :key="regionOption.value"
          :value="regionOption.value"
          :title="regionOption.label"
        >
          {{ regionOption.value.toUpperCase() }}
        </option>
      </select>
    </label>
    <button
      v-if="searchAction === 'cancel'"
      type="button"
      class="btn btn-danger match-search-btn"
      data-testid="match-cancel-search"
      @click="emit('cancel')"
    >
      <PhX :size="16" />
      Cancel search
    </button>
    <button
      v-else-if="searchAction === 'retry'"
      type="button"
      class="btn btn-secondary match-search-btn"
      data-testid="match-retry"
      :disabled="!canSearch"
      :title="
        isSearchLocked
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
        isSearchLocked
          ? 'Another book is being searched'
          : 'The search Add New uses, with the same rules and priorities'
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
  // The button at the row's end: Search, Retry (nothing changed since the last search) or Cancel
  // search (one runs)
  searchAction: 'search' | 'retry' | 'cancel'
  // Search or Retry can run now
  canSearch: boolean
  // Another book is being searched
  isSearchLocked: boolean
  // Changing the region searches: not while a search runs, here or on another book
  isRegionLocked: boolean
  // The dialog is short of room: the narrow screen's layout
  isNarrow: boolean
}>()

const bookMatch = defineModel<BookMatch>('bookMatch', { required: true })

const emit = defineEmits<{
  // Any change to the search box, even back to what was searched
  edited: []
  search: []
  cancel: []
  regionChanged: []
}>()

const queryInputRef = ref<InstanceType<typeof ClearableInput> | null>(null)

// Every Audible region stays available, in canary's order (as in Add to Library): the book's
// region, then the preferred (configured) one and the US, then the others alphabetically by name
// (each option's tooltip; the field shows the codes)
const regionOptions = computed(() =>
  orderedSearchRegionOptions([
    bookMatch.value.initialValues.region,
    props.configuredRegion,
    FALLBACK_REGION,
  ]),
)

// The search box focused with its text selected, ready to be typed over
function focusQuery() {
  queryInputRef.value?.focusAndSelect()
}

defineExpose({ focusQuery })
</script>

<style scoped>
.match-fields {
  display: grid;
  grid-template-columns: minmax(0, 1fr) minmax(5rem, 6rem) auto;
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

/* A narrow screen, or a dialog short of room: the search box on a line of its own, then the region
   and Search */
@media (max-width: 720px) {
  .match-fields {
    grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
  }

  .match-field-query,
  .match-search-btn {
    grid-column: 1 / -1;
    width: auto;
  }
}

.match-fields-narrow {
  grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
}

.match-fields-narrow .match-field-query,
.match-fields-narrow .match-search-btn {
  grid-column: 1 / -1;
  width: auto;
}
</style>
