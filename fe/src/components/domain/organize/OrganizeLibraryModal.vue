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
  <transition name="modal-fade">
    <div v-if="visible" class="modal-overlay" @click.self="onClose">
      <div
        class="modal organize-modal"
        role="dialog"
        aria-modal="true"
        aria-labelledby="org-modal-title"
      >
        <header class="modal-header">
          <h2 id="org-modal-title">Organize library folders</h2>
          <button class="modal-close" :disabled="applying" @click="onClose" aria-label="Close">
            <PhX />
          </button>
        </header>

        <div class="modal-body">
          <!-- Always-visible move-queue status banner: polls /library/move/summary
               on a 5s tick while open. Useful both BEFORE Apply (shows leftover
               state from a previous run) and AFTER (live progress). Renders
               nothing when the queue has never had any jobs. -->
          <MoveQueueStatusBanner v-if="visible" />

          <div v-if="loading" class="state-msg">Computing canonical paths for your library…</div>

          <div v-else-if="loadError" class="state-msg error">
            Failed to load preview: {{ loadError }}
          </div>

          <template v-else-if="preview">
            <p class="help-text">
              Each audiobook below has been compared against its canonical
              <code>{Author}/{Title}</code> path under the configured
              <strong>Folder Naming Pattern</strong>. Confirm the selection then click Apply to
              queue per-book moves. <strong>Collision</strong> and
              <strong>Invalid target</strong> rows are surfaced for your awareness — they must be
              resolved by hand (run the duplicates tool, or fix missing metadata) before they can be
              organized.
            </p>

            <div class="bucket-summary">
              <span class="bucket pill pill-action">{{ preview.willMoveCount }} will move</span>
              <span class="bucket pill pill-ok"
                >{{ preview.alreadyCanonicalCount }} already canonical</span
              >
              <span class="bucket pill pill-warn"
                >{{ preview.collisionCount }} collision{{
                  preview.collisionCount === 1 ? '' : 's'
                }}</span
              >
              <span class="bucket pill pill-warn"
                >{{ preview.invalidTargetCount }} invalid target{{
                  preview.invalidTargetCount === 1 ? '' : 's'
                }}</span
              >
            </div>

            <section v-if="willMoveRows.length > 0" class="section">
              <header class="section-header">
                <h3>Will move ({{ willMoveRows.length }})</h3>
                <div class="section-tools">
                  <button type="button" class="link" @click="toggleAll(true)">Select all</button>
                  <button type="button" class="link" @click="toggleAll(false)">Select none</button>
                  <span class="section-count"
                    >{{ selectedCount }} selected · {{ formatBytes(selectedBytes) }}</span
                  >
                </div>
              </header>
              <div class="rows">
                <label v-for="row in willMoveRows" :key="row.id" class="row">
                  <input
                    type="checkbox"
                    :checked="selected[row.id] === true"
                    :disabled="applying"
                    @change="onToggleRow(row.id, ($event.target as HTMLInputElement).checked)"
                  />
                  <div class="row-body">
                    <div class="row-title">
                      <strong>{{ row.title || '(no title)' }}</strong>
                      <span class="row-author">{{ row.author || 'Unknown Author' }}</span>
                    </div>
                    <div class="row-paths">
                      <div class="row-path" :title="row.currentPath || ''">
                        <span class="path-label">from</span>
                        <span class="path-value">{{ row.currentPath || '(empty)' }}</span>
                      </div>
                      <div class="row-path" :title="row.targetPath || ''">
                        <span class="path-label">to</span>
                        <span class="path-value path-target">{{ row.targetPath }}</span>
                      </div>
                    </div>
                    <div class="row-meta">
                      {{ row.fileCount }} file{{ row.fileCount === 1 ? '' : 's' }} ·
                      {{ formatBytes(row.totalSize) }}
                      <span
                        v-if="row.replacesStubTarget"
                        class="stub-replace-tag"
                        title="The target folder exists but only holds leftover metadata (covers, .opf, playlists) that nothing references. The move will replace it."
                        >replaces leftover metadata</span
                      >
                    </div>
                  </div>
                </label>
              </div>
            </section>

            <section v-if="collisionGroups.length > 0" class="section">
              <header class="section-header">
                <h3>Collisions ({{ preview.collisionCount }})</h3>
                <p class="section-help">
                  These audiobooks compute to the same canonical folder. Organizing them would
                  clobber each other on disk. Resolve via the duplicates tool (same ASIN) or by
                  fixing the metadata difference (different ASIN but identical
                  <code>{Author}/{Title}</code>).
                </p>
              </header>
              <div v-for="group in collisionGroups" :key="group.key" class="collision-group">
                <div class="collision-target">→ {{ group.targetPath }}</div>
                <ul class="collision-members">
                  <li v-for="r in group.rows" :key="r.id">
                    <strong>id {{ r.id }}</strong
                    >: {{ r.title || '(no title)' }} —
                    <em>{{ r.author || 'Unknown Author' }}</em>
                    <div class="row-path">
                      <span class="path-label">from</span>
                      <span class="path-value">{{ r.currentPath || '(empty)' }}</span>
                    </div>
                  </li>
                </ul>
              </div>
            </section>

            <section v-if="invalidRows.length > 0" class="section">
              <header class="section-header">
                <h3>Invalid target ({{ invalidRows.length }})</h3>
                <p class="section-help">
                  These rows can't be safely organized as-is. They're grouped by problem below, each
                  with how to resolve it. Open any record in a new tab to fix it, then re-run the
                  preview.
                </p>
              </header>
              <div v-for="group in invalidGroups" :key="group.code" class="invalid-group">
                <div class="invalid-group-head">
                  <span class="invalid-group-title">{{ group.title }}</span>
                  <span class="invalid-group-count">{{ group.rows.length }}</span>
                </div>
                <p v-if="group.help" class="invalid-group-help">{{ group.help }}</p>
                <ul class="invalid-list">
                  <li v-for="row in group.rows" :key="row.id" class="invalid-row">
                    <div class="invalid-row-head">
                      <strong>{{ row.title || '(no title)' }}</strong>
                      <span class="row-author">{{ row.author || 'Unknown Author' }}</span>
                      <a
                        class="row-link"
                        :href="`/audiobooks/${row.id}`"
                        target="_blank"
                        rel="noopener"
                        title="Open audiobook detail in a new tab"
                        @click.stop
                        >id {{ row.id }} ↗</a
                      >
                    </div>
                    <div class="invalid-row-paths">
                      <div class="row-path" :title="row.currentPath || ''">
                        <span class="path-label">from</span>
                        <span class="path-value">{{ row.currentPath || '(empty)' }}</span>
                      </div>
                      <div v-if="row.targetPath" class="row-path" :title="row.targetPath">
                        <span class="path-label">to</span>
                        <span class="path-value path-target">{{ row.targetPath }}</span>
                      </div>
                    </div>
                    <div v-if="!group.help" class="invalid-reason">{{ row.reason }}</div>
                    <div
                      v-if="group.code === 'target_ancestor' && !row.canFlatten"
                      class="invalid-row-note"
                    >
                      Automatic flatten isn't available here — the canonical folder already holds
                      other files. Move this book's files up a level by hand, then re-run the
                      preview.
                    </div>
                    <div
                      v-if="group.code === 'target_ancestor' && row.canFlatten"
                      class="invalid-row-action"
                    >
                      <template v-if="flattenConfirmId !== row.id">
                        <button
                          type="button"
                          class="link"
                          :disabled="flatteningId !== null"
                          @click="flattenConfirmId = row.id"
                        >
                          Flatten into canonical folder
                        </button>
                      </template>
                      <template v-else>
                        <span class="flatten-confirm-text">
                          Move {{ row.fileCount }} file{{ row.fileCount === 1 ? '' : 's' }} up into
                          the canonical folder and remove the empty subfolder?
                        </span>
                        <button
                          type="button"
                          class="link flatten-go"
                          :disabled="flatteningId !== null"
                          @click="flattenRow(row)"
                        >
                          {{ flatteningId === row.id ? 'Flattening…' : 'Confirm' }}
                        </button>
                        <button
                          type="button"
                          class="link flatten-cancel"
                          :disabled="flatteningId !== null"
                          @click="flattenConfirmId = null"
                        >
                          Cancel
                        </button>
                      </template>
                    </div>
                  </li>
                </ul>
              </div>
            </section>

            <div
              v-if="
                willMoveRows.length === 0 &&
                collisionGroups.length === 0 &&
                invalidRows.length === 0
              "
              class="state-msg"
            >
              Nothing to organize — every audiobook is already at its canonical folder.
            </div>
          </template>
        </div>

        <footer v-if="!results" class="modal-footer">
          <template v-if="!pendingConfirm">
            <div class="footer-summary">
              <span v-if="!loading && preview">
                {{ selectedCount }} of {{ willMoveRows.length }} ready to move ·
                {{ formatBytes(selectedBytes) }}
              </span>
              <span v-if="applyError" class="error">{{ applyError }}</span>
            </div>
            <div class="footer-actions">
              <button type="button" class="btn" :disabled="applying" @click="onClose">Close</button>
              <button
                type="button"
                class="btn btn-primary"
                :disabled="applying || loading || selectedCount === 0"
                @click="pendingConfirm = true"
              >
                Apply…
              </button>
            </div>
          </template>

          <template v-else-if="results">
            <div class="results-panel">
              <div class="results-title">Move queue status</div>
              <div class="results-tally">
                <span class="pill pill-action">{{ runningCount }} in flight</span>
                <span class="pill pill-ok">{{ completedCount }} completed</span>
                <span class="pill pill-err">{{ failedCount }} failed</span>
                <span class="results-meta"
                  >{{ queuedCount }} queued · {{ skippedCount }} skipped ·
                  {{ failedToQueueCount }} failed to queue</span
                >
              </div>
              <ul v-if="failedJobs.length > 0" class="results-failures">
                <li v-for="job in failedJobs" :key="job.jobId">
                  <strong>{{
                    jobTitleFor(job.jobId) || `id ${jobAudiobookFor(job.jobId)}`
                  }}</strong>
                  <span class="results-error">{{
                    jobErrorFor(job.jobId) || '(no error text)'
                  }}</span>
                </li>
              </ul>
              <ul v-if="skippedDetails.length > 0" class="results-failures">
                <li v-for="(s, i) in skippedDetails" :key="`skip-${i}`">
                  <strong>id {{ s.audiobookId }}</strong>
                  <span class="results-error">{{ s.reason }}</span>
                </li>
              </ul>
              <div class="confirm-actions">
                <button type="button" class="btn" @click="onClose">Close</button>
              </div>
            </div>
          </template>

          <template v-else>
            <div class="confirm-panel">
              <div class="confirm-title">
                About to queue {{ selectedCount }} folder move{{ selectedCount === 1 ? '' : 's' }}:
              </div>
              <ul class="confirm-list">
                <li>
                  <strong>{{ selectedCount }}</strong> audiobook{{
                    selectedCount === 1 ? '' : 's'
                  }}
                  totaling {{ formatBytes(selectedBytes) }} will be moved to
                  <code>{Author}/{Title}</code>.
                </li>
                <li>
                  Each move runs through the existing background queue with the same safety checks
                  as the per-book Organize button.
                </li>
              </ul>
              <div class="confirm-warning">
                File moves are queued but execute on disk. If your FolderNamingPattern is
                misconfigured, books will land in the wrong place. Back up the SQLite DB before
                running a real sweep on a live library.
              </div>
              <div class="confirm-actions">
                <button
                  type="button"
                  class="btn"
                  :disabled="applying"
                  @click="pendingConfirm = false"
                >
                  Back
                </button>
                <button
                  type="button"
                  class="btn btn-primary"
                  :disabled="applying"
                  @click="executeApply"
                >
                  {{ applying ? 'Queuing…' : 'Confirm and queue moves' }}
                </button>
              </div>
              <div v-if="applyError" class="error confirm-error">{{ applyError }}</div>
            </div>
          </template>
        </footer>
      </div>
    </div>
  </transition>
