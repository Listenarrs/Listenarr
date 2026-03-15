<template>
  <div class="tab-content">
    <div class="indexers-tab">
      <div class="section-header">
        <h3>
          Indexers
          <PhSpinner v-if="loading" class="ph-spin small-inline-spinner" />
        </h3>
      </div>

      <LoadingState v-if="loading && indexers.length === 0" message="Loading indexers..." />

      <div v-else-if="indexers.length === 0" class="empty-state">
        <PhListMagnifyingGlass />
        <p>No indexers configured. Add Newznab or Torznab indexers to search for audiobooks.</p>
      </div>

      <div v-else class="indexers-grid">
        <div
          v-for="indexer in indexers"
          :key="indexer.id"
          class="indexer-card"
          :class="{ disabled: !indexer.isEnabled, highlight: isNew(indexer.id) }"
        >
          <div class="indexer-header">
            <div class="indexer-info">
              <h4>{{ indexer.name }}</h4>
              <img
                v-if="indexer.type === 'Torrent'"
                src="@/assets/icons/indexers/torrent.svg"
                alt="Torrent"
                class="indexer-type-icon"
                title="Torrent"
              />
              <img
                v-else-if="indexer.type === 'Usenet'"
                src="@/assets/icons/indexers/usenet.svg"
                alt="Usenet"
                class="indexer-type-icon"
                title="Usenet"
              />
              <span v-else class="indexer-type" :class="indexer.type.toLowerCase()">
                {{ indexer.implementation === 'InternetArchive' ? 'DDL' : indexer.type }}
              </span>
            </div>
            <div class="indexer-actions">
              <button
                @click="toggleIndexerFunc(indexer.id)"
                class="icon-button action-secondary action-toggle"
                :class="{ active: indexer.isEnabled }"
                :title="indexer.isEnabled ? 'Disable' : 'Enable'"
              >
                <template v-if="indexer.isEnabled">
                  <PhToggleRight />
                </template>
                <template v-else>
                  <PhToggleLeft />
                </template>
              </button>
              <button
                @click="testIndexerFunc(indexer.id)"
                class="icon-button action-secondary"
                :class="{
                  'test-success': lastTestResults[indexer.id] === 'success',
                  'test-fail': lastTestResults[indexer.id] === 'fail'
                }"
                title="Test"
                :disabled="testingIndexer === indexer.id"
              >
                <template v-if="testingIndexer === indexer.id">
                  <PhSpinner class="ph-spin" />
                </template>
                <template v-else-if="lastTestResults[indexer.id] === 'success'">
                  <PhCheckCircle />
                </template>
                <template v-else-if="lastTestResults[indexer.id] === 'fail'">
                  <PhXCircle />
                </template>
                <!-- Fall back to persisted indexer.lastTestSuccessful if available -->
                <template v-else-if="indexer.lastTestSuccessful === true">
                  <PhCheckCircle />
                </template>
                <template v-else-if="indexer.lastTestSuccessful === false">
                  <PhXCircle />
                </template>
                <template v-else>
                  <PhCheckCircle />
                </template>
              </button>
              <button @click="editIndexer(indexer)" class="icon-button action-edit" title="Edit">
                <PhPencil />
              </button>
              <button
                @click="confirmDeleteIndexer(indexer)"
                class="icon-button danger action-delete"
                title="Delete"
              >
                <PhTrash />
              </button>
            </div>
          </div>

          <div class="indexer-details">
            <div class="detail-row">
              <PhLink />
              <span class="detail-label">URL:</span>
              <span class="detail-value">{{ indexer.url }}</span>
            </div>
            <div class="detail-row">
              <PhListChecks />
              <span class="detail-label">Features:</span>
              <div class="feature-badges">
                <Pill variant="success" v-if="indexer.enableRss">RSS</Pill>
                <Pill variant="primary" v-if="indexer.enableAutomaticSearch">Automatic Search</Pill>
                <Pill variant="info" v-if="indexer.enableInteractiveSearch">Interactive Search</Pill>
              </div>
            </div>
            <div class="detail-row" v-if="indexer.lastTestedAt">
              <PhClock />
              <span class="detail-label">Last Tested:</span>
              <span
                class="detail-value"
                :class="{
                  success: indexer.lastTestSuccessful,
                  error: indexer.lastTestSuccessful === false,
                }"
              >
                {{ formatDate(indexer.lastTestedAt) }}
                <template v-if="indexer.lastTestSuccessful">
                  <PhCheckCircle class="success" />
                </template>
                <template v-else-if="indexer.lastTestSuccessful === false">
                  <PhXCircle class="error" />
                </template>
              </span>
            </div>
            <div class="detail-row error-row" v-if="indexer.lastTestError">
              <PhWarning />
              <span class="detail-value error">{{ indexer.lastTestError }}</span>
            </div>
          </div>
        </div>
      </div>

      <!-- Indexer Form Modal -->
      <IndexerFormModal
        :visible="showIndexerForm"
        :editing-indexer="editingIndexer"
        @close="showIndexerForm = false; editingIndexer = null"
        @saved="loadIndexers()"
      />

      <Modal :visible="showProwlarrModal" size="md" @close="closeProwlarrModal">
        <template #header>
          <ModalHeader title="Import from Prowlarr" :icon="PhDownloadSimple" @close="closeProwlarrModal" />
        </template>
        <template #default>
          <ModalForm :submitting="importingProwlarr" @submit="importFromProwlarr">
            <ModalBody>
              <FormSection title="Connection" :icon="PhGlobe">
                <FormRow
                  label="Prowlarr URL / IP"
                  labelFor="prowlarr-url"
                  help="Enter hostname/IP with optional scheme and optional :port (for example: 192.168.1.10:4545 or http://localhost)."
                >
                  <input
                    id="prowlarr-url"
                    v-model="prowlarrUrl"
                    type="text"
                    class="form-input"
                    placeholder="192.168.1.10:4545"
                    autocomplete="off"
                  />
                </FormRow>

                <FormRow label="Port (optional)" labelFor="prowlarr-port">
                  <input
                    id="prowlarr-port"
                    v-model="prowlarrPort"
                    type="number"
                    class="form-input"
                    min="1"
                    max="65535"
                    placeholder="9696"
                  />
                </FormRow>

                <FormRow label="API Key" labelFor="prowlarr-key" help="Find this in Prowlarr Settings → General">
                  <PasswordInput
                    id="prowlarr-key"
                    v-model="prowlarrApiKey"
                    autocomplete="off"
                    class="form-input"
                    placeholder="Prowlarr API Key"
                  />
                </FormRow>
              </FormSection>

              <div v-if="prowlarrSummary" class="prowlarr-summary">
                Imported {{ prowlarrSummary.addedCount }} indexer(s), skipped {{
                  prowlarrSummary.skippedCount
                }}.
              </div>
            </ModalBody>
          </ModalForm>
        </template>
        <template #footer>
          <ModalFooter
            :showCancel="true"
            :showSave="true"
            :saving="importingProwlarr"
            saveLabel="Import"
            saveLabelLoading="Importing..."
            @cancel="closeProwlarrModal"
            @save="importFromProwlarr"
          />
        </template>
      </Modal>

      <!-- Delete Indexer Confirmation Modal (shared) -->
      <DeleteConfirmationModal :visible="!!indexerToDelete" title="Delete Indexer" @close="indexerToDelete = null" @confirm="executeDeleteIndexer">
        <template v-slot>
          <p>Are you sure you want to delete the indexer <strong>{{ indexerToDelete?.name }}</strong>?</p>
          <p>This action cannot be undone.</p>
        </template>
      </DeleteConfirmationModal>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, onMounted, onUnmounted, nextTick } from 'vue'
