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
<!-- The state bar along the bottom edge of a card or a dialog, with the library's colours and
     thickness (its cards' bottom bar): grey idle, green success, orange warning, red error, blue
     progress. Breathing while something runs that has no progress to show. With a percent, a
     progress bar: a grey track filled from left to right in its tone. Standby keeps its room
     unseen. Its parent is positioned and keeps the 3px free at its bottom. -->
<template>
  <div
    class="status-bar"
    :class="[
      `status-bar-${tone}`,
      {
        'status-bar-breathing': isBreathing,
        'status-bar-track': percent !== undefined,
        'status-bar-standby': isStandby,
      },
    ]"
    :data-tone="tone"
    :data-breathing="isBreathing || undefined"
    :data-standby="isStandby || undefined"
  >
    <div v-if="percent !== undefined" class="status-bar-fill" :style="{ width: `${percent}%` }" />
  </div>
</template>

<script setup lang="ts">
export type StatusTone = 'idle' | 'success' | 'warning' | 'error' | 'progress'

withDefaults(
  defineProps<{
    tone?: StatusTone
    isBreathing?: boolean
    // From 0 to 100: a progress bar
    percent?: number
    isStandby?: boolean
  }>(),
  {
    tone: 'idle',
    isBreathing: false,
    percent: undefined,
    isStandby: false,
  },
)
</script>

<style scoped>
.status-bar {
  --status-bar-tone: var(--status-idle);
  position: absolute;
  left: 0;
  right: 0;
  bottom: 0;
  height: 3px;
  background: var(--status-bar-tone);
}

.status-bar-success {
  --status-bar-tone: var(--status-success);
}

.status-bar-warning {
  --status-bar-tone: var(--status-warning);
}

.status-bar-error {
  --status-bar-tone: var(--status-error);
}

.status-bar-progress {
  --status-bar-tone: var(--status-progress);
}

.status-bar-breathing:not(.status-bar-track) {
  animation: status-bar-breathe 1.6s ease-in-out infinite alternate;
}

/* A progress bar: the tone fills the grey track */
.status-bar.status-bar-track {
  background: var(--status-idle);
  overflow: hidden;
}

.status-bar-fill {
  height: 100%;
  background: var(--status-bar-tone);
  transition: width 0.3s ease;
}

.status-bar-breathing .status-bar-fill {
  animation: status-bar-fill-breathe 1.6s ease-in-out infinite alternate;
}

/* Unseen, the background's own colour, but it keeps its room */
.status-bar.status-bar-standby {
  background: transparent;
}

@keyframes status-bar-breathe {
  from {
    opacity: 0.2;
  }
  to {
    opacity: 1;
  }
}

@keyframes status-bar-fill-breathe {
  from {
    opacity: 0.35;
  }
  to {
    opacity: 1;
  }
}

@media (prefers-reduced-motion: reduce) {
  .status-bar-breathing,
  .status-bar-breathing .status-bar-fill {
    animation: none;
  }
}
</style>