</template>

<script setup lang="ts">
import { ref, computed, watch, reactive } from 'vue'
import { PhX } from '@phosphor-icons/vue'
import { apiService } from '@/services/api'
import { useToast } from '@/services/toastService'
import { signalRService } from '@/services/signalr'
import { errorTracking } from '@/services/errorTracking'
import MoveQueueStatusBanner from '@/components/domain/organize/MoveQueueStatusBanner.vue'
import type {
  OrganizeLibraryPreview,
  OrganizeLibraryApplyResult,
  OrganizePreviewRow,
} from '@/types'

interface JobState {
  audiobookId: number
  audiobookTitle: string | null
  targetPath: string | null
  status: string
  error: string | null
}

const props = defineProps<{ visible: boolean }>()
const emit = defineEmits<{
  (e: 'close'): void
  (e: 'organized', result: OrganizeLibraryApplyResult): void
}>()

const toast = useToast()

const loading = ref(false)
const loadError = ref<string | null>(null)
const preview = ref<OrganizeLibraryPreview | null>(null)
const selected = reactive<Record<number, boolean>>({})
const applying = ref(false)
const applyError = ref<string | null>(null)
const pendingConfirm = ref(false)
const results = ref<OrganizeLibraryApplyResult | null>(null)
const jobs = reactive<Record<string, JobState>>({})
let unsubMoveJob: (() => void) | null = null