import {
  PhListMagnifyingGlass,
  PhToggleRight,
  PhToggleLeft,
  PhGlobe,
  PhCheckCircle,
  PhXCircle,
  PhPencil,
  PhTrash,
  PhLink,
  PhListChecks,
  PhClock,
  PhWarning,
  PhSpinner,
  PhDownloadSimple,
} from '@phosphor-icons/vue'
import DeleteConfirmationModal from '@/components/feedback/DeleteConfirmationModal.vue'
import IndexerFormModal from '@/components/settings/IndexerFormModal.vue'
import FormSection from '@/components/settings/FormSection.vue'
import FormRow from '@/components/settings/FormRow.vue'
import PasswordInput from '@/components/form/PasswordInput.vue'
import { useToast } from '@/services/toastService'
import { errorTracking } from '@/services/errorTracking'
import { Pill, LoadingState } from '@/components/base'
import { Modal, ModalHeader, ModalFooter, ModalBody, ModalForm } from '@/components/feedback'
import type { Indexer } from '@/types'
import {
  getIndexers,
  toggleIndexer as apiToggleIndexer,
  testIndexer as apiTestIndexer,
  deleteIndexer,
  importProwlarrIndexers,
} from '@/services/api'
import { signalRService } from '@/services/signalr'

// State
const toast = useToast()
const indexers = ref<Indexer[]>([])
const loading = ref(false)
const showIndexerForm = ref(false)
const editingIndexer = ref<Indexer | null>(null)
const indexerToDelete = ref<Indexer | null>(null)
const testingIndexer = ref<number | null>(null)
// Per-indexer ephemeral test results: 'success' | 'fail' | undefined
const lastTestResults = reactive<Record<number, 'success' | 'fail' | undefined>>({})

