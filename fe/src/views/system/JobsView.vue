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
  <!-- The session's background jobs (Library Import scans, moves, folder scans, deletes), laid out
       like Activity -->
  <div class="jobs-view">
    <div class="page-header">
      <h1><PhGear /> Jobs</h1>
      <div class="jobs-actions">
        <div class="filter-input-wrapper">
          <PhMagnifyingGlass class="filter-icon" />
          <input
            v-model="filterText"
            type="text"
            class="filter-input"
            placeholder="Filter jobs..."
            aria-label="Filter jobs"
          />
          <button
            v-if="filterText"
            class="filter-clear"
            title="Clear filter"
            aria-label="Clear filter"
            @click="filterText = ''"
          >
            <PhX />
          </button>
        </div>
        <button
          class="btn btn-secondary"
          data-testid="jobs-clear"
          :disabled="!hasFinishedJobs"
          @click="jobsStore.clearFinished()"
        >
          <PhBroom /> Clear finished
        </button>
      </div>
    </div>

    <div v-if="filteredJobs.length > 0" class="jobs-grid-container">
      <div class="jobs-header">
        <div class="col-action">Action</div>
        <div class="col-title">Job</div>
        <div class="col-details">Details</div>
        <div class="col-progress">Progress</div>
        <div class="col-started">Started</div>
        <div class="col-finished">Finished</div>
        <div class="col-duration">Duration</div>
        <div class="col-status">Status</div>
        <div class="col-actions"></div>
      </div>
      <div
        v-for="job in filteredJobs"
        :key="job.id"
        class="jobs-row"
        :class="[`jobs-row--${job.status}`, { 'jobs-row--stale': job.stale }]"
        data-testid="job-row"
      >
        <div class="col-action">
          <!-- The title is on a wrapper: a disabled button shows no tooltip in every browser -->
          <span
            v-if="job.action"
            class="job-action-wrap"
            :title="job.action.disabledReason ?? job.action.label"
          >
            <button
              type="button"
              class="btn btn-secondary btn-sm job-action-btn"
              data-testid="job-action"
              :disabled="!job.action.to"
              @click="job.action.to && router.push(job.action.to)"
            >
              <PhArrowSquareOut v-if="job.action.label === 'See job results'" />
              <PhEye v-else />
              <span>{{ job.action.label }}</span>
            </button>
          </span>
        </div>
        <div class="col-title">
          <div class="title-cell">
            <RouterLink v-if="job.to" :to="job.to" class="title-link" :title="job.title">{{
              job.title
            }}</RouterLink>
            <span v-else class="title-text" :title="job.title">{{ job.title }}</span>
          </div>
        </div>
        <div class="col-details" :title="job.message">{{ job.message }}</div>
        <div class="col-progress">
          <div v-if="job.active" class="progress-cell">
            <ProgressBar
              v-if="job.progress != null"
              :value="job.progress"
              variant="activity"
              height="small"
              :show-size="false"
              :animating="true"
            />
            <PhSpinner v-else class="ph-spin job-spinner" />
          </div>
          <span v-else class="muted">-</span>
        </div>
        <div class="col-started">
          <span v-if="job.startedAt">{{ formatTime(job.startedAt) }}</span>
          <span v-else class="muted">-</span>
        </div>
        <div class="col-finished">
          <span v-if="job.finishedAt">{{ formatTime(job.finishedAt) }}</span>
          <span v-else class="muted">-</span>
        </div>
        <div class="col-duration">
          <span v-if="duration(job) !== null">{{ duration(job) }}</span>
          <span v-else class="muted">-</span>
        </div>
        <div class="col-status">
          <span :class="['status-badge', job.status]">{{ statusLabel(job.status) }}</span>
        </div>
        <div class="col-actions">
          <button
            v-if="job.cancelable"
            class="btn-icon btn-danger-icon"
            title="Cancel"
            aria-label="Cancel"
            data-testid="job-cancel"
            @click="jobsStore.cancel(job.id)"
          >
            <PhStop />
          </button>
          <button
            v-else-if="!job.active"
            class="btn-icon btn-danger-icon"
            title="Dismiss"
            aria-label="Dismiss"
            data-testid="job-dismiss"
            @click="jobsStore.dismiss(job.id)"
          >
            <PhX />
          </button>
        </div>
      </div>
    </div>

    <EmptyState
      v-else
      :title="filterText ? 'No matching jobs' : 'No jobs'"
      :message="
        filterText
          ? 'No job matches this filter.'
          : 'Scans, moves and deletes started in this session show here while they run and after they end.'
      "
    >
      <template #icon><PhGear :size="48" /></template>
    </EmptyState>
  </div>
</template>

<script setup lang="ts">
import { computed, onUnmounted, ref, watch } from 'vue'
import { RouterLink, useRouter } from 'vue-router'
import {
  PhArrowSquareOut,
  PhBroom,
  PhEye,
  PhGear,
  PhMagnifyingGlass,
  PhSpinner,
  PhStop,
  PhX,
} from '@phosphor-icons/vue'
import { EmptyState, ProgressBar } from '@/components/base'
import { useJobsStore, type JobEntry, type JobStatus } from '@/stores/jobs'