const queuedCount = computed(() => results.value?.queued ?? 0)
const skippedCount = computed(() => results.value?.skipped ?? 0)
const failedToQueueCount = computed(() => results.value?.failedToQueue ?? 0)
const skippedDetails = computed(() => results.value?.skippedDetails ?? [])

const failedJobs = computed(() => {
  if (!results.value) return []
  return results.value.queuedJobs.filter((j) => jobs[j.jobId]?.status === 'Failed')
})
const completedCount = computed(() => {
  if (!results.value) return 0
  return results.value.queuedJobs.filter((j) => jobs[j.jobId]?.status === 'Completed').length
})
const failedCount = computed(() => failedJobs.value.length)
const runningCount = computed(() => {
  if (!results.value) return 0
  return results.value.queuedJobs.length - completedCount.value - failedCount.value
})

function jobTitleFor(jobId: string): string | null {
  return jobs[jobId]?.audiobookTitle || null
}
function jobAudiobookFor(jobId: string): number | null {
  return jobs[jobId]?.audiobookId ?? null
}
function jobErrorFor(jobId: string): string | null {
  return jobs[jobId]?.error || null
}

const willMoveRows = computed(
  () => preview.value?.rows.filter((r) => r.status === 'will_move') ?? [],
)
const invalidRows = computed(
  () => preview.value?.rows.filter((r) => r.status === 'invalid_target') ?? [],
)
const collisionRows = computed(
  () => preview.value?.rows.filter((r) => r.status === 'collision') ?? [],
)

