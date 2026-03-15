<template>
  <Modal :visible="visible" size="lg" @close="closeModal">
    <template #header>
      <ModalHeader :title="editingIndexer ? 'Edit Indexer' : 'Add Indexer'" :icon="PhGlobe" iconLabel="Indexer" @close="closeModal" />
    </template> 

    <template #default>
      <ModalForm :submitting="saving" @submit="handleSubmit">
        <ModalBody>
          <!-- Activation -->
          <FormSection title="Activation" :icon="PhToggleRight">
            <CheckboxCard v-model="formData.isEnabled" title="Enable" description="Enable this indexer" />
          </FormSection>

          <!-- Basic Information -->
          <FormSection title="Basic Information" :icon="PhGlobe">
            <FormRow label="Name *" labelFor="name">
              <input id="name" v-model="formData.name" type="text" required placeholder="e.g., My Indexer" />
            </FormRow>

            <FormRow label="Implementation *" labelFor="implementation">
              <select id="implementation" v-model="formData.implementation" required>
                <option value="Newznab">Newznab</option>
                <option value="Torznab">Torznab</option>
                <option value="MyAnonamouse">MyAnonamouse</option>
                <option value="InternetArchive">Internet Archive</option>
                <option value="Custom">Custom</option>
              </select>
            </FormRow>

            <FormRow v-if="formData.implementation !== 'InternetArchive'" label="URL *" labelFor="url">
              <select
                v-if="formData.implementation === 'MyAnonamouse'"
                id="url"
                v-model="formData.url"
                required
              >
                <option value="https://www.myanonamouse.net">https://www.myanonamouse.net</option>
              </select>
              <input
                v-else
                id="url"
                v-model="formData.url"
                type="url"
                required
                :placeholder="'https://indexer.example.com'"
              />
            </FormRow>

            <!-- MyAnonamouse Authentication & Options -->
            <div v-if="formData.implementation === 'MyAnonamouse'" class="form-section">
              <h4>MyAnonamouse Settings</h4>
              <FormRow label="MAM ID *" labelFor="mam-id">
                <input
                  id="mam-id"
                  v-model="mamId"
                  type="text"
                  :required="formData.implementation === 'MyAnonamouse'"
                  placeholder="Your MyAnonamouse MAM ID"
                />
              </FormRow>
              <small class="info-text">
                <PhInfo />
                MyAnonamouse requires your MAM ID for authentication. This is a unique identifier
                for your account. These are stored securely and only used to search the indexer.
              </small>

              <div class="form-row mam-options">
                <FormRow label="Filter">
                  <select v-model="mamFilter">
                    <option value="">Search everything</option>
                    <option value="Active">Active</option>
                    <option value="Freeleech">Freeleech</option>
                    <option value="FreeleechOrVip">Freeleech or VIP</option>
                    <option value="Vip">VIP only</option>
                    <option value="NotVip">Not VIP</option>
                  </select>
                </FormRow>

                <FormRow>
                  <Checkbox v-model="mamSearchInDescription">Search in description</Checkbox>
                </FormRow>

                <FormRow>
                  <Checkbox v-model="mamSearchInSeries">Search in series</Checkbox>
                </FormRow>

                <FormRow>
                  <Checkbox v-model="mamSearchInFilenames">Search in filenames</Checkbox>
                </FormRow>

                <FormRow label="Language (numeric id)">
                  <input type="text" v-model="mamLanguage" placeholder="e.g., 1" />
                </FormRow>

                <FormRow label="Freeleech wedge">
                  <select v-model="mamFreeleechWedge">
                    <option value="">Never</option>
                    <option value="Preferred">Preferred</option>
                    <option value="Required">Required</option>
                  </select>
                </FormRow>

                <FormRow>
                  <Checkbox v-model="mamEnrichResults">Enrich results (fetch item page for missing fields)</Checkbox>
                </FormRow>

                <FormRow label="Enrich top results">
                  <input type="number" v-model.number="mamEnrichTopResults" min="1" max="20" />
                </FormRow>
              </div>
            </div>

            <!-- Internet Archive Collection Selection -->
            <div v-if="formData.implementation === 'InternetArchive'" class="form-section">
              <h4>Collection</h4>
              <div class="form-group">
                <label for="ia-collection">Collection *</label>
                <select
                  id="ia-collection"
                  v-model="iaCollection"
                  :required="formData.implementation === 'InternetArchive'"
                >
                  <option value="librivoxaudio">Librivox (Free Audiobooks)</option>
                  <option value="audio_bookspoetry">Audio Books & Poetry</option>
                </select>
              </div>
              <small class="info-text">
                <PhInfo />
                Choose which Internet Archive collection to search. Librivox contains public domain
                audiobooks read by volunteers.
              </small>
            </div>

            <!-- API Key for other implementations -->
            <FormRow
              v-if="
                formData.implementation !== 'MyAnonamouse' &&
                formData.implementation !== 'InternetArchive'
              "
              label="API Key"
              labelFor="apiKey"
            >
              <PasswordInput id="apiKey" v-model="formData.apiKey" autocomplete="off" placeholder="Your API key" class="admin-input" />
            </FormRow>

            <FormRow v-if="formData.implementation !== 'InternetArchive'" label="Categories" labelFor="categories" help="Leave empty to search all categories">
              <input
                id="categories"
                v-model="formData.categories"
                type="text"
                placeholder="Comma-separated category IDs (e.g., 3030,3040)"
              />
            </FormRow>
          </FormSection>

          <!-- Features -->
          <FormSection title="Features" :icon="PhGear">
            <CheckboxCard v-if="formData.implementation !== 'InternetArchive'" v-model="formData.enableRss" title="Enable RSS" description="Use RSS feeds to monitor for new releases" />

            <CheckboxCard v-model="formData.enableAutomaticSearch" title="Enable Automatic Search" description="Use this indexer for automatic searches" />

            <CheckboxCard v-model="formData.enableInteractiveSearch" title="Enable Interactive Search" description="Use this indexer for manual searches" />
          </FormSection>

          <!-- Advanced Settings -->
          <FormSection title="Advanced Settings" :icon="PhGear">
            <div class="form-row">
              <FormRow label="Priority" labelFor="priority" help="Higher priority indexers are searched first (1-100)">
                <input id="priority" v-model.number="formData.priority" type="number" min="1" max="100" />
              </FormRow>

              <FormRow v-if="formData.implementation !== 'InternetArchive'" label="Minimum Age (minutes)" labelFor="minimumAge" help="Wait time before grabbing new releases (0 = disabled)">
                <input id="minimumAge" v-model.number="formData.minimumAge" type="number" min="0" />
              </FormRow>
            </div>

            <div class="form-row" v-if="formData.implementation === 'Newznab'">
              <FormRow label="Retention (days)" labelFor="retention" help="Usenet retention in days (0 = unlimited)">
                <input id="retention" v-model.number="formData.retention" type="number" min="0" />
              </FormRow>
            </div>

            <FormRow label="Maximum Size (MB)" labelFor="maximumSize" help="Maximum allowed size in megabytes (0 = unlimited)">
              <input id="maximumSize" v-model.number="formData.maximumSize" type="number" min="0" />
            </FormRow>
          </FormSection>
        </ModalBody>
      </ModalForm>

    </template>

    <template #footer>
      <ModalFooter
        :showCancel="true"
        :showTest="true"
        :showSave="true"
        :testing="testing"
        :saving="saving"
        @cancel="closeModal"
        @test="testConnection"
        @save="handleSubmit"
      />
    </template>
  </Modal>
