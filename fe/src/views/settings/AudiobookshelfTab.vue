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
  <div class="tab-content">
    <div class="audiobookshelf-tab">
      <div class="section-header">
        <h3>
          Audiobookshelf
          <PhSpinner v-if="loading" class="ph-spin small-inline-spinner" />
        </h3>
      </div>

      <div class="form-section">
        <p class="section-description">
          Connect an Audiobookshelf server so it picks up books imported by Listenarr without a
          manual library scan. Useful when Audiobookshelf's folder watcher cannot see file changes
          (network shares, separate hosts).
        </p>

        <FormRow
          label="Server URL"
          help="Base URL of your Audiobookshelf server, e.g. http://audiobookshelf:13378"
        >
          <input v-model="url" type="text" placeholder="http://audiobookshelf:13378" />
        </FormRow>

        <FormRow
          label="API Token"
          :help="
            hasSavedApiKey
              ? 'A token is saved. Leave blank to keep it, or enter a new one to replace it.'
              : 'An Audiobookshelf API token with admin permissions (Settings → Users → your user → API Token).'
          "
        >
          <div class="password-field">
            <input
              v-model="apiKey"
              :type="showApiKey ? 'text' : 'password'"
              :placeholder="hasSavedApiKey ? '(unchanged)' : 'Audiobookshelf API token'"
              autocomplete="off"
            />
            <button
              type="button"
              class="icon-button"
              :title="showApiKey ? 'Hide token' : 'Show token'"
              @click="showApiKey = !showApiKey"
            >
              <PhEyeSlash v-if="showApiKey" />
              <PhEye v-else />
            </button>
          </div>
        </FormRow>

        <FormRow
          label="Library"
          help="Which Audiobookshelf library to scan. With 'All book libraries', every library with media type 'book' is scanned."
        >
          <CustomSelect v-model="libraryId" :options="libraryOptions" />
        </FormRow>

        <CheckboxCard
          v-model="notifyOnImport"
          title="Scan automatically after import"
          description="Request an Audiobookshelf scan whenever Listenarr imports files (downloads and manual uploads). Bursts of imports are coalesced into a single scan."
        />

        <div class="actions-row">
          <button class="btn btn-primary" :disabled="saving" @click="save">
            <PhSpinner v-if="saving" class="ph-spin" />
            <PhFloppyDisk v-else />
            {{ saving ? 'Saving...' : 'Save' }}
          </button>
          <button class="btn" :disabled="testing || !url" @click="testConnection">
            <PhSpinner v-if="testing" class="ph-spin" />
            <PhPlugs v-else />
            {{ testing ? 'Testing...' : 'Test Connection' }}
          </button>
          <button class="btn" :disabled="scanning || !canScan" @click="scanNow">
            <PhSpinner v-if="scanning" class="ph-spin" />
            <PhArrowsClockwise v-else />
            {{ scanning ? 'Scanning...' : 'Scan Now' }}
          </button>
        </div>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { apiService } from '@/services/api'
import { useToast } from '@/services/toastService'
import { errorTracking } from '@/services/errorTracking'
import type { AudiobookshelfLibrary } from '@/types'
import {
  PhArrowsClockwise,
  PhEye,
  PhEyeSlash,
  PhFloppyDisk,
  PhPlugs,
  PhSpinner,
} from '@phosphor-icons/vue'
import FormRow from '@/components/settings/FormRow.vue'
import CheckboxCard from '@/components/settings/CheckboxCard.vue'
import CustomSelect from '@/components/form/CustomSelect.vue'

const toast = useToast()

const loading = ref(false)
const saving = ref(false)
const testing = ref(false)
const scanning = ref(false)
const showApiKey = ref(false)

const url = ref('')
const apiKey = ref('')
const libraryId = ref<string | null>('')
const notifyOnImport = ref(false)
const hasSavedApiKey = ref(false)
const libraries = ref<AudiobookshelfLibrary[]>([])

