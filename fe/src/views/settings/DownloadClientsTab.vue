<template>
  <div class="tab-content">
    <div class="download-clients-tab">
      <div class="section-header">
        <h3>
          Download Clients
          <PhSpinner v-if="configStore.isLoading" class="ph-spin small-inline-spinner" />
        </h3>
      </div>

      <LoadingState v-if="configStore.isLoading && configStore.downloadClientConfigurations.length === 0" message="Loading download clients..." />

      <div v-else-if="configStore.downloadClientConfigurations.length === 0" class="empty-state">
        <PhDownloadSimple />
        <p>
          No download clients configured. Add qBittorrent, Transmission, SABnzbd, or NZBGet to
          download audiobooks.
        </p>
      </div>

      <div v-else class="indexers-grid">
        <div
          v-for="client in configStore.downloadClientConfigurations"
          :key="client.id"
          class="indexer-card"
          :class="{ disabled: !client.isEnabled }"
        >
          <div class="indexer-header">
            <div class="indexer-info">
              <h4>{{ client.name }}</h4>
              <img 
                v-if="client.type === 'qbittorrent'"
                src="@/assets/icons/clients/qbittorrent.svg" 
                alt="qBittorrent" 
                class="client-type-icon" 
                title="qBittorrent"
              />
              <img 
                v-else-if="client.type === 'transmission'"
                src="@/assets/icons/clients/transmission.svg" 
                alt="Transmission" 
                class="client-type-icon" 
                title="Transmission"
              />
              <img 
                v-else-if="client.type === 'sabnzbd'"
                src="@/assets/icons/clients/sabnzbd.svg" 
                alt="SABnzbd" 
                class="client-type-icon" 
                title="SABnzbd"
              />
              <img 
                v-else-if="client.type === 'nzbget'"
                src="@/assets/icons/clients/nzbget.svg" 
                alt="NZBGet" 
                class="client-type-icon" 
                title="NZBGet"
              />
            </div>
            <div class="indexer-actions">
              <button
                @click="toggleDownloadClientFunc(client)"
                class="icon-button action-secondary action-toggle"
                :class="{ active: client.isEnabled }"
                :title="client.isEnabled ? 'Disable' : 'Enable'"
              >
                <template v-if="client.isEnabled">
                  <PhToggleRight />
                </template>
                <template v-else>
                  <PhToggleLeft />
                </template>
              </button>
              <button
                @click="testClient(client)"
                class="icon-button action-secondary"
                :class="{
                  'test-success': lastClientTestResults[client.id] === 'success',
                  'test-fail': lastClientTestResults[client.id] === 'fail'
                }"
                title="Test"
                :disabled="testingClient === client.id"
              >
                <template v-if="testingClient === client.id">
                  <PhSpinner class="ph-spin" />
                </template>
                <template v-else-if="lastClientTestResults[client.id] === 'success'">
                  <PhCheckCircle />
                </template>
                <template v-else-if="lastClientTestResults[client.id] === 'fail'">
                  <PhXCircle />
                </template>                <!-- Fall back to persisted client.lastTestSuccessful if available -->
                <template v-else-if="client.lastTestSuccessful === true">
                  <PhCheckCircle />
                </template>
                <template v-else-if="client.lastTestSuccessful === false">
                  <PhXCircle />
                </template>                <template v-else>
                  <PhCheckCircle />
                </template>
              </button>
              <button @click="editClientConfig(client)" class="icon-button action-edit" title="Edit">
                <PhPencil />
              </button>
              <button
                @click="confirmDeleteClient(client)"
                class="icon-button danger"
                title="Delete"
              >
                <PhTrash />
              </button>
            </div>
          </div>

          <div class="indexer-details">
            <div class="detail-row">
              <PhLink />
              <span class="detail-label">Host:</span>
              <span class="detail-value">{{ client.host }}:{{ client.port }}</span>
            </div>
            <div class="detail-row">
              <PhShieldCheck />
              <span class="detail-label">Security:</span>
              <div class="feature-badges">
                <Pill variant="info" v-if="client.useSSL">
                  <PhLock />
                  SSL
                </Pill>
                <Pill variant="subtle" v-else>
                  <PhLockOpen />
                  No SSL
                </Pill>
              </div>
            </div>
            <div class="detail-row">
              <PhFolder />
              <span class="detail-label">Download Path:</span>
              <span class="detail-value">{{ client.downloadPath || '(client local)' }}</span>
            </div>
            <div class="detail-row">
              <PhLinkSimple />
              <span class="detail-label">Mappings:</span>
              <div class="feature-badges">
                <Pill
                  variant="primary"
                  v-for="m in getMappingsForClient(client)"
                  :key="m.id"
                >
                  <PhLink />
                  {{ m.name || m.remotePath }}
                </Pill>
                <span v-if="getMappingsForClient(client).length === 0" class="detail-value"
                  >(none)</span
                >
              </div>
            </div>
            <div class="detail-row">
              <PhCheckCircle />
              <span class="detail-label">Status:</span>
              <span
                class="detail-value"
                :class="{ success: client.isEnabled, error: !client.isEnabled }"
              >
                {{ client.isEnabled ? 'Enabled' : 'Disabled' }}
              </span>
            </div>
          </div>
        </div>
      </div>

      <!-- Remote Path Mappings Section -->
      <div class="section-header" style="margin-top: 2rem">
        <h3>Remote Path Mappings</h3>
      </div>

      <div v-if="remotePathMappings.length === 0" class="empty-state">
        <PhLinkSimple />
        <p>
          No remote path mappings configured. Add a mapping to translate client remote paths to
          local paths the server can access.
        </p>
      </div>

      <div v-else class="config-list">
        <div v-for="mapping in remotePathMappings" :key="mapping.id" class="config-card">
          <div class="config-info">
            <h4>{{ mapping.name || mapping.remotePath }}</h4>
            <div class="detail-row">
              <PhBrowser />
              <span class="detail-label">Remote Path:</span>
              <span class="detail-value">{{ mapping.remotePath }}</span>
            </div>
            <div class="detail-row">
              <PhFolder />
              <span class="detail-label">Local Path:</span>
              <span class="detail-value">{{ mapping.localPath }}</span>
            </div>
          </div>
          <div class="config-actions">
            <button @click="editMapping(mapping)" class="edit-button" title="Edit">
              <PhPencil />
            </button>
            <button @click="confirmDeleteMapping(mapping)" class="delete-button" title="Delete">
              <PhTrash />
            </button>
          </div>
        </div>
      </div>

      <!-- Download Client Form Modal -->
      <DownloadClientFormModal
        :visible="showClientForm"
        :editing-client="editingClient"
        @close="showClientForm = false; editingClient = null"
        @saved="configStore.loadDownloadClientConfigurations()"
        @delete="executeDeleteClient"
      />

      <!-- Delete Client Confirmation Modal (shared) -->
      <DeleteConfirmationModal :visible="!!clientToDelete" title="Delete Download Client" @close="clientToDelete = null" @confirm="executeDeleteClient">
        <template v-slot>
          <p>Are you sure you want to delete the download client <strong>{{ clientToDelete?.name }}</strong>?</p>
          <p>This action cannot be undone.</p>
        </template>
      </DeleteConfirmationModal>

      <!-- Remote Path Mapping Modal -->
      <RemotePathMappingModal
        :visible="showMappingForm"
        :editing-mapping="mappingToEdit"
        :download-clients="configStore.downloadClientConfigurations"
        @close="closeMappingForm"
        @save="handleSaveMapping"
      />

      <!-- Delete Remote Path Mapping Confirmation (shared) -->
      <DeleteConfirmationModal :visible="!!mappingToDelete" title="Delete Remote Path Mapping" @close="mappingToDelete = null" @confirm="executeDeleteMapping">
        <template v-slot>
          <p>
            Are you sure you want to delete the remote path mapping
            <strong>{{ mappingToDelete?.name || mappingToDelete?.remotePath }}</strong>?
          </p>
          <p>This action cannot be undone.</p>
        </template>
      </DeleteConfirmationModal>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, onMounted, nextTick } from 'vue'
