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
  <div class="author-monitoring-exclusions">
    <div class="section-header">
      <h3>Author Monitoring Exclusions</h3>
      <p class="description">
        Books you deleted and chose not to re-add from a monitored author's catalog sync. Remove an
        entry here to let author monitoring add the book again.
      </p>
    </div>

    <!-- Error State -->
    <div v-if="error" class="error-banner">
      <PhWarningCircle />
      <span>{{ error }}</span>
      <button class="close-btn" @click="error = null">
        <PhX />
      </button>
    </div>

    <!-- Success Message -->
    <div v-if="successMessage" class="success-banner">
      <PhCheckCircle />
      <span>{{ successMessage }}</span>
      <button class="close-btn" @click="successMessage = null">
        <PhX />
      </button>
    </div>

    <!-- Loading State -->
    <div v-if="loading" class="loading-state">
      <PhSpinner class="ph-spin" />
      <p>Loading exclusions...</p>
    </div>

    <!-- Exclusions List -->
    <div v-if="!loading && exclusions.length > 0" class="exclusions-list">
      <div v-for="exclusion in exclusions" :key="exclusion.id" class="exclusion-card">
        <div class="exclusion-info">
          <h4>{{ exclusion.title || 'Untitled' }}</h4>
          <span class="exclusion-meta">
            <template v-if="exclusion.authorName">{{ exclusion.authorName }} • </template>
            <template v-if="exclusion.asin">ASIN {{ exclusion.asin }} • </template>
            Excluded {{ formatDate(exclusion.createdAt) }}
          </span>
        </div>
        <div class="exclusion-actions">
          <button
            class="btn btn-icon btn-danger action-delete"
            title="Remove exclusion"
            @click="handleRemove(exclusion)"
          >
            <PhTrash />
          </button>
        </div>
      </div>
    </div>

    <!-- Empty State -->
    <div v-if="!loading && exclusions.length === 0" class="empty-state">
      <PhProhibit class="empty-icon" />
      <h4>No Exclusions</h4>
      <p>
        When you delete a book by a monitored author you can choose to keep it from being re-added.
        Those choices show up here.
      </p>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted } from 'vue'
import {
  PhWarningCircle,
  PhX,
  PhCheckCircle,
  PhSpinner,
  PhTrash,
  PhProhibit,
} from '@phosphor-icons/vue'
import { showConfirm } from '@/composables/useConfirm'
import type { AuthorMonitoringExclusion } from '@/types'
import { getAuthorMonitoringExclusions, removeAuthorMonitoringExclusion } from '@/services/api'

const exclusions = ref<AuthorMonitoringExclusion[]>([])
const loading = ref(false)
const error = ref<string | null>(null)
const successMessage = ref<string | null>(null)

const loadExclusions = async () => {
  loading.value = true
  error.value = null
  try {
    exclusions.value = await getAuthorMonitoringExclusions()
  } catch (err) {
    error.value = err instanceof Error ? err.message : 'Failed to load exclusions'
  } finally {
    loading.value = false
  }
}

const handleRemove = async (exclusion: AuthorMonitoringExclusion) => {
  const ok = await showConfirm(
    `Remove the exclusion for "${exclusion.title || 'this book'}"?\n\n` +
      `If the author is monitored, the catalog sync may re-add this book.`,
    'Remove Exclusion',
  )
  if (!ok) return

  error.value = null
  try {
    await removeAuthorMonitoringExclusion(exclusion.id)
    successMessage.value = 'Exclusion removed.'
    await loadExclusions()
    setTimeout(() => {
      successMessage.value = null
    }, 3000)
  } catch (err) {
    error.value = err instanceof Error ? err.message : 'Failed to remove exclusion'
  }
}

const formatDate = (dateString: string) => {
  const date = new Date(dateString)
  return date.toLocaleString()
}

onMounted(() => {
  loadExclusions()
})
</script>

<style scoped>
.author-monitoring-exclusions {
  margin-top: 1.5rem;
}

.section-header h3 {
  color: #fff;
  font-size: 1.1rem;
  margin: 0 0 1rem 0;
  padding-bottom: 0.5rem;
  border-bottom: 1px solid #444;
}

.description {
  color: #999;
  margin-bottom: 1.5rem;
  line-height: 1.6;
  font-size: 0.9rem;
}

.loading-state {
  text-align: center;
  padding: 2rem;
  color: #999;
}

.ph-spin {
  animation: spin 1s linear infinite;
}

.error-banner,
.success-banner {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  padding: 0.75rem 1rem;
  border-radius: 6px;
  margin-bottom: 1rem;
  position: relative;
}

.error-banner {
  background-color: rgba(220, 53, 69, 0.1);
  border: 1px solid rgba(220, 53, 69, 0.3);
  color: #ff6b7a;
}

.success-banner {
  background-color: rgba(40, 167, 69, 0.1);
  border: 1px solid rgba(40, 167, 69, 0.3);
  color: #6fbf73;
}

.error-banner .close-btn,
.success-banner .close-btn {
  margin-left: auto;
  background: none;
  border: none;
  color: inherit;
  cursor: pointer;
  padding: 0.25rem;
  opacity: 0.7;
  transition: opacity 0.2s;
}

.error-banner .close-btn:hover,
.success-banner .close-btn:hover {
  opacity: 1;
}

.exclusions-list {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
  margin-bottom: 1.5rem;
}

.exclusion-card {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 1rem;
  background-color: #1a1a1a;
  border: 1px solid #444;
  border-radius: 6px;
  padding: 0.75rem 1rem;
  transition: all 0.2s;
}

.exclusion-card:hover {
  border-color: #666;
}

.exclusion-info {
  flex: 1;
  min-width: 0;
}

.exclusion-info h4 {
  margin: 0 0 0.25rem 0;
  color: #fff;
  font-size: 0.95rem;
  font-weight: 500;
}

.exclusion-meta {
  color: #999;
  font-size: 0.8rem;
}

.exclusion-actions {
  flex-shrink: 0;
}

.empty-state {
  text-align: center;
  padding: 2.5rem 2rem;
  color: #999;
}

.empty-icon {
  font-size: 3rem;
  margin-bottom: 1rem;
  color: #666;
  display: block;
}

.empty-state h4 {
  color: #fff;
  margin: 0 0 0.5rem 0;
  font-size: 1.1rem;
}

.empty-state p {
  max-width: 500px;
  margin: 0 auto;
  line-height: 1.6;
  font-size: 0.9rem;
}

.btn-icon {
  padding: 0.5rem;
  background-color: #2a2a2a;
  border: 1px solid #444;
  color: #fff;
  min-width: auto;
}

.btn-icon.btn-danger:hover {
  background-color: #333;
  border-color: #dc3545;
  color: #dc3545;
}
</style>
