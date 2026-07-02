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
<!--
  Break a multi-book record apart: server-side clustering (subdirectory /
  embedded tag / filename stem) with a suggested existing library record per
  group. Each group can be MOVED to a record (files relocate to its folder),
  DELETED (a redundant duplicate copy — files removed from disk), or left
  alone.
-->
<template>
  <Modal :visible="visible" size="lg" title="Split collection" @close="onClose">
    <ModalBody>
      <div v-if="loading" class="split-empty">Analyzing files…</div>
      <div v-else-if="error" class="split-error">{{ error }}</div>
      <template v-else>
        <p class="split-intro">
          {{ clusters.length }} group{{ clusters.length === 1 ? '' : 's' }} detected. Move a group
          to the record it belongs to (files relocate into its folder), delete a redundant copy, or
          leave it alone.
        </p>
        <div class="split-clusters">
          <div v-for="c in clusters" :key="c.key" class="split-cluster">
            <div class="split-cluster-head">
              <span class="split-cluster-name">
                <strong>{{ c.displayName }}</strong>
                <small
                  >{{ c.fileIds.length }} file{{ c.fileIds.length === 1 ? '' : 's' }}
                  <template v-if="c.fileNames.length">· e.g. {{ c.fileNames[0] }}</template></small
                >
              </span>
              <div class="split-actions">
                <label
                  ><input type="radio" :name="`act-${c.key}`" value="none" v-model="c.action" />
                  Leave</label
                >
                <label
                  ><input
                    type="radio"
                    :name="`act-${c.key}`"
                    value="move"
                    v-model="c.action"
                    :disabled="!c.targetId"
                  />
                  Move</label
                >
                <label class="split-delete-label"
                  ><input type="radio" :name="`act-${c.key}`" value="delete" v-model="c.action" />
                  Delete</label
                >
              </div>
            </div>
            <div class="split-cluster-dest">
              <template v-if="c.editing">
                <input
                  v-model="c.query"
                  type="text"
                  class="split-search"
                  placeholder="Search your library…"
                />
                <div class="split-candidates">
                  <button
                    v-for="cand in candidatesFor(c)"
                    :key="cand.id"
                    type="button"
                    class="split-candidate"
                    @click="pickTarget(c, cand)"
                  >
                    {{ cand.title }}
                    <small>id {{ cand.id }}</small>
                  </button>
                  <div v-if="candidatesFor(c).length === 0" class="split-empty">No matches.</div>
                </div>
              </template>
              <template v-else>
                <span v-if="c.targetId" class="split-dest-label">
                  → {{ c.targetTitle }}
                  <small>id {{ c.targetId }}</small>
                </span>
                <span v-else class="split-dest-label split-dest-none">no destination</span>
                <button type="button" class="split-change-btn" @click="c.editing = true">
                  {{ c.targetId ? 'Change…' : 'Choose…' }}
                </button>
              </template>
            </div>
            <details class="split-files">
              <summary>files</summary>
              <div class="split-file" v-for="name in c.fileNames" :key="name">{{ name }}</div>
            </details>
          </div>
        </div>
        <div v-if="applying" class="split-progress">{{ progressText }}</div>
      </template>
    </ModalBody>
    <div class="modal-footer">
      <button type="button" class="btn" :disabled="applying" @click="onClose">Cancel</button>
      <button
        type="button"
        class="btn btn-primary"
        :disabled="applying || (moveCount === 0 && deleteCount === 0)"
        @click="apply"
      >
        {{ applyLabel }}
      </button>
    </div>
  </Modal>
</template>

<script setup lang="ts">
import { ref, computed, watch, reactive } from 'vue'
import { Modal, ModalBody } from '@/components/feedback'
import { apiService } from '@/services/api'
import { useToast } from '@/services/toastService'
import { useLibraryStore } from '@/stores/library'
import { showConfirm } from '@/composables/useConfirm'
import type { Audiobook } from '@/types'

type GroupAction = 'none' | 'move' | 'delete'

interface ClusterRow {
  key: string
  displayName: string
  fileIds: number[]
  fileNames: string[]
  targetId: number | null
  targetTitle: string | null
  action: GroupAction
  editing: boolean
  query: string
}

const props = defineProps<{
  visible: boolean
  audiobook: Audiobook | null
}>()

const emit = defineEmits<{
  (e: 'close'): void
  (e: 'done', result: { groupsMoved: number; filesMoved: number; filesDeleted: number }): void
}>()

