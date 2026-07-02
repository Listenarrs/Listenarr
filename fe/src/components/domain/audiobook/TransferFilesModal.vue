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
  Move audio files from this record to ANOTHER EXISTING library record — the
  remediation when files actually belong to a different book the library
  already tracks (often still "wanted"): two books' tracks imported onto one
  record, or a collection being split into its real books.
-->
<template>
  <Modal :visible="visible" size="lg" title="Move files to another audiobook" @close="onClose">
    <ModalBody>
      <div v-if="!audiobook" class="transfer-empty">No audiobook selected.</div>
      <template v-else>
        <div class="transfer-section">
          <div class="transfer-section-title">Files to move</div>
          <label v-for="f in sourceFiles" :key="f.id" class="transfer-file-row">
            <input
              type="checkbox"
              :checked="selectedFileIds.has(f.id)"
              @change="toggleFile(f.id)"
            />
            <span class="transfer-file-name">{{ fileName(f.path) }}</span>
          </label>
          <div v-if="sourceFiles.length === 0" class="transfer-empty">
            This audiobook has no tracked files.
          </div>
        </div>

        <div class="transfer-section">
          <div class="transfer-section-title">Destination record</div>
          <input
            v-model="query"
            type="text"
            class="transfer-search"
            placeholder="Search your library by title…"
            @input="chosenTargetId = null"
          />
          <div v-if="libraryLoading" class="transfer-empty">Loading library…</div>
          <div v-else-if="candidates.length === 0" class="transfer-empty">
            No library records match "{{ query }}".
          </div>
          <div v-else class="transfer-candidates">
            <label v-for="c in candidates" :key="c.id" class="transfer-candidate">
              <input type="radio" name="transfer-target" :value="c.id" v-model="chosenTargetId" />
              <span class="transfer-candidate-text">
                <strong>{{ c.title }}</strong>
                <small>
                  {{ (c.narrators || []).slice(0, 2).join(', ') || 'Unknown narrator' }}
                  <template v-if="c.publishYear"> · {{ c.publishYear }}</template>
                  · id {{ c.id }}
                  <template v-if="(c.fileCount ?? 0) > 0">
                    · <span class="transfer-has-files">{{ c.fileCount }} file(s)</span>
                  </template>
                </small>
              </span>
            </label>
          </div>
        </div>

        <div v-if="chosenTargetFileCount > 0" class="transfer-warning">
          <strong>{{ chosenTarget?.title }}</strong> already has {{ chosenTargetFileCount }} audio
          file(s). The moved files will be <strong>added alongside</strong> them — not replace them
          — which can leave two books' tracks mixed in one record.
        </div>

        <div class="transfer-note">
          Files keep their audio untouched: ownership moves to the destination record (and the file
          relocates into its folder when possible).
        </div>
      </template>
    </ModalBody>
    <div class="modal-footer">
      <button type="button" class="btn" @click="onClose">Cancel</button>
      <button
        type="button"
        class="btn btn-primary"
        :disabled="transferring || !chosenTargetId || selectedFileIds.size === 0"
        @click="confirmTransfer"
      >
        {{ transferring ? 'Moving…' : `Move ${selectedFileIds.size} file(s)` }}
      </button>
    </div>
  </Modal>
</template>

<script setup lang="ts">
import { ref, computed, watch } from 'vue'
import { Modal, ModalBody } from '@/components/feedback'
import { apiService } from '@/services/api'
import { useToast } from '@/services/toastService'
import { showConfirm } from '@/composables/useConfirm'
import { useLibraryStore } from '@/stores/library'
import type { Audiobook } from '@/types'

type SourceFile = { id: number; path?: string | null }

const props = defineProps<{
  visible: boolean
  audiobook: Audiobook | null
  // Seeds the destination search — e.g. the title the files' tags claim.
  initialQuery?: string | null
  // Pre-select a subset of files (e.g. a multi-select made in the file list).
  // When omitted/empty, all of the source's files are selected as before.
  initialFileIds?: number[] | null
}>()

const emit = defineEmits<{
  (e: 'close'): void
  (e: 'done', result: { targetId: number; transferred: number }): void
}>()

const toast = useToast()
const libraryStore = useLibraryStore()

const query = ref('')
const chosenTargetId = ref<number | null>(null)
const selectedFileIds = ref<Set<number>>(new Set())
const transferring = ref(false)
const libraryLoading = ref(false)

const sourceFiles = computed<SourceFile[]>(() => props.audiobook?.files ?? [])

const chosenTarget = computed<Audiobook | null>(
  () => libraryStore.audiobooks.find((b) => b.id === chosenTargetId.value) ?? null,
)
const chosenTargetFileCount = computed(() => chosenTarget.value?.fileCount ?? 0)

watch(
  () => props.visible,
  (visible) => {
    if (!visible) return
    query.value = (props.initialQuery || '').trim()
    chosenTargetId.value = null
    selectedFileIds.value =
      props.initialFileIds && props.initialFileIds.length
        ? new Set(props.initialFileIds)
        : new Set(sourceFiles.value.map((f) => f.id))
    transferring.value = false
    if (libraryStore.audiobooks.length === 0) {
      libraryLoading.value = true
      void libraryStore
        .fetchLibrary()
        .catch(() => {
          /* candidates list shows empty; search still usable after retry */
        })
        .finally(() => {
          libraryLoading.value = false
        })
    }
  },
)

