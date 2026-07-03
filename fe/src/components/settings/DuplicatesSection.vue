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
  Duplicate sweep results: duplicate RECORDS (same book tracked twice) with a
  suggested keeper and per-record delete, and records holding duplicate COPIES
  of their own audio (resolved on the book page via multi-select / split).
-->
<template>
  <div class="settings-section duplicates-section">
    <div class="dupes-header">
      <h3>Duplicates</h3>
      <button type="button" class="dupes-refresh-btn" :disabled="loading" @click="load">
        {{ loading ? 'Scanning…' : hasLoaded ? 'Rescan' : 'Scan library' }}
      </button>
    </div>
    <p class="dupes-help">
      Finds books tracked twice (same ASIN, or same title and author without an edition signal) and
      records holding two copies of their own audio.
    </p>

    <div v-if="error" class="dupes-error">{{ error }}</div>

    <template v-if="hasLoaded && !loading && !error">
      <div class="dupes-subhead">
        Duplicate records
        <small
          >{{ groups.length }} group{{ groups.length === 1 ? '' : 's' }} — the suggested keeper is
          marked; deleting removes the record only (files stay on disk).</small
        >
      </div>
      <div v-if="groups.length === 0" class="dupes-empty">No duplicate records found.</div>
      <div v-for="g in groups" :key="g.key" class="dupes-group">
        <span class="dupes-reason" :class="`dupes-reason-${g.reason}`">{{
          g.reason === 'asin' ? 'same ASIN' : 'same title + author'
        }}</span>
        <div v-for="b in g.books" :key="b.id" class="dupes-row">
          <router-link :to="`/audiobooks/${b.id}`" class="dupes-title">{{ b.title }}</router-link>
          <span v-if="b.id === g.suggestedKeeperId" class="dupes-keeper">keep</span>
          <small class="dupes-meta">
            {{ b.fileCount }} file{{ b.fileCount === 1 ? '' : 's' }}
            <template v-if="b.fileSize > 0"> · {{ formatSize(b.fileSize) }}</template>
            <template v-if="b.monitored"> · monitored</template>
            · id {{ b.id }}
          </small>
          <button
            v-if="b.id !== g.suggestedKeeperId"
            type="button"
            class="dupes-delete-btn"
            :disabled="deletingId === b.id"
            @click="deleteRecord(b)"
          >
            {{ deletingId === b.id ? 'Deleting…' : 'Delete record' }}
          </button>
        </div>
      </div>

      <div class="dupes-subhead">
        Duplicate copies
        <small
          >{{ copies.length }} record{{ copies.length === 1 ? '' : 's' }} holding the same audio
          twice — open the book and use file multi-select or Split Collection.</small
        >
      </div>
      <div v-if="copies.length === 0" class="dupes-empty">No duplicate copies found.</div>
      <div v-for="c in copies" :key="c.id" class="dupes-row">
        <router-link :to="`/audiobooks/${c.id}`" class="dupes-title">{{ c.title }}</router-link>
        <small class="dupes-meta"
          >{{ c.fileCount }} files in {{ c.clusterCount }} groups · id {{ c.id }}</small
        >
      </div>
    </template>
  </div>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import { apiService } from '@/services/api'
import { useToast } from '@/services/toastService'
import { showConfirm } from '@/composables/useConfirm'
import type { DuplicateBookSummary, DuplicateGroup, DuplicateCopyBook } from '@/types'

const toast = useToast()

const loading = ref(false)
const hasLoaded = ref(false)
const error = ref<string | null>(null)
const groups = ref<DuplicateGroup[]>([])
const copies = ref<DuplicateCopyBook[]>([])
const deletingId = ref<number | null>(null)

async function load() {
  loading.value = true
  error.value = null
  try {
    const resp = await apiService.getLibraryDuplicates()
    groups.value = resp.duplicateGroups
    copies.value = resp.duplicateCopyBooks
    hasLoaded.value = true
  } catch (err) {
    error.value = err instanceof Error ? err.message : 'Duplicate scan failed.'
  } finally {
    loading.value = false
  }
}