// Prowlarr import state
const prowlarrUrl = ref('')
const prowlarrPort = ref('')
const prowlarrApiKey = ref('')
const importingProwlarr = ref(false)
const prowlarrSummary = ref<{ addedCount: number; skippedCount: number } | null>(null)
const showProwlarrModal = ref(false)

// Functions
const formatApiError = (error: unknown): string => {
  const err = error as { response?: { data?: unknown }; message?: string }
  const resp = err.response as Record<string, unknown> | undefined
  const data = resp?.data as unknown
  if (data) {
    if (typeof data === 'string') return data
    if ((data as Record<string, unknown>)['message']) return String((data as Record<string, unknown>)['message'])
    if ((data as Record<string, unknown>)['error']) return String((data as Record<string, unknown>)['error'])
    if ((data as Record<string, unknown>)['title']) return String((data as Record<string, unknown>)['title'])
  }
  if (err?.message) return err.message
  return 'An unknown error occurred'
}

const formatDate = (dateString: string | undefined): string => {
  if (!dateString) return 'Never'
  const date = new Date(dateString)
  return date.toLocaleString()
}

const loadIndexers = async () => {
  loading.value = true
  try {
    indexers.value = await getIndexers()
  } catch (error) {
    errorTracking.captureException(error as Error, {
      component: 'IndexersTab',
      operation: 'loadIndexers',
    })
    const errorMessage = formatApiError(error)
    toast.error('Load failed', errorMessage)
  } finally {
    loading.value = false
  }
}

const toggleIndexerFunc = async (id: number) => {
  try {
    const updatedIndexer = await apiToggleIndexer(id)
    const index = indexers.value.findIndex((i) => i.id === id)
    if (index !== -1) {
      indexers.value[index] = updatedIndexer
    }
    toast.success(
      'Indexer',
      `Indexer ${updatedIndexer.isEnabled ? 'enabled' : 'disabled'} successfully`,
    )
  } catch (error) {
    errorTracking.captureException(error as Error, {
      component: 'IndexersTab',
      operation: 'toggleIndexer',
    })
    const errorMessage = formatApiError(error)
    toast.error('Toggle failed', errorMessage)
  }
}