import { useConfigurationStore } from '@/stores/configuration'
import { useToast } from '@/services/toastService'
import { errorTracking } from '@/services/errorTracking'
import type { DownloadClientConfiguration, RemotePathMapping } from '@/types'
import FolderBrowser from '@/components/ui/FolderBrowser.vue'
import DownloadClientFormModal from '@/components/domain/download/DownloadClientFormModal.vue'
import { Modal, ModalHeader, ModalFooter, ModalForm, ModalBody } from '@/components/feedback' 
import DeleteConfirmationModal from '@/components/feedback/DeleteConfirmationModal.vue'
import RemotePathMappingModal from '@/components/feedback/RemotePathMappingModal.vue'
import FormSection from '@/components/settings/FormSection.vue'
import { Pill, LoadingState } from '@/components/base'
import {
  PhDownloadSimple,
  PhToggleRight,
  PhToggleLeft,
  PhSpinner,
  PhCheckCircle,
  PhXCircle,
  PhPencil,
  PhTrash,
  PhLink,
  PhShieldCheck,
  PhLock,
  PhLockOpen,
  PhFolder,
  PhLinkSimple,
  PhBrowser,
  PhWarningCircle,
  PhX,
  PhCheck,
} from '@phosphor-icons/vue'
import {
  getRemotePathMappings,
  createRemotePathMapping,
  updateRemotePathMapping,
  deleteRemotePathMapping,
  testDownloadClient as apiTestDownloadClient,
} from '@/services/api'

