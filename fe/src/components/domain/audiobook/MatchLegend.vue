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
<!-- What the bar along the bottom of each card means: by default the book match card's (Repair
     Books and Find Match), or the entries given (Repair's step 2) -->
<template>
  <ul class="match-legend" data-testid="match-legend">
    <li v-for="entry in entries" :key="entry.label">
      <span class="match-legend-bar" :class="entry.tone" />{{ entry.label }}
    </li>
  </ul>
</template>

<script setup lang="ts">
// A bar colour, from the status palette, and what it means
export interface LegendEntry {
  tone: 'idle' | 'success' | 'warning' | 'error' | 'progress'
  label: string
}

withDefaults(defineProps<{ entries?: LegendEntry[] }>(), {
  entries: () => [
    { tone: 'idle', label: 'Ignored' },
    { tone: 'success', label: 'Matched' },
    { tone: 'warning', label: 'Invalid' },
    { tone: 'error', label: 'Error' },
    { tone: 'progress', label: 'Searching' },
  ],
})
</script>

<style scoped>
.match-legend {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 0.9rem;
  margin: 0;
  padding: 0;
  list-style: none;
  font-size: 0.8rem;
  color: #aaa;
}

.match-legend li {
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
}

/* The same colours as the card's bar, drawn the same way: a short horizontal bar */
.match-legend-bar {
  display: inline-block;
  width: 1rem;
  height: 3px;
  border-radius: 2px;
  background: var(--status-idle);
}

.match-legend-bar.success {
  background: var(--status-success);
}

.match-legend-bar.warning {
  background: var(--status-warning);
}

.match-legend-bar.error {
  background: var(--status-error);
}

.match-legend-bar.progress {
  background: var(--status-progress);
}
</style>