// Scan Now uses the saved connection server-side, so it needs a saved token
const canScan = computed(() => Boolean(url.value && hasSavedApiKey.value))

const libraryOptions = computed(() => {
  const options = [{ value: '', label: 'All book libraries' }]
  for (const library of libraries.value) {
    options.push({
      value: library.id,
      label: library.mediaType === 'book' ? library.name : `${library.name} (${library.mediaType})`,
    })
  }
  // Keep a configured id selectable even before the library list has loaded
  if (libraryId.value && !options.some((o) => o.value === libraryId.value)) {
    options.push({ value: libraryId.value, label: libraryId.value })
  }
  return options
})

onMounted(async () => {
  loading.value = true
  try {
    const settings = await apiService.getAudiobookshelfSettings()
    url.value = settings.url ?? ''
    libraryId.value = settings.libraryId ?? ''
    notifyOnImport.value = settings.notifyOnImport
    hasSavedApiKey.value = settings.hasSavedApiKey
    if (settings.url && settings.hasSavedApiKey) {
      const result = await apiService.getAudiobookshelfLibraries()
      if (result.success && result.libraries) {
        libraries.value = result.libraries
      }
    }
  } catch (error) {
    errorTracking.captureException(error as Error, {
      component: 'AudiobookshelfTab',
      operation: 'load',
    })
    toast.error('Load failed', 'Could not load Audiobookshelf settings')
  } finally {
    loading.value = false
  }
})

const save = async () => {
  saving.value = true
  try {
    const saved = await apiService.saveAudiobookshelfSettings({
      url: url.value.trim(),
      apiKey: apiKey.value.trim() || undefined,
      libraryId: libraryId.value || '',
      notifyOnImport: notifyOnImport.value,
      hasSavedApiKey: hasSavedApiKey.value,
    })
    hasSavedApiKey.value = saved.hasSavedApiKey
    apiKey.value = ''
    toast.success('Saved', 'Audiobookshelf settings saved')
  } catch (error) {
    errorTracking.captureException(error as Error, {
      component: 'AudiobookshelfTab',
      operation: 'save',
    })
    toast.error('Save failed', 'Could not save Audiobookshelf settings')
  } finally {
    saving.value = false
  }
}

const testConnection = async () => {
  testing.value = true
  try {
    const result = await apiService.testAudiobookshelfConnection({
      url: url.value.trim() || undefined,
      apiKey: apiKey.value.trim() || undefined,
    })
    if (result.success) {
      if (result.libraries) {
        libraries.value = result.libraries
      }
      toast.success('Connected', result.message)
    } else {
      toast.error('Connection failed', result.message)
    }
  } catch (error) {
    errorTracking.captureException(error as Error, {
      component: 'AudiobookshelfTab',
      operation: 'test',
    })
    toast.error('Connection failed', 'Could not reach the Listenarr API')
  } finally {
    testing.value = false
  }
}

const scanNow = async () => {
  scanning.value = true
  try {
    const result = await apiService.triggerAudiobookshelfScan()
    if (result.success) {
      toast.success('Scan requested', result.message)
    } else {
      toast.error('Scan failed', result.message)
    }
  } catch (error) {
    errorTracking.captureException(error as Error, {
      component: 'AudiobookshelfTab',
      operation: 'scan',
    })
    toast.error('Scan failed', 'Could not reach the Listenarr API')
  } finally {
    scanning.value = false
  }
}
</script>

<style scoped>
.section-description {
  margin: 0 0 1.25rem;
  color: #adb5bd;
  line-height: 1.5;
}

.password-field {
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

.password-field input {
  flex: 1;
}

.actions-row {
  display: flex;
  flex-wrap: wrap;
  gap: 0.75rem;
  margin-top: 1.25rem;
}

.actions-row .btn {
  display: inline-flex;
  align-items: center;
  gap: 0.4rem;
}
</style>