const testIndexerFunc = async (id: number) => {
  testingIndexer.value = id
  try {
    const result = await apiTestIndexer(id)
    if (result.success) {
      toast.success('Indexer test', `Indexer tested successfully: ${result.message}`)
      const index = indexers.value.findIndex((i) => i.id === id)
      if (index !== -1) {
        indexers.value[index] = result.indexer
      }
      // mark success (persist until next test)
      lastTestResults[id] = 'success'
      console.debug('IndexersTab: lastTestResults set', id, lastTestResults[id])
      await nextTick()
    } else {
      const errorMessage = formatApiError({ response: { data: result.error || result.message } })
      toast.error('Indexer test failed', errorMessage)
      const index = indexers.value.findIndex((i) => i.id === id)
      if (index !== -1) {
        indexers.value[index] = result.indexer
      }
      // mark failure (persist until next test)
      lastTestResults[id] = 'fail'
      console.debug('IndexersTab: lastTestResults set', id, lastTestResults[id])
      await nextTick()
    }
  } catch (error) {
    errorTracking.captureException(error as Error, {
      component: 'IndexersTab',
      operation: 'testIndexer',
    })
    const errorMessage = formatApiError(error)
    toast.error('Indexer test failed', errorMessage)
    lastTestResults[id] = 'fail'
    console.debug('IndexersTab: lastTestResults set', id, lastTestResults[id])
    await nextTick()
  } finally {
    testingIndexer.value = null
  }
}

const editIndexer = (indexer: Indexer) => {
  editingIndexer.value = indexer
  showIndexerForm.value = true
}

const confirmDeleteIndexer = (indexer: Indexer) => {
  indexerToDelete.value = indexer
}

const executeDeleteIndexer = async () => {
  if (!indexerToDelete.value) return

  try {
    await deleteIndexer(indexerToDelete.value.id)
    indexers.value = indexers.value.filter((i) => i.id !== indexerToDelete.value!.id)
    toast.success('Indexer', 'Indexer deleted successfully')
  } catch (error) {
    errorTracking.captureException(error as Error, {
      component: 'IndexersTab',
      operation: 'deleteIndexer',
    })
    const errorMessage = formatApiError(error)
    toast.error('Delete failed', errorMessage)
  } finally {
    indexerToDelete.value = null
  }
}

const openProwlarrModal = () => {
  showProwlarrModal.value = true
}

const closeProwlarrModal = () => {
  showProwlarrModal.value = false
  prowlarrSummary.value = null
}

const importFromProwlarr = async () => {
  const url = prowlarrUrl.value.trim()
  const apiKey = prowlarrApiKey.value.trim()
  const portRaw = prowlarrPort.value.trim()

  if (!url) {
    toast.warning('Prowlarr', 'Please enter a Prowlarr URL or IP')
    return
  }

  if (!apiKey) {
    toast.warning('Prowlarr', 'Please enter your Prowlarr API key')
    return
  }

  let portValue: number | undefined
  const parsed = Number(portRaw)
  if (portRaw.length > 0) {
    if (!Number.isInteger(parsed) || parsed <= 0 || parsed > 65535) {
      toast.warning('Prowlarr', 'Please enter a valid port number (1-65535)')
      return
    }
    portValue = parsed
  }

  importingProwlarr.value = true
  prowlarrSummary.value = null

  try {
    const result = await importProwlarrIndexers({
      url,
      port: portValue,
      apiKey,
    })

    prowlarrSummary.value = {
      addedCount: result.addedCount,
      skippedCount: result.skippedCount,
    }

    await loadIndexers()
    toast.success('Prowlarr', `Imported ${result.addedCount} indexer(s)`)
  } catch (error) {
    errorTracking.captureException(error as Error, {
      component: 'IndexersTab',
      operation: 'importProwlarrIndexers',
    })
    const errorMessage = formatApiError(error)
    toast.error('Prowlarr import failed', errorMessage)
  } finally {
    importingProwlarr.value = false
  }
}

