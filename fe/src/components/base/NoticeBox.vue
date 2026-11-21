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
<!-- A note across a dialog: an icon and lines of text, in its tone's colours. info is the dialogs'
     blue info box (the Organize dialog's), whose icon the modal styles size; warning, progress and
     muted say how something went, not to be missed. -->
<template>
  <div class="notice-box" :class="[`notice-box-${tone}`, { 'info-section': tone === 'info' }]">
    <slot name="icon" />
    <div class="notice-box-content">
      <slot />
    </div>
  </div>
</template>

<script setup lang="ts">
withDefaults(defineProps<{ tone?: 'info' | 'warning' | 'progress' | 'muted' }>(), {
  tone: 'info',
})
</script>

<style scoped>
.notice-box {
  --notice-box-tone: #3498db;
  display: flex;
  align-items: flex-start;
  gap: 0.6rem;
  padding: 0.75rem;
  background-color: color-mix(in srgb, var(--notice-box-tone) 10%, transparent);
  border: 1px solid color-mix(in srgb, var(--notice-box-tone) 45%, transparent);
  border-radius: 6px;
  color: var(--notice-box-tone);
}

.notice-box-info {
  background-color: rgba(52, 152, 219, 0.09);
  border-color: rgba(52, 152, 219, 0.28);
}

.notice-box-warning {
  --notice-box-tone: var(--status-warning);
}

.notice-box-progress {
  --notice-box-tone: var(--status-progress);
}

.notice-box-muted {
  background-color: rgba(255, 255, 255, 0.04);
  border-color: #444;
  color: #aaa;
}

.notice-box-content {
  display: flex;
  flex-direction: column;
  gap: 0.3rem;
  font-size: 0.95rem;
  line-height: 1.4;
  color: #ddd;
}

.notice-box-info .notice-box-content {
  color: #ccc;
}

.notice-box-content :slotted(p) {
  margin: 0;
}
</style>