</template>

<script setup lang="ts">
import { ref, watch } from 'vue'
import { Modal, ModalHeader, ModalFooter, ModalBody, ModalForm } from '@/components/feedback'
import PasswordInput from '@/components/form/PasswordInput.vue'
import Checkbox from '@/components/form/Checkbox.vue'
import FormRow from '@/components/settings/FormRow.vue'
import CheckboxCard from '@/components/settings/CheckboxCard.vue'
import FormSection from '@/components/settings/FormSection.vue'
import { PhGlobe, PhGear, PhToggleRight, PhInfo } from '@phosphor-icons/vue' 
import type { Indexer } from '@/types' 
import {
  createIndexer,
  updateIndexer,
  testIndexerDraft as apiTestIndexerDraft,
} from '@/services/api'
import { useToast } from '@/services/toastService'

interface Props {
  visible: boolean
  editingIndexer: Indexer | null
}

interface Emits {
  (e: 'close'): void
  (e: 'saved'): void
}

const props = defineProps<Props>()
const emit = defineEmits<Emits>()
const toast = useToast()

const saving = ref(false)
const testing = ref(false)

// MyAnonamouse authentication field
const mamId = ref('')
// MyAnonamouse options
const mamFilter = ref('')
const mamSearchInDescription = ref(false)
const mamSearchInSeries = ref(true)
const mamSearchInFilenames = ref(true)
const mamLanguage = ref('')
const mamFreeleechWedge = ref('')
const mamEnrichResults = ref(false)
const mamEnrichTopResults = ref(3)

// Internet Archive collection field
const iaCollection = ref('librivoxaudio')

const defaultFormData = {
  name: '',
  type: 'Torrent',
  implementation: 'Torznab',
  url: '',
  apiKey: '',
  categories: '',
  enableRss: true,
  enableAutomaticSearch: true,
  enableInteractiveSearch: true,
  enableAnimeStandardSearch: false,
  isEnabled: true,
  priority: 25,
  minimumAge: 0,
  retention: 0,
  maximumSize: 0,
  additionalSettings: '',
}

