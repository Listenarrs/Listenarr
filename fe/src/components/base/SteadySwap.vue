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
<!-- Several contents in one place, only the current one seen: the place keeps the size of the
     largest from the start, so nothing moves when the current one changes (a button's labels, a
     box's messages). Each named slot is one content, currentSlot names the one seen; a slot never
     current only sizes the place (the longest text a content can take). inline lays each content
     out like a button's label: its icon and text centred on one line. -->
<template>
  <span class="steady-swap" :class="{ 'steady-swap-inline': isInline }">
    <span
      v-for="slotName in slotNames"
      :key="slotName"
      class="steady-swap-item"
      :class="{ 'steady-swap-unseen': slotName !== currentSlot }"
      :style="isInline ? { gap } : undefined"
      :aria-hidden="slotName !== currentSlot"
      :data-testid="slotName === currentSlot ? currentTestId : undefined"
    >
      <slot :name="slotName" />
    </span>
  </span>
</template>

<script setup lang="ts">
import { computed, useSlots } from 'vue'

withDefaults(
  defineProps<{
    currentSlot: string
    isInline?: boolean
    // Between an inline content's icon and text
    gap?: string
    currentTestId?: string
  }>(),
  {
    isInline: false,
    gap: '0.4rem',
    currentTestId: undefined,
  },
)

const slots = useSlots()
const slotNames = computed(() => Object.keys(slots))
</script>

<style scoped>
.steady-swap {
  display: grid;
}

/* All in one cell: the place as large as the largest */
.steady-swap-item {
  grid-area: 1 / 1;
}

.steady-swap-inline > .steady-swap-item {
  display: inline-flex;
  align-items: center;
  justify-content: center;
}

.steady-swap-unseen {
  visibility: hidden;
}
</style>