const toast = useToast()
const libraryStore = useLibraryStore()

const loading = ref(false)
const error = ref<string | null>(null)
const clusters = ref<ClusterRow[]>([])
const applying = ref(false)
const progressText = ref('')

const moveCount = computed(
  () => clusters.value.filter((c) => c.action === 'move' && c.targetId).length,
)
const deleteCount = computed(() => clusters.value.filter((c) => c.action === 'delete').length)
const applyLabel = computed(() => {
  if (applying.value) return 'Working…'
  const parts: string[] = []
  if (moveCount.value > 0) parts.push(`move ${moveCount.value}`)
  if (deleteCount.value > 0) parts.push(`delete ${deleteCount.value}`)
  return parts.length ? `Apply (${parts.join(', ')})` : 'Apply'
})

watch(
  () => props.visible,
  async (visible) => {
    if (!visible || !props.audiobook) return
    loading.value = true
    error.value = null
    clusters.value = []
    applying.value = false
    if (libraryStore.audiobooks.length === 0) {
      void libraryStore.fetchLibrary().catch(() => {})
    }
    try {
      const preview = await apiService.getSplitPreview(props.audiobook.id)
      clusters.value = preview.clusters.map((c) =>
        reactive({
          key: c.key,
          displayName: c.displayName,
          fileIds: c.fileIds,
          fileNames: c.fileNames,
          targetId: c.suggestedTargetId ?? null,
          targetTitle: c.suggestedTargetTitle ?? null,
          action: (c.suggestedTargetId ? 'move' : 'none') as GroupAction,
          editing: false,
          query: c.displayName,
        }),
      )
    } catch (err) {
      error.value = err instanceof Error ? err.message : 'Could not analyze this record.'
    } finally {
      loading.value = false
    }
  },
)

// Common words carry no signal — "The Rolling Stones" must not surface every
// record containing "the".
const STOPWORDS = new Set([
  'the',
  'a',
  'an',
  'of',
  'and',
  'or',
  'in',
  'on',
  'to',
  'for',
  'by',
  'at',
  'is',
  'it',
  'vol',
  'volume',
  'book',
  'part',
  'unabridged',
  'abridged',
])

function tokenize(text: string): string[] {
  return (text || '')
    .toLowerCase()
    .split(/[^a-z0-9]+/)
    .filter((t) => t.length > 1 && !STOPWORDS.has(t))
}

function candidatesFor(c: ClusterRow) {
  const sourceId = props.audiobook?.id
  const tokens = tokenize(c.query)
  if (tokens.length === 0) return []
  return libraryStore.audiobooks
    .filter((b) => b.id !== sourceId)
    .map((b) => {
      const titleTokens = new Set(tokenize(b.title || ''))
      const score = tokens.reduce((s, t) => s + (titleTokens.has(t) ? 1 : 0), 0)
      return { id: b.id, title: b.title || '', score }
    })
    .filter((x) => x.score > 0)
    .sort((a, b) => b.score - a.score || a.title.localeCompare(b.title))
    .slice(0, 8)
}

function pickTarget(c: ClusterRow, cand: { id: number; title: string }) {
  c.targetId = cand.id
  c.targetTitle = cand.title
  c.action = 'move'
  c.editing = false
}