const jobsStore = useJobsStore()
const router = useRouter()
const filterText = ref('')

// The clock running jobs' durations count against: ticks every second while a job runs
const now = ref(Date.now())
let clock: ReturnType<typeof setInterval> | null = null
watch(
  () => jobsStore.jobs.some((job) => job.active),
  (anyActive) => {
    if (anyActive && !clock) {
      now.value = Date.now()
      clock = setInterval(() => (now.value = Date.now()), 1000)
    } else if (!anyActive && clock) {
      clearInterval(clock)
      clock = null
    }
  },
  { immediate: true },
)
onUnmounted(() => {
  if (clock) clearInterval(clock)
})

// How long the job took, or has been running: "45s", "2m 05s", "1h 02m"
function duration(job: JobEntry): string | null {
  if (!job.startedAt) return null
  const start = new Date(job.startedAt).getTime()
  const end = job.finishedAt ? new Date(job.finishedAt).getTime() : job.active ? now.value : NaN
  if (Number.isNaN(start) || Number.isNaN(end)) return null
  const seconds = Math.max(0, Math.round((end - start) / 1000))
  if (seconds < 60) return `${seconds}s`
  const minutes = Math.floor(seconds / 60)
  if (minutes < 60) return `${minutes}m ${String(seconds % 60).padStart(2, '0')}s`
  return `${Math.floor(minutes / 60)}h ${String(minutes % 60).padStart(2, '0')}m`
}

const filteredJobs = computed(() => {
  const needle = filterText.value.trim().toLowerCase()
  if (!needle) return jobsStore.jobs
  return jobsStore.jobs.filter(
    (job) => job.title.toLowerCase().includes(needle) || job.message.toLowerCase().includes(needle),
  )
})

const hasFinishedJobs = computed(() => jobsStore.jobs.some((job) => !job.active))

const STATUS_LABELS: Record<JobStatus, string> = {
  queued: 'Queued',
  running: 'Running',
  done: 'Done',
  failed: 'Failed',
  canceled: 'Canceled',
}

function statusLabel(status: JobStatus): string {
  return STATUS_LABELS[status]
}

function formatTime(timestamp: string): string {
  const date = new Date(timestamp)
  if (Number.isNaN(date.getTime())) return timestamp
  const sameDay = date.toDateString() === new Date().toDateString()
  return sameDay
    ? date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
    : date.toLocaleString([], {
        month: 'short',
        day: 'numeric',
        hour: '2-digit',
        minute: '2-digit',
      })
}
</script>

<style scoped>
.jobs-view {
  padding: 1em;
}

.page-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 0.75rem;
  flex-wrap: wrap;
  gap: 0.75rem;
}

.page-header h1 {
  margin: 0;
  color: white;
  font-size: 2rem;
  display: flex;
  align-items: center;
  gap: 0.5rem;
  font-weight: 500;
}

.page-header h1 svg {
  width: 32px;
  height: 32px;
}

.jobs-actions {
  display: flex;
  gap: 0.75rem;
  align-items: center;
}

.filter-input-wrapper {
  position: relative;
  display: flex;
  align-items: center;
}

.filter-icon {
  position: absolute;
  left: 0.75rem;
  color: #868e96;
  width: 16px;
  height: 16px;
  pointer-events: none;
}

.filter-input {
  background: #2a2a2a;
  border: 1px solid rgba(255, 255, 255, 0.1);
  border-radius: 6px;
  color: #fff;
  padding: 0.5rem 2rem 0.5rem 2.25rem;
  font-size: 0.875rem;
  width: 220px;
  height: var(--control-height, 40px);
  box-sizing: border-box;
  transition:
    border-color 0.2s,
    box-shadow 0.2s;
}

.filter-input::placeholder {
  color: #868e96;
}

.filter-input:focus {
  outline: none;
  border-color: #4dabf7;
  box-shadow: 0 0 0 2px rgba(77, 171, 247, 0.15);
}

.filter-clear {
  position: absolute;
  right: 0.5rem;
  background: none;
  border: none;
  color: #868e96;
  cursor: pointer;
  padding: 0.25rem;
  border-radius: 4px;
  display: flex;
  align-items: center;
  justify-content: center;
  transition: color 0.2s;
}

.filter-clear:hover {
  color: #fa5252;
  background: rgba(250, 82, 82, 0.15);
}

.filter-clear svg {
  width: 14px;
  height: 14px;
}

.jobs-grid-container {
  border: 1px solid rgba(255, 255, 255, 0.06);
  border-radius: 6px;
  background: #1e1e1e;
}

.jobs-header,
.jobs-row {
  display: grid;
  /* The Action column is as wide as its button, the same for every row */
  grid-template-columns:
    11.5rem minmax(0, 3fr) minmax(0, 2fr) minmax(0, 1.5fr) minmax(0, 1fr)
    minmax(0, 1fr) minmax(0, 0.8fr) minmax(0, 1fr) 40px;
  align-items: center;
}