interface CollisionGroup {
  key: string
  targetPath: string
  rows: OrganizePreviewRow[]
}

const collisionGroups = computed<CollisionGroup[]>(() => {
  const byKey = new Map<string, CollisionGroup>()
  for (const r of collisionRows.value) {
    const key = r.collisionKey || r.targetPath || `id-${r.id}`
    const existing = byKey.get(key)
    if (existing) {
      existing.rows.push(r)
    } else {
      byKey.set(key, { key, targetPath: r.targetPath || '(unknown)', rows: [r] })
    }
  }
  return Array.from(byKey.values()).sort((a, b) => a.targetPath.localeCompare(b.targetPath))
})

interface InvalidGroup {
  code: string
  title: string
  help: string
  rows: OrganizePreviewRow[]
}

// Human copy + resolution guidance keyed by the backend's machine-readable
// reasonCode (OrganizeInvalidReasonCode). Order here is also the display
// order of the groups. Keep prose here so the backend's `reason` string can
// be reworded without touching grouping/styling. Codes not listed fall into
// an "Other" bucket that shows each row's raw `reason` verbatim.
const INVALID_REASON_META: Record<string, { title: string; help: string }> = {
  missing_author: {
    title: 'Missing author',
    help: 'No author metadata, so no canonical folder can be built. Open the record, set an author, then re-run the preview.',
  },
  missing_title: {
    title: 'Missing title',
    help: 'No title metadata, so no canonical folder can be built. Open the record, set a title, then re-run the preview.',
  },
  source_at_root: {
    title: 'Folder is the library root',
    help: "The record's path points at a library root itself rather than the book's own subfolder. Re-scan the library to correct BasePath before organizing.",
  },
  target_ancestor: {
    title: 'Nested one level too deep',
    help: 'The files sit in an extra subfolder beneath their canonical folder (a historical import artifact). Where the canonical folder is otherwise empty, use "Flatten" to collapse it in one click; where it already holds other files, the button is hidden — move those up a level on disk by hand first.',
  },
  source_missing: {
    title: 'Files missing on disk',
    help: "The record's folder no longer exists on disk — its files are gone (a stale record left by an earlier move or deletion). Re-scan the library to clear it, or open the record and remove it.",
  },
  target_exists: {
    title: 'Canonical folder already occupied',
    help: 'Another folder already exists at the canonical path (shown under "to") and contains files — usually a leftover duplicate copy from an earlier import. Check the Duplicates tool to see if it is a tracked duplicate you can resolve there; otherwise remove or merge the stale folder on disk, then re-run the preview.',
  },
  outside_root: {
    title: 'Outside library roots',
    help: 'This book lives outside every configured library root. Add the root (or move the folder under one) and re-scan, then re-run the preview.',
  },
  pattern_not_configured: {
    title: 'Naming pattern not configured',
    help: 'No Folder Naming Pattern is set. Configure one under Settings → Media Management, then re-run the preview.',
  },
  empty_pattern: {
    title: 'Pattern produced an empty path',
    help: "The naming pattern rendered to an empty path for this book's metadata. Check the pattern and the record's fields.",
  },
}

