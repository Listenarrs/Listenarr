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
  <div class="scan-task" :class="`scan-task--${task.status}`" role="status" data-testid="scan-task">
    <component :is="icon" class="scan-task-icon" :class="{ 'ph-spin': active }" :size="18" />

    <div class="scan-task-text">
      <p class="scan-task-title">
        <template v-if="active">
          Scanning <strong>{{ folderName }}</strong>
        </template>
        <template v-else-if="task.status === 'done'">
          Scan of <strong>{{ folderName }}</strong> finished:
          {{ task.itemCount === 1 ? '1 unmatched book' : `${task.itemCount ?? 0} unmatched books` }}
        </template>
        <template v-else-if="task.status === 'failed'">
          Scan of <strong>{{ folderName }}</strong> failed
        </template>
        <template v-else>
          Scan of <strong>{{ folderName }}</strong> canceled
        </template>
        <span class="scan-task-time">{{ timeLabel }}</span>
      </p>
      <p v-if="active" class="scan-task-help">
        You can browse other folders while this runs. Starting a new scan cancels the current scan.
      </p>
      <p v-else-if="task.status === 'failed' && task.error" class="scan-task-error">
        {{ task.error }}
      </p>
    </div>

    <div class="scan-task-actions">
      <button
        v-if="!shown"
        type="button"
        class="btn btn-secondary btn-sm"
        data-testid="scan-task-show"
        @click="emit('show')"
      >
        {{ task.status === 'done' ? 'Show results' : 'Show folder' }}
      </button>
      <button
        v-if="active"
        type="button"
        class="btn btn-secondary btn-sm"
        data-testid="scan-task-cancel"
        @click="emit('cancel')"
      >
        <PhStop :size="14" />
        Cancel scan
      </button>
      <button
        v-else
        type="button"
        class="scan-task-dismiss"
        title="Dismiss"
        aria-label="Dismiss"
        data-testid="scan-task-dismiss"
        @click="emit('dismiss')"
      >
        <PhX :size="14" />
      </button>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { PhCheckCircle, PhProhibit, PhSpinner, PhStop, PhWarning, PhX } from '@phosphor-icons/vue'
import type { LibraryImportScanTask } from '@/stores/libraryImport'

const props = defineProps<{
  task: LibraryImportScanTask
  folderName: string
  // The page shows the task's folder
  shown: boolean
}>()

const emit = defineEmits<{
  (e: 'cancel'): void
  (e: 'dismiss'): void
  (e: 'show'): void
}>()

const active = computed(() => props.task.status === 'starting' || props.task.status === 'running')

const icon = computed(() => {
  if (active.value) return PhSpinner
  if (props.task.status === 'done') return PhCheckCircle
  if (props.task.status === 'failed') return PhWarning
  return PhProhibit
})

function clock(isoString: string): string {
  return new Date(isoString).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
}

const timeLabel = computed(() =>
  active.value || !props.task.finishedAt
    ? `started at ${clock(props.task.startedAt)}`
    : `at ${clock(props.task.finishedAt)}`,
)
</script>

<style scoped>
.scan-task {
  display: flex;
  align-items: flex-start;
  gap: 0.75rem;
  margin-top: 1rem;
  padding: 0.75rem 1rem;
  border: 1px solid #333;
  border-radius: 6px;
  background: rgba(255, 255, 255, 0.03);
}

.scan-task--running,
.scan-task--starting {
  border-color: rgba(99, 102, 241, 0.45);
}

.scan-task--done {
  border-color: rgba(34, 197, 94, 0.4);
}

.scan-task--failed {
  border-color: rgba(239, 68, 68, 0.45);
}

.scan-task-icon {
  flex-shrink: 0;
  margin-top: 0.1rem;
  color: #9aa3b2;
}

.scan-task--done .scan-task-icon {
  color: #22c55e;
}

.scan-task--failed .scan-task-icon {
  color: #ef4444;
}

.scan-task-text {
  flex: 1;
  min-width: 0;
}

.scan-task-text p {
  margin: 0;
}

.scan-task-title {
  color: #e0e0e0;
  font-size: 0.875rem;
}

.scan-task-time {
  margin-left: 0.4rem;
  color: #777;
  font-size: 0.8rem;
}

.scan-task-help {
  margin-top: 0.25rem !important;
  color: #888;
  font-size: 0.8rem;
}

.scan-task-error {
  margin-top: 0.25rem !important;
  color: #ef4444;
  font-size: 0.8rem;
}

.scan-task-actions {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  flex-shrink: 0;
}

.scan-task-dismiss {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 1.75rem;
  height: 1.75rem;
  padding: 0;
  border: 0;
  border-radius: 4px;
  background: transparent;
  color: #888;
  cursor: pointer;
}

.scan-task-dismiss:hover {
  background: rgba(255, 255, 255, 0.08);
  color: #e0e0e0;
}

@media (max-width: 720px) {
  .scan-task {
    flex-wrap: wrap;
  }

  .scan-task-actions {
    width: 100%;
    justify-content: flex-end;
  }
}
</style>
