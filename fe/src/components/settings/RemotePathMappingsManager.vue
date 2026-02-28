<template>
  <div class="remote-path-mappings-manager">
    <div class="section-header">
      <h3>Remote Path Mappings</h3>
      <p class="description">
        Configure path mappings to translate file locations between the download client and
        Listenarr. This is essential for Docker setups where containers have different mount points.
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
    <div v-if="loading && !showForm" class="loading-state">
      <PhSpinner class="ph-spin" />
      <p>Loading path mappings...</p>
    </div>

    <!-- Add/Edit Form -->
    <div v-if="showForm" class="form-wrapper">
      <RemotePathMappingForm
        :download-client-id="downloadClientId"
        :mapping="editingMapping"
        @save="handleSave"
        @cancel="handleCancelForm"
      />
    </div>

    <!-- Add Button -->
    <div v-if="!showForm && !loading" class="add-button-section">
      <button class="btn btn-primary" @click="handleAdd">
        <PhPlus />
        Add Path Mapping
      </button>
    </div>

    <!-- Mappings List -->
    <div v-if="!loading && mappings.length > 0 && !showForm" class="mappings-list">
      <div v-for="mapping in mappings" :key="mapping.id" class="mapping-card">
        <div class="mapping-header">
          <div class="mapping-info">
            <h4>{{ mapping.name || 'Path Mapping' }}</h4>
            <span class="mapping-meta">
              Created {{ formatDate(mapping.createdAt) }}
              <span v-if="mapping.updatedAt && mapping.updatedAt !== mapping.createdAt">
                • Updated {{ formatDate(mapping.updatedAt) }}
              </span>
            </span>
          </div>
          <div class="mapping-actions">
            <button class="btn btn-icon action-edit" title="Edit mapping" @click="handleEdit(mapping)">
              <PhPencil />
            </button>
            <button
              class="btn btn-icon btn-danger action-delete"
              title="Delete mapping"
              @click="handleDelete(mapping)"
            >
              <PhTrash />
            </button>
          </div>
        </div>

        <div class="mapping-paths">
          <div class="path-item">
            <div class="path-label">
              <PhDesktop />
              <span>Remote Path</span>
            </div>
            <code class="path-value">{{ mapping.remotePath }}</code>
          </div>
          <div class="path-arrow">
            <PhArrowDown />
          </div>
          <div class="path-item">
            <div class="path-label">
              <PhFolderOpen />
              <span>Local Path</span>
            </div>
            <code class="path-value">{{ mapping.localPath }}</code>
          </div>
        </div>
      </div>
    </div>

    <!-- Empty State -->
    <div v-if="!loading && !showForm && mappings.length === 0" class="empty-state">
      <PhFolderOpen class="empty-icon" />
      <h4>No Path Mappings</h4>
      <p>
        No remote path mappings configured for this download client yet. Add one to enable path
        translation for Docker environments.
      </p>
    </div>

    <!-- Path Translation Tester -->
    <div v-if="mappings.length > 0 && !showForm && !loading" class="path-tester">
      <h4>
        <PhFlask />
        Test Path Translation
      </h4>
      <div class="tester-controls">
        <input
          v-model="testPath"
          type="text"
          placeholder="Enter a remote path to test (e.g., /path/to/downloads/book.m4b)"
          class="test-input"
          @keyup.enter="handleTestPath"
        />
        <button
          class="btn btn-secondary"
          @click="handleTestPath"
          :disabled="!testPath.trim() || testing"
        >
          <PhSpinner v-if="testing" class="ph-spin" />
          <PhPlay v-else />
          {{ testing ? 'Testing...' : 'Translate' }}
        </button>
      </div>

      <div v-if="testResult" class="test-result">
        <div v-if="testResult.translated" class="result-success">
          <div class="result-header">
            <PhCheckCircle />
            <strong>Translation Applied</strong>
          </div>
          <div class="result-paths">
            <div class="result-path">
              <span class="result-label">Remote:</span>
              <code>{{ testResult.remotePath }}</code>
            </div>
            <div class="result-arrow">
              <PhArrowRight />
            </div>
            <div class="result-path">
              <span class="result-label">Local:</span>
              <code>{{ testResult.localPath }}</code>
            </div>
          </div>
        </div>
        <div v-else class="result-info">
          <div class="result-header">
            <PhInfo />
            <strong>No Translation</strong>
          </div>
          <p>No mapping matches this path. The path will be used as-is.</p>
          <code>{{ testResult.remotePath }}</code>
        </div>
      </div>
    </div>

    <!-- Confirm dialog removed: using centralized showConfirm service mounted in App.vue -->
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { PhWarningCircle, PhX, PhCheckCircle, PhSpinner, PhPlus, PhPencil, PhTrash, PhDesktop, PhArrowDown, PhFolderOpen, PhFlask, PhPlay, PhArrowRight, PhInfo } from '@phosphor-icons/vue'
import RemotePathMappingForm from './RemotePathMappingForm.vue'
import { showConfirm } from '@/composables/useConfirm'
import type { RemotePathMapping, TranslatePathResponse } from '@/types'
import {
  getRemotePathMappingsByClient,
  createRemotePathMapping,
  updateRemotePathMapping,
  deleteRemotePathMapping,
  translatePath,
} from '@/services/api'