const invalidGroups = computed<InvalidGroup[]>(() => {
  const order = Object.keys(INVALID_REASON_META)
  const byCode = new Map<string, InvalidGroup>()
  for (const r of invalidRows.value) {
    const code = r.reasonCode || 'other'
    let group = byCode.get(code)
    if (!group) {
      const meta = INVALID_REASON_META[code]
      group = { code, title: meta?.title || 'Other', help: meta?.help || '', rows: [] }
      byCode.set(code, group)
    }
    group.rows.push(r)
  }
  return Array.from(byCode.values()).sort((a, b) => {
    const ia = order.indexOf(a.code)
    const ib = order.indexOf(b.code)
    return (ia < 0 ? 999 : ia) - (ib < 0 ? 999 : ib)
  })
})

// Per-row "flatten" action for the nested-one-level-too-deep group. Tracks
// which row is awaiting confirmation and which is mid-request so the buttons
// can disable/spinner without a heavier state machine.
const flattenConfirmId = ref<number | null>(null)
const flatteningId = ref<number | null>(null)

async function flattenRow(row: OrganizePreviewRow) {
  flatteningId.value = row.id
  try {
    const res = await apiService.flattenOrganizeRow(row.id)
    if (res.success) {
      const label = row.title || `id ${row.id}`
      const suffix = res.error ? ` — ${res.error}` : ''
      toast.success(
        'Organize library',
        `Flattened "${label}" (${res.filesMoved} file${res.filesMoved === 1 ? '' : 's'})${suffix}`,
      )
      await load()
    } else {
      toast.error('Flatten failed', res.error || 'Unknown error')
    }
  } catch (err) {
    toast.error('Flatten failed', err instanceof Error ? err.message : 'Unknown error')
    errorTracking.captureException(err as Error, {
      component: 'OrganizeLibraryModal',
      operation: 'flatten',
    })
  } finally {
    flatteningId.value = null
    flattenConfirmId.value = null
  }
}

const selectedCount = computed(
  () => willMoveRows.value.filter((r) => selected[r.id] === true).length,
)
const selectedBytes = computed(() =>
  willMoveRows.value
    .filter((r) => selected[r.id] === true)
    .reduce((acc, r) => acc + (r.totalSize || 0), 0),
)

function onToggleRow(id: number, checked: boolean) {
  selected[id] = checked
}

function toggleAll(checked: boolean) {
  for (const r of willMoveRows.value) selected[r.id] = checked
}

async function load() {
  loading.value = true
  loadError.value = null
  applyError.value = null
  for (const k of Object.keys(selected)) delete selected[Number(k)]
  for (const k of Object.keys(jobs)) delete jobs[k]
  results.value = null
  unsubscribeMoveJobs()
  pendingConfirm.value = false
  try {
    const resp = await apiService.getOrganizeLibraryPreview()
    preview.value = resp
    // Default every will_move row to selected so the user starts from "move everything".
    for (const r of resp.rows) {
      if (r.status === 'will_move') selected[r.id] = true
    }
  } catch (err) {
    loadError.value = err instanceof Error ? err.message : 'Unknown error'
    errorTracking.captureException(err as Error, {
      component: 'OrganizeLibraryModal',
      operation: 'load',
    })
  } finally {
    loading.value = false
  }
}