const formData = ref({ ...defaultFormData })

type IndexerPayload = Omit<Indexer, 'id' | 'createdAt' | 'updatedAt'>

const buildIndexerPayload = (): IndexerPayload => {
  const payload = { ...formData.value } as IndexerPayload
  payload.additionalSettings = payload.additionalSettings || ''

  if (payload.implementation === 'MyAnonamouse') {
    const mamOpts: Record<string, unknown> = {
      searchInDescription: mamSearchInDescription.value,
      searchInSeries: mamSearchInSeries.value,
      searchInFilenames: mamSearchInFilenames.value,
      language: mamLanguage.value || undefined,
      filter: mamFilter.value || undefined,
      freeleechWedge: mamFreeleechWedge.value || undefined,
      enrichResults: mamEnrichResults.value,
      enrichTopResults: mamEnrichTopResults.value,
    }
    payload.additionalSettings = JSON.stringify({ mam_id: mamId.value, mam_options: mamOpts })
    payload.apiKey = ''
  } else if (payload.implementation === 'InternetArchive') {
    payload.additionalSettings = JSON.stringify({ collection: iaCollection.value })
    payload.apiKey = ''
    // Always set to archive.org since backend hardcodes this URL
    payload.url = 'https://archive.org'
    // Set sensible defaults for hidden fields
    payload.categories = ''
    payload.enableRss = false
    payload.minimumAge = 0
  }

  return payload
}

// Watch for editing indexer changes
watch(
  () => props.editingIndexer,
  (newIndexer) => {
    if (newIndexer) {
      formData.value = {
        name: newIndexer.name,
        type: newIndexer.type,
        implementation: newIndexer.implementation,
        url: newIndexer.url,
        apiKey: newIndexer.apiKey || '',
        categories: newIndexer.categories || '',
        enableRss: newIndexer.enableRss,
        enableAutomaticSearch: newIndexer.enableAutomaticSearch,
        enableInteractiveSearch: newIndexer.enableInteractiveSearch,
        enableAnimeStandardSearch: newIndexer.enableAnimeStandardSearch,
        isEnabled: newIndexer.isEnabled,
        priority: newIndexer.priority,
        minimumAge: newIndexer.minimumAge,
        retention: newIndexer.retention,
        maximumSize: newIndexer.maximumSize,
        additionalSettings: newIndexer.additionalSettings || '',
      }

      // Parse MyAnonamouse credentials from additionalSettings
      if (newIndexer.implementation === 'MyAnonamouse' && newIndexer.additionalSettings) {
        try {
          const settings = JSON.parse(newIndexer.additionalSettings)
          mamId.value = settings.mam_id || ''
          // Parse options if present
          if (settings.mam_options) {
            mamSearchInDescription.value =
              settings.mam_options.searchInDescription ?? mamSearchInDescription.value
            mamSearchInSeries.value = settings.mam_options.searchInSeries ?? mamSearchInSeries.value
            mamSearchInFilenames.value =
              settings.mam_options.searchInFilenames ?? mamSearchInFilenames.value
            mamLanguage.value = settings.mam_options.language ?? mamLanguage.value
            mamFilter.value = settings.mam_options.filter ?? mamFilter.value
            mamFreeleechWedge.value = settings.mam_options.freeleechWedge ?? mamFreeleechWedge.value
            mamEnrichResults.value = settings.mam_options.enrichResults ?? mamEnrichResults.value
            mamEnrichTopResults.value =
              settings.mam_options.enrichTopResults ?? mamEnrichTopResults.value
          } else {
            // Also allow flat properties for backward compatibility
            mamSearchInDescription.value =
              settings.searchInDescription ?? mamSearchInDescription.value
            mamSearchInSeries.value = settings.searchInSeries ?? mamSearchInSeries.value
            mamSearchInFilenames.value = settings.searchInFilenames ?? mamSearchInFilenames.value
            mamLanguage.value = settings.language ?? mamLanguage.value
            mamFilter.value = settings.filter ?? mamFilter.value
            mamFreeleechWedge.value = settings.freeleechWedge ?? mamFreeleechWedge.value
            mamEnrichResults.value = settings.enrichResults ?? mamEnrichResults.value
            mamEnrichTopResults.value = settings.enrichTopResults ?? mamEnrichTopResults.value
          }
        } catch (e) {
          console.error('Failed to parse MyAnonamouse settings:', e)
          mamId.value = ''
        }
      }

      // Parse Internet Archive collection from additionalSettings
      if (newIndexer.implementation === 'InternetArchive' && newIndexer.additionalSettings) {
        try {
          const settings = JSON.parse(newIndexer.additionalSettings)
          iaCollection.value = settings.collection || 'librivoxaudio'
        } catch (e) {
          console.error('Failed to parse Internet Archive settings:', e)
          iaCollection.value = 'librivoxaudio'
        }
      }
    } else {
      formData.value = { ...defaultFormData }
      mamId.value = ''
      iaCollection.value = 'librivoxaudio'
    }
  },
  { immediate: true },
)

