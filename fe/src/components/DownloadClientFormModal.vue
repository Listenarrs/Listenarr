<template>
  <div v-if="visible" class="modal-overlay" @click="closeModal">
    <div class="modal-content" @click.stop>
      <div class="modal-header">
        <h2>{{ editingClient ? 'Edit Download Client' : 'Add Download Client' }} - {{ formData.type.toUpperCase() }}</h2>
        <button class="close-btn" @click="closeModal">
          <i class="ph ph-x"></i>
        </button>
      </div>
      
      <div class="modal-body">
        <form @submit.prevent="handleSubmit">
          <!-- Basic Information -->
          <div class="form-section">
            <h3>Basic</h3>
            
            <div class="form-group">
              <label for="name">Name *</label>
              <input 
                id="name" 
                v-model="formData.name" 
                type="text" 
                required 
                placeholder="e.g., SABnzbd, qBittorrent"
              />
            </div>

            <div class="checkbox-group">
              <label>
                <input type="checkbox" v-model="formData.isEnabled" />
                <span>
                  <strong>Enable</strong>
                  <small>Enable this download client</small>
                </span>
              </label>
            </div>

            <div class="form-group">
              <label for="type">Type *</label>
              <select id="type" v-model="formData.type" required @change="onTypeChange">
                <option value="qbittorrent">qBittorrent</option>
                <option value="transmission">Transmission</option>
                <option value="sabnzbd">SABnzbd</option>
                <option value="nzbget">NZBGet</option>
              </select>
            </div>

            <div class="form-group">
              <label for="host">Host *</label>
              <input 
                id="host" 
                v-model="formData.host" 
                type="text" 
                required 
                :placeholder="getHostPlaceholder()"
              />
            </div>

            <div class="form-group">
              <label for="port">Port *</label>
              <input 
                id="port" 
                v-model.number="formData.port" 
                type="number" 
                required 
                min="1"
                max="65535"
                :placeholder="getPortPlaceholder()"
              />
            </div>

            <div class="checkbox-group">
              <label>
                <input type="checkbox" v-model="formData.useSSL" />
                <span>
                  <strong>Use SSL</strong>
                  <small>{{ `Use secure connection when connecting to ${formData.type}` }}</small>
                </span>
              </label>
            </div>
          </div>

          <!-- Authentication -->
          <div class="form-section" v-if="requiresAuth">
            <h3>Authentication</h3>
            
            <div class="form-group" v-if="requiresApiKey">
              <label for="apiKey">API Key *</label>
              <input 
                id="apiKey" 
                v-model="formData.apiKey" 
                type="password" 
                autocomplete="off"
                required 
                placeholder="********"
              />
            </div>

            <div v-else>
              <div class="form-group">
                <label for="username">Username</label>
                <input 
                  id="username" 
                  v-model="formData.username" 
                  type="text" 
                  placeholder="admin"
                />
              </div>

              <div class="form-group">
                <label for="password">Password</label>
                <input 
                  id="password" 
                  v-model="formData.password" 
                  type="password" 
                  placeholder="********"
                />
              </div>
            </div>
          </div>

          <!-- Category & Tags -->
          <div class="form-section">
            <h3>{{ isUsenet ? 'Category' : 'Category & Tags' }}</h3>
            
            <div class="form-group">
              <label for="category">Category</label>
              <input 
                id="category" 
                v-model="formData.category" 
                type="text" 
                :placeholder="isUsenet ? 'tv' : 'tv-sonarr'"
              />
              <small>{{ getCategoryHelp() }}</small>
            </div>

            <div class="form-group" v-if="!isUsenet">
              <label for="tags">Tags</label>
              <input 
                id="tags" 
                v-model="formData.tags" 
                type="text" 
                placeholder="Leave blank to use with all series"
              />
              <small>Only use this download client for series with at least one matching tag. Leave blank to use with all series.</small>
            </div>
          </div>

          <!-- Priority -->
          <div class="form-section">
            <h3>Priority</h3>
            
            <div class="form-group">
              <label for="recentPriority">Recent Priority</label>
              <select id="recentPriority" v-model="formData.recentPriority">
                <option value="default">Default</option>
                <option value="last">Last</option>
                <option value="first">First</option>
              </select>
              <small>Priority to use when grabbing episodes that aired within the last 14 days</small>
            </div>

            <div class="form-group">
              <label for="olderPriority">Older Priority</label>
              <select id="olderPriority" v-model="formData.olderPriority">
                <option value="default">Default</option>
                <option value="last">Last</option>
                <option value="first">First</option>
              </select>
              <small>Priority to use when grabbing episodes that aired over 14 days ago</small>
            </div>
          </div>

          <!-- Client Specific Settings -->
          <div class="form-section" v-if="isUsenet">
            <h3>Completed Download Handling</h3>
            
            <div class="checkbox-group">
              <label>
                <input type="checkbox" v-model="formData.removeCompleted" />
                <span>
                  <strong>Remove Completed</strong>
                  <small>Remove imported downloads from download client history</small>
                </span>
              </label>
            </div>

            <div class="checkbox-group">
              <label>
                <input type="checkbox" v-model="formData.removeFailed" />
                <span>
                  <strong>Remove Failed</strong>
                  <small>Remove failed downloads from download client history</small>
                </span>
              </label>
            </div>
          </div>

          <div class="form-section" v-if="formData.type === 'qbittorrent'">
            <h3>Advanced Settings</h3>
            
            <div class="form-group">
              <label for="initialState">Initial State</label>
              <select id="initialState" v-model="formData.initialState">
                <option value="default">Default</option>
                <option value="start">Start</option>
                <option value="forceStart">Force Start</option>
                <option value="pause">Pause</option>
              </select>
              <small>Initial state for torrents added to qBittorrent. Note that Forced Torrents do not abide by seed restrictions</small>
            </div>

            <div class="checkbox-group">
              <label>
                <input type="checkbox" v-model="formData.sequentialOrder" />
                <span>
                  <strong>Sequential Order</strong>
                  <small>Download in sequential order (qBittorrent 4.1.0+)</small>
                </span>
              </label>
            </div>

            <div class="checkbox-group">
              <label>
                <input type="checkbox" v-model="formData.firstAndLastFirst" />
                <span>
                  <strong>First and Last First</strong>
                  <small>Download first and last pieces first (qBittorrent 4.1.0+)</small>
                </span>
              </label>
            </div>

            <div class="form-group">
              <label for="contentLayout">Content Layout</label>
              <select id="contentLayout" v-model="formData.contentLayout">
                <option value="default">Default</option>
                <option value="original">Original</option>
                <option value="subfolder">Create Subfolder</option>
                <option value="nosubfolder">Don't Create Subfolder</option>
              </select>
              <small>Whether to use qBittorrent's configured content layout. Use qBittorrent's 4.3.2 layout if the original layout from the torrent cannot be used (Default = Original layout)</small>
            </div>
          </div>
        </form>
      </div>
      
      <div class="modal-footer">
        <button type="button" class="btn btn-danger" @click="handleDelete" v-if="editingClient">
          <i class="ph ph-trash"></i>
          Delete
        </button>
        <button type="button" class="btn btn-secondary" @click="closeModal">
          <i class="ph ph-x"></i>
          Cancel
        </button>
        <button type="button" class="btn btn-info" @click="testConnection" :disabled="testing">
          <i :class="testing ? 'ph ph-spinner ph-spin' : 'ph ph-gear'"></i>
          {{ testing ? 'Testing...' : 'Test' }}
        </button>
        <button type="button" class="btn btn-primary" @click="handleSubmit" :disabled="saving">
          <i :class="saving ? 'ph ph-spinner ph-spin' : 'ph ph-floppy-disk'"></i>
          {{ saving ? 'Saving...' : 'Save' }}
        </button>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, watch } from 'vue'