async function executeApply() {
  applyError.value = null
  const ids = willMoveRows.value.filter((r) => selected[r.id] === true).map((r) => r.id)
  if (ids.length === 0) {
    applyError.value = 'Nothing selected to move.'
    return
  }
  applying.value = true
  try {
    const result = await apiService.applyOrganizeLibrary(ids)
    emit('organized', result)
    // Seed jobs state from the apply response so the results panel can
    // render immediately; SignalR MoveJobUpdate then fills in error text
    // as each background move finishes (Completed or Failed).
    for (const k of Object.keys(jobs)) delete jobs[k]
    for (const j of result.queuedJobs) {
      jobs[j.jobId] = {
        audiobookId: j.audiobookId,
        audiobookTitle: j.audiobookTitle,
        targetPath: j.targetPath,
        status: 'Queued',
        error: null,
      }
    }
    subscribeMoveJobs()
    results.value = result
    const parts: string[] = [`Queued ${result.queued} move${result.queued === 1 ? '' : 's'}`]
    if (result.skipped > 0) parts.push(`${result.skipped} skipped`)
    if (result.failedToQueue > 0) parts.push(`${result.failedToQueue} failed to queue`)
    const summary = parts.join(', ')
    if (result.failedToQueue > 0 || result.warnings.length > 0) {
      toast.warning('Organize library', summary)
    } else {
      toast.success('Organize library', summary)
    }
  } catch (err) {
    applyError.value = err instanceof Error ? err.message : 'Unknown error'
    errorTracking.captureException(err as Error, {
      component: 'OrganizeLibraryModal',
      operation: 'apply',
    })
  } finally {
    applying.value = false
  }
}

function subscribeMoveJobs() {
  if (unsubMoveJob) return
  unsubMoveJob = signalRService.onMoveJobUpdate((job) => {
    if (!job || !job.jobId) return
    const existing = jobs[job.jobId]
    if (!existing) return // not one of ours
    existing.status = job.status
    existing.error = job.error || null
  })
}

function unsubscribeMoveJobs() {
  if (unsubMoveJob) {
    try {
      unsubMoveJob()
    } catch {
      /* ignore */
    }
    unsubMoveJob = null
  }
}

function onClose() {
  if (applying.value) return
  unsubscribeMoveJobs()
  emit('close')
}

function formatBytes(bytes: number): string {
  if (!bytes || bytes <= 0) return '0 B'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let n = bytes
  let i = 0
  while (n >= 1024 && i < units.length - 1) {
    n /= 1024
    i++
  }
  return `${n.toFixed(n >= 100 || i === 0 ? 0 : 1)} ${units[i]}`
}

watch(
  () => props.visible,
  (v) => {
    if (v) {
      preview.value = null
      void load()
    }
  },
  { immediate: true },
)
</script>

