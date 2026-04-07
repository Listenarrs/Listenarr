<template>
  <div class="tab-content">
    <div class="section-header">
      <div class="section-title-wrapper">
        <h3>Audiobookshelf</h3>
        <p class="section-subtitle">
          Connect Listenarr to Audiobookshelf so imported books can be rescanned automatically.
        </p>
      </div>
    </div>

    <div class="settings-form">
      <div class="form-section">
        <h4>
          <PhBooks />
          Connection
        </h4>

        <div class="checkbox-group">
          <label>
            <input
              :checked="localSettings.audiobookshelfEnabled ?? false"
              @change="updateBool('audiobookshelfEnabled', ($event.target as HTMLInputElement).checked)"
              type="checkbox"
            />
            <span>
              <strong>Enable Audiobookshelf integration</strong>
              <small>Allow Listenarr to communicate with your Audiobookshelf server.</small>
            </span>
          </label>
        </div>

        <div class="form-group">
          <label for="abs-url">Audiobookshelf URL</label>
          <input
            id="abs-url"
            :value="localSettings.audiobookshelfUrl ?? ''"
            @input="updateString('audiobookshelfUrl', ($event.target as HTMLInputElement).value)"
            type="url"
            placeholder="http://192.168.1.100:13378"
          />
          <small class="form-help">Use the base server URL only. Do not include /api.</small>
        </div>

        <div class="form-group">
          <label for="abs-api-key">API Key</label>
          <PasswordInput
            id="abs-api-key"
            :modelValue="localSettings.audiobookshelfApiKey ?? ''"
            @update:modelValue="updateString('audiobookshelfApiKey', $event)"
            autocomplete="off"
            placeholder="Paste your Audiobookshelf API key"
          />
        </div>

        <div class="form-group">
          <label for="abs-library-id">Library ID</label>
          <div class="input-row">
            <input
              id="abs-library-id"
              :value="localSettings.audiobookshelfLibraryId ?? ''"
              @input="updateString('audiobookshelfLibraryId', ($event.target as HTMLInputElement).value)"
              type="text"
              placeholder="Select a library or paste an ID"
            />
            <button
              type="button"
              class="btn btn-primary"
              @click="loadLibraries"
              :disabled="loadingLibraries"
            >
              <template v-if="loadingLibraries">
                <PhSpinner class="ph-spin" />
              </template>
              <template v-else>
                <PhArrowsClockwise />
              </template>
              Load Libraries
            </button>
          </div>
        </div>

        <div v-if="libraries.length" class="form-group">
          <label for="abs-library-select">Available Libraries</label>
          <select
            id="abs-library-select"
            :value="localSettings.audiobookshelfLibraryId ?? ''"
            @change="updateString('audiobookshelfLibraryId', ($event.target as HTMLSelectElement).value)"
          >
            <option value="">Select a library</option>
            <option
              v-for="library in libraries"
              :key="library.id"
              :value="library.id"
            >
              {{ library.name }}{{ library.mediaType ? ` (${library.mediaType})` : '' }}
            </option>
          </select>
        </div>
      </div>

      <div class="form-section">
        <h4>
          <PhGear />
          Behavior
        </h4>

        <div class="checkbox-group">
          <label>
            <input
              :checked="localSettings.audiobookshelfScanAfterImport ?? true"
              @change="updateBool('audiobookshelfScanAfterImport', ($event.target as HTMLInputElement).checked)"
              type="checkbox"
            />
            <span>
              <strong>Scan after import</strong>
              <small>Trigger an Audiobookshelf scan after Listenarr imports files.</small>
            </span>
          </label>
        </div>

        <div class="checkbox-group">
          <label>
            <input
              :checked="localSettings.audiobookshelfScanOnManualImport ?? true"
              @change="updateBool('audiobookshelfScanOnManualImport', ($event.target as HTMLInputElement).checked)"
              type="checkbox"
            />
            <span>
              <strong>Scan after manual import</strong>
              <small>Run a scan when you manually import books.</small>
            </span>
          </label>
        </div>

        <div class="checkbox-group">
          <label>
            <input
              :checked="localSettings.audiobookshelfScanOnCompletedDownload ?? true"
              @change="updateBool('audiobookshelfScanOnCompletedDownload', ($event.target as HTMLInputElement).checked)"
              type="checkbox"
            />
            <span>
              <strong>Scan after completed download import</strong>
              <small>Run a scan when Listenarr processes completed downloads.</small>
            </span>
          </label>
        </div>
      </div>

      <div class="form-section">
        <h4>
          <PhPlug />
          Actions
        </h4>

        <div class="action-row">
          <button
            type="button"
            class="btn btn-primary"
            @click="testConnection"
            :disabled="testingConnection || !canTest"
          >
            <template v-if="testingConnection">
              <PhSpinner class="ph-spin" />
            </template>
            <template v-else>
              <PhCheckCircle />
            </template>
            Test Connection
          </button>

          <button
            type="button"
            class="btn btn-primary"
            @click="scanNow"
            :disabled="scanning || !canScan"
          >
            <template v-if="scanning">
              <PhSpinner class="ph-spin" />
            </template>
            <template v-else>
              <PhMagnifyingGlass />
            </template>
            Scan Now
          </button>
        </div>

        <div v-if="statusMessage" class="status-message" :class="{ error: statusError }">
          {{ statusMessage }}
        </div>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import type { ApplicationSettings } from '@/types'
