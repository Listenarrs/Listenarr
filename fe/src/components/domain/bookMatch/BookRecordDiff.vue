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
     from the other record highlighted: in green on the new record (what it brings), in orange on
     the old one (replaced: what goes). The same record again (metadata read anew) has nothing
     highlighted. Repair's Current/Old and New lines, on the book card and on step 2 -->
<template>
  <span class="book-record-diff">
    <template v-for="(part, partIndex) in comparedParts" :key="part.key">
      <template v-if="partIndex"> - </template>
      <span
        :class="{ [isReplaced ? 'book-record-replaced' : 'book-record-changed']: part.isChanged }"
        :data-changed="part.isChanged || undefined"
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
  // The other record: the one it replaces, or with replaced, the one replacing it
  baseRecord: BookRecord
  // The old record: its parts the new one changes are the replaced ones (orange)
  isReplaced?: boolean
}>()

// Compared part by part, regardless of case
const comparedParts = computed(() => {
  const baseTextByKey = new Map(recordParts(props.baseRecord).map((part) => [part.key, part.text]))
  return recordParts(props.record).map((part) => ({
    ...part,
    isChanged: (baseTextByKey.get(part.key) ?? '').toUpperCase() !== part.text.toUpperCase(),
  }))
})
</script>

<style scoped>
.book-record-changed {
  color: var(--status-success);
  font-weight: 600;
}

.book-record-replaced {
  color: var(--status-warning);
  font-weight: 600;
}
</style>