<style scoped>
.modal-overlay {
  position: fixed;
  inset: 0;
  background: rgba(0, 0, 0, 0.55);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 3000;
  padding: 1rem;
}
.modal {
  background: #1a1a1a;
  border-radius: 8px;
  width: 100%;
  max-width: 1100px;
  max-height: 90vh;
  display: flex;
  flex-direction: column;
  border: 1px solid rgba(255, 255, 255, 0.08);
}
.modal-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 14px 16px;
  border-bottom: 1px solid rgba(255, 255, 255, 0.06);
}
.modal-header h2 {
  font-size: 16px;
  margin: 0;
  color: #fff;
}
.modal-close {
  background: transparent;
  border: 0;
  color: #aaa;
  cursor: pointer;
  font-size: 18px;
}
.modal-close:hover {
  color: #fff;
}
.modal-body {
  flex: 1;
  overflow-y: auto;
  padding: 14px 16px;
}
.modal-footer {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 12px 16px;
  border-top: 1px solid rgba(255, 255, 255, 0.06);
  gap: 12px;
  flex-wrap: wrap;
}
.footer-summary {
  font-size: 12px;
  color: #aaa;
}
.footer-summary .error {
  color: #f06060;
  display: block;
  margin-top: 4px;
}
.footer-actions {
  display: flex;
  gap: 8px;
}
.help-text {
  font-size: 12px;
  color: #bbb;
  margin: 0 0 14px;
  line-height: 1.5;
}
.help-text code {
  background: rgba(255, 255, 255, 0.06);
  padding: 1px 4px;
  border-radius: 3px;
  font-size: 11px;
}
.bucket-summary {
  display: flex;
  gap: 8px;
  margin-bottom: 16px;
  flex-wrap: wrap;
}
.pill {
  display: inline-flex;
  align-items: center;
  padding: 3px 9px;
  border-radius: 999px;
  font-size: 11px;
  border: 1px solid transparent;
}
.pill-action {
  background: rgba(42, 111, 208, 0.16);
  color: #6fa8e8;
  border-color: rgba(42, 111, 208, 0.25);
}
.pill-ok {
  background: rgba(96, 192, 128, 0.12);
  color: #80c896;
  border-color: rgba(96, 192, 128, 0.2);
}
.pill-warn {
  background: rgba(240, 176, 96, 0.12);
  color: #e8b070;
  border-color: rgba(176, 128, 64, 0.3);
}
.pill-err {
  background: rgba(240, 96, 96, 0.12);
  color: #f06060;
  border-color: rgba(176, 64, 64, 0.3);
}
.results-panel {
  width: 100%;
  display: flex;
  flex-direction: column;
  gap: 10px;
}
.results-title {
  font-size: 13px;
  color: #fff;
  font-weight: 600;
}
.results-tally {
  display: flex;
  gap: 8px;
  flex-wrap: wrap;
  align-items: center;
}
.results-meta {
  font-size: 11px;
  color: #999;
}
.results-failures {
  margin: 0;
  padding-left: 16px;
  color: #ddd;
  font-size: 12px;
  max-height: 220px;
  overflow-y: auto;
}
.results-failures li {
  margin-bottom: 6px;
  display: flex;
  flex-direction: column;
  gap: 2px;
}
.results-failures strong {
  color: #fff;
  font-size: 12px;
}
.results-error {
  font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
  font-size: 11px;
  color: #f06060;
  white-space: pre-wrap;
  word-break: break-word;
}
.section {
  margin-bottom: 18px;
  border: 1px solid rgba(255, 255, 255, 0.05);
  border-radius: 6px;
  background: rgba(255, 255, 255, 0.015);
}
.section-header {
  padding: 10px 14px;
  border-bottom: 1px solid rgba(255, 255, 255, 0.04);
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}
.section-header h3 {
  margin: 0;
  font-size: 13px;
  color: #fff;
}
.section-help {
  width: 100%;
  margin: 6px 0 0;
  font-size: 12px;
  color: #aaa;
  line-height: 1.45;
}
.section-tools {
  display: flex;
  align-items: center;
  gap: 10px;
  font-size: 12px;
  color: #999;
}
.section-count {
  color: #ccc;
}
.link {
  background: transparent;
  border: 0;
  color: #6fa8e8;
  font-size: 12px;
  cursor: pointer;
  padding: 0;
}
.link:hover {
  text-decoration: underline;
}
.rows {
  padding: 4px 0;
}
.row {
  display: flex;
  align-items: flex-start;
  gap: 10px;
  padding: 8px 14px;
  border-bottom: 1px solid rgba(255, 255, 255, 0.03);
  cursor: pointer;
}
.row:last-child {
  border-bottom: 0;
}
.row:hover {
  background: rgba(255, 255, 255, 0.02);
}
.row input[type='checkbox'] {
  margin-top: 4px;
  flex-shrink: 0;
}
.row-body {
  flex: 1;
  min-width: 0;
}
.row-title {
  display: flex;
  gap: 10px;
  align-items: baseline;
  flex-wrap: wrap;
}
.row-title strong {
  color: #fff;
  font-size: 13px;
}
.row-author {
  color: #aaa;
  font-size: 12px;
}
.row-paths {
  margin-top: 4px;
  display: flex;
  flex-direction: column;
  gap: 2px;
}
.row-path {
  display: flex;
  gap: 6px;
  font-size: 11px;
  color: #999;
  overflow: hidden;
}
.path-label {
  flex-shrink: 0;
  color: #777;
  text-transform: uppercase;
  letter-spacing: 0.04em;
}
.path-value {
  font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.path-target {
  color: #80c896;
}
.row-meta {
  margin-top: 3px;
  font-size: 11px;
  color: #777;
}
.stub-replace-tag {
  margin-left: 6px;
  padding: 1px 6px;
  border-radius: 8px;
  border: 1px solid rgba(230, 175, 46, 0.45);
  background: rgba(230, 175, 46, 0.12);
  color: #e6af2e;
  cursor: help;
}
.collision-group {
  padding: 10px 14px;
  border-bottom: 1px solid rgba(255, 255, 255, 0.03);
}
.collision-group:last-child {
  border-bottom: 0;
}
.collision-target {
  font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
  font-size: 12px;
  color: #e8b070;
  margin-bottom: 6px;
}
.collision-members {
  margin: 0;
  padding-left: 16px;
  color: #ccc;
  font-size: 12px;
}
.collision-members li {
  margin-bottom: 4px;
}
.invalid-group {
  padding: 10px 14px;
  border-bottom: 1px solid rgba(255, 255, 255, 0.03);
}
.invalid-group:last-child {
  border-bottom: 0;
}
.invalid-group-head {
  display: flex;
  align-items: center;
  gap: 8px;
}
.invalid-group-title {
  font-size: 12px;
  font-weight: 600;
  color: #e8b070;
}
.invalid-group-count {
  font-size: 11px;
  color: #aaa;
  background: rgba(240, 176, 96, 0.12);
  border: 1px solid rgba(176, 128, 64, 0.3);
  border-radius: 999px;
  padding: 1px 8px;
}
.invalid-group-help {
  margin: 5px 0 8px;
  font-size: 12px;
  color: #aaa;
  line-height: 1.45;
}
.invalid-list {
  margin: 0;
  padding: 0;
  list-style: none;
  color: #ccc;
  font-size: 12px;
}
.invalid-row {
  padding: 6px 0;
  border-top: 1px solid rgba(255, 255, 255, 0.03);
}
.invalid-row:first-child {
  border-top: 0;
}
.invalid-row-head {
  display: flex;
  gap: 8px;
  align-items: baseline;
  flex-wrap: wrap;
}
.invalid-row-head strong {
  color: #fff;
  font-size: 12px;
}
.invalid-row-paths {
  margin-top: 3px;
  display: flex;
  flex-direction: column;
  gap: 2px;
}
.row-link {
  font-size: 11px;
  color: #6fa8e8;
  text-decoration: none;
  white-space: nowrap;
}
.row-link:hover {
  text-decoration: underline;
}
.invalid-reason {
  margin-top: 3px;
  color: #e8b070;
}
.invalid-row-note {
  margin-top: 4px;
  font-size: 11px;
  color: #999;
  font-style: italic;
}
.invalid-row-action {
  margin-top: 5px;
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
}
.flatten-confirm-text {
  font-size: 11px;
  color: #e8b070;
}
.flatten-go {
  color: #80c896;
  font-weight: 600;
}
.flatten-cancel {
  color: #999;
}
.btn {
  padding: 6px 14px;
  border-radius: 4px;
  border: 1px solid rgba(255, 255, 255, 0.1);
  background: rgba(255, 255, 255, 0.04);
  color: #ddd;
  font-size: 13px;
  cursor: pointer;
}
.btn:hover:not(:disabled) {
  background: rgba(255, 255, 255, 0.08);
}
.btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}
.btn-primary {
  background: #2a6fd0;
  border-color: #2a6fd0;
  color: #fff;
}
.btn-primary:hover:not(:disabled) {
  background: #3380e5;
}
.confirm-panel {
  width: 100%;
  display: flex;
  flex-direction: column;
  gap: 8px;
}
.confirm-title {
  font-size: 13px;
  color: #fff;
  font-weight: 600;
}
.confirm-list {
  margin: 0;
  padding-left: 18px;
  color: #ddd;
  font-size: 12px;
  line-height: 1.5;
}
.confirm-list li {
  margin-bottom: 2px;
}
.confirm-list code {
  background: rgba(255, 255, 255, 0.06);
  padding: 1px 4px;
  border-radius: 3px;
}
.confirm-warning {
  font-size: 12px;
  color: #f0b060;
  padding: 6px 10px;
  background: rgba(240, 176, 96, 0.08);
  border-left: 3px solid #b08040;
  border-radius: 3px;
}
.confirm-actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
  margin-top: 4px;
}
.confirm-error {
  font-size: 12px;
}
.state-msg {
  padding: 24px 8px;
  text-align: center;
  color: #aaa;
}
.state-msg.error {
  color: #f06060;
}
.error {
  color: #f06060;
}
.modal-fade-enter-active,
.modal-fade-leave-active {
  transition: opacity 0.18s ease;
}
.modal-fade-enter-from,
.modal-fade-leave-to {
  opacity: 0;
}
</style>