// Lifecycle
const newlyAddedIds = ref<Set<number>>(new Set())

function isNew(id: number) {
  return newlyAddedIds.value.has(id)
}

onMounted(() => {
  loadIndexers()
  // Subscribe to IndexersUpdated messages so external changes (eg. Prowlarr sync) refresh the list
  const unsub = signalRService.onIndexersUpdated((payload) => {
    const payloadIndexers = Array.isArray(payload?.indexers)
      ? payload.indexers.filter(
          (item: unknown): item is { id: number; name: string } =>
            typeof item === 'object' &&
            item !== null &&
            typeof (item as { id?: unknown }).id === 'number' &&
            typeof (item as { name?: unknown }).name === 'string',
        )
      : []

    // If the payload contains created indexer details, highlight and list them
    if (payloadIndexers.length > 0) {
      const names = payloadIndexers.map((i) => i.name).join(', ')
      const ids = payloadIndexers.map((i) => i.id)

      ids.forEach((id: number) => newlyAddedIds.value.add(id))

      // Refresh list and show names
      loadIndexers()
      toast.success('Indexers', `Imported ${payloadIndexers.length} indexer(s): ${names}`)

      // Clear highlights after 10 seconds
      setTimeout(() => {
        newlyAddedIds.value.clear()
      }, 10000)

      return
    }

    // Fallback: keep previous behavior for payload with counts only
    loadIndexers()
    if (payload?.created) {
      toast.success('Indexers', `Imported ${payload.created} indexer(s) successfully`)
    }
  })

  // Cleanup on unmount
  onUnmounted(() => unsub())
})

const openAddIndexer = () => {
  editingIndexer.value = null
  showIndexerForm.value = true
}

defineExpose({ openAddIndexer, openProwlarrImport: openProwlarrModal })
</script>

<style scoped>
.tab-content {
  animation: fadeIn 0.2s ease;
}

/* @keyframes fadeIn is centralized in src/assets/animations.css */

.indexers-tab {
  width: 100%;
}

.section-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 2rem;
  padding-bottom: 1rem;
  border-bottom: 1px solid rgba(255, 255, 255, 0.08);
}

.section-header h3 {
  margin: 0;
  color: #fff;
  font-size: 1.5rem;
  font-weight: 500;
}

.prowlarr-summary {
  color: var(--text-secondary);
  font-size: 0.9rem;
}

.add-button {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  padding: 0.5rem 1rem;
  background: var(--primary);
  color: white;
  border: none;
  border-radius: var(--border-radius);
  cursor: pointer;
  font-size: 0.9rem;
  transition: all 0.2s;
}

.add-button:hover {
  background: var(--primary-hover);
  transform: translateY(-1px);
}

.empty-state {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  padding: 3rem;
  text-align: center;
  color: var(--text-secondary);
  background: var(--background-secondary);
  border-radius: var(--border-radius);
  border: 2px dashed var(--border);
}

.empty-state svg {
  font-size: 3rem;
  margin-bottom: 1rem;
  opacity: 0.5;
}

.indexers-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(450px, 1fr));
  gap: 1rem;
}

.indexer-card {
  background: var(--background-secondary);
  border: 1px solid var(--border);
  border-radius: var(--border-radius);
  transition: all 0.2s;
}

.indexer-card:hover {
  transform: translateY(-2px);
  box-shadow: 0 4px 12px rgba(0, 0, 0, 0.1);
}

.indexer-card.highlight {
  border-color: var(--success);
  box-shadow: 0 6px 20px rgba(34, 197, 94, 0.12);
  animation: highlightPulse 1.2s ease-in-out 2;
}

@keyframes highlightPulse {
  0% { transform: translateY(0); }
  50% { transform: translateY(-4px); }
  100% { transform: translateY(0); }
}

.indexer-card.disabled {
  opacity: 0.6;
}

.indexer-header {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  margin-bottom: 1rem;
  padding-bottom: 1rem;
  border-bottom: 1px solid var(--border);
}