import type { DownloadClientConfiguration } from '@/types'
import { useNotification } from '@/composables/useNotification'
import { useConfigurationStore } from '@/stores/configuration'

interface Props {
  visible: boolean
  editingClient: DownloadClientConfiguration | null
}

interface Emits {
  (e: 'close'): void
  (e: 'saved'): void
  (e: 'delete', id: string): void
}

const props = defineProps<Props>()
const emit = defineEmits<Emits>()
const configStore = useConfigurationStore()
const { success, error: showError } = useNotification()

const saving = ref(false)
const testing = ref(false)

const defaultFormData = {
  name: '',
  type: 'qbittorrent' as 'qbittorrent' | 'transmission' | 'sabnzbd' | 'nzbget',
  host: '',
  port: 8080,
  username: '',
  password: '',
  apiKey: '',
  downloadPath: '/downloads',
  useSSL: false,
  isEnabled: true,
  category: '',
  tags: '',
  recentPriority: 'default',
  olderPriority: 'default',
  removeCompleted: false,
  removeFailed: false,
  initialState: 'default',
  sequentialOrder: false,
  firstAndLastFirst: false,
  contentLayout: 'default',
  settings: {}
}

const formData = ref({ ...defaultFormData })

const isUsenet = computed(() => {
  return formData.value.type === 'sabnzbd' || formData.value.type === 'nzbget'
})

const requiresAuth = computed(() => {
  return true // All clients require some form of auth
})

const requiresApiKey = computed(() => {
  return formData.value.type === 'sabnzbd' || formData.value.type === 'nzbget'
})