async function apply() {
  const book = props.audiobook
  if (!book || applying.value) return
  const moves = clusters.value.filter((c) => c.action === 'move' && c.targetId)
  const deletes = clusters.value.filter((c) => c.action === 'delete')
  if (moves.length === 0 && deletes.length === 0) return

  if (deletes.length > 0) {
    const fileCount = deletes.reduce((n, c) => n + c.fileIds.length, 0)
    const names = deletes.map((c) => `"${c.displayName}"`).join(', ')
    const ok = await showConfirm(
      `Delete ${fileCount} file(s) from disk for group(s) ${names}? This cannot be undone.`,
      'Confirm deletion',
      { danger: true, confirmText: 'Delete files', cancelText: 'Cancel' },
    )
    if (!ok) return
  }

  applying.value = true
  let groupsMoved = 0
  let filesMoved = 0
  let filesDeleted = 0
  const failures: string[] = []

  for (const [index, c] of moves.entries()) {
    progressText.value = `Moving "${c.displayName}" (${index + 1}/${moves.length})…`
    try {
      const result = await apiService.transferAudiobookFiles(book.id, c.targetId!, c.fileIds)
      groupsMoved++
      filesMoved += result.transferred
      if (result.warnings?.length) failures.push(`${c.displayName}: ${result.warnings.join(' ')}`)
    } catch (err) {
      failures.push(`${c.displayName}: ${err instanceof Error ? err.message : 'move failed'}`)
    }
  }

  for (const c of deletes) {
    progressText.value = `Deleting "${c.displayName}"…`
    for (const fileId of c.fileIds) {
      try {
        await apiService.deleteAudiobookFile(book.id, fileId, { deleteFromDisk: true })
        filesDeleted++
      } catch (err) {
        failures.push(`${c.displayName}: ${err instanceof Error ? err.message : 'delete failed'}`)
        break
      }
    }
  }
  applying.value = false

  const summary = [
    filesMoved > 0 ? `moved ${filesMoved} file(s) across ${groupsMoved} book(s)` : null,
    filesDeleted > 0 ? `deleted ${filesDeleted} file(s)` : null,
  ]
    .filter(Boolean)
    .join('; ')
  if (failures.length > 0) {
    toast.warning(`Split finished with issues — ${summary}`, failures.slice(0, 3).join(' · '))
  } else {
    toast.success('Split collection complete', `${summary}.`)
  }
  emit('done', { groupsMoved, filesMoved, filesDeleted })
}

function onClose() {
  if (!applying.value) emit('close')
}
</script>

<style scoped>
.split-intro {
  color: #adb5bd;
  font-size: 0.9rem;
  margin: 0 0 1rem;
}

.split-clusters {
  display: flex;
  flex-direction: column;
  gap: 0.6rem;
  max-height: 420px;
  overflow-y: auto;
}

.split-cluster {
  border: 1px solid rgba(255, 255, 255, 0.06);
  border-radius: 6px;
  padding: 0.6rem 0.75rem;
}

.split-cluster-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 0.6rem;
  flex-wrap: wrap;
}

.split-cluster-name {
  display: flex;
  align-items: baseline;
  gap: 0.6rem;
}

.split-cluster-name small {
  color: #8a93a0;
}

.split-actions {
  display: flex;
  gap: 0.9rem;
  font-size: 0.85rem;
  color: #adb5bd;
}

.split-actions label {
  display: flex;
  align-items: center;
  gap: 0.3rem;
  cursor: pointer;
}

.split-delete-label {
  color: #e74c3c;
}

.split-cluster-dest {
  margin: 0.4rem 0 0 0;
  display: flex;
  flex-direction: column;
  gap: 0.4rem;
}

.split-dest-label {
  color: #d8dee6;
  font-size: 0.9rem;
}

.split-dest-label small {
  color: #8a93a0;
  margin-left: 0.35rem;
}

.split-dest-none {
  color: #f39c12;
  font-style: italic;
}

.split-change-btn {
  align-self: flex-start;
  background: none;
  border: 1px solid var(--brand-500, #4dabf7);
  color: var(--brand-500, #4dabf7);
  border-radius: 5px;
  padding: 2px 10px;
  font-size: 0.8rem;
  cursor: pointer;
}

.split-search {
  width: 100%;
  padding: 0.45rem 0.6rem;
  border: 1px solid #444;
  border-radius: 6px;
  background-color: #1a1a1a;
  color: #fff;
  font-size: 0.9rem;
}

.split-candidates {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
}

.split-candidate {
  text-align: left;
  background: rgba(255, 255, 255, 0.03);
  border: 1px solid rgba(255, 255, 255, 0.06);
  color: #d8dee6;
  border-radius: 5px;
  padding: 0.35rem 0.6rem;
  cursor: pointer;
}

.split-candidate:hover {
  background: rgba(77, 171, 247, 0.12);
}

.split-candidate small {
  color: #8a93a0;
  margin-left: 0.4rem;
}

.split-files {
  margin: 0.45rem 0 0 0;
  font-size: 0.82rem;
  color: #8a93a0;
}

.split-files summary {
  cursor: pointer;
}

.split-file {
  padding-left: 0.8rem;
  overflow-wrap: anywhere;
}

.split-progress {
  margin-top: 0.8rem;
  color: #4dabf7;
  font-size: 0.9rem;
}

.split-empty {
  color: #8a93a0;
  font-size: 0.9rem;
  padding: 0.3rem 0;
}

.split-error {
  color: #e74c3c;
}

.modal-footer {
  display: flex;
  justify-content: flex-end;
  gap: 0.75rem;
  padding: 1rem 1.5rem;
}
</style>