/* Align indexer info inline (name + type) */
.indexer-info {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  min-width: 0;
}

.indexer-info h4 {
  margin: 0;
  font-size: 1.1rem;
  color: var(--text-primary);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.indexer-type {
  display: inline-block;
  padding: 0.25rem 0.5rem;
  border-radius: 4px;
  font-size: 0.75rem;
  font-weight: 500;
  text-transform: uppercase;
}

.indexer-type-icon {
  width: 20px;
  height: 20px;
  flex-shrink: 0;
}

.indexer-type.torrent {
  background: #10b981;
  color: white;
}

.indexer-type.usenet {
  background: #3b82f6;
  color: white;
}

.indexer-type.ddl {
  background: #8b5cf6;
  color: white;
}

.indexer-actions {
  display: flex;
  gap: 0.5rem;
}

/* Use centralized .icon-button in src/assets/buttons.css for consistent icon buttons */

.indexer-details {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
}

.detail-row {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  font-size: 0.9rem;
}

/* Icon sizing & color consistency */
.detail-row svg,
.detail-row .ph-icon {
  width: 16px;
  height: 16px;
  flex-shrink: 0;
  color: var(--text-secondary);
}

.indexer-header svg,
.indexer-header .ph-icon {
  width: 18px;
  height: 18px;
  flex-shrink: 0;
  vertical-align: middle;
}

.detail-label {
  font-weight: 500;
  color: var(--text-secondary);
  min-width: 90px;
}

.detail-value {
  color: var(--text-primary);
  word-break: break-all;
  display: flex;
  align-items: center;
  gap: 0.25rem;
}

.detail-value.success {
  color: var(--success);
}

.detail-value.error {
  color: var(--error);
}

.detail-value svg.success {
  color: var(--success);
}

.detail-value svg.error {
  color: var(--error);
}

.feature-badges {
  display: flex;
  flex-wrap: wrap;
  gap: 0.5rem;
}

/* Badge styles - Now using Pill component from @/components/base */

.section-header .small-inline-spinner {
  margin-left: 0.5rem;
  width: 18px;
  height: 18px;
}

.error-row {
  background: rgba(239, 68, 68, 0.1);
  padding: 0.5rem;
  border-radius: 4px;
  border: 1px solid var(--error);
}



/* Section Header */
.section-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 2rem;
  padding-bottom: 1rem;
  border-bottom: 1px solid rgba(255, 255, 255, 0.08);
}

.section-header h3 {
  margin: 0;
  color: #fff;
  font-size: 1.5rem;
  font-weight: 500;
}

/* Add Button */
.add-button {
  padding: 0.75rem 1.5rem;
  background: #1e88e5;
  color: white;
  border: none;
  border-radius: 6px;
  cursor: pointer;
  transition: all 0.2s ease;
  display: flex;
  align-items: center;
  gap: 0.5rem;
  font-weight: 500;
  font-size: 0.95rem;
  box-shadow: 0 2px 8px rgba(30, 136, 229, 0.3);
}

.add-button:hover {
  background: #1976d2;
  transform: translateY(-1px);
  box-shadow: 0 4px 12px rgba(30, 136, 229, 0.4);
}

/* Empty State */
.empty-state {
  text-align: center;
  padding: 4rem 2rem;
  color: #868e96;
}

.empty-state .empty-icon {
  font-size: 4rem;
  color: #868e96;
  margin-bottom: 1rem;
  width: 4rem;
  height: 4rem;
}

.empty-state h3 {
  margin: 1rem 0 0.5rem 0;
  color: #fff;
  font-size: 1.5rem;
  font-weight: 500;
}

.empty-state p {
  margin: 0.5rem 0;
  font-size: 1.05rem;
  line-height: 1.6;
  color: #adb5bd;
}