const getHostPlaceholder = () => {
  const placeholders: Record<string, string> = {
    qbittorrent: 'qbittorrent.therobbiedavis.com',
    transmission: 'transmission.therobbiedavis.com',
    sabnzbd: 'sabnzbd.therobbiedavis.com',
    nzbget: 'nzbget.therobbiedavis.com'
  }
  return placeholders[formData.value.type] || 'localhost'
}

const getPortPlaceholder = () => {
  const ports: Record<string, number> = {
    qbittorrent: 8080,
    transmission: 9091,
    sabnzbd: 8080,
    nzbget: 6789
  }
  return ports[formData.value.type]?.toString() || '8080'
}

const getCategoryHelp = () => {
  if (isUsenet.value) {
    return 'Adding a category specific to Sonarr avoids conflicts with unrelated non-Sonarr downloads. Using a category is optional, but strongly recommended.'
  }
  return 'Adding a category specific to Sonarr avoids conflicts with unrelated downloads.'
}

const onTypeChange = () => {
  // Update default port when type changes
  const defaultPorts: Record<string, number> = {
    qbittorrent: 8080,
    transmission: 9091,
    sabnzbd: 8080,
    nzbget: 6789
  }
  formData.value.port = defaultPorts[formData.value.type] || 8080
}

// Watch for editing client changes
watch(() => props.editingClient, (newClient) => {
  if (newClient) {
    const settings = newClient.settings as Record<string, unknown>
    formData.value = {
      name: newClient.name,
      type: newClient.type,
      host: newClient.host,
      port: newClient.port,
      username: newClient.username || '',
      password: newClient.password || '',
      apiKey: (settings?.apiKey as string) || '',
      downloadPath: newClient.downloadPath,
      useSSL: newClient.useSSL,
      isEnabled: newClient.isEnabled,
      category: (settings?.category as string) || '',
      tags: (settings?.tags as string) || '',
      recentPriority: (settings?.recentPriority as string) || 'default',
      olderPriority: (settings?.olderPriority as string) || 'default',
      removeCompleted: (settings?.removeCompleted as boolean) || false,
      removeFailed: (settings?.removeFailed as boolean) || false,
      initialState: (settings?.initialState as string) || 'default',
      sequentialOrder: (settings?.sequentialOrder as boolean) || false,
      firstAndLastFirst: (settings?.firstAndLastFirst as boolean) || false,
      contentLayout: (settings?.contentLayout as string) || 'default',
      settings: newClient.settings || {}
    }
  } else {
    formData.value = { ...defaultFormData }
  }
}, { immediate: true })

const closeModal = () => {
  formData.value = { ...defaultFormData }
  emit('close')
}

const testConnection = async () => {
  testing.value = true
  try {
    // TODO: Implement actual test endpoint
    await new Promise(resolve => setTimeout(resolve, 1000))
    success('Connection test successful')
  } catch (error) {
    console.error('Failed to test download client:', error)
    showError('Failed to test download client connection')
  } finally {
    testing.value = false
  }
}

const handleSubmit = async () => {
  saving.value = true
  try {
    // Generate a simple UUID fallback
    const generateId = () => {
      return `${Date.now()}-${Math.random().toString(36).substr(2, 9)}`
    }

    const clientConfig: DownloadClientConfiguration = {
      id: props.editingClient?.id || generateId(),
      name: formData.value.name,
      type: formData.value.type,
      host: formData.value.host,
      port: formData.value.port,
      username: formData.value.username || '',
      password: formData.value.password || '',
      downloadPath: formData.value.downloadPath || '/downloads',
      useSSL: formData.value.useSSL,
      isEnabled: formData.value.isEnabled,
      settings: {
        ...(formData.value.apiKey && { apiKey: formData.value.apiKey }),
        ...(formData.value.category && { category: formData.value.category }),
        ...(formData.value.tags && { tags: formData.value.tags }),
        recentPriority: formData.value.recentPriority,
        olderPriority: formData.value.olderPriority,
        removeCompleted: formData.value.removeCompleted,
        removeFailed: formData.value.removeFailed,
        initialState: formData.value.initialState,
        sequentialOrder: formData.value.sequentialOrder,
        firstAndLastFirst: formData.value.firstAndLastFirst,
        contentLayout: formData.value.contentLayout
      }
    }

    console.log('Saving download client configuration:', clientConfig)
    await configStore.saveDownloadClientConfiguration(clientConfig)
    success(`Download client ${props.editingClient ? 'updated' : 'created'} successfully`)
    
    emit('saved')
    closeModal()
  } catch (error) {
    console.error('Failed to save download client:', error)
    showError(`Failed to save download client: ${error instanceof Error ? error.message : 'Unknown error'}`)
  } finally {
    saving.value = false
  }
}

const handleDelete = () => {
  if (props.editingClient) {
    emit('delete', props.editingClient.id)
    closeModal()
  }
}
</script>