// State
const configStore = useConfigurationStore()
const toast = useToast()
const showClientForm = ref(false)
const editingClient = ref<DownloadClientConfiguration | null>(null)
const clientToDelete = ref<DownloadClientConfiguration | null>(null)
const testingClient = ref<string | null>(null)
// Per-client ephemeral test results: 'success' | 'fail' | undefined
const lastClientTestResults = reactive<Record<string, 'success' | 'fail' | undefined>>({})
const remotePathMappings = ref<RemotePathMapping[]>([])
const showMappingForm = ref(false)
const mappingToEdit = ref<RemotePathMapping | null>(null)
const mappingToDelete = ref<RemotePathMapping | null>(null)

// Functions
const formatApiError = (error: unknown): string => {
  const e = error as { response?: { data?: unknown }; message?: unknown } | undefined
  const data = e?.response?.data
  if (data) {
    if (typeof data === 'string') return data
    if (typeof data === 'object' && data !== null) {
      const obj = data as Record<string, unknown>
      if (typeof obj.message === 'string') return obj.message
      if (typeof obj.error === 'string') return obj.error
      if (typeof obj.title === 'string') return obj.title
    }
  }
  if (typeof e?.message === 'string') return e!.message as string
  return 'An unknown error occurred'
}

const getClientTypeClass = (type: string): string => {
  const typeMap: Record<string, string> = {
    qbittorrent: 'torrent',
    transmission: 'torrent',
    sabnzbd: 'usenet',
    nzbget: 'usenet',
  }
  return typeMap[type.toLowerCase()] || 'torrent'
}

const getMappingsForClient = (client: DownloadClientConfiguration): RemotePathMapping[] => {
  const assignedIds = new Set<number>()
  try {
    const s = (client as unknown as Record<string, unknown>)?.settings as
      | Record<string, unknown>
      | undefined
    const raw = s?.remotePathMappingIds ?? s?.RemotePathMappingIds
    if (Array.isArray(raw)) {
      for (const v of raw) {
        const n = Number(v)
        if (!Number.isNaN(n)) assignedIds.add(n)
      }
    }
  } catch {
    // ignore malformed settings
  }

  return remotePathMappings.value.filter((m) => assignedIds.has(m.id))
}

const toggleDownloadClientFunc = async (client: DownloadClientConfiguration) => {
  try {
    const updatedClient = { ...client, isEnabled: !client.isEnabled }
    await configStore.saveDownloadClientConfiguration(updatedClient)
    const idx = configStore.downloadClientConfigurations.findIndex((c) => c.id === client.id)
    if (idx !== -1) {
      configStore.downloadClientConfigurations[idx] = updatedClient
    }
    toast.success(
      'Download client',
      `${client.name} ${updatedClient.isEnabled ? 'enabled' : 'disabled'} successfully`,
    )
  } catch (error) {
    errorTracking.captureException(error as Error, {
      component: 'DownloadClientsTab',
      operation: 'toggleDownloadClient',
    })
    const errorMessage = formatApiError(error)
    toast.error('Toggle failed', errorMessage)
  }
}

const testClient = async (client: DownloadClientConfiguration) => {
  testingClient.value = client.id
  try {
    // Use the latest saved configuration from the store (DB values)
    const storedClient =
      configStore.downloadClientConfigurations.find((c) => c.id === client.id) || client

    const result = await apiTestDownloadClient(storedClient)
    if (result.success) {
      toast.success(
        'Download client test',
        `Download client tested successfully: ${result.message}`,
      )
      const index = configStore.downloadClientConfigurations.findIndex((c) => c.id === client.id)
      if (index !== -1 && result.client) {
        configStore.downloadClientConfigurations[index] = result.client
      }
      lastClientTestResults[client.id] = 'success'
      console.debug('DownloadClientsTab: lastClientTestResults set', client.id, lastClientTestResults[client.id])
      await nextTick()
    } else {
      const errorMessage = formatApiError({ response: { data: result.message } })
      toast.error('Download client test failed', errorMessage)
      const index = configStore.downloadClientConfigurations.findIndex((c) => c.id === client.id)
      if (index !== -1 && result.client) {
        configStore.downloadClientConfigurations[index] = result.client
      }
      lastClientTestResults[client.id] = 'fail'
      console.debug('DownloadClientsTab: lastClientTestResults set', client.id, lastClientTestResults[client.id])
      await nextTick()
    }
  } catch (error) {
    errorTracking.captureException(error as Error, {
      component: 'DownloadClientsTab',
      operation: 'testDownloadClient',
    })
    const errorMessage = formatApiError(error)
    toast.error('Download client test failed', errorMessage)
    lastClientTestResults[client.id] = 'fail'
    console.debug('DownloadClientsTab: lastClientTestResults set', client.id, lastClientTestResults[client.id])
    await nextTick()
  } finally {
    testingClient.value = null
  }
}