.jobs-header {
  position: sticky;
  top: 0;
  z-index: 2;
  background: #252525;
  padding: 0.5rem 0;
  border-bottom: 1px solid rgba(255, 255, 255, 0.08);
}

.jobs-header > div {
  padding: 0 0.75rem;
  font-size: 0.75rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.5px;
  color: #868e96;
  white-space: nowrap;
}

.jobs-row {
  transition: background-color 0.15s;
}

.jobs-row:hover {
  background-color: rgba(255, 255, 255, 0.03);
}

.jobs-row > div {
  padding: 0.5rem 0.75rem;
  font-size: 0.85rem;
  color: #adb5bd;
  border-bottom: 1px solid rgba(255, 255, 255, 0.04);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  display: flex;
  align-items: center;
  align-self: stretch;
}

.jobs-row > .col-details {
  display: block;
  line-height: 1.9;
}

.jobs-row > .col-progress {
  overflow: visible;
}

.jobs-row > .col-actions {
  justify-content: center;
  overflow: visible;
}

.job-action-wrap {
  display: inline-flex;
}

/* One width for every button, and no taller than the row's text */
.job-action-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 0.35rem;
  width: 10.5rem;
  padding: 0.15rem 0.5rem;
  font-size: 0.75rem;
  line-height: 1.2;
  white-space: nowrap;
}

.job-action-btn svg {
  width: 14px;
  height: 14px;
  flex-shrink: 0;
}

.job-action-btn:disabled {
  opacity: 0.45;
  cursor: not-allowed;
  pointer-events: none;
}

.jobs-row--stale .col-title,
.jobs-row--stale .col-details {
  opacity: 0.6;
}

.title-cell {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  min-width: 0;
}

.title-text,
.title-link {
  color: white;
  font-weight: 500;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.title-link {
  text-decoration: none;
}

.title-link:hover {
  color: #4dabf7;
}

.progress-cell {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  width: 100%;
  min-width: 0;
}

.progress-cell :deep(.progress-wrapper) {
  flex: 1;
  min-width: 0;
}

.job-spinner {
  width: 18px;
  height: 18px;
  color: #4dabf7;
}

.muted {
  color: #495057;
  font-size: 0.8rem;
}

.status-badge {
  padding: 0.2rem 0.5rem;
  border-radius: 4px;
  font-size: 0.7rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.3px;
}

.status-badge.queued {
  background-color: rgba(134, 142, 150, 0.15);
  color: #868e96;
}

.status-badge.running {
  background-color: rgba(77, 171, 247, 0.15);
  color: #4dabf7;
}

.status-badge.done {
  background-color: rgba(81, 207, 102, 0.15);
  color: #51cf66;
}

.status-badge.failed {
  background-color: rgba(250, 82, 82, 0.15);
  color: #fa5252;
}

.status-badge.canceled {
  background-color: rgba(255, 212, 59, 0.15);
  color: #ffd43b;
}

.btn-danger-icon {
  background: none;
  border: none;
  color: #868e96;
  cursor: pointer;
  padding: 0.35rem;
  border-radius: 4px;
  display: flex;
  align-items: center;
  justify-content: center;
  transition: all 0.2s;
}

.btn-danger-icon:hover {
  background: rgba(250, 82, 82, 0.15);
  color: #fa5252;
}

.btn-danger-icon svg {
  width: 16px;
  height: 16px;
}

@media (max-width: 768px) {
  .page-header {
    flex-direction: column;
    align-items: flex-start;
  }

  .jobs-actions {
    width: 100%;
    flex-wrap: wrap;
  }

  .filter-input-wrapper {
    flex: 1;
    min-width: 0;
  }

  .filter-input {
    width: 100%;
  }

  .jobs-grid-container {
    border: none;
    background: transparent;
  }

  .jobs-header {
    display: none;
  }

  /* Each row becomes a card: title and status, details, progress and actions */
  .jobs-row {
    grid-template-columns: 1fr auto;
    gap: 0.3rem 0.5rem;
    padding: 0.75rem;
    margin-bottom: 0.5rem;
    background: #2a2a2a;
    border-radius: 6px;
    border: 1px solid rgba(255, 255, 255, 0.06);
  }

  .jobs-row > div {
    padding: 0;
    border: none;
    white-space: normal;
  }

  .jobs-row .col-title {
    grid-column: 1;
    grid-row: 1;
    min-width: 0;
  }

  .jobs-row .col-status {
    grid-column: 2;
    grid-row: 1;
    justify-content: flex-end;
  }

  .jobs-row .col-details {
    grid-column: 1 / -1;
    grid-row: 2;
  }

  .jobs-row .col-progress {
    grid-column: 1;
    grid-row: 3;
  }

  .jobs-row .col-started,
  .jobs-row .col-finished {
    display: none;
  }

  .jobs-row .col-action {
    grid-column: 1 / -1;
    grid-row: 4;
  }

  .jobs-row .col-duration {
    grid-column: 1;
    grid-row: 5;
  }

  .jobs-row .col-action:empty {
    display: none;
  }

  .jobs-row .col-actions {
    grid-column: 2;
    grid-row: 3;
    justify-content: flex-end;
  }
}
</style>