import { apiService } from '@/services/api'
import PasswordInput from '@/components/form/PasswordInput.vue'
import { useToast } from '@/services/toastService'
import {
  PhBooks,
  PhGear,
  PhPlug,
  PhSpinner,
  PhCheckCircle,
  PhMagnifyingGlass,
  PhArrowsClockwise,
  PhDownloadSimple,
} from '@phosphor-icons/vue'

type AudiobookshelfLibrary = {
  id: string
  name: string
  mediaType?: string
}

const props = defineProps<{
  settings: ApplicationSettings
}>()

const emit = defineEmits<{
  (e: 'update:settings', value: ApplicationSettings): void
}>()

const toast = useToast()

const localSettings = ref<ApplicationSettings>({ ...props.settings })
const libraries = ref<AudiobookshelfLibrary[]>([])
const loadingLibraries = ref(false)
const testingConnection = ref(false)
const scanning = ref(false)
const statusMessage = ref('')
const statusError = ref(false)

const previewItems = ref<Array<{
  itemId: string
  title: string
  author: string
  path: string
  existingAudiobookId?: number | null
  willImport: boolean
  reason: string
  asin?: string
  isbn?: string[]
}>>([])

const selectedImportIds = ref<string[]>([])
const loadingPreview = ref(false)
const importing = ref(false)

watch(
  () => props.settings,
  (newSettings) => {
    localSettings.value = { ...newSettings }
  },
  { deep: true }
)

watch(
  () => localSettings.value.audiobookshelfLibraryId,
  () => {
    previewItems.value = []
    selectedImportIds.value = []
  }
)

function updateSettings(patch: Partial<ApplicationSettings>) {
  localSettings.value = {
    ...localSettings.value,
    ...patch,
  }
  emit('update:settings', localSettings.value)
}

function updateString(key: keyof ApplicationSettings, value: string) {
  updateSettings({ [key]: value } as Partial<ApplicationSettings>)
}

async function saveAudiobookshelfSettings(): Promise<boolean> {
  statusMessage.value = ''
  statusError.value = false

  try {
    const saved = await apiService.saveApplicationSettings(localSettings.value)
    localSettings.value = saved
    emit('update:settings', saved)
    return true
  } catch (error) {
    console.error('Failed to save Audiobookshelf settings', error)
    statusMessage.value = 'Failed to save settings.'
    statusError.value = true
    toast.error('Audiobookshelf', 'Failed to save settings')
    return false
  }
}

function updateBool(key: keyof ApplicationSettings, value: boolean) {
  updateSettings({ [key]: value } as Partial<ApplicationSettings>)
}

const canTest = computed(() => {
  return !!(
    localSettings.value.audiobookshelfEnabled &&
    localSettings.value.audiobookshelfUrl &&
    localSettings.value.audiobookshelfApiKey
  )
})