interface Props {
  downloadClientId: string
  downloadClientName: string
}

const props = defineProps<Props>()

const mappings = ref<RemotePathMapping[]>([])
const loading = ref(false)
const error = ref<string | null>(null)
const successMessage = ref<string | null>(null)
const showForm = ref(false)
const editingMapping = ref<RemotePathMapping | null>(null)
const testPath = ref('')
const testResult = ref<TranslatePathResponse | null>(null)
const testing = ref(false)

const loadMappings = async () => {
  loading.value = true
  error.value = null
  try {
    mappings.value = await getRemotePathMappingsByClient(props.downloadClientId)
  } catch (err) {
    error.value = err instanceof Error ? err.message : 'Failed to load path mappings'
  } finally {
    loading.value = false
  }
}

const handleAdd = () => {
  editingMapping.value = null
  showForm.value = true
  error.value = null
  successMessage.value = null
}

const handleEdit = (mapping: RemotePathMapping) => {
  editingMapping.value = mapping
  showForm.value = true
  error.value = null
  successMessage.value = null
}

const handleSave = async (
  mappingData: Omit<RemotePathMapping, 'id' | 'createdAt' | 'updatedAt'>,
) => {
  loading.value = true
  error.value = null

  try {
    if (editingMapping.value) {
      // Update existing
      await updateRemotePathMapping(editingMapping.value.id, mappingData)
      successMessage.value = 'Path mapping updated successfully!'
    } else {
      // Create new
      await createRemotePathMapping(mappingData)
      successMessage.value = 'Path mapping created successfully!'
    }

    showForm.value = false
    editingMapping.value = null
    await loadMappings()

    // Clear success message after 3 seconds
    setTimeout(() => {
      successMessage.value = null
    }, 3000)
  } catch (err) {
    error.value = err instanceof Error ? err.message : 'Failed to save path mapping'
  } finally {
    loading.value = false
  }
}

const handleCancelForm = () => {
  showForm.value = false
  editingMapping.value = null
  error.value = null
}

const handleDelete = async (mapping: RemotePathMapping) => {
  const ok = await showConfirm(
    `Are you sure you want to delete the path mapping "${mapping.name || mapping.remotePath}"?\n\n` +
      `This will stop translating paths for this download client.`,
    'Delete Path Mapping',
  )
  if (!ok) return

  loading.value = true
  error.value = null

  try {
    await deleteRemotePathMapping(mapping.id)
    successMessage.value = 'Path mapping deleted successfully!'
    await loadMappings()

    setTimeout(() => {
      successMessage.value = null
    }, 3000)
  } catch (err) {
    error.value = err instanceof Error ? err.message : 'Failed to delete path mapping'
  } finally {
    loading.value = false
  }
}

const handleTestPath = async () => {
  if (!testPath.value.trim()) return

  testing.value = true
  error.value = null

  try {
    testResult.value = await translatePath({
      downloadClientId: props.downloadClientId,
      remotePath: testPath.value.trim(),
    })
  } catch (err) {
    error.value = err instanceof Error ? err.message : 'Failed to test path translation'
    testResult.value = null
  } finally {
    testing.value = false
  }
}

const formatDate = (dateString: string) => {
  const date = new Date(dateString)
  return date.toLocaleString()
}

onMounted(() => {
  loadMappings()
})
</script>