const editClientConfig = (client: DownloadClientConfiguration) => {
  editingClient.value = client
  showClientForm.value = true
}

const confirmDeleteClient = (client: DownloadClientConfiguration) => {
  clientToDelete.value = client
}

const executeDeleteClient = async (id?: string) => {
  const clientId = id || clientToDelete.value?.id
  if (!clientId) return

  try {
    await configStore.deleteDownloadClientConfiguration(clientId)
    toast.success('Download client', 'Download client deleted successfully')
  } catch (error) {
    errorTracking.captureException(error as Error, {
      component: 'DownloadClientsTab',
      operation: 'deleteDownloadClient',
    })
    const errorMessage = formatApiError(error)
    toast.error('Delete failed', errorMessage)
  } finally {
    clientToDelete.value = null
  }
}

// Remote Path Mappings functions
const loadRemotePathMappings = async () => {
  try {
    remotePathMappings.value = await getRemotePathMappings()
  } catch (error) {
    errorTracking.captureException(error as Error, {
      component: 'DownloadClientsTab',
      operation: 'loadRemotePathMappings',
    })
  }
}

const openMappingForm = (mapping?: RemotePathMapping) => {
  mappingToEdit.value = mapping || null
  showMappingForm.value = true
}

const closeMappingForm = () => {
  showMappingForm.value = false
  mappingToEdit.value = null
}

const handleSaveMapping = async (mappingData: Omit<RemotePathMapping, 'id' | 'createdAt' | 'updatedAt'>) => {
  try {
    if (mappingToEdit.value && mappingToEdit.value.id) {
      const updated = await updateRemotePathMapping(mappingToEdit.value.id, mappingData)
      const idx = remotePathMappings.value.findIndex((m) => m.id === updated.id)
      if (idx !== -1) remotePathMappings.value[idx] = updated
      toast.success('Remote path mapping', 'Remote path mapping updated')
    } else {
      const created = await createRemotePathMapping(mappingData)
      remotePathMappings.value.push(created)

      // Automatically assign the new mapping to the selected download client
      if (mappingData.downloadClientId) {
        const selectedClient = configStore.downloadClientConfigurations.find(
          (c) => c.id === mappingData.downloadClientId,
        )
        if (selectedClient) {
          const updatedClient = { ...selectedClient }
          if (!updatedClient.settings.remotePathMappingIds) {
            updatedClient.settings.remotePathMappingIds = []
          }
          if (!updatedClient.settings.remotePathMappingIds.includes(created.id)) {
            updatedClient.settings.remotePathMappingIds.push(created.id)
            const clientIndex = configStore.downloadClientConfigurations.findIndex(
              (c) => c.id === mappingData.downloadClientId,
            )
            if (clientIndex !== -1) {
              configStore.downloadClientConfigurations[clientIndex] = updatedClient
            }
            configStore.saveDownloadClientConfiguration(updatedClient).catch((err) => {
              errorTracking.captureException(err as Error, {
                component: 'DownloadClientsTab',
                operation: 'saveClientConfig',
              })
              configStore.loadDownloadClientConfigurations()
            })
          }
        }
      }

      toast.success('Remote path mapping', 'Remote path mapping created')
    }

    closeMappingForm()
  } catch (err) {
    errorTracking.captureException(err as Error, {
      component: 'DownloadClientsTab',
      operation: 'saveMapping',
    })
    toast.error('Save failed', 'Failed to save mapping')
  }
}

const editMapping = (mapping: RemotePathMapping) => openMappingForm(mapping)

const confirmDeleteMapping = (mapping: RemotePathMapping) => {
  mappingToDelete.value = mapping
}