// Token-overlap ranking so a filename-ish query ("Warbreaker 2 of 3 Dramatized")
// surfaces the matching record even when it isn't a substring of the title.
const candidates = computed(() => {
  const sourceId = props.audiobook?.id
  const tokens = tokenize(query.value)
  if (tokens.length === 0) return []
  return libraryStore.audiobooks
    .filter((b) => b.id !== sourceId)
    .map((b) => ({ book: b, score: overlapScore(tokens, tokenize(b.title || '')) }))
    .filter((x) => x.score > 0)
    .sort((a, b) => b.score - a.score || (a.book.title || '').localeCompare(b.book.title || ''))
    .slice(0, 12)
    .map((x) => x.book)
})

function tokenize(text: string): string[] {
  return (text || '')
    .toLowerCase()
    .split(/[^a-z0-9]+/)
    .filter((t) => t.length > 1)
}

function overlapScore(queryTokens: string[], titleTokens: string[]): number {
  const titleSet = new Set(titleTokens)
  return queryTokens.reduce((score, t) => score + (titleSet.has(t) ? 1 : 0), 0)
}

function toggleFile(id: number) {
  const next = new Set(selectedFileIds.value)
  if (next.has(id)) next.delete(id)
  else next.add(id)
  selectedFileIds.value = next
}

function fileName(path?: string | null): string {
  if (!path) return 'Unknown file'
  const parts = path.split(/[\\/]/)
  return parts[parts.length - 1] || path
}

async function confirmTransfer() {
  const book = props.audiobook
  const targetId = chosenTargetId.value
  if (!book || !targetId || transferring.value) return

  // Moving into a record that already has audio merges the two sets (it never
  // overwrites) — confirm so a wrong destination doesn't silently end up with
  // two books' tracks mixed together.
  if (chosenTargetFileCount.value > 0) {
    const ok = await showConfirm(
      `${chosenTarget.value?.title || 'This record'} already has ${chosenTargetFileCount.value} audio ` +
        `file(s). The ${selectedFileIds.value.size} moved file(s) will be added alongside the ` +
        `existing ones, not replace them.\n\nMove them anyway?`,
      'Destination already has files',
    )
    if (!ok) return
  }

  transferring.value = true
  try {
    const allSelected = selectedFileIds.value.size === sourceFiles.value.length
    const result = await apiService.transferAudiobookFiles(
      book.id,
      targetId,
      allSelected ? null : Array.from(selectedFileIds.value),
    )
    if (result.warnings?.length) {
      toast.warning('Files moved with warnings', result.warnings.join(' '))
    } else {
      toast.success('Files moved', result.message)
    }
    emit('done', { targetId, transferred: result.transferred })
  } catch (err) {
    const message = err instanceof Error ? err.message : 'Unknown error'
    toast.error('Could not move files', message)
    transferring.value = false
  }
}

function onClose() {
  if (!transferring.value) emit('close')
}
</script>

<style scoped>
.transfer-section {
  margin-bottom: 1.25rem;
}

.transfer-section-title {
  font-weight: 500;
  color: #fff;
  margin-bottom: 0.5rem;
}

.transfer-file-row {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  padding: 0.3rem 0;
  cursor: pointer;
}

.transfer-file-name {
  font-size: 0.9rem;
  color: #d8dee6;
  overflow-wrap: anywhere;
}

.transfer-search {
  width: 100%;
  padding: 0.6rem 0.75rem;
  border: 1px solid #444;
  border-radius: 6px;
  background-color: #1a1a1a;
  color: #fff;
  font-size: 0.95rem;
  margin-bottom: 0.6rem;
}

.transfer-candidates {
  display: flex;
  flex-direction: column;
  gap: 0.35rem;
  max-height: 260px;
  overflow-y: auto;
}

.transfer-candidate {
  display: flex;
  align-items: center;
  gap: 0.6rem;
  padding: 0.45rem 0.6rem;
  border: 1px solid rgba(255, 255, 255, 0.06);
  border-radius: 6px;
  cursor: pointer;
}

.transfer-candidate:hover {
  background: rgba(255, 255, 255, 0.04);
}

.transfer-candidate-text {
  display: flex;
  flex-direction: column;
}

.transfer-candidate-text small {
  color: #8a93a0;
}

.transfer-has-files {
  color: #e0a458;
}

.transfer-warning {
  margin-bottom: 0.85rem;
  padding: 0.6rem 0.75rem;
  border-radius: 6px;
  font-size: 0.85rem;
  line-height: 1.5;
  color: #f0c674;
  background-color: rgba(224, 164, 88, 0.1);
  border: 1px solid rgba(224, 164, 88, 0.3);
}

.transfer-warning strong {
  color: #fff;
}

.transfer-note {
  font-size: 0.85rem;
  color: #adb5bd;
}

.transfer-empty {
  color: #8a93a0;
  font-size: 0.9rem;
  padding: 0.4rem 0;
}

.modal-footer {
  display: flex;
  justify-content: flex-end;
  gap: 0.75rem;
  padding: 1rem 1.5rem;
}
</style>