// Watch for implementation changes to auto-set type
watch(
  () => formData.value.implementation,
  (newImplementation) => {
    // Internet Archive is DDL only, set type to Usenet
    if (newImplementation === 'InternetArchive') {
      formData.value.type = 'Usenet'
    }
    // MyAnonamouse is torrent only
    else if (newImplementation === 'MyAnonamouse') {
      formData.value.type = 'Torrent'
      // Set default URL for MyAnonamouse
      formData.value.url = 'https://www.myanonamouse.net'
    }
    // Torznab defaults to Torrent
    else if (newImplementation === 'Torznab') {
      formData.value.type = 'Torrent'
    }
    // Newznab defaults to Usenet
    else if (newImplementation === 'Newznab') {
      formData.value.type = 'Usenet'
    }
  },
)

const closeModal = () => {
  formData.value = { ...defaultFormData }
  emit('close')
}

const testConnection = async () => {
  testing.value = true
  try {
    const payload = buildIndexerPayload()
    // If API key is empty and editing an existing indexer, include the ID so server merges the saved API key
    if (!payload.apiKey && props.editingIndexer?.id) {
      (payload as unknown as { id?: number }).id = props.editingIndexer.id
    }
    const result = await apiTestIndexerDraft(payload)
    if (result.success) {
      toast.success('Test successful', result.message || '')
    } else {
      toast.error('Test failed', result.error || result.message || '')
    }
  } catch (error: unknown) {
    console.error('Failed to test indexer:', error)

    // Try to parse error response body for detailed message
    let errorMessage = 'Failed to test indexer connection'
    const err = error as { body?: unknown; message?: string }
    if (err?.body) {
      try {
        const errorData =
          typeof err.body === 'string'
            ? JSON.parse(err.body)
            : (err.body as Record<string, unknown>)
        errorMessage =
          (errorData as { message?: string; error?: string }).message ||
          (errorData as { message?: string; error?: string }).error ||
          errorMessage
      } catch {
        // If body isn't JSON, use it as-is if it's a string
        if (typeof err.body === 'string' && err.body.length > 0) {
          errorMessage = err.body
        }
      }
    } else if (err?.message) {
      errorMessage = err.message
    }

    toast.error('Test failed', errorMessage)
  } finally {
    testing.value = false
  }
}

const handleSubmit = async () => {
  saving.value = true
  try {
    const submitData = buildIndexerPayload()

    if (props.editingIndexer) {
      // Update existing indexer
      await updateIndexer(props.editingIndexer.id, submitData)
      toast.success('Indexer saved', 'Indexer updated successfully')
    } else {
      // Create new indexer
      await createIndexer(submitData)
      toast.success('Indexer saved', 'Indexer created successfully')
    }

    emit('saved')
    closeModal()
  } catch (error) {
    console.error('Failed to save indexer:', error)
    toast.error('Save failed', 'Failed to save indexer')
  } finally {
    saving.value = false
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
  border-radius: 6px;
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

.modal-body {
  padding: 2rem;
  overflow-y: auto;
  flex: 1;
}

.form-group {
  margin-bottom: 1.5rem;
  flex: 1;
}

.form-group:last-child {
  margin-bottom: 0;
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

.form-group small {
  display: block;
  margin-top: 0.5rem;
  color: #999;
  font-size: 0.85rem;
}

.form-row {
  display: flex;
  gap: 1rem;
}



/* Settings indexer-specific overrides */
.checkbox-group { display: flex; flex-direction: column; gap: 0.75rem; }
.checkbox-group label:hover { border-color: var(--brand-500); background-color: #222 }
.checkbox-group label span { flex: 1 }

.checkbox-group label strong {
  color: #fff;
  font-size: 0.95rem;
}

.checkbox-group label small {
  color: #999;
  font-size: 0.85rem;
  font-weight: normal;
}

/* modal-footer styles are centralized in src/assets/modals.css */

/* Button visuals are centralized in `src/assets/buttons.css`; modal-specific color variants remain in `src/assets/modals.css`.
   Use `.btn`, `.btn-primary`, `.btn-info`, etc., instead of duplicating styles locally. */

.btn-primary:hover:not(:disabled) {
  background-color: var(--brand-700);
  transform: translateY(-1px);
}

.ph-spin {
  animation: spin 1s linear infinite;
}

/* @keyframes spin is centralized in src/assets/main.css */

@media (max-width: 768px) {
  .modal-overlay {
    padding: 1rem;
  }

  .form-row {
    flex-direction: column;
  }

  /* modal footer responsive behavior and button layout handled by src/assets/modals.css */
}
</style>
