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
<!-- What the book points at today (Current), and the record picked in the results shown (New; no
     record otherwise: why a search found none is said over the results' room). With a new record
     picked, the parts of Current it replaces in orange. -->
<template>
  <dl class="match-records" :class="{ 'match-records-narrow': narrow }">
    <dt>Current</dt>
    <dd data-testid="match-current">
      <BookRecordDiff v-if="picked" :record="current" :base="picked" replaced />
      <template v-else>{{ bookSummary(current) || 'None' }}</template>
    </dd>
    <dt>New</dt>
    <dd v-if="picked" class="match-new-picked" data-testid="match-picked">
      <BookRecordDiff :record="picked" :base="current" />
      <span class="match-status">
        <PhCheckCircle :size="14" weight="fill" />
        checked
      </span>
    </dd>
    <dd v-else data-testid="match-none">
      No new record yet: search the book and pick it, or type its ASIN
    </dd>
  </dl>
</template>

<script setup lang="ts">
import { PhCheckCircle } from '@phosphor-icons/vue'
import BookRecordDiff from './BookRecordDiff.vue'
import { bookSummary, type BookRecord } from '@/utils/bookMatch'

defineProps<{
  current: BookRecord
  picked: BookRecord | null
  // The dialog is short of room: two lines kept for each, as on a narrow screen
  narrow: boolean
}>()
</script>

<style scoped>
/* Current and New read like the summary next to Revert; only their state is coloured. Each keeps
   room for the lines a record summary usually takes (one, two on a phone), so the card doesn't
   grow or shrink when the new record changes */
.match-records {
  display: grid;
  grid-template-columns: auto 1fr;
  align-items: start;
  line-height: 1.4;
  gap: 0.3rem 0.75rem;
  margin: 0.75rem 0 0;
  font-size: 0.8rem;
  color: #aaa;
}

.match-records dt {
  color: #aaa;
}

.match-records dd {
  color: #aaa;
  display: flex;
  align-items: center;
  align-content: flex-start;
  flex-wrap: wrap;
  gap: 0.4rem;
  margin: 0;
  min-width: 0;
  min-height: 1.4em;
}

/* The new record flows as text, so "checked" follows its last line instead of taking a line of
   its own */
.match-records dd.match-new-picked {
  display: block;
}

/* Line height 1: a "checked" box as tall as the text line, centred on it, would stick out of it
   and make the picked record taller than "No new record yet" */
.match-status {
  display: inline-flex;
  align-items: center;
  gap: 0.25rem;
  margin-left: 0.4rem;
  line-height: 1;
  vertical-align: middle;
  white-space: nowrap;
  color: var(--status-success);
}

@media (max-width: 720px) {
  .match-records dd {
    min-height: 2.8em;
  }
}

.match-records-narrow dd {
  min-height: 2.8em;
}
</style>