<style scoped>
.remote-path-mappings-manager {
  margin-top: 0;
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

.form-wrapper {
  margin-bottom: 1.5rem;
}

.add-button-section {
  margin-bottom: 1.5rem;
}

/* Loading State */
.loading-state {
  text-align: center;
  padding: 2rem;
  color: #999;
}

.loading-state i {
  font-size: 2rem;
  margin-bottom: 0.5rem;
  display: block;
}

.ph-spin {
  animation: spin 1s linear infinite;
}

/* @keyframes spin is centralized in src/assets/main.css */

/* Error and Success Banners */
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

.error-banner i,
.success-banner i {
  font-size: 1.25rem;
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

/* Mappings List */
.mappings-list {
  display: flex;
  flex-direction: column;
  gap: 1rem;
  margin-bottom: 1.5rem;
}

.mapping-card {
  background-color: #1a1a1a;
  border: 1px solid #444;
  border-radius: 6px;
  padding: 1rem;
  transition: all 0.2s;
}

.mapping-card:hover {
  border-color: #666;
}

.mapping-header {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  margin-bottom: 1rem;
  gap: 1rem;
}

.mapping-info {
  flex: 1;
}

.mapping-info h4 {
  margin: 0 0 0.25rem 0;
  color: #fff;
  font-size: 0.95rem;
  font-weight: 500;
}

.mapping-meta {
  color: #999;
  font-size: 0.8rem;
}

.mapping-actions {
  display: flex;
  gap: 0.5rem;
  flex-shrink: 0;
}

.mapping-paths {
  background-color: #222;
  border: 1px solid #333;
  border-radius: 6px;
  padding: 1rem;
}

.path-item {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
}

.path-label {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  color: #999;
  font-size: 0.85rem;
  font-weight: 500;
}

.path-label i {
  font-size: 1rem;
}

.path-value {
  padding: 0.5rem 0.75rem;
  background-color: #1a1a1a;
  border: 1px solid #444;
  border-radius: 6px;
  font-family: 'Courier New', Courier, monospace;
  font-size: 0.85rem;
  color: #fff;
  word-break: break-all;
}

.path-arrow {
  text-align: center;
  color: var(--brand-500);
  font-size: 1.5rem;
  margin: 0.5rem 0;
}

/* Empty State */
.empty-state {
  text-align: center;
  padding: 3rem 2rem;
  color: #999;
}

.empty-icon {
  font-size: 3.5rem;
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
  margin: 0 auto 1.5rem;
  line-height: 1.6;
  font-size: 0.9rem;
}

/* Path Tester */
.path-tester {
  margin-top: 1.5rem;
  padding: 1rem;
  background-color: #1a1a1a;
  border: 1px solid #444;
  border-radius: 6px;
}

.path-tester h4 {
  margin: 0 0 1rem 0;
  color: #fff;
  font-size: 0.95rem;
  font-weight: 500;
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

.path-tester h4 i {
  font-size: 1.1rem;
  color: var(--brand-500);
}

.tester-controls {
  display: flex;
  gap: 0.5rem;
  margin-bottom: 1rem;
}

.test-input {
  flex: 1;
  padding: 0.75rem;
  background-color: #222;
  border: 1px solid #444;
  border-radius: 6px;
  color: #fff;
  font-size: 0.9rem;
  font-family: 'Courier New', Courier, monospace;
  transition: all 0.2s;
}

.test-input:focus {
  outline: none;
  border-color: var(--brand-focus);
  box-shadow: 0 0 0 3px rgba(var(--brand-rgb), 0.1);
}

.test-input::placeholder {
  color: #666;
}

.test-result {
  margin-top: 1rem;
}

.result-success,
.result-info {
  padding: 1rem;
  border-radius: 6px;
  border: 1px solid;
}

.result-success {
  background-color: rgba(40, 167, 69, 0.1);
  border-color: rgba(40, 167, 69, 0.3);
}

.result-info {
  background-color: rgba(var(--brand-rgb), 0.1);
  border-color: rgba(var(--brand-rgb), 0.3);
}

.result-header {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  margin-bottom: 0.75rem;
}

.result-header i {
  font-size: 1.25rem;
}

.result-success .result-header {
  color: #6fbf73;
}

.result-info .result-header {
  color: var(--brand-500);
}

.result-header strong {
  font-size: 0.95rem;
}

.result-paths {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
  margin-top: 0.75rem;
}

.result-path {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
}

.result-label {
  font-size: 0.8rem;
  color: #999;
  font-weight: 500;
}

.result-path code,
.result-info code {
  display: block;
  padding: 0.5rem 0.75rem;
  background-color: rgba(0, 0, 0, 0.3);
  border-radius: 6px;
  font-family: 'Courier New', Courier, monospace;
  font-size: 0.85rem;
  color: #fff;
  word-break: break-all;
}

.result-arrow {
  text-align: center;
  color: #6fbf73;
  font-size: 1.25rem;
  margin: 0.25rem 0;
}

.result-info p {
  color: #ccc;
  margin: 0.5rem 0;
  font-size: 0.9rem;
}

/* Buttons are centralized in `src/assets/buttons.css`. Use `.btn`, `.btn-primary` and `.btn-sm` for sizing needs.
   Keep local layout helpers here if required (e.g., `.result-action` wrappers). */

.btn-primary:hover:not(:disabled) {
  background-color: var(--brand-700);
  transform: translateY(-1px);
} 

/* Button color variants centralized in `src/assets/modals.css` */

.btn-icon {
  padding: 0.5rem;
  background-color: #2a2a2a;
  border: 1px solid #444;
  color: #fff;
  min-width: auto;
}

.btn-icon:hover {
  background-color: #333;
  border-color: var(--brand-500);
  color: var(--brand-500);
}

.btn-icon.btn-danger:hover {
  background-color: #333;
  border-color: #dc3545;
  color: #dc3545;
}

.btn i {
  font-size: 1.1rem;
}

.btn-icon i {
  margin: 0;
}

/* Responsive */
@media (max-width: 768px) {
  .mapping-header {
    flex-direction: column;
    gap: 0.75rem;
  }

  .mapping-actions {
    width: 100%;
    justify-content: flex-end;
  }

  .tester-controls {
    flex-direction: column;
  }

  .test-input {
    width: 100%;
  }
}
</style>
