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
<!-- A book record on one line (ASIN - Title: Subtitle - Author(s) - Region), each part that differs
     from the record it replaces highlighted. The same record again (metadata read anew) has nothing
     highlighted. Repair's New lines, on the book card and on the results -->
<template>
  <span class="book-record-diff">
    <template v-for="(part, index) in parts" :key="part.key">
      <template v-if="index"> - </template>
      <span
        :class="{ 'book-record-changed': part.changed }"
        :data-changed="part.changed || undefined"
        >{{ part.text }}</span
      >
    </template>
  </span>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { recordParts, type BookRecord } from '@/utils/bookMatch'

const props = defineProps<{
  record: BookRecord
  // The record it replaces
  base: BookRecord
}>()

// Compared part by part, regardless of case
const parts = computed(() => {
  const base = new Map(recordParts(props.base).map((part) => [part.key, part.text]))
  return recordParts(props.record).map((part) => ({
    ...part,
    changed: (base.get(part.key) ?? '').toUpperCase() !== part.text.toUpperCase(),
  }))
})
</script>

<style scoped>
.book-record-changed {
  color: var(--status-success);
  font-weight: 600;
}
</style>