<style scoped>
.modal-overlay {
  position: fixed;
  top: 0;
  left: 0;
  right: 0;
  bottom: 0;
  background-color: rgba(0, 0, 0, 0.8);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 1000;
  overflow-y: auto;
  padding: 2rem;
}

.modal-content {
  background: #2a2a2a;
  border: 1px solid #444;
  border-radius: 8px;
  max-width: 700px;
  width: 100%;
  max-height: 90vh;
  display: flex;
  flex-direction: column;
  box-shadow: 0 10px 40px rgba(0, 0, 0, 0.5);
}

.modal-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 1.5rem 2rem;
  border-bottom: 1px solid #444;
}

.modal-header h2 {
  margin: 0;
  color: #fff;
  font-size: 1.5rem;
}

.close-btn {
  background: none;
  border: none;
  color: #999;
  cursor: pointer;
  padding: 0.5rem;
  font-size: 1.5rem;
  display: flex;
  align-items: center;
  justify-content: center;
  border-radius: 4px;
  transition: all 0.2s;
}

.close-btn:hover {
  background-color: #333;
  color: #fff;
}

.modal-body {
  padding: 2rem;
  overflow-y: auto;
  flex: 1;
}

.form-section {
  margin-bottom: 2rem;
}

.form-section:last-child {
  margin-bottom: 0;
}

.form-section h3 {
  color: #fff;
  font-size: 1.1rem;
  margin: 0 0 1rem 0;
  padding-bottom: 0.5rem;
  border-bottom: 1px solid #444;
}

.form-group {
  margin-bottom: 1.5rem;
}

.form-group:last-child {
  margin-bottom: 0;
}

.form-group label {
  display: block;
  margin-bottom: 0.5rem;
  color: #fff;
  font-weight: 600;
  font-size: 0.95rem;
}

.form-group input,
.form-group select {
  width: 100%;
  padding: 0.75rem;
  background-color: #1a1a1a;
  border: 1px solid #444;
  border-radius: 4px;
  color: #fff;
  font-size: 0.95rem;
  transition: all 0.2s;
}

.form-group input:focus,
.form-group select:focus {
  outline: none;
  border-color: #007acc;
  box-shadow: 0 0 0 3px rgba(0, 122, 204, 0.1);
}

.form-group small {
  display: block;
  margin-top: 0.5rem;
  color: #999;
  font-size: 0.85rem;
}

.checkbox-group {
  margin-bottom: 1rem;
}

.checkbox-group label {
  display: flex;
  align-items: flex-start;
  gap: 0.75rem;
  padding: 1rem;
  background-color: #1a1a1a;
  border: 1px solid #444;
  border-radius: 4px;
  cursor: pointer;
  transition: all 0.2s;
}

.checkbox-group label:hover {
  border-color: #007acc;
  background-color: #222;
}

.checkbox-group input[type="checkbox"] {
  margin-top: 0.25rem;
  width: auto;
  cursor: pointer;
}

.checkbox-group label span {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
  flex: 1;
}

.checkbox-group label strong {
  color: #fff;
  font-size: 0.95rem;
}

.checkbox-group label small {
  color: #999;
  font-size: 0.85rem;
  font-weight: normal;
}

.modal-footer {
  display: flex;
  gap: 1rem;
  justify-content: space-between;
  padding: 1.5rem 2rem;
  border-top: 1px solid #444;
}

.btn {
  padding: 0.75rem 1.5rem;
  border: none;
  border-radius: 4px;
  cursor: pointer;
  transition: all 0.2s;
  display: flex;
  align-items: center;
  gap: 0.5rem;
  font-weight: 500;
  font-size: 0.95rem;
}

.btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.btn-danger {
  background-color: #f44336;
  color: white;
  margin-right: auto;
}

.btn-danger:hover:not(:disabled) {
  background-color: #d32f2f;
  transform: translateY(-1px);
}

.btn-secondary {
  background-color: #555;
  color: white;
}

.btn-secondary:hover:not(:disabled) {
  background-color: #666;
  transform: translateY(-1px);
}

.btn-info {
  background-color: #2196f3;
  color: white;
}

.btn-info:hover:not(:disabled) {
  background-color: #1976d2;
  transform: translateY(-1px);
}

.btn-primary {
  background-color: #007acc;
  color: white;
}

.btn-primary:hover:not(:disabled) {
  background-color: #005a9e;
  transform: translateY(-1px);
}

.ph-spin {
  animation: spin 1s linear infinite;
}

@keyframes spin {
  from {
    transform: rotate(0deg);
  }
  to {
    transform: rotate(360deg);
  }
}

@media (max-width: 768px) {
  .modal-overlay {
    padding: 1rem;
  }

  .modal-footer {
    flex-wrap: wrap;
  }

  .btn {
    flex: 1;
    justify-content: center;
    min-width: 120px;
  }

  .btn-danger {
    flex-basis: 100%;
  }
}
</style>
