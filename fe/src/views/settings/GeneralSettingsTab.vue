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
    <div class="general-settings-tab">
      <div class="section-header">
        <h3>General Settings</h3>
      </div>

      <div v-if="validationErrors.length > 0" class="error-summary" role="alert">
        <strong>Please fix the following:</strong>
        <ul>
          <li v-for="(e, idx) in validationErrors" :key="idx">{{ e }}</li>
        </ul>
      </div>

      <div v-if="props.settings" class="settings-form">
        <HostSettingsSection
          :startupConfig="props.startupConfig"
          @update:startupConfig="(val) => emit('update:startupConfig', val)"
        ></HostSettingsSection>

        <FileManagementSection
          :settings="localSettings"
          @update:settings="(val) => Object.assign(localSettings, val)"
        ></FileManagementSection>
        <DownloadSettingsSection
          :settings="localSettings"
          @update:settings="(val) => Object.assign(localSettings, val)"
        ></DownloadSettingsSection>

        <FeaturesSection
          :settings="localSettings"
          @update:settings="(val) => Object.assign(localSettings, val)"
        ></FeaturesSection>

        <SearchSettingsSection
          :settings="localSettings"
          @update:settings="(val) => Object.assign(localSettings, val)"
        ></SearchSettingsSection>

        <AuthenticationSection
          :settings="localSettings"
          :apiKey="props.apiKey"
          v-model:authEnabled="authEnabled"
          @update:settings="(val) => Object.assign(localSettings, val)"
          @update:apiKey="(val) => emit('update:apiKey', val)"
        ></AuthenticationSection>
      </div>
      <!-- settings-form -->
    </div>
    <!-- general-settings-tab -->
  </div>
  <!-- tab-content -->
</template>

<script setup lang="ts">
import { computed } from 'vue'
import type { ApplicationSettings, StartupConfig } from '@/types'
// icons not used directly in this view

import HostSettingsSection from '@/components/settings/HostSettingsSection.vue'
import FileManagementSection from '@/components/settings/FileManagementSection.vue'
import DownloadSettingsSection from '@/components/settings/DownloadSettingsSection.vue'
import FeaturesSection from '@/components/settings/FeaturesSection.vue'
import SearchSettingsSection from '@/components/settings/SearchSettingsSection.vue'
import AuthenticationSection from '@/components/settings/AuthenticationSection.vue'

interface Props {
  settings: ApplicationSettings | null
  startupConfig: StartupConfig | null | undefined
  apiKey?: string
  authEnabled: boolean
}

const props = defineProps<Props>()
const emit = defineEmits<{
  'update:authEnabled': [value: boolean]
  'update:startupConfig': [value: StartupConfig]
  'update:apiKey': [value: string]
  'update:settings': [value: ApplicationSettings | null]
}>()

// Local reactive copy of settings to avoid mutating incoming prop directly
import { reactive, watch, nextTick } from 'vue'
const localSettings = reactive<ApplicationSettings>({} as ApplicationSettings)

// Prevent recursive update loops: when syncing from parent props we set this flag to
// avoid emitting update:settings during the sync process.
let isSyncing = false

watch(
  () => props.settings,
  (val) => {
    if (val) {
      isSyncing = true
      // Replace properties rather than reassigning the reactive object
      for (const key of Object.keys(localSettings) as Array<keyof ApplicationSettings>) {
        delete (localSettings as unknown as Record<string, unknown>)[key as string]
      }
      Object.assign(localSettings, val)
      // Release syncing flag after the microtask so subsequent user-driven changes emit
      nextTick(() => {
        isSyncing = false
      })
    } else {
      isSyncing = true
      for (const key of Object.keys(localSettings) as Array<keyof ApplicationSettings>) {
        delete (localSettings as unknown as Record<string, unknown>)[key as string]
      }
      nextTick(() => {
        isSyncing = false
      })
    }
  },
  { immediate: true, deep: true },
)

// Also watch the proxy toggle specifically so tests that mutate the parent object in-place
// reliably propagate to the child without waiting for a full object replacement.
// proxy configuration removed: useUsProxy and related fields are deprecated

// Emit updates upstream whenever the user changes a field
watch(
  localSettings,
  (val) => {
    if (isSyncing) return
    emit('update:settings', { ...val })
  },
  { deep: true },
)

// Local computed for two-way binding with parent
const authEnabled = computed({
  get: () => props.authEnabled,
  set: (value) => emit('update:authEnabled', value),
})

const validationErrors = computed(() => {
  const errs: string[] = []
  if (!localSettings) return errs
  return errs
})

// proxy config removed; nothing to expose here
</script>

<style scoped>
.tab-content {
  animation: fadeIn 0.2s ease;
}