const executeDeleteMapping = async (id?: number) => {
  const mappingId = id || mappingToDelete.value?.id
  if (!mappingId) return

  try {
    await deleteRemotePathMapping(mappingId)
    remotePathMappings.value = remotePathMappings.value.filter((m) => m.id !== mappingId)
    toast.success('Remote path mapping', 'Remote path mapping deleted')
  } catch (err) {
    errorTracking.captureException(err as Error, {
      component: 'DownloadClientsTab',
      operation: 'deleteMapping',
    })
    toast.error('Delete failed', 'Failed to delete mapping')
  } finally {
    mappingToDelete.value = null
  }
}

// Lifecycle
onMounted(async () => {
  await Promise.all([configStore.loadDownloadClientConfigurations(), loadRemotePathMappings()])
})

// Expose methods for parent component to open add client form and add mapping
defineExpose({
  openAddClient: () => {
    editingClient.value = null
    showClientForm.value = true
  },
  openAddMapping: () => {
    openMappingForm()
  },
})
</script>

<style scoped>
.tab-content {
  animation: fadeIn 0.2s ease;
}

/* @keyframes fadeIn is centralized in src/assets/animations.css */

.download-clients-tab {
  width: 100%;
  display: flex;
  flex-direction: column;
  gap: 1.5rem;
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

.section-header .small-inline-spinner {
  margin-left: 0.5rem;
  width: 18px;
  height: 18px;
}

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
  background: var(--brand-600);
  transform: translateY(-1px);
  box-shadow: 0 4px 12px rgba(30, 136, 229, 0.4);
}

.empty-state {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  padding: 3rem;
  text-align: center;
  color: var(--color-text-secondary);
  background: var(--color-background-secondary);
  border-radius: 8px;
  border: 2px dashed var(--color-border);
}

.empty-state svg {
  width: 48px;
  height: 48px;
  margin-bottom: 1rem;
  opacity: 0.5;
}

.indexers-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(450px, 1fr));
  gap: 1rem;
}

.indexer-card {
  background-color: #2a2a2a;
  border: 1px solid rgba(255, 255, 255, 0.08);
  border-radius: 6px;
  overflow: hidden;
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

.indexer-info {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  min-width: 0;
}

.indexer-info h4 {
  margin: 0;
  font-size: 1.1rem;
  color: #fff;
  font-weight: 500;
}

.client-type-icon {
  width: 20px;
  height: 20px;
  flex-shrink: 0;
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

.indexer-details {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
  padding: 1.5rem;
}

.indexer-type.torrent {
  background: rgba(34, 197, 94, 0.1);
  color: #22c55e;
}

.indexer-type.usenet {
  background: rgba(59, 130, 246, 0.1);
  color: #3b82f6;
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
  font-size: 0.875rem;
}

.detail-row svg {
  width: 16px;
  height: 16px;
  color: var(--color-text-secondary);
  flex-shrink: 0;
}

.detail-label {
  font-weight: 500;
  color: var(--color-text-secondary);
  min-width: 100px;
}

.detail-value {
  color: var(--color-text);
}

.detail-value.success {
  color: #22c55e;
}

.detail-value.error {
  color: #ef4444;
}

.feature-badges {
  display: flex;
  flex-wrap: wrap;
  gap: 0.5rem;
}

/* Badge styles - Now using Pill component from @/components/base */

.config-list {
  display: flex;
  flex-direction: column;
  gap: 1rem;
}

.config-card {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 1rem;
  background-color: #2a2a2a;
  border: 1px solid rgba(255, 255, 255, 0.08);
  border-radius: 6px;
  transition: all 0.2s ease;
}

.config-card:hover {
  border-color: rgba(77, 171, 247, 0.3);
  transform: translateY(-1px);
  box-shadow: 0 4px 12px rgba(77, 171, 247, 0.12);
}

.config-info {
  flex: 1;
}

.config-info h4 {
  margin: 0 0 0.75rem 0;
  font-size: 1rem;
  font-weight: 500;
}

.config-actions {
  display: flex;
  gap: 0.5rem;
}
/* `.edit-button` and `.delete-button` centralized in `src/assets/buttons.css` */

/* Modal-specific styling moved to shared `modals.css` */



.btn.btn-primary {
  /* Use centralized primary button styles; per-component overrides removed */
}

.delete-button {
  /* Use centralized modal delete styles in src/assets/modals.css for full-size modal actions.
     The small icon-style delete button (defined earlier) remains for list actions. */
}

.ph-spin {
  animation: spin 1s linear infinite;
}

/* @keyframes spin is centralized in src/assets/animations.css */
</style>