.add-button-large {
  margin-top: 1.5rem;
  padding: 1rem 2rem;
  background: #1e88e5;
  color: white;
  border: none;
  border-radius: 6px;
  cursor: pointer;
  transition: all 0.2s ease;
  display: inline-flex;
  align-items: center;
  gap: 0.75rem;
  font-weight: 500;
  font-size: 1rem;
  box-shadow: 0 4px 12px rgba(30, 136, 229, 0.3);
}

.add-button-large:hover {
  background: #1976d2;
  transform: translateY(-2px);
  box-shadow: 0 6px 16px rgba(30, 136, 229, 0.4);
}

/* Indexer Grid */
.indexers-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(400px, 1fr));
  gap: 1.5rem;
  margin-top: 1.5rem;
}

/* Indexer Card */
.indexer-card {
  background-color: #2a2a2a;
  border: 1px solid rgba(255, 255, 255, 0.08);
  border-radius: 6px;
  transition: all 0.2s ease;
}

.indexer-card:hover {
  border-color: rgba(77, 171, 247, 0.3);
  transform: translateY(-1px);
  box-shadow: 0 4px 12px rgba(77, 171, 247, 0.15);
}

.indexer-card.disabled {
  opacity: 0.5;
  filter: grayscale(50%);
}

.indexer-header {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  padding: 1.5rem;
  background-color: rgba(0, 0, 0, 0.2);
  border-bottom: 1px solid rgba(255, 255, 255, 0.08);
}

.indexer-info h4 {
  margin: 0 0 0.5rem 0;
  color: #fff;
  font-size: 1.1rem;
  font-weight: 500;
}

.indexer-type {
  display: inline-block;
  padding: 0.3rem 0.75rem;
  border-radius: 6px;
  font-size: 0.75rem;
  font-weight: 500;
  text-transform: uppercase;
  letter-spacing: 0.5px;
}

.indexer-type.torrent {
  background-color: rgba(76, 175, 80, 0.15);
  color: #51cf66;
  border: 1px solid rgba(76, 175, 80, 0.3);
}

.indexer-type.usenet {
  background-color: rgba(33, 150, 243, 0.15);
  color: #4dabf7;
  border: 1px solid rgba(33, 150, 243, 0.3);
}

.indexer-type.ddl {
  background-color: rgba(155, 89, 182, 0.15);
  color: #9b59b6;
  border: 1px solid rgba(155, 89, 182, 0.3);
}

.indexer-actions {
  display: flex;
  gap: 0.5rem;
  margin-left: 1rem;
}

/* Use centralized .icon-button in src/assets/buttons.css for consistent icon buttons */

.indexer-details {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
  padding: 1.5rem;
}

.detail-row {
  display: flex;
  align-items: center;
  gap: 0.65rem;
  font-size: 0.9rem;
}

.detail-row i {
  color: #4dabf7;
  font-size: 1rem;
  flex-shrink: 0;
}

.detail-label {
  color: #868e96;
  min-width: 100px;
}

.detail-value {
  color: #adb5bd;
  word-break: break-all;
}

.detail-value.success {
  color: #51cf66;
}

.detail-value.error {
  color: #ff6b6b;
}

.feature-badges {
  display: flex;
  flex-wrap: wrap;
  gap: 0.5rem;
}

/* Badge styles - Now using Pill component from @/components/base */

/* Mobile Responsive */
@media (max-width: 768px) {
  .section-header {
    flex-direction: column;
    align-items: flex-start;
    gap: 1rem;
  }

  .add-button {
    width: 100%;
    justify-content: center;
  }

  .indexers-grid {
    grid-template-columns: 1fr;
  }

  .indexer-header {
    flex-direction: column;
    gap: 1rem;
  }

  .indexer-actions {
    width: 100%;
    justify-content: flex-start;
    margin-left: 0;
  }

  .detail-row {
    flex-direction: column;
    align-items: flex-start;
    gap: 0.25rem;
  }

  .detail-label {
    min-width: auto;
  }
}
</style>