const canScan = computed(() => {
  return !!(
    canTest.value &&
    localSettings.value.audiobookshelfLibraryId
  )
})

async function loadLibraries() {
  loadingLibraries.value = true
  statusMessage.value = ''
  statusError.value = false

  try {
    const saved = await saveAudiobookshelfSettings()
    if (!saved) return

    const result = await apiService.getAudiobookshelfLibraries()
    libraries.value = Array.isArray(result) ? result : []

    if (libraries.value.length > 0) {
    toast.success('Audiobookshelf', `Loaded ${libraries.value.length} librar${libraries.value.length === 1 ? 'y' : 'ies'}`)
    } else {
      toast.info('Audiobookshelf', 'Connection worked, but no accessible libraries were returned')
    }
    
  } catch (error) {
    console.error('Failed to load Audiobookshelf libraries', error)
    statusMessage.value = 'Failed to load libraries.'
    statusError.value = true
    toast.error('Audiobookshelf', 'Failed to load libraries')
  } finally {
    loadingLibraries.value = false
  }
}

async function previewImport() {
  if (!localSettings.value.audiobookshelfLibraryId) return

  loadingPreview.value = true
  try {
    const saved = await saveAudiobookshelfSettings()
    if (!saved) return

    const result = await apiService.previewAudiobookshelfImport(
      localSettings.value.audiobookshelfLibraryId
    )

    previewItems.value = Array.isArray(result) ? result : []
    selectedImportIds.value = previewItems.value
      .filter(x => x.willImport)
      .map(x => x.itemId)

    toast.success('Audiobookshelf', `Preview loaded: ${previewItems.value.length} item(s)`)
  } catch (error) {
    console.error(error)
    toast.error('Audiobookshelf', 'Failed to preview import')
  } finally {
    loadingPreview.value = false
  }
}

async function importSelected() {
  if (!localSettings.value.audiobookshelfLibraryId || selectedImportIds.value.length === 0) return

  importing.value = true
  try {
    const saved = await saveAudiobookshelfSettings()
    if (!saved) return

    const result = await apiService.importAudiobookshelfItems({
      libraryId: localSettings.value.audiobookshelfLibraryId,
      itemIds: selectedImportIds.value,
      monitored: true,
      skipExisting: true,
    })

    toast.success(
      'Audiobookshelf',
      `Imported ${result?.importedCount ?? 0}, skipped ${result?.skippedCount ?? 0}`
    )

    await previewImport()
  } catch (error) {
    console.error(error)
    toast.error('Audiobookshelf', 'Import failed')
  } finally {
    importing.value = false
  }
}

async function testConnection() {
  testingConnection.value = true
  statusMessage.value = ''
  statusError.value = false

  try {
    const saved = await saveAudiobookshelfSettings()
    if (!saved) return

    const result = await apiService.testAudiobookshelf()
    statusMessage.value = result?.message || (result?.success ? 'Connection successful.' : 'Connection failed.')
    statusError.value = !result?.success

    if (result?.success) {
      toast.success('Audiobookshelf', statusMessage.value)
    } else {
      toast.error('Audiobookshelf', statusMessage.value)
    }
  } catch (error) {
    console.error('Audiobookshelf test failed', error)
    statusMessage.value = 'Connection test failed.'
    statusError.value = true
    toast.error('Audiobookshelf', 'Connection test failed')
  } finally {
    testingConnection.value = false
  }
}

async function scanNow() {
  if (!localSettings.value.audiobookshelfLibraryId) return

  scanning.value = true
  statusMessage.value = ''
  statusError.value = false

  try {
    const saved = await saveAudiobookshelfSettings()
    if (!saved) return

    const result = await apiService.triggerAudiobookshelfScan(
      localSettings.value.audiobookshelfLibraryId
    )
    statusMessage.value = result?.message || (result?.success ? 'Scan triggered.' : 'Scan failed.')
    statusError.value = !result?.success

    if (result?.success) {
      toast.success('Audiobookshelf', statusMessage.value)
    } else {
      toast.error('Audiobookshelf', statusMessage.value)
    }
  } catch (error) {
    console.error('Audiobookshelf scan failed', error)
    statusMessage.value = 'Failed to trigger scan.'
    statusError.value = true
    toast.error('Audiobookshelf', 'Failed to trigger scan')
  } finally {
    scanning.value = false
  }
}
</script>