async function deleteRecord(b: DuplicateBookSummary) {
  const ok = await showConfirm(
    `Delete the record "${b.title}" (id ${b.id})? Its ${b.fileCount} tracked file(s) stay on ` +
      `disk; only the library record is removed.`,
    'Delete duplicate record',
    { danger: true, confirmText: 'Delete record', cancelText: 'Cancel' },
  )
  if (!ok) return

  deletingId.value = b.id
  try {
    await apiService.removeFromLibrary(b.id)
    toast.success('Record deleted', `Removed "${b.title}" (id ${b.id}).`)
    await load()
  } catch (err) {
    toast.warning(
      'Delete failed',
      `Could not remove "${b.title}": ${err instanceof Error ? err.message : 'unknown error'}`,
    )
  } finally {
    deletingId.value = null
  }
}

function formatSize(bytes: number): string {
  if (bytes >= 1024 ** 3) return `${(bytes / 1024 ** 3).toFixed(1)} GB`
  if (bytes >= 1024 ** 2) return `${(bytes / 1024 ** 2).toFixed(0)} MB`
  return `${Math.max(1, Math.round(bytes / 1024))} KB`
}
</script>

<style scoped>
.duplicates-section {
  background: rgba(255, 255, 255, 0.02);
  border: 1px solid rgba(255, 255, 255, 0.08);
  border-radius: 8px;
  padding: 1.5rem;
  margin-top: 1.5rem;
}

.dupes-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 0.75rem;
}

.dupes-header h3 {
  margin: 0;
  color: #fff;
  font-size: 1.1rem;
  font-weight: 500;
}

.dupes-refresh-btn {
  padding: 6px 12px;
  background-color: rgba(var(--brand-rgb), 0.1);
  border: 1px solid var(--brand-500);
  border-radius: 4px;
  color: var(--brand-500);
  font-size: 13px;
  cursor: pointer;
}

.dupes-refresh-btn:disabled {
  opacity: 0.6;
  cursor: default;
}

.dupes-help {
  margin: 0.5rem 0 0;
  color: #868e96;
  font-size: 0.85rem;
}

.dupes-error {
  margin-top: 0.75rem;
  color: #e74c3c;
  font-size: 0.9rem;
}

.dupes-subhead {
  margin-top: 1.25rem;
  color: #fff;
  font-weight: 500;
  font-size: 0.95rem;
}

.dupes-subhead small {
  display: block;
  color: #868e96;
  font-weight: 400;
  font-size: 0.8rem;
}

.dupes-empty {
  color: #8a93a0;
  font-size: 0.85rem;
  padding: 0.4rem 0;
}

.dupes-group {
  margin-top: 0.6rem;
  padding: 0.5rem 0.7rem;
  border: 1px solid rgba(255, 255, 255, 0.06);
  border-radius: 6px;
}

.dupes-reason {
  display: inline-block;
  font-size: 0.7rem;
  padding: 1px 8px;
  border-radius: 999px;
  margin-bottom: 0.3rem;
}

.dupes-reason-asin {
  background: rgba(231, 76, 60, 0.12);
  color: #e74c3c;
  border: 1px solid rgba(231, 76, 60, 0.3);
}

.dupes-reason-title-author {
  background: rgba(243, 156, 18, 0.12);
  color: #f39c12;
  border: 1px solid rgba(243, 156, 18, 0.3);
}

.dupes-row {
  display: flex;
  align-items: baseline;
  gap: 0.6rem;
  padding: 0.25rem 0;
  flex-wrap: wrap;
}

.dupes-title {
  color: #4dabf7;
  text-decoration: none;
}

.dupes-title:hover {
  text-decoration: underline;
}

.dupes-keeper {
  font-size: 0.7rem;
  padding: 1px 8px;
  border-radius: 999px;
  background: rgba(46, 204, 113, 0.12);
  color: #2ecc71;
  border: 1px solid rgba(46, 204, 113, 0.3);
}

.dupes-meta {
  color: #8a93a0;
  font-size: 0.78rem;
}

.dupes-delete-btn {
  margin-left: auto;
  padding: 2px 10px;
  font-size: 0.78rem;
  background-color: rgba(255, 107, 107, 0.1);
  color: #ff6b6b;
  border: 1px solid rgba(255, 107, 107, 0.3);
  border-radius: 4px;
  cursor: pointer;
}

.dupes-delete-btn:disabled {
  opacity: 0.6;
  cursor: default;
}
</style>
