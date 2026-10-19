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
  <!-- The heading of a search's results, with what that search was made with and a way to load it
       back into the form (the form itself is never changed behind the user's back) -->
  <div class="search-results-heading">
    <div class="search-results-heading-text">
      <h2>{{ heading }}</h2>
      <p v-if="description" class="search-results-for" data-testid="search-results-for">
        {{ description }}
      </p>
    </div>
    <button
      v-if="context"
      type="button"
      class="btn btn-secondary restore-search-btn"
      data-testid="restore-search-inputs"
      title="Put these search parameters back into the search form"
      @click="emit('restore', context)"
    >
      <PhArrowCounterClockwise />
      Restore search inputs
    </button>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { PhArrowCounterClockwise } from '@phosphor-icons/vue'
import { describeSearchContext } from '@/utils/searchContext'
import type { SearchContext } from '@/stores/addNewSearch'

const props = defineProps<{ heading: string; context: SearchContext | null }>()
const emit = defineEmits<{ restore: [context: SearchContext] }>()

const description = computed(() => (props.context ? describeSearchContext(props.context) : ''))
</script>

<style scoped>
.search-results-heading {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 1rem;
  margin-bottom: 1rem;
}

.search-results-heading-text h2 {
  margin: 0;
}

.search-results-for {
  margin: 0.25rem 0 0;
  font-size: 0.9rem;
  color: #bfcad6;
}

.restore-search-btn {
  display: inline-flex;
  align-items: center;
  gap: 0.4rem;
  flex-shrink: 0;
  white-space: nowrap;
}
</style>