/* @keyframes fadeIn is centralized in src/assets/animations.css */

.general-settings-tab {
  width: 100%;
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
}

.section-header h3 {
  margin: 0;
  color: #fff;
  font-size: 1.5rem;
  font-weight: 500;
}

.error-summary {
  background: rgba(244, 67, 54, 0.1);
  border: 1px solid rgba(244, 67, 54, 0.3);
  border-radius: 6px;
  padding: 1rem;
  margin-bottom: 1.5rem;
  color: #f44336;
}

.error-summary strong {
  display: block;
  margin-bottom: 0.5rem;
}

.error-summary ul {
  margin: 0;
  padding-left: 1.5rem;
}

.settings-form {
  display: flex;
  flex-direction: column;
  gap: 2rem;
}

.form-section:first-of-type {
  margin-top: 0;
}

.form-section {
  margin-top: 2rem;
  background: transparent;
  border: none;
  border-radius: 8px;
  padding: 0;
  transition: all 0.12s ease;
}

.form-section:hover {
  transform: none;
  box-shadow: none;
}

.form-section > :deep(h3) {
  margin: 0 0 0.75rem 0 !important;
  padding: 0;
  font-size: 1.1rem;
  font-weight: 500;
  display: flex;
  align-items: center;
  gap: 0.5rem;
  color: #fff;
}

.form-section .form-body {
  /* single inner card that matches modal panels (darker) */
  padding: 1.25rem 1.25rem;
  border-radius: 8px;
  border: 1px solid rgba(255, 255, 255, 0.03);
  box-shadow: 0 6px 20px rgba(0, 0, 0, 0.7);
  background: #0b0b0b; /* darker card background */
}

/* Checkbox-related styles have been moved to component-scoped styles in the individual settings components */

.form-section .form-body .form-group + .form-group {
  margin-top: 0.85rem;
}

.form-section h4 {
  margin: 0 0 1.5rem 0;
  font-size: 1.1rem;
  font-weight: 500;
  display: flex;
  align-items: center;
  gap: 0.5rem;
  color: #fff;
}

.info-inline {
  background: none;
  border: none;
  color: #4dabf7;
  cursor: pointer;
  padding: 0.25rem;
  display: inline-flex;
  align-items: center;
  border-radius: 4px;
  transition: all 0.2s;
  font-size: 1rem;
}

.info-inline:hover {
  background: rgba(33, 150, 243, 0.1);
}
/* Form input, label and help styles have been moved into their respective component-scoped styles to reduce global coupling */

.form-group input:focus,
.form-group select:focus {
  outline: none;
  border-color: #4dabf7;
  box-shadow: 0 0 0 3px rgba(77, 171, 247, 0.1);
}

.form-group input:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}
/* .form-row and checkbox item styles moved into component-scoped styles */
/* Authentication layout styles moved to AuthenticationSection.vue */

.input-group {
  display: flex;
  gap: 0;
}

.input-group-input {
  flex: 1;
  border-top-right-radius: 0;
  border-bottom-right-radius: 0;
}

.input-group-append {
  display: flex;
}

.input-group-btn {
  border-top-left-radius: 0;
  border-bottom-left-radius: 0;
  border-left: none;
  padding: 0.75rem 1rem;
  background: rgba(255, 255, 255, 0.05);
  border: 1px solid rgba(255, 255, 255, 0.08);
  color: #fff;
  cursor: pointer;
  display: flex;
  align-items: center;
  gap: 0.5rem;
  transition: all 0.2s;
  font-size: 0.95rem;
  white-space: nowrap;
}

.input-group-btn:hover:not(:disabled) {
  background: rgba(77, 171, 247, 0.15);
  border-color: #4dabf7;
  color: #4dabf7;
}

.input-group-btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.icon-button {
  border-top-right-radius: 0;
  border-bottom-right-radius: 0;
  border-right: none;
}

/* Use centralized .icon-button.copied in src/assets/buttons.css */

.regenerate-button {
  border-top-left-radius: 6px;
  border-bottom-left-radius: 6px;
}

.ph-spin {
  animation: spin 1s linear infinite;
}

/* @keyframes spin is centralized in src/assets/animations.css */

/* Modal-specific styling moved to shared `modals.css` */

.modal-body ul {
  padding-left: 1.5rem;
  margin: 1rem 0;
}

.modal-body li {
  margin-bottom: 0.5rem;
  line-height: 1.6;
}

/* Keep spacing and alignment; padding and border handled by centralized modal stylesheet */
.modal-footer {
  gap: 0.75rem;
  justify-content: flex-end;
}

/* Modal primary action uses centralized modal styles (.btn.btn-primary) */
</style>