<style scoped>
.tab-content {
  padding: 2rem;
}

.settings-form {
  display: flex;
  flex-direction: column;
  gap: 2rem;
}

.section-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding-bottom: 1rem;
  border-bottom: 1px solid rgba(255, 255, 255, 0.08);
  margin-bottom: 1rem;
}

.section-title-wrapper {
  flex: 1;
}

.section-header h3 {
  margin: 0;
  color: #fff;
  font-size: 1.5rem;
  font-weight: 500;
}

.section-subtitle {
  margin: 0.5rem 0 0 0;
  font-size: 0.95rem;
  color: #868e96;
  font-weight: normal;
}

.row-disabled {
  opacity: 0.5;
}

.form-section h4 {
  margin: 0 0 1.5rem 0;
  color: #fff;
  font-size: 1.1rem;
  font-weight: 500;
  display: flex;
  align-items: center;
  gap: 0.65rem;
  padding-bottom: 1rem;
  border-bottom: 1px solid rgba(255, 255, 255, 0.08);
}

.form-section h4 :deep(svg) {
  color: #4dabf7;
}

.form-group {
  margin-bottom: 1.5rem;
}

.form-group label {
  display: block;
  margin-bottom: 0.5rem;
  color: #fff;
  font-weight: 500;
  font-size: 0.95rem;
}

.form-group input,
.form-group select {
  width: 100%;
  padding: 0.75rem;
  background-color: #1a1a1a;
  border: 1px solid #444;
  border-radius: 6px;
  color: #fff;
  font-size: 0.95rem;
  transition: all 0.2s;
}

.form-group input:focus,
.form-group select:focus {
  outline: none;
  border-color: var(--brand-focus);
  box-shadow: 0 0 0 3px rgba(var(--brand-rgb), 0.1);
}

.form-help {
  display: block;
  margin-top: 0.5rem;
  font-size: 0.85rem;
  color: #868e96;
}

.checkbox-group {
  flex-direction: row;
  align-items: flex-start;
  background-color: rgba(0, 0, 0, 0.2);
  border: 1px solid rgba(255, 255, 255, 0.08);
  border-radius: 6px;
  padding: 1rem;
  margin-bottom: 1rem;
  transition: all 0.2s ease;
}

.checkbox-group label {
  display: flex;
  align-items: flex-start;
  gap: 1rem;
  cursor: pointer;
  width: 100%;
}

.checkbox-group input[type='checkbox'] {
  margin: 0.25rem 0 0 0;
  width: 18px;
  height: 18px;
  cursor: pointer;
  flex-shrink: 0;
  accent-color: #4dabf7;
}

.checkbox-group label span {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
}

.checkbox-group label strong {
  color: #fff;
  font-size: 0.95rem;
  font-weight: 500;
}

.checkbox-group label small {
  color: #868e96;
  font-size: 0.85rem;
  font-weight: normal;
  line-height: 1.5;
}

.input-row {
  display: flex;
  gap: 0.75rem;
  align-items: stretch;
}

.input-row > input {
  flex: 1;
}

.action-row {
  display: flex;
  gap: 0.75rem;
  flex-wrap: wrap;
}

.status-message {
  margin-top: 1rem;
  padding: 0.85rem 1rem;
  border-radius: 6px;
  background: rgba(81, 207, 102, 0.12);
  border: 1px solid rgba(81, 207, 102, 0.24);
  color: #51cf66;
}

.status-message.error {
  background: rgba(255, 107, 107, 0.12);
  border: 1px solid rgba(255, 107, 107, 0.24);
  color: #ff6b6b;
}

@media (max-width: 768px) {
  .tab-content {
    padding: 1rem;
  }

  .input-row,
  .action-row {
    flex-direction: column;
  }

  .btn.btn-primary {
    width: 100%;
    justify-content: center;
  }
}
</style>